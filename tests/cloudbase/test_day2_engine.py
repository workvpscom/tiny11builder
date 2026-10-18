"""D2-T01: actual pinned InitManager scheduling gọi plugin entrypoint; OS giả lập."""
import ast
import hashlib
import pathlib
import types
import unittest
from test_day2_plugin import Backend, Metadata, Store, module

ROOT = pathlib.Path('/tmp/tiny11-cbi-day2-119')
SOURCE = ROOT / 'cloudbaseinit/init.py'
PIN = 'a8bedb3e656ff9a3259c39db2a3bc4543923cd5c57c64e328be5cecb024fc4bb'


class EngineTests(unittest.TestCase):
    def test_reused_instance_uuid_still_reconciles_password(self):
        if not SOURCE.exists():
            self.skipTest('Pinned upstream source required; not a PASS')
        raw = SOURCE.read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest(), PIN)
        tree = ast.parse(raw)
        node = next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == 'InitManager')
        scope = {'plugins_base': types.SimpleNamespace(PLUGIN_EXECUTION_DONE=1),
                 'LOG': types.SimpleNamespace(**{n: lambda *a, **k: None for n in ('info', 'error', 'debug', 'exception')})}
        exec(compile(ast.Module(body=[node], type_ignores=[]), str(SOURCE), 'exec'), scope)
        status = {}
        osutils = types.SimpleNamespace(get_config_value=lambda n, s: status.get((s,n)),
                                        set_config_value=lambda n,v,s: status.__setitem__((s,n),v))
        backend, store, service = Backend(), Store(), Metadata()
        plugin = module.ProxmoxReconcilePlugin(backend=backend, store=store, network_reader=lambda s: s.network)
        engine = scope['InitManager']()
        for instance, value in [('A','Synthetic-A!'),('B','Synthetic-B!'),('A','Synthetic-A!')]:
            service.password = value
            self.assertEqual(engine._exec_plugin(osutils, service, plugin, instance, {}), (True,False))
        self.assertEqual([x[1] for x in backend.calls if x[0]=='password'], ['Synthetic-A!','Synthetic-B!','Synthetic-A!'])
        self.assertEqual(set(status.values()), {2})
