"""AC-I1/I2: verify integrity before spawn; not a Windows policy E2E test."""
import hashlib
import json
import os
import pathlib
import tempfile
import unittest
from unittest.mock import patch
from test_day2_plugin import module


class AccessIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name) / 'Tiny11Template'
        self.root.mkdir()
        self.script = self.root / 'Configure-GuestPolicy.ps1'
        self.content = b'Write-Output policy-fixture'
        (self.root / 'guest-files.json').write_text(json.dumps([
            {'Path': 'Configure-GuestPolicy.ps1',
             'Sha256': hashlib.sha256(self.content).hexdigest().upper()}]))
        self.environment = patch.dict(os.environ, {'ProgramData': self.temp.name})
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def test_missing_helper_rejects_before_spawn(self):
        with patch.object(module.subprocess, 'run') as spawn:
            with self.assertRaises(FileNotFoundError):
                module.WindowsBackend.activate_access(object())
            spawn.assert_not_called()

    def test_tampered_helper_rejects_before_spawn(self):
        self.script.write_bytes(b'changed')
        with patch.object(module.subprocess, 'run') as spawn:
            with self.assertRaisesRegex(RuntimeError, 'Guest policy integrity failed'):
                module.WindowsBackend.activate_access(object())
            spawn.assert_not_called()

    def test_verified_helper_invokes_process_local_policy(self):
        self.script.write_bytes(self.content)
        with patch.object(module.subprocess, 'run') as spawn:
            spawn.return_value.returncode = 0
            module.WindowsBackend.activate_access(object())
            self.assertEqual(spawn.call_args.args[0], [
                'powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy',
                'Bypass', '-File', str(self.script), '-ActivateRdp'])
            self.assertEqual(spawn.call_args.kwargs['timeout'], 60)
