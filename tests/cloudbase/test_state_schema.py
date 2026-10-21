"""AC-S1/S2: persisted state fails closed at the plugin entrypoint."""
import contextlib
import copy
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch
from test_day2_plugin import module, Backend, Metadata


class SandboxState(module.PrivateState):
    def fingerprint(self, value):
        import hashlib
        return hashlib.sha256(value.encode()).hexdigest()

    @contextlib.contextmanager
    def locked(self):
        yield


class StateSchemaTests(unittest.TestCase):
    @staticmethod
    def invalid_states():
        baseline = {'schema': 1, 'applied': {}, 'attempts': {}}
        invalid = [
            {'schema': True}, {'access_ready': 'false'}, {'access_ready': {}},
            {'unknown': 1}, {'groups': []}, {'groups': {'password': 'Invalid'}},
            {'result': []}, {'result': 'Success'}, {'error': {}}, {'error': 'Unknown'},
            {'attempts': {'password': {'target': 'a'*64, 'count': 1,
                                      'boot': 'boot-1', 'status': 'Applying', 'unknown': True}}},
        ]
        return [dict(baseline, **delta) for delta in invalid]

    def test_validator_rejects_invalid_states(self):
        for value in self.invalid_states():
            with self.subTest(value=value), self.assertRaises(ValueError):
                module.PrivateState.validate(value)

    def test_invalid_state_never_mutates_or_overwrites(self):
        for value in self.invalid_states():
            with self.subTest(value=value), tempfile.TemporaryDirectory() as temporary:
                with patch.dict(module.os.environ, {'ProgramData': temporary}):
                    store = SandboxState()
                store.root = pathlib.Path(temporary)
                store.path = store.root / 'state.json'
                raw = json.dumps(value).encode()
                store.path.write_bytes(raw)
                backend = Backend()
                backend.activate_access = lambda: self.fail('Access activation on invalid state')
                plugin = module.ProxmoxReconcilePlugin(backend=backend, store=store,
                    network_reader=lambda metadata: copy.deepcopy(metadata.network))
                with patch.object(module.subprocess, 'run') as spawn:
                    self.assertEqual(plugin.execute(Metadata(), {}), (2, False))
                    self.assertEqual(backend.calls, [])
                    spawn.assert_not_called()
                self.assertEqual(store.path.read_bytes(), raw)

    def test_valid_initial_and_journal_states_load(self):
        minimal = {'schema': 1, 'applied': {}, 'attempts': {}}
        journal = dict(minimal, access_ready=False, result='Reconciling',
                       groups={'password': 'Provided', 'network': 'Static'},
                       error='AccessPolicyFailed', mutation_revisions={'password': 1},
                       hostname_reboots={'a'*64: 1})
        journal['attempts'] = {'hostname': {'target': 'b'*64, 'count': 1,
                                          'boot': 'boot-1', 'status': 'PendingReboot'}}
        for value in (minimal, journal):
            module.PrivateState.validate(value)
