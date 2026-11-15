"""Regression checks for the observed legacy-port override and owner protection."""
from pathlib import Path
import hashlib
import importlib.util
import json
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[2] / 'deploy/aethermu-europe/origin-prestige-staging/configure_client.py'
spec = importlib.util.spec_from_file_location('endpoint', MODULE)
endpoint = importlib.util.module_from_spec(spec)
spec.loader.exec_module(endpoint)


class EndpointTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.client = Path(self.temp.name)
        self.executable = b'verified-test-client'
        (self.client / 'Main.exe').write_bytes(self.executable)
        self.original_hash = endpoint.EXPECTED_MAIN
        endpoint.EXPECTED_MAIN = hashlib.sha256(self.executable).hexdigest()
        self.addCleanup(setattr, endpoint, 'EXPECTED_MAIN', self.original_hash)
        self.manifest = {'edition': 'Aether Client v2', 'realm': 'origin', 'files': {}}
        self.write_manifest()
        self.ini = '[CONNECTION SETTINGS]\nServerIP=192.0.2.1\nServerPort=45406\n[VIDEO]\nTheme=Eternal Kingdom\nServerPort=12345\n'
        (self.client / 'config.ini').write_text(self.ini)

    def write_manifest(self):
        (self.client / 'aether-client-manifest.json').write_text(json.dumps(self.manifest))

    def test_legacy_port_corrected_without_touching_theme_or_other_section(self):
        endpoint.configure(self.client)
        actual = (self.client / 'config.ini').read_text()
        self.assertEqual(actual, self.ini.replace('192.0.2.1', '127.0.0.1').replace('45406', '44416'))
        self.assertFalse(endpoint.configure(self.client)['changed'])
        manifest = json.loads((self.client / 'aether-client-manifest.json').read_text())
        self.assertEqual(manifest['files']['config.ini'], hashlib.sha256((self.client / 'config.ini').read_bytes()).hexdigest())

    def test_owner_client_without_marker_refused_unchanged(self):
        (self.client / 'aether-client-manifest.json').unlink()
        with self.assertRaises(FileNotFoundError):
            endpoint.configure(self.client)
        self.assertEqual((self.client / 'config.ini').read_text(), self.ini)

    def test_other_realm_refused_unchanged(self):
        self.manifest['realm'] = 'ascension'
        self.write_manifest()
        with self.assertRaises(ValueError):
            endpoint.configure(self.client)
        self.assertEqual((self.client / 'config.ini').read_text(), self.ini)

    def test_unverified_executable_refused_unchanged(self):
        (self.client / 'Main.exe').write_bytes(b'incompatible')
        with self.assertRaises(ValueError):
            endpoint.configure(self.client)
        self.assertEqual((self.client / 'config.ini').read_text(), self.ini)


if __name__ == '__main__':
    unittest.main(verbosity=2)
