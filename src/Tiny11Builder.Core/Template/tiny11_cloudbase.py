"""Cloudbase extension reconciling Proxmox metadata for the opt-in profile."""
import base64
import contextlib
import hashlib
import hmac
import ipaddress
import json
import os
import pathlib
import re
import subprocess
import tempfile
import time

from cloudbaseinit.plugins.common import base


MAX_ATTEMPTS = 3


def validate_static(desired):
    if (not isinstance(desired, dict) or set(desired) != {'mode', 'ip', 'prefix', 'gateway', 'dns'} or
            desired['mode'] != 'static' or type(desired['prefix']) is not int or
            not 1 <= desired['prefix'] <= 32 or not isinstance(desired['ip'], str) or
            not isinstance(desired['gateway'], str) or not isinstance(desired['dns'], list) or
            not desired['dns'] or not all(isinstance(x, str) for x in desired['dns'])):
        raise ValueError('Invalid network fields')
    interface = ipaddress.IPv4Interface(desired['ip'] + '/' + str(desired['prefix']))
    if ipaddress.IPv4Address(desired['gateway']) not in interface.network:
        raise ValueError('Invalid gateway subnet')
    for value in desired['dns']:
        ipaddress.IPv4Address(value)


def valid_hostname(name):
    return (isinstance(name, str) and
            re.fullmatch(r'[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?', name) is not None and
            not name.isdigit())


def _hostname(service):
    import yaml

    class UniqueLoader(yaml.SafeLoader):
        pass

    def mapping(loader, node, deep=False):
        result = {}
        for key_node, value_node in node.value:
            key = loader.construct_object(key_node, deep=deep)
            if not isinstance(key, str) or key in result:
                raise ValueError('Invalid metadata keys')
            result[key] = loader.construct_object(value_node, deep=deep)
        return result

    UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, mapping)
    raw = service.get_user_data()
    if not isinstance(raw, (str, bytes)) or len(raw) > 65536:
        raise ValueError('Missing or oversized user data')
    if isinstance(raw, bytes):
        raw = raw.decode('utf-8', errors='strict')
    if not raw.startswith('#cloud-config'):
        raise ValueError('Unsupported user data')
    value = yaml.load(raw, Loader=UniqueLoader)
    name = value.get('hostname') if isinstance(value, dict) else None
    if not valid_hostname(name):
        raise ValueError('Invalid hostname')
    return name.lower()


def _network(service):
    content = service.get_content('0000')
    if isinstance(content, bytes):
        content = content.decode('utf-8', errors='strict')
    if not isinstance(content, str) or len(content) > 65536:
        raise ValueError('Missing network metadata')
    modes = re.findall(r'^\s*iface\s+(?!lo\s)\S+\s+inet\s+(\S+)\s*$', content, re.MULTILINE)
    if modes == ['dhcp']:
        # Initial DHCP preserves inbox defaults; inventory is not network verification.
        return None
    if modes != ['static']:
        raise ValueError('Unsupported or missing IPv4 network definition')
    details = service.get_network_details() or []
    ipv4 = [nic for nic in details if nic.address]
    if not ipv4:
        raise ValueError('Incomplete static network metadata')
    if len(ipv4) != 1:
        raise ValueError('Only one IPv4 NIC is supported')
    nic = ipv4[0]
    interface = ipaddress.IPv4Interface(str(nic.address) + '/' + str(nic.netmask))
    gateway = str(ipaddress.IPv4Address(nic.gateway)) if nic.gateway else None
    dns = [str(ipaddress.IPv4Address(value)) for value in (nic.dnsnameservers or [])]
    if not gateway or not dns or ipaddress.IPv4Address(gateway) not in interface.network:
        raise ValueError('Missing or invalid gateway/DNS')
    return {'mode': 'static', 'ip': str(interface.ip), 'prefix': interface.network.prefixlen,
            'gateway': gateway, 'dns': dns}


