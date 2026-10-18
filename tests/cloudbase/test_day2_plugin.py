"""D2-T01..04: public Cloudbase plugin entrypoint, OS/state seams giả lập."""
import copy
import importlib.util
import pathlib
import types
import unittest
import sys

# Stub chỉ trong test host; production không có fallback Cloudbase giả.
if importlib.util.find_spec('cloudbaseinit') is None:
    for name in ('cloudbaseinit', 'cloudbaseinit.plugins', 'cloudbaseinit.plugins.common'):
        stub = types.ModuleType(name)
        stub.__path__ = []
        sys.modules[name] = stub
    base_stub = types.ModuleType('cloudbaseinit.plugins.common.base')
    class TestBasePlugin:
        def get_name(self):
            return self.__class__.__name__
    base_stub.BasePlugin = TestBasePlugin
    sys.modules[base_stub.__name__] = base_stub

PATH = pathlib.Path(__file__).parents[2] / 'src/Tiny11Builder.Core/Template/tiny11_cloudbase.py'
spec = importlib.util.spec_from_file_location('tiny11_cloudbase', PATH)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Store:
    def __init__(self):
        self.data = {'schema': 1, 'applied': {}, 'attempts': {}}
        self.key = b'test-only-key-not-a-credential'

    def load(self):
        return copy.deepcopy(self.data)

    def save(self, value):
        self.data = copy.deepcopy(value)

    def fingerprint(self, value):
        import hashlib
        import hmac
        return hmac.new(self.key, value.encode(), hashlib.sha256).hexdigest()


class Backend:
    def __init__(self):
        self.calls = []
        self.active = 'host-a'
        self.pending = 'host-a'
        self.fail_network = False
        self.sid = 'S-1-5-21-1-2-3-500'
        self.boot = 'boot-1'

    def validate_account(self):
        if not self.sid.endswith('-500'):
            raise ValueError('Wrong SID')

    def boot_identity(self):
        return self.boot

    def active_hostname(self):
        return self.active

    def pending_hostname(self):
        return self.pending

    def apply_hostname(self, name):
        self.calls.append(('hostname', name))
        self.pending = name
        return self.active != name

    def validate_network(self, network):
        if network.get('mode') not in ('static', 'dhcp'):
            raise ValueError('Invalid network')

    def apply_network(self, network):
        self.calls.append(('network', copy.deepcopy(network)))
        if self.fail_network:
            raise ValueError('Sensitive downstream failure must not escape')

    def apply_password(self, password):
        self.calls.append(('password', password))


class Metadata:
    def __init__(self):
        self.password = 'Synthetic-Password-A!'
        self.name = 'host-a'
        self.network = {'mode': 'static', 'ip': '10.10.10.188', 'prefix': 22, 'gateway': '10.10.10.1', 'dns': ['10.10.10.1']}

    def get_admin_password(self):
        return self.password

    def get_user_data(self):
        return ('#cloud-config\nhostname: '+self.name+'\npassword: ignored\n').encode()


