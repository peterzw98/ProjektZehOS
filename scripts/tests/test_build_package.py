from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'build-package.py'


class BuildPackageTests(unittest.TestCase):
    """Das Werkzeug muss die Zeilenfolge schreiben, die PackageManifest.Parse im Kernel erwartet."""

    def run_tool(self, *arguments):
        return subprocess.run([sys.executable, str(SCRIPT), *arguments], capture_output=True, text=True)

    def test_manifest_order_with_network_and_minimum_kernel(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            (directory / 'app.wasm').write_bytes(b'x')
            self.assertEqual(self.run_tool('keygen', str(directory / 'key')).returncode, 0)
            built = self.run_tool('package', '--key', str(directory / 'key' / 'private.pem'), '--repo', str(directory / 'repo'),
                                  '--id', 'app', '--version', '1.0.0', '--name', 'App', '--entry', 'app.wasm', '--network',
                                  '--min-kernel', '0.3.0', f"{directory / 'app.wasm'}=app.wasm")
            self.assertEqual(built.returncode, 0, built.stderr)
            lines = (directory / 'repo' / 'app' / '1.0.0' / 'manifest').read_text(encoding='utf-8').split('\n')
            self.assertEqual(lines[5:8], ['entry=app.wasm', 'capability=net', 'min-kernel=0.3.0'])
            self.assertTrue(lines[8].startswith('file='))
            self.assertEqual(lines[-2:], ['end', ''])
            self.assertEqual((directory / 'repo' / 'app' / '1.0.0' / 'manifest.sig').stat().st_size, 64)

    def test_invalid_minimum_kernel_is_rejected_without_output(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            (directory / 'app.wasm').write_bytes(b'x')
            self.assertEqual(self.run_tool('keygen', str(directory / 'key')).returncode, 0)
            for version in ('0.3', '01.0.0', 'v0.3.0', ''):
                built = self.run_tool('package', '--key', str(directory / 'key' / 'private.pem'), '--repo', str(directory / 'repo'),
                                      '--id', 'app', '--version', '1.0.0', '--name', 'App', '--entry', 'app.wasm',
                                      '--min-kernel', version, f"{directory / 'app.wasm'}=app.wasm")
                self.assertEqual(built.returncode, 1, version)
                self.assertIn('Mindest-Kernelversion', built.stderr)
            self.assertFalse((directory / 'repo' / 'app').exists())


if __name__ == '__main__':
    unittest.main()
