from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock
import hashlib
import importlib.util
import io
import json
import os
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


def load_tool():
    spec = importlib.util.spec_from_file_location('build_package', SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


FAKE_WAMRC = """
import json, sys
from pathlib import Path
here = Path(__file__).resolve().parent
if sys.argv[1:] == ['--version']:
    print(VERSION)
    sys.exit(0)
(here / 'calls.json').write_text(json.dumps(sys.argv[1:]), encoding='utf-8')
output = Path(sys.argv[sys.argv.index('-o') + 1])
output.write_bytes(b'AOT:' + Path(sys.argv[-1]).read_bytes())
"""


class BuildPackageAotTests(unittest.TestCase):
    """--aot: festes wamrc (Pruefsumme, Version), feste Schalter, Dateiname mit Kennung und sortierte file-Zeile."""

    ABI_FILE = 'app.wamr245-aot5-x64-bc.aot'

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.tool = load_tool()
        self.assertEqual(self.call('keygen', str(self.directory / 'key'))[0], 0)
        for name, data in (('app.wasm', b'wasm'), ('App.txt', b'gross'), ('b.dat', b'b')):
            (self.directory / name).write_bytes(data)

    def fake_wamrc(self, version='wamrc 2.4.5'):
        """Ersatz-wamrc, das seine Schalter protokolliert und eine Ausgabedatei schreibt."""
        tool = self.directory / 'fake'
        tool.mkdir()
        (tool / 'fake_wamrc.py').write_text(f'VERSION = {version!r}\n' + FAKE_WAMRC, encoding='utf-8')
        if os.name == 'nt':
            launcher = tool / 'wamrc.cmd'
            launcher.write_text(f'@"{sys.executable}" "{tool / "fake_wamrc.py"}" %*\r\n', encoding='ascii')
        else:
            launcher = tool / 'wamrc'
            launcher.write_text(f'#!{sys.executable}\n' +(tool / 'fake_wamrc.py').read_text(encoding='utf-8'), encoding='utf-8')
            launcher.chmod(0o755)
        return launcher

    def call(self, *arguments):
        stdout, stderr = io.StringIO(), io.StringIO()
        with mock.patch.object(sys, 'argv', ['build-package.py', *arguments]), redirect_stdout(stdout), redirect_stderr(stderr):
            code = self.tool.main()
        return code, stderr.getvalue()

    def build(self, *extra):
        return self.call('package', '--key', str(self.directory / 'key' / 'private.pem'), '--repo', str(self.directory / 'repo'),
                         '--id', 'app', '--version', '1.0.0', '--name', 'App', '--entry', 'app.wasm', *extra,
                         f"{self.directory / 'app.wasm'}=app.wasm", f"{self.directory / 'App.txt'}=App.txt",
                         f"{self.directory / 'b.dat'}=b.dat")

    def allow(self, wamrc):
        digest = hashlib.sha256(wamrc.read_bytes()).hexdigest()
        return mock.patch.object(self.tool, 'WAMRC_SHA256', frozenset({digest}))

    def package_dir(self):
        return self.directory / 'repo' / 'app' / '1.0.0'

    def test_aot_uses_exactly_the_fixed_flags(self):
        wamrc = self.fake_wamrc()
        with self.allow(wamrc):
            code, error = self.build('--aot', str(wamrc))
        self.assertEqual(code, 0, error)
        calls = json.loads((wamrc.parent / 'calls.json').read_text(encoding='utf-8'))
        self.assertEqual(calls[:6], ['--target=x86_64', '--cpu=x86-64', '--bounds-checks=1', '--stack-bounds-checks=1',
                                     '--size-level=0', '--disable-simd'])
        self.assertEqual(calls[6], '-o')
        self.assertEqual(calls[8:], [str(self.directory / 'app.wasm')])

    def test_aot_file_name_manifest_order_and_hash(self):
        wamrc = self.fake_wamrc()
        with self.allow(wamrc):
            code, error = self.build('--aot', str(wamrc))
        self.assertEqual(code, 0, error)
        aot = (self.package_dir() / 'files' / self.ABI_FILE).read_bytes()
        self.assertEqual(aot, b'AOT:wasm')
        lines = (self.package_dir() / 'manifest').read_text(encoding='utf-8').split('\n')
        names = [line.split(' ')[2] for line in lines if line.startswith('file=')]
        # Ordinal wie string.CompareOrdinal in PackageManifest.Parse: Grossbuchstaben vor Kleinbuchstaben, "wam" vor "was".
        self.assertEqual(names, ['App.txt', self.ABI_FILE, 'app.wasm', 'b.dat'])
        self.assertIn(f'file={hashlib.sha256(aot).hexdigest()} {len(aot)} {self.ABI_FILE}', lines)
        self.assertEqual((self.package_dir() / 'manifest.sig').stat().st_size, 64)

    def test_wrong_version_aborts_without_compiling(self):
        wamrc = self.fake_wamrc('wamrc 2.4.4')
        with self.allow(wamrc):
            code, error = self.build('--aot', str(wamrc))
        self.assertEqual(code, 1)
        self.assertIn('2.4.5 erwartet', error)
        self.assertFalse((wamrc.parent / 'calls.json').exists())
        self.assertFalse((self.directory / 'repo').exists())

    def test_unknown_checksum_aborts_before_running_wamrc(self):
        wamrc = self.fake_wamrc()
        code, error = self.build('--aot', str(wamrc))
        self.assertEqual(code, 1)
        self.assertIn('nicht das zugelassene wamrc', error)
        self.assertFalse((wamrc.parent / 'calls.json').exists())
        self.assertFalse((self.directory / 'repo').exists())

    def test_without_aot_output_is_unchanged(self):
        code, error = self.build()
        self.assertEqual(code, 0, error)
        sha = lambda data: hashlib.sha256(data).hexdigest()
        expected = ['zehos-package 1', 'id=app', 'version=1.0.0', 'runtime=zehos-wasm32-v1', 'name=App', 'entry=app.wasm',
                    f"file={sha(b'gross')} 5 App.txt", f"file={sha(b'wasm')} 4 app.wasm", f"file={sha(b'b')} 1 b.dat", 'end', '']
        self.assertEqual((self.package_dir() / 'manifest').read_text(encoding='utf-8').split('\n'), expected)
        self.assertEqual(sorted(p.name for p in (self.package_dir() / 'files').iterdir()), ['App.txt', 'app.wasm', 'b.dat'])


if __name__ == '__main__':
    unittest.main()