class PluginTests(unittest.TestCase):
    def setUp(self):
        self.backend, self.store, self.metadata = Backend(), Store(), Metadata()
        self.plugin = module.ProxmoxReconcilePlugin(backend=self.backend, store=self.store,
                                                    network_reader=lambda s: copy.deepcopy(s.network))

    def execute(self):
        return self.plugin.execute(self.metadata, {})

    # PL-07 / D2-T01
    def test_password_A_B_A_and_unchanged(self):
        for value in ('Synthetic-Password-A!', 'Synthetic-Password-B!', 'Synthetic-Password-A!'):
            self.metadata.password = value
            self.assertEqual(self.execute(), (2, False))
        self.execute()
        self.assertEqual([x[1] for x in self.backend.calls if x[0] == 'password'],
                         ['Synthetic-Password-A!', 'Synthetic-Password-B!', 'Synthetic-Password-A!'])
        self.assertNotIn('Synthetic-Password', repr(self.store.data))

    # PL-07 / D2-T01
    def test_missing_password_never_resets_or_generates(self):
        self.metadata.password = None
        self.execute()
        self.assertEqual([x for x in self.backend.calls if x[0] == 'password'], [])

    # PL-07 / D2-T01
    def test_wrong_sid_blocks_every_mutation(self):
        self.backend.sid = 'S-1-5-21-1-2-3-1000'
        self.execute()
        self.assertEqual(self.backend.calls, [])

    # PL-05 / D2-T02
    def test_pending_hostname_ack_only_after_next_boot_active(self):
        self.execute()
        self.backend.calls.clear()
        self.metadata.name = 'host-b'
        self.assertEqual(self.execute(), (2, True))
        self.assertEqual(self.store.data['applied']['hostname'], 'host-a')
        self.assertEqual(self.execute(), (2, False))
        self.backend.boot = 'boot-2'
        self.backend.active = 'host-b'
        self.assertEqual(self.execute(), (2, False))
        self.assertEqual(self.store.data['applied']['hostname'], 'host-b')
        self.assertEqual(self.backend.calls, [('hostname', 'host-b')])

    # PL-05 / D2-T02
    def test_hostname_return_A_without_reapplying_other_groups(self):
        self.execute()
        self.metadata.name = 'host-b'
        self.execute()
        self.backend.active, self.backend.boot = 'host-b', 'boot-2'
        self.execute()
        self.backend.calls.clear()
        self.metadata.name = 'host-a'
        self.assertEqual(self.execute(), (2, True))
        self.assertEqual(self.backend.calls, [('hostname', 'host-a')])

    # PL-06 / D2-T03
    def test_network_only_A_B_A_isolated_from_password(self):
        self.execute()
        self.backend.calls.clear()
        for value in ('10.10.10.189', '10.10.10.188'):
            self.metadata.network['ip'] = value
            self.execute()
        self.assertEqual([x[0] for x in self.backend.calls], ['network', 'network'])

    # PL-06 / D2-T03
    def test_failed_network_is_not_committed_and_retries_next_boot(self):
        self.backend.fail_network = True
        self.execute()
        self.assertNotIn('network', self.store.data['applied'])
        self.backend.fail_network = False
        self.execute()
        self.assertNotIn('network', self.store.data['applied'])
        self.backend.boot = 'boot-2'
        self.execute()
        self.assertEqual(self.store.data['applied']['network'], self.metadata.network)

    # GP-07 / D2-T04
    def test_retries_are_bounded_and_status_does_not_expose_exception(self):
        self.backend.fail_network = True
        for index in range(4):
            self.backend.boot = str(index)
            self.execute()
        self.assertEqual(len([x for x in self.backend.calls if x[0] == 'network']), 3)
        self.assertNotIn('Sensitive', repr(self.store.data))
        self.assertEqual(self.store.data['attempts']['network']['status'], 'BlockedRetryLimit')
        self.backend.boot = 'operator-reboot-after-cooldown'
        self.backend.fail_network = False
        self.execute()
        self.assertIn('network', self.store.data['applied'])

    # PL-06 / D2-T03: dirty mutation phải được reconcile kể cả desired quay last-applied.
    def test_failed_network_B_then_return_A_reapplies_A(self):
        self.execute()
        self.metadata.network['ip'] = '10.10.10.189'
        self.backend.fail_network = True
        self.execute()
        self.backend.fail_network = False
        self.backend.calls.clear()
        self.metadata.network['ip'] = '10.10.10.188'
        self.backend.boot = 'boot-2'
        self.execute()
        self.assertEqual([x[0] for x in self.backend.calls], ['network'])

    # PL-05 / D2-T02: pending B có thể thành active sau reboot dù desired đã quay A.
    def test_pending_rename_B_then_return_A_does_not_skip(self):
        self.execute()
        self.metadata.name = 'host-b'
        self.execute()
        self.backend.active, self.backend.boot = 'host-b', 'boot-2'
        self.metadata.name = 'host-a'
        self.backend.calls.clear()
        self.assertEqual(self.execute(), (2, True))
        self.assertEqual(self.backend.calls, [('hostname', 'host-a')])

    # PL-05 / D2-T02: active vẫn A nhưng registry pending B phải được hủy.
    def test_pending_B_active_A_then_desired_A_cancels_pending(self):
        self.execute()
        self.metadata.name = 'host-b'
        self.execute()
        self.assertEqual(self.backend.pending, 'host-b')
        self.metadata.name = 'host-a'
        self.backend.calls.clear()
        self.execute()
        self.assertEqual(self.backend.pending, 'host-a')
        self.assertEqual(self.backend.calls, [('hostname', 'host-a')])

    # GP-07 / D2-T04: diagnostics lỗi không chặn boot sau khi metadata sửa đúng.
    def test_invalid_metadata_then_corrected_metadata(self):
        self.metadata.name = 'bad name'
        self.execute()
        self.metadata.name = 'host-a'
        self.execute()
        self.assertIn('password', self.store.data['applied'])

    # PL-07 / D2-T01: missingsecret ban đầu không chặn password xuất hiện sau.
    def test_missing_password_then_supplied_later(self):
        self.metadata.password = None
        self.execute()
        self.metadata.password = 'Synthetic-Later!'
        self.execute()
        self.assertIn(('password', 'Synthetic-Later!'), self.backend.calls)

    # PL-07 / D2-T01: setter thành công nhưng journal chưa acknowledge phải reapply A.
    def test_dirty_password_B_then_return_A_reapplies_A(self):
        self.execute()
        self.store.data['attempts']['password'] = {'target': 'other-target', 'count': 1, 'boot': 'boot-1', 'status': 'Applying'}
        self.backend.calls.clear()
        self.backend.boot = 'boot-2'
        self.execute()
        self.assertEqual(self.backend.calls, [('password', 'Synthetic-Password-A!')])

    # GP-07 / D2-T04: retry access không reapply password/network.
    def test_access_retry_does_not_repeat_mutations(self):
        attempts = []
        def access():
            attempts.append(True)
            if len(attempts) == 1:
                raise ValueError('access failed')
        self.backend.activate_access = access
        self.execute()
        self.assertEqual(self.store.data['error'], 'AccessPolicyFailed')
        self.backend.calls.clear()
        self.execute()
        self.assertTrue(self.store.data['access_ready'])
        self.assertEqual(self.backend.calls, [])

    # GP-07 / D2-T04: thiếupasswordkhông mởRDP.
    def test_missing_password_does_not_activate_access(self):
        self.metadata.password = None
        self.backend.activate_access = lambda: self.fail('Access activated without password')
        self.execute()
        self.assertNotIn('access_ready', self.store.data)

    # PL-05 / D2-T02: successful lifecycle không tích lũy reboot budget vĩnh viễn.
    def test_five_hostname_A_B_A_cycles(self):
        self.execute()
        for index in range(5):
            for name in ('host-b', 'host-a'):
                self.metadata.name = name
                self.backend.boot = str(index)+name+'request'
                self.assertEqual(self.execute(), (2, True))
                self.backend.active = name
                self.backend.boot = str(index)+name+'ack'
                self.assertEqual(self.execute(), (2, False))
                self.assertEqual(self.store.data['applied']['hostname'], name)
        self.assertEqual(self.store.data['hostname_reboots'], {})

    # GP-07 / D2-T04: auto-reboot cùng desired có giới hạn, không reset theo boot.
    def test_hostname_auto_reboot_budget_does_not_reset(self):
        self.metadata.name = 'host-b'
        requests = 0
        for index in range(12):
            self.backend.boot = str(index)
            requests += int(self.execute()[1])
        self.assertEqual(requests, 3)
        self.assertEqual(self.store.data['attempts']['hostname']['status'], 'BlockedRebootLimit')

    # GP-07 / D2-T04: corrupt journal không được gọi mutation.
    def test_corrupt_attempt_schema_rejected(self):
        for record in ({'count': '3'}, {'target': 'x', 'boot': [], 'count': 1, 'status': 'Applying'}):
            with self.assertRaises(ValueError):
                module.PrivateState.validate({'schema':1,'applied':{},'attempts':{'password':record}})

    # GP-07 / D2-T04
    def test_invalid_hostname_rejected_before_mutation(self):
        self.metadata.name = "bad'; command"
        self.execute()
        self.assertEqual(self.backend.calls, [])

    # GP-07 / D2-T04
    def test_duplicate_hostname_in_userdata_rejected(self):
        self.metadata.get_user_data = lambda: b'#cloud-config\nhostname: host-a\nhostname: host-b\n'
        self.execute()
        self.assertEqual(self.backend.calls, [])


if __name__ == '__main__':
    unittest.main()
