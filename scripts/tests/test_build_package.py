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

    def test_storage_capability_follows_network(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            (directory / 'app.wasm').write_bytes(b'x')
            self.assertEqual(self.run_tool('keygen', str(directory / 'key')).returncode, 0)
            built = self.run_tool('package', '--key', str(directory / 'key' / 'private.pem'), '--repo', str(directory / 'repo'),
                                  '--id', 'app', '--version', '1.0.0', '--name', 'App', '--entry', 'app.wasm', '--storage',
                                  '--network', f"{directory / 'app.wasm'}=app.wasm")
            self.assertEqual(built.returncode, 0, built.stderr)
            lines = (directory / 'repo' / 'app' / '1.0.0' / 'manifest').read_text(encoding='utf-8').split('\n')
            self.assertEqual(lines[5:8], ['entry=app.wasm', 'capability=net', 'capability=storage'])

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
if sys.argv[1:] == ['--version']:
    print(VERSION)
    sys.exit(0)
Path(LOG).write_text(json.dumps(sys.argv[1:]), encoding='utf-8')
output = Path(sys.argv[sys.argv.index('-o') + 1])
if FAIL:
    output.write_bytes(b'teil')
    sys.exit(1)
output.write_bytes(b'AOT:' + Path(sys.argv[-1]).read_bytes())
"""


class BuildPackageAotTests(unittest.TestCase):
    """aot uebersetzt nur mit dem zugelassenen wamrc und festen Schaltern; package --aot-file nimmt das Ergebnis auf."""

    ABI_FILE = 'app.wamr245-aot5-x64-bc.aot'

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name).resolve()
        self.tool = load_tool()
        self.assertEqual(self.call('keygen', str(self.directory / 'key'))[0], 0)
        for name, data in (('app.wasm', b'wasm'), ('App.txt', b'gross'), ('b.dat', b'b')):
            (self.directory / name).write_bytes(data)

    def fake_wamrc(self, folder='fake', version='wamrc 2.4.5', fail=False):
        """Ersatz-wamrc, das seine Schalter in folder/calls.json protokolliert und eine Ausgabedatei schreibt."""
        tool = self.directory / folder
        tool.mkdir()
        code = f'VERSION = {version!r}\nLOG = {str(tool / "calls.json")!r}\nFAIL = {fail!r}\n' + FAKE_WAMRC
        (tool / 'fake_wamrc.py').write_text(code, encoding='utf-8')
        if os.name == 'nt':
            launcher = tool / 'wamrc.cmd'
            launcher.write_text(f'@"{sys.executable}" "{tool / "fake_wamrc.py"}" %*\r\n', encoding='ascii')
        else:
            launcher = tool / 'wamrc'
            launcher.write_text(f'#!{sys.executable}\n' + code, encoding='utf-8')
            launcher.chmod(0o755)
        return launcher

    def call(self, *arguments):
        stdout, stderr = io.StringIO(), io.StringIO()
        with mock.patch.object(sys, 'argv', ['build-package.py', *arguments]), redirect_stdout(stdout), redirect_stderr(stderr):
            code = self.tool.main()
        return code, stderr.getvalue()

    def allow(self, wamrc):
        digest = hashlib.sha256(wamrc.read_bytes()).hexdigest()
        return mock.patch.object(self.tool, 'WAMRC_SHA256', frozenset({digest}))

    def translate(self, wamrc, allowed=True, output=None):
        output = output or self.directory / 'out.aot'
        if not allowed:
            return self.call('aot', str(wamrc), str(self.directory / 'app.wasm'), '-o', str(output))
        with self.allow(wamrc):
            return self.call('aot', str(wamrc), str(self.directory / 'app.wasm'), '-o', str(output))

    def build(self, *extra, entry='app.wasm', files=None, wamrc=None):
        files = files or [f"{self.directory / 'app.wasm'}=app.wasm", f"{self.directory / 'App.txt'}=App.txt",
                          f"{self.directory / 'b.dat'}=b.dat"]
        arguments = ('package', '--key', str(self.directory / 'key' / 'private.pem'), '--repo', str(self.directory / 'repo'),
                     '--id', 'app', '--version', '1.0.0', '--name', 'App', '--entry', entry, *extra, *files)
        if wamrc is None:
            return self.call(*arguments)
        with self.allow(wamrc):
            return self.call(*arguments)

    def translated(self):
        """Uebersetzt app.wasm mit dem Ersatz-wamrc und liefert dessen Pfad fuer die Zulassung in build()."""
        wamrc = self.fake_wamrc()
        code, error = self.translate(wamrc)
        self.assertEqual(code, 0, error)
        return wamrc

    def receipt(self):
        return self.directory / 'out.aot.json'

    def package_dir(self):
        return self.directory / 'repo' / 'app' / '1.0.0'

    def test_aot_uses_exactly_the_fixed_flags(self):
        wamrc = self.fake_wamrc()
        code, error = self.translate(wamrc)
        self.assertEqual(code, 0, error)
        calls = json.loads((wamrc.parent / 'calls.json').read_text(encoding='utf-8'))
        flags = ['--target=x86_64', '--cpu=x86-64', '--bounds-checks=1', '--stack-bounds-checks=1', '--size-level=0', '--disable-simd']
        # Eingabe ist die Kopie im Temp-Verzeichnis, damit der Beleg genau die gelesenen Bytes nennt.
        self.assertEqual(calls[:-1], [*flags, '-o', str(self.directory / 'out.aot')])
        self.assertEqual(Path(calls[-1]).name, 'input.wasm')
        self.assertEqual((self.directory / 'out.aot').read_bytes(), b'AOT:wasm')
        receipt = json.loads(self.receipt().read_text(encoding='utf-8'))
        sha = lambda data: hashlib.sha256(data).hexdigest()
        self.assertEqual(receipt, {'aot_abi': 'wamr245-aot5-x64-bc', 'wamrc_version': 'wamrc 2.4.5', 'flags': flags,
                                   'wamrc_sha256': sha(wamrc.read_bytes()), 'wasm_sha256': sha(b'wasm'), 'aot_sha256': sha(b'AOT:wasm')})

    def test_aot_rejects_output_equal_to_input_and_removes_partial_output(self):
        wamrc = self.fake_wamrc()
        code, error = self.translate(wamrc, output=self.directory / 'app.wasm')
        self.assertEqual(code, 1)
        self.assertIn('dieselbe Datei', error)
        self.assertEqual((self.directory / 'app.wasm').read_bytes(), b'wasm')
        failing = self.fake_wamrc('failing', fail=True)
        code, _ = self.translate(failing)
        self.assertEqual(code, 1)
        self.assertTrue((failing.parent / 'calls.json').exists())
        self.assertFalse((self.directory / 'out.aot').exists())
        self.assertFalse(self.receipt().exists())

    def test_relative_wamrc_runs_the_checked_copy_not_one_from_path(self):
        checked = self.fake_wamrc('checked')
        foreign = self.fake_wamrc('foreign')
        previous = os.getcwd()
        os.chdir(checked.parent)
        self.addCleanup(os.chdir, previous)
        with mock.patch.dict(os.environ, {'PATH': str(foreign.parent) + os.pathsep + os.environ.get('PATH', '')}):
            code, error = self.translate(Path(checked.name))
        self.assertEqual(code, 0, error)
        self.assertTrue((checked.parent / 'calls.json').exists())
        self.assertFalse((foreign.parent / 'calls.json').exists())

    def test_wrong_version_aborts_without_compiling(self):
        wamrc = self.fake_wamrc(version='wamrc 2.4.4')
        code, error = self.translate(wamrc)
        self.assertEqual(code, 1)
        self.assertIn('2.4.5 erwartet', error)
        self.assertFalse((wamrc.parent / 'calls.json').exists())
        self.assertFalse((self.directory / 'out.aot').exists())

    def test_unknown_checksum_aborts_before_running_wamrc(self):
        wamrc = self.fake_wamrc()
        code, error = self.translate(wamrc, allowed=False)
        self.assertEqual(code, 1)
        self.assertIn('nicht das zugelassene wamrc', error)
        self.assertFalse((wamrc.parent / 'calls.json').exists())
        self.assertFalse((self.directory / 'out.aot').exists())

    def test_aot_file_name_manifest_order_and_hash(self):
        wamrc = self.translated()
        code, error = self.build('--aot-file', str(self.directory / 'out.aot'), wamrc=wamrc)
        self.assertEqual(code, 0, error)
        aot = (self.package_dir() / 'files' / self.ABI_FILE).read_bytes()
        self.assertEqual(aot, b'AOT:wasm')
        lines = (self.package_dir() / 'manifest').read_text(encoding='utf-8').split('\n')
        names = [line.split(' ')[2] for line in lines if line.startswith('file=')]
        # Ordinal wie string.CompareOrdinal in PackageManifest.Parse: Grossbuchstaben vor Kleinbuchstaben, "wam" vor "was".
        self.assertEqual(names, ['App.txt', self.ABI_FILE, 'app.wasm', 'b.dat'])
        self.assertIn(f'file={hashlib.sha256(aot).hexdigest()} {len(aot)} {self.ABI_FILE}', lines)
        self.assertEqual((self.package_dir() / 'manifest.sig').stat().st_size, 64)

    def test_aot_file_rejects_case_collision_too_many_files_and_long_entry(self):
        (self.directory / 'out.aot').write_bytes(b'AOT:wasm')
        aot = ('--aot-file', str(self.directory / 'out.aot'))
        app = f"{self.directory / 'app.wasm'}=app.wasm"
        collision = [app, f"{self.directory / 'b.dat'}=APP.wamr245-aot5-x64-bc.aot"]
        full = [app, *(f"{self.directory / 'b.dat'}=f{index:03}.dat" for index in range(127))]
        # 195 Zeichen sind als Einstieg gueltig, mit dem AOT-Suffix aber laenger als 200.
        long_entry = 'd/' + 'a' * 188 + '.wasm'
        cases = ((collision, 'app.wasm', 'bereits als Paketdatei gelistet'), (full, 'app.wasm', 'mehr als 128 Dateien'),
                 ([f"{self.directory / 'app.wasm'}={long_entry}"], long_entry, 'laenger als 200 Zeichen'))
        for files, entry, message in cases:
            code, error = self.build(*aot, entry=entry, files=files)
            self.assertEqual(code, 1, message)
            self.assertIn(message, error)
        self.assertFalse((self.directory / 'repo').exists())

    def test_aot_file_requires_matching_receipt(self):
        wamrc = self.translated()
        aot = self.directory / 'out.aot'
        receipt = self.receipt().read_text(encoding='utf-8')

        def tamper(key, value):
            data = json.loads(receipt)
            data[key] = value
            self.receipt().write_text(json.dumps(data), encoding='utf-8')

        cases = (
            (lambda: (self.directory / 'app.wasm').write_bytes(b'neu'), 'anderen .wasm'),
            (lambda: aot.write_bytes(b'AOT:anders'), 'passt nicht zu ihrem Beleg'),
            (lambda: self.receipt().unlink(), 'fehlt'),
            (lambda: self.receipt().write_text('kein json', encoding='utf-8'), 'Paketfehler'),
            (lambda: tamper('flags', ['--bounds-checks=0']), 'andere wamrc-Schalter'),
            (lambda: tamper('wamrc_sha256', '0' * 64), 'nicht zugelassenes wamrc'),
            (lambda: tamper('aot_abi', 'wamr246-aot6-x64-bc'), 'andere AOT-Kennung'),
        )
        for change, message in cases:
            (self.directory / 'app.wasm').write_bytes(b'wasm')
            aot.write_bytes(b'AOT:wasm')
            self.receipt().write_text(receipt, encoding='utf-8')
            change()
            code, error = self.build('--aot-file', str(aot), wamrc=wamrc)
            self.assertEqual(code, 1, message)
            self.assertIn(message, error)
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