class PrivateState:
    def __init__(self):
        self.root = pathlib.Path(os.environ['ProgramData']) / 'Tiny11Template' / 'day2-state'
        self.path = self.root / 'state.json'

    @contextlib.contextmanager
    def locked(self):
        import msvcrt
        self.root.mkdir(parents=True, exist_ok=True)
        self._regular(self.root)
        self._regular(self.root.parent)
        self._regular(self.root / 'lock')
        subprocess.run(['icacls.exe', str(self.root), '/inheritance:r', '/grant:r',
                        '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F'],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True, timeout=15)
        with open(self.root / 'lock', 'a+b') as lock:
            lock.seek(0)
            lock.write(b'0')
            lock.flush()
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            try:
                # Create the key before diagnostics/state so validation failures do not block bootstrap.
                self.fingerprint('state-initialization')
                yield
            finally:
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)

    @staticmethod
    def _regular(path):
        import stat
        if path.exists() and (path.is_symlink() or getattr(path.stat(follow_symlinks=False), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
            raise ValueError('Reparse points are not allowed')

    def _atomic(self, path, data):
        self._regular(self.root)
        self._regular(path)
        fd, temporary = tempfile.mkstemp(dir=self.root, prefix='.pending-')
        try:
            with os.fdopen(fd, 'wb') as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def load(self):
        if not self.path.exists():
            return {'schema': 1, 'applied': {}, 'attempts': {}}
        if self.path.is_symlink() or self.path.stat().st_size > 131072:
            raise ValueError('Invalid state file')
        value = json.loads(self.path.read_text(encoding='utf-8'))
        self.validate(value)
        return value

    @staticmethod
    def validate(value):
        groups = {'hostname', 'network', 'password'}
        allowed = {'schema', 'applied', 'attempts', 'mutation_revisions', 'hostname_reboots',
                   'access_ready', 'groups', 'result', 'error'}
        if (not isinstance(value, dict) or not set(value).issubset(allowed) or
                type(value.get('schema')) is not int or value['schema'] != 1 or
                not isinstance(value.get('applied'), dict) or not isinstance(value.get('attempts'), dict) or
                not set(value['applied']).issubset(groups) or not set(value['attempts']).issubset(groups)):
            raise ValueError('Invalid state schema')
        for group, applied in value['applied'].items():
            if group == 'password' and (not isinstance(applied, str) or not re.fullmatch(r'[0-9a-f]{64}', applied)):
                raise ValueError('Invalid password fingerprint')
            if group == 'hostname' and (not valid_hostname(applied) or applied != applied.lower()):
                raise ValueError('Invalid hostname state')
            if group == 'network':
                validate_static(applied)
        if 'access_ready' in value and type(value['access_ready']) is not bool:
            raise ValueError('Invalid access readiness')
        if 'result' in value and (not isinstance(value['result'], str) or
                value['result'] not in {'Reconciling', 'Failed', 'PendingOrFailed', 'Applied'}):
            raise ValueError('Invalid result state')
        if 'error' in value and (not isinstance(value['error'], str) or
                value['error'] not in {'ValidateAccount', 'ReadBootIdentity', 'ReadMetadata',
                                       'ValidateMetadata', 'AccessPolicyFailed'}):
            raise ValueError('Invalid error state')
        if 'groups' in value:
            labels = value['groups']
            if (not isinstance(labels, dict) or not set(labels).issubset({'password', 'network'}) or
                    any(not isinstance(label, str) or label not in
                        ({'Provided', 'NotProvided'} if group == 'password' else {'Static', 'InboxDhcpUnverified'})
                        for group, label in labels.items())):
                raise ValueError('Invalid group labels')
        revisions = value.get('mutation_revisions', {})
        if not isinstance(revisions, dict) or not set(revisions).issubset(groups) or any(type(v) is not int or v < 0 for v in revisions.values()):
            raise ValueError('Invalid mutation revision')
        budgets = value.get('hostname_reboots', {})
        if not isinstance(budgets, dict) or any(not isinstance(key, str) or not re.fullmatch(r'[0-9a-f]{64}', key) or
                                               type(count) is not int or not 0 <= count <= MAX_ATTEMPTS
                                               for key, count in budgets.items()):
            raise ValueError('Invalid hostname reboot budget')
        for attempt in value['attempts'].values():
            if (not isinstance(attempt, dict) or set(attempt) != {'target', 'count', 'boot', 'status'} or
                    not isinstance(attempt.get('target'), str) or
                    not re.fullmatch(r'[0-9a-f]{64}', attempt['target']) or
                    type(attempt.get('count')) is not int or not 0 <= attempt['count'] <= MAX_ATTEMPTS or
                    not isinstance(attempt.get('boot'), str) or
                    attempt.get('status') not in {'Applying', 'PendingReboot', 'Failed', 'BlockedRetryLimit', 'BlockedRebootLimit'}):
                raise ValueError('Invalid attempt state')

    def save(self, value):
        self._atomic(self.path, json.dumps(value, sort_keys=True).encode('utf-8'))

    def fingerprint(self, password):
        import win32crypt
        path = self.root / 'key.dpapi'
        self._regular(path)
        if not path.exists():
            if self.path.exists():
                raise ValueError('Protected key missing for existing state')
            key = os.urandom(32)
            encrypted = win32crypt.CryptProtectData(key, 'Tiny11 day2 key', None, None, None, 4)
            self._atomic(path, encrypted)
        key = win32crypt.CryptUnprotectData(path.read_bytes(), None, None, None, 0)[1]
        if len(key) != 32:
            raise ValueError('Invalid protected key')
        return hmac.new(key, password.encode('utf-8'), hashlib.sha256).hexdigest()


class WindowsBackend:
    def __init__(self):
        from cloudbaseinit.osutils import factory
        self.osutils = factory.get_os_utils()
        self.username = None
        self.interface = None

    @staticmethod
    def _powershell(script):
        encoded = base64.b64encode(('$ErrorActionPreference="Stop";$ProgressPreference="SilentlyContinue";[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false);' + script).encode('utf-16-le')).decode()
        result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
                                capture_output=True, timeout=30, check=False)
        if result.returncode:
            raise RuntimeError('Windows observation failed')
        return result.stdout.decode('utf-8-sig').strip()

    def boot_identity(self):
        return self._powershell("(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')")

    def validate_account(self):
        accounts = [name for name in self.osutils.enum_users() if self.osutils.is_builtin_admin(name)]
        if len(accounts) != 1:
            raise ValueError('Built-in Administrator not unique')
        self.username = accounts[0]

    def active_hostname(self):
        import platform
        return platform.node().lower()

    def pending_hostname(self):
        import winreg
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                            r'SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName') as key:
            return winreg.QueryValueEx(key, 'ComputerName')[0].lower()

    def apply_hostname(self, name):
        # Call the setter to cancel pending renames even when the active name already matches.
        self.osutils.set_host_name(name)
        if self.pending_hostname() != name:
            raise RuntimeError('Pending hostname postcondition failed')
        return self.active_hostname() != name

    def validate_network(self, desired):
        validate_static(desired)
        adapters = self.osutils.get_network_adapters()
        if len(adapters) != 1:
            raise ValueError('Expected one Ethernet adapter')
        self.interface = adapters[0][0]

    def apply_network(self, desired):
        self.osutils.set_static_network_config(self.interface, desired['ip'], desired['prefix'],
                                               desired['gateway'], desired['dns'])
        interface = self.interface.replace("'", "''")
        expected_dns = desired['dns']
        for _ in range(10):
            # Mark applied only after verifying the actual IP, routes, and DNS.
            raw = self._powershell("$i='" + interface + "';"
                "$a=@(Get-NetIPAddress -InterfaceAlias $i -AddressFamily IPv4 | Select IPAddress,PrefixLength,AddressState);"
                "$r=@(Get-NetRoute -InterfaceAlias $i -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select -ExpandProperty NextHop);"
                "$d=@((Get-DnsClientServerAddress -InterfaceAlias $i -AddressFamily IPv4).ServerAddresses);"
                "@{Addresses=$a;Routes=$r;DNS=$d}|ConvertTo-Json -Depth 4 -Compress")
            actual = json.loads(raw)
            addresses = actual['Addresses']
            if (len(addresses) == 1 and addresses[0]['IPAddress'] == desired['ip'] and
                    addresses[0]['PrefixLength'] == desired['prefix'] and
                    addresses[0]['AddressState'] in (4, 'Preferred') and
                    actual['Routes'] == [desired['gateway']] and actual['DNS'] == expected_dns):
                return
            time.sleep(1)
        raise RuntimeError('Network postcondition failed')

    def activate_access(self):
        root = pathlib.Path(os.environ['ProgramData']) / 'Tiny11Template'
        script = root / 'Configure-GuestPolicy.ps1'
        PrivateState._regular(root)
        PrivateState._regular(script)
        inventory = json.loads((root / 'guest-files.json').read_text(encoding='utf-8-sig'))
        entries = [item for item in inventory if item.get('Path') == 'Configure-GuestPolicy.ps1']
        if len(entries) != 1 or hashlib.sha256(script.read_bytes()).hexdigest().upper() != entries[0]['Sha256'].upper():
            raise RuntimeError('Guest policy integrity failed')
        result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(script), '-ActivateRdp'],
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60, check=False)
        if result.returncode:
            raise RuntimeError('Guest access policy failed')

    def apply_password(self, password):
        if not self.username or not self.osutils.is_builtin_admin(self.username):
            raise ValueError('Administrator identity changed')
        self.osutils.set_user_password(self.username, password)
        self.osutils.set_user_info(self.username, disabled=False)


class ProxmoxReconcilePlugin(base.BasePlugin):
    def __init__(self, backend=None, store=None, network_reader=None):
        self.backend = backend
        self.store = store
        self.network_reader = network_reader or _network

    def execute(self, service, shared_data):
        store = self.store or PrivateState()
        try:
            with store.locked() if hasattr(store, 'locked') else contextlib.nullcontext():
                return self._reconcile(service, store)
        except Exception:
            # Upstream logs exception strings; never leak passwords or metadata through them.
            from logging import getLogger
            getLogger(__name__).error('Tiny11 reconciliation failed; inspect private state and guest health')
            return 2, False

    def _reconcile(self, service, store):
        backend = self.backend or WindowsBackend()
        state = store.load()
        stage = 'ValidateAccount'
        try:
            backend.validate_account()
            stage = 'ReadBootIdentity'
            boot = backend.boot_identity()
            stage = 'ReadMetadata'
            name = _hostname(service)
        except Exception:
            state['result'] = 'Failed'
            state['error'] = stage
            store.save(state)
            return 2, False
        try:
            network = self.network_reader(service)
            if network is not None:
                backend.validate_network(network)
            password = service.get_admin_password()
            if password is not None and (not isinstance(password, str) or not password or len(password) > 256):
                raise ValueError('Invalid password metadata')
        except Exception:
            state['result'] = 'Failed'
            state['error'] = 'ValidateMetadata'
            store.save(state)
            return 2, False
        state.pop('error', None)
        desired = {'hostname': name}
        if network is not None:
            desired['network'] = network
        if password:
            desired['password'] = store.fingerprint(password)
        reboot = False
        state['result'] = 'Reconciling'
        for group in ('network', 'password', 'hostname'):
            if group not in desired:
                continue
            target = desired[group]
            dirty = group in state['attempts']
            if state['applied'].get(group) == target and not dirty:
                continue
            signature = store.fingerprint(json.dumps(target, sort_keys=True))
            attempt = state['attempts'].get(group, {})
            if attempt.get('target') != signature:
                attempt = {'target': signature, 'count': 0, 'boot': None}
            reboot_budget = state.setdefault('hostname_reboots', {})
            if group == 'hostname' and not (backend.active_hostname() == name and backend.pending_hostname() == name) and reboot_budget.get(signature, 0) >= MAX_ATTEMPTS:
                attempt.update(status='BlockedRebootLimit', boot=boot)
                state['attempts'][group] = attempt
                store.save(state)
                continue
            if group == 'hostname' and backend.active_hostname() == name and backend.pending_hostname() == name:
                state['applied'][group] = target
                state['attempts'].pop(group, None)
                reboot_budget.pop(signature, None)
                store.save(state)
                continue
            # Do not reboot on retry failures. After three failed boots, pause for one boot,
            # then allow another retry batch after a GUI/API reboot without manual state edits.
            if attempt.get('count', 0) >= MAX_ATTEMPTS and attempt.get('boot') != boot:
                if attempt.get('status') == 'BlockedRetryLimit':
                    attempt = {'target': signature, 'count': 0, 'boot': None}
                else:
                    attempt.update(status='BlockedRetryLimit', boot=boot)
                    state['attempts'][group] = attempt
                    store.save(state)
                    continue
            if attempt.get('boot') == boot:
                state['attempts'][group] = attempt
                store.save(state)
                continue
            attempt.update(count=attempt.get('count', 0) + 1, boot=boot, status='Applying')
            state['attempts'][group] = attempt
            store.save(state)
            try:
                if group == 'network':
                    backend.apply_network(network)
                elif group == 'password':
                    backend.apply_password(password)
                else:
                    # Persist the budget before setters/reboots to prevent interruption loops.
                    reboot_budget[signature] = reboot_budget.get(signature, 0) + 1
                    store.save(state)
                    reboot = backend.apply_hostname(name)
                    if backend.active_hostname() != name or backend.pending_hostname() != name:
                        attempt['status'] = 'PendingReboot'
                        store.save(state)
                        continue
                state['applied'][group] = target
                state['attempts'].pop(group, None)
                revisions = state.setdefault('mutation_revisions', {})
                revisions[group] = revisions.get(group, 0) + 1
                if group == 'hostname':
                    reboot_budget.pop(signature, None)
                store.save(state)
            except Exception:
                attempt['status'] = 'Failed'
                state['attempts'][group] = attempt
                store.save(state)
        state['groups'] = {'password': 'Provided' if password else 'NotProvided',
                           'network': 'Static' if network is not None else 'InboxDhcpUnverified'}
        if (password and state['applied'].get('password') == desired.get('password') and
                'password' not in state['attempts'] and not state.get('access_ready') and hasattr(backend, 'activate_access')):
            try:
                backend.activate_access()
                state['access_ready'] = True
            except Exception:
                state['error'] = 'AccessPolicyFailed'
        state['result'] = 'PendingOrFailed' if state['attempts'] or state.get('error') else 'Applied'
        store.save(state)
        return 2, reboot
