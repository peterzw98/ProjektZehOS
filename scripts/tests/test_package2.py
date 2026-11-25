from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock
import argparse
import base64
import hashlib
import importlib.util
import io
import os
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts' / 'package2.py'
FIXTURE = ROOT / 'ProjektZeh.Tests' / 'TestData' / 'Package2'
_spec = importlib.util.spec_from_file_location('package2', SCRIPT)
package2 = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(package2)

# Eingaben der festen Testdaten: Spec und Quelldateien je Paket (Quellen sind die eingecheckten Inhaltsdateien selbst).
FIXTURE_PACKAGES = {
    'musl': ('1.2.5-r1', ['lib/ld-musl-x86_64.so.1', 'usr/share/licenses/musl/COPYRIGHT']),
    'libcxx': ('17.0.6-r2', ['usr/lib/libc++.so.1', 'usr/lib/libc++abi.so.1']),
    'hello': ('1.2.3-r4', ['usr/bin/hello', 'usr/lib/libhello.so.1', 'usr/share/hello/greeting.txt']),
}
ED25519_DER_PREFIX = bytes.fromhex('302a300506032b6570032100')

BASE_SPEC = '''id = "app"
version = "1.0.0"
release = 1
runtime = "linux-x64-v1"
license = "MIT"
'''


def openssl_verify(public_pem: Path, content: Path, signature: Path) -> bool:
    return subprocess.run(['openssl', 'pkeyutl', '-verify', '-pubin', '-inkey', str(public_pem), '-rawin', '-in', str(content),
                           '-sigfile', str(signature)], capture_output=True).returncode == 0


class Package2Tests(unittest.TestCase):
    """package2.py muss genau die kanonischen Bytes schreiben, die PackageManifest2.Parse und PackageCatalog2.Parse annehmen."""

    def setUp(self):
        self._temp = tempfile.TemporaryDirectory()
        self.dir = Path(self._temp.name)
        self.key = self.dir / 'key' / 'private.pem'
        self.key.parent.mkdir()
        subprocess.run(['openssl', 'genpkey', '-algorithm', 'ed25519', '-out', str(self.key)], check=True, capture_output=True)
        self.public = self.dir / 'key' / 'public.pem'
        subprocess.run(['openssl', 'pkey', '-in', str(self.key), '-pubout', '-out', str(self.public)], check=True, capture_output=True)
        self.repo = self.dir / 'repo'
        self.source = self.dir / 'in'
        self.source.mkdir()

    def tearDown(self):
        self._temp.cleanup()

    def run_tool(self, *arguments):
        return subprocess.run([sys.executable, '-W', 'error', str(SCRIPT), *map(str, arguments)], capture_output=True, text=True)

    def write_spec(self, text: str, name='spec.toml') -> Path:
        path = self.dir / name
        path.write_text(text, encoding='utf-8')
        return path

    def input_file(self, name: str, data: bytes = b'x') -> Path:
        path = self.source / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def build(self, spec_text: str, *files: str, expect=0):
        """files: Paketpfade; die Quelle ist jeweils eine kleine Datei mit dem Pfad als Inhalt."""
        arguments = [f'{self.input_file(path.replace("/", "_"), path.encode())}={path}' for path in files]
        result = self.run_tool('package', '--key', self.key, '--repo', self.repo, '--spec', self.write_spec(spec_text), *arguments)
        self.assertEqual(result.returncode, expect, result.stderr)
        return result

    def assert_nothing_written(self):
        """Kein Versionsordner und kein temporaerer Rest, nach jedem Fehler."""
        leftovers = [p for p in self.repo.rglob('*')] if self.repo.exists() else []
        self.assertEqual(leftovers, [], leftovers)

    def catalog(self, generation: int, expect=0):
        result = self.run_tool('catalog', '--key', self.key, '--repo', self.repo, '--generation', generation)
        self.assertEqual(result.returncode, expect, result.stderr)
        return result

    # --- Feste Testdaten fuer den C#-Test ------------------------------------------------------------------------------

    def build_fixture(self):
        for package_id, (version, paths) in FIXTURE_PACKAGES.items():
            files = [f'{FIXTURE / "repo" / package_id / version / "files" / path}={path}' for path in paths]
            result = self.run_tool('package', '--key', self.key, '--repo', self.repo, '--spec', FIXTURE / 'specs' / f'{package_id}.toml',
                                   *files)
            self.assertEqual(result.returncode, 0, result.stderr)
        self.catalog(3)

    def test_regenerates_fixture_bytewise(self):
        # Ed25519 ist deterministisch, aber der Testschluessel ist geloescht: Alles ausser den Signaturen muss gleich sein.
        self.build_fixture()
        expected = {p.relative_to(FIXTURE / 'repo') for p in (FIXTURE / 'repo').rglob('*') if p.is_file()}
        written = {p.relative_to(self.repo) for p in self.repo.rglob('*') if p.is_file()}
        self.assertEqual(written, expected)
        for relative in sorted(written):
            if relative.name.endswith('.sig'):
                self.assertTrue(openssl_verify(self.public, self.repo / relative.with_suffix(''), self.repo / relative), relative)
            else:
                self.assertEqual((self.repo / relative).read_bytes(), (FIXTURE / 'repo' / relative).read_bytes(), relative)

    def test_fixture_signatures_verify_with_test_key(self):
        der = ED25519_DER_PREFIX + base64.b64decode((FIXTURE / 'public.b64').read_text(encoding='ascii'))
        public = self.dir / 'fixture.pem'
        public.write_text('-----BEGIN PUBLIC KEY-----\n' + base64.b64encode(der).decode('ascii') + '\n-----END PUBLIC KEY-----\n',
                          encoding='ascii')
        signatures = sorted((FIXTURE / 'repo').rglob('*.sig'))
        self.assertEqual(len(signatures), 4)
        for signature in signatures:
            self.assertTrue(openssl_verify(public, signature.with_suffix(''), signature), signature)
        self.assertFalse(openssl_verify(public, FIXTURE / 'repo' / 'catalog2', FIXTURE / 'repo' / 'musl' / '1.2.5-r1' / 'manifest.sig'))
        self.assertFalse(list(FIXTURE.rglob('*.pem')), 'Kein privater Schluessel in den Testdaten')

    def test_fixture_manifests_are_canonical(self):
        for package_id, (version, _) in FIXTURE_PACKAGES.items():
            manifest = package2.parse_manifest((FIXTURE / 'repo' / package_id / version / 'manifest').read_bytes())
            self.assertEqual(manifest.full_version, version)

    # --- Manifest ------------------------------------------------------------------------------------------------------

    def test_writes_canonical_manifest_with_all_fields(self):
        spec = BASE_SPEC + '''name = "App"
entry = "usr/bin/app"
capability = ["files", "storage", "net"]
memory-mib = 64
min-kernel = "0.5.0"
depends = ["zlib>=1.3.1-r2", "musl>=1.2.5-r1"]
provides = ["so:libz.so", "so:liba.so"]
exec = ["usr/bin/app"]

[link]
"usr/lib/b.so" = "a.so"
"usr/bin/alias" = "app"
'''
        self.build(spec, 'usr/lib/a.so', 'usr/bin/app')
        data = (self.repo / 'app' / '1.0.0-r1' / 'manifest').read_bytes()
        lines = data.decode('ascii').split('\n')
        self.assertEqual(lines[:15], [
            'zehos-package 2', 'id=app', 'version=1.0.0', 'release=1', 'runtime=linux-x64-v1', 'license=MIT', 'name=App',
            'entry=usr/bin/app', 'capability=net', 'capability=storage', 'capability=files', 'memory-mib=64', 'min-kernel=0.5.0',
            'depends=musl>=1.2.5-r1', 'depends=zlib>=1.3.1-r2'])
        self.assertEqual(lines[15:17], ['provides=so:liba.so', 'provides=so:libz.so'])
        self.assertEqual(lines[17], f'exec={hashlib.sha256(b"usr/bin/app").hexdigest()} 11 usr/bin/app')
        self.assertTrue(lines[18].startswith('file=') and lines[18].endswith(' 12 usr/lib/a.so'))
        self.assertEqual(lines[19:], ['link=usr/bin/alias app', 'link=usr/lib/b.so a.so', 'end', ''])
        self.assertEqual((self.repo / 'app' / '1.0.0-r1' / 'files' / 'usr' / 'bin' / 'app').read_bytes(), b'usr/bin/app')
        self.assertTrue(openssl_verify(self.public, self.repo / 'app' / '1.0.0-r1' / 'manifest',
                                       self.repo / 'app' / '1.0.0-r1' / 'manifest.sig'))
        self.assertEqual(package2.parse_manifest(data).render(), data)

    def test_output_does_not_depend_on_argument_order(self):
        spec = BASE_SPEC + 'exec = ["usr/bin/b", "usr/bin/a"]\n'
        self.build(spec, 'usr/bin/a', 'usr/bin/b', 'etc/c')
        first = (self.repo / 'app' / '1.0.0-r1' / 'manifest').read_bytes()
        other = self.repo
        self.repo = self.dir / 'repo2'
        self.build(BASE_SPEC + 'exec = ["usr/bin/a", "usr/bin/b"]\n', 'etc/c', 'usr/bin/b', 'usr/bin/a')
        self.assertEqual((self.repo / 'app' / '1.0.0-r1' / 'manifest').read_bytes(), first)
        self.assertNotEqual(other, self.repo)

    def test_rejects_unsafe_target_paths(self):
        for target in ('/usr/bin/x', 'usr/../etc/x', 'usr/bin/../../x', 'usr/./x', 'bin/x', 'x', 'usr/bin/x.', 'usr//x',
                       'usr/bin/x\\y', 'usr/bin/a b', 'usr/bin/', 'usr/' + 'a' * 197, 'usr/bin/x\n', 'C:/usr/x', 'usr/bin/ä',
                       'usr/bin/x\x1b'):
            with self.subTest(target=target):
                result = self.run_tool('package', '--key', self.key, '--repo', self.repo, '--spec', self.write_spec(BASE_SPEC),
                                       f'{self.input_file("x")}={target}')
                self.assertEqual(result.returncode, 1)
                self.assertTrue(all(' ' <= c <= '~' for c in result.stderr.rstrip('\n')), result.stderr)
                self.assert_nothing_written()
        self.build(BASE_SPEC, 'usr/' + 'a' * 196)

    def test_rejects_symlinked_or_special_sources(self):
        real = self.input_file('real')
        link = self.source / 'link'
        link.symlink_to(real)
        for source in (link, self.source, self.source / 'missing'):
            with self.subTest(source=source.name):
                result = self.run_tool('package', '--key', self.key, '--repo', self.repo, '--spec', self.write_spec(BASE_SPEC),
                                       f'{source}=usr/bin/x')
                self.assertEqual(result.returncode, 1, result.stderr)
                self.assert_nothing_written()

    def test_rejects_symlinks_in_repository(self):
        outside = self.dir / 'outside'
        outside.mkdir()
        self.repo.mkdir()
        (self.repo / 'app').symlink_to(outside, target_is_directory=True)
        result = self.run_tool('package', '--key', self.key, '--repo', self.repo, '--spec', self.write_spec(BASE_SPEC),
                               f'{self.input_file("x")}=usr/bin/x')
        self.assertEqual(result.returncode, 1)
        self.assertIn('ymbolischer Link', result.stderr)
        self.assertEqual(list(outside.iterdir()), [])

    def test_never_overwrites_existing_version(self):
        self.build(BASE_SPEC, 'usr/bin/x')
        version = self.repo / 'app' / '1.0.0-r1'
        before = {p: p.read_bytes() for p in version.rglob('*') if p.is_file()}
        result = self.build(BASE_SPEC, 'usr/bin/y', expect=1)
        self.assertIn('unveraenderlich', result.stderr)
        self.assertEqual({p: p.read_bytes() for p in version.rglob('*') if p.is_file()}, before)
        self.assertEqual([p.name for p in (self.repo / 'app').iterdir()], ['1.0.0-r1'])
        self.build(BASE_SPEC.replace('release = 1', 'release = 2'), 'usr/bin/y')

    def test_failure_or_interrupt_leaves_no_version_directory(self):
        # Kaputter Schluessel: OpenSSL scheitert, bevor etwas geschrieben wird.
        broken = self.dir / 'broken.pem'
        broken.write_text('kein Schluessel\n', encoding='ascii')
        result = self.run_tool('package', '--key', broken, '--repo', self.repo, '--spec', self.write_spec(BASE_SPEC),
                               f'{self.input_file("x")}=usr/bin/x')
        self.assertEqual(result.returncode, 1)
        self.assertNotIn(str(broken), result.stderr)
        self.assert_nothing_written()
        # Abbruch mitten im Schreiben (Strg+C beim Signieren) und fehlende zweite Quelle nach der ersten Kopie.
        for failure in (KeyboardInterrupt(), package2.PackageError('Test')):
            with self.subTest(failure=type(failure).__name__):
                args = argparse.Namespace(key=self.key, repo=self.repo, spec=self.write_spec(BASE_SPEC),
                                 files=[f'{self.input_file("a")}=usr/bin/a', f'{self.input_file("b")}=usr/bin/b'])
                with mock.patch.object(package2.Signer, 'sign', side_effect=failure), \
                        redirect_stdout(io.StringIO()), self.assertRaises(type(failure)):
                    package2.package(args)
                self.assert_nothing_written()
        args = argparse.Namespace(key=self.key, repo=self.repo, spec=self.write_spec(BASE_SPEC),
                         files=[f'{self.input_file("a")}=usr/bin/a', f'{self.source / "gone"}=usr/bin/b'])
        with self.assertRaises(package2.PackageError):
            package2.package(args)
        self.assert_nothing_written()

    def test_rejects_private_key_content(self):
        # Auch eine Kopie (anderer Inode) und ein Schluessel mitten in einer Datei werden nie signiert.
        copy = self.input_file('keycopy', b'Vorspann\n' + self.key.read_bytes())
        result = self.run_tool('package', '--key', self.key, '--repo', self.repo, '--spec', self.write_spec(BASE_SPEC),
                               f'{copy}=etc/k')
        self.assertEqual(result.returncode, 1)
        self.assertIn('privaten Schluessel', result.stderr)
        self.assert_nothing_written()
        for header in (b'-----BEGIN ENCRYPTED PRIVATE KEY-----', b'-----BEGIN RSA PRIVATE KEY-----',
                       b'-----BEGIN EC PRIVATE KEY-----', b'-----BEGIN OPENSSH PRIVATE KEY-----',
                       b'-----BEGIN PGP PRIVATE KEY BLOCK-----'):
            with self.subTest(header=header):
                source = self.input_file('pem', b'x' * 5 + header + b'\n')
                # Kleine Bloecke: Der Kopf liegt ueber mehreren Blockgrenzen.
                with mock.patch.object(package2, 'CHUNK', 7), self.assertRaisesRegex(package2.PackageError, 'privaten Schluessel'):
                    package2.copy_hashed(source, self.dir / f'out-{header[11:14].decode()}-{len(header)}', 1000)
        public = self.input_file('pub', self.public.read_bytes())
        self.assertEqual(package2.copy_hashed(public, self.dir / 'pub-out', 1000)[0], len(self.public.read_bytes()))

    def test_rejects_fifo_without_blocking(self):
        fifo = self.source / 'fifo'
        os.mkfifo(fifo)
        result = subprocess.run([sys.executable, '-W', 'error', str(SCRIPT), 'package', '--key', str(self.key), '--repo',
                                 str(self.repo), '--spec', str(self.write_spec(BASE_SPEC)), f'{fifo}=usr/bin/x'],
                                capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 1)
        self.assertIn('keine regulaere Datei', result.stderr)
        result = subprocess.run([sys.executable, '-W', 'error', str(SCRIPT), 'package', '--key', str(self.key), '--repo',
                                 str(self.repo), '--spec', str(fifo), f'{self.input_file("x")}=usr/bin/x'],
                                capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 1)
        self.assert_nothing_written()

    def test_sigterm_while_signing_leaves_nothing(self):
        # Der Wrapper startet das Werkzeug als __main__ und schickt sich beim Signieren selbst SIGTERM.
        wrapper = self.dir / 'wrapper.py'
        wrapper.write_text(
            'import os, runpy, signal, subprocess, sys\n'
            'real = subprocess.run\n'
            'def run(command, *args, **kwargs):\n'
            '    if "-sign" in command:\n'
            '        os.kill(os.getpid(), signal.SIGTERM)\n'
            '    return real(command, *args, **kwargs)\n'
            'subprocess.run = run\n'
            'sys.argv = sys.argv[1:]\n'
            'runpy.run_path(sys.argv[0], run_name="__main__")\n', encoding='utf-8')
        result = subprocess.run([sys.executable, '-W', 'error', str(wrapper), str(SCRIPT), 'package', '--key', str(self.key),
                                 '--repo', str(self.repo), '--spec', str(self.write_spec(BASE_SPEC)), f'{self.input_file("x")}=usr/bin/x'],
                                capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 130, result.stderr)
        self.assertIn('abgebrochen', result.stderr)
        self.assert_nothing_written()

    def test_empty_target_created_meanwhile_is_not_replaced(self):
        final = self.repo / 'app' / '1.0.0-r1'
        original = package2.Signer.sign

        def sign_then_race(signer, content, signature):
            original(signer, content, signature)
            final.mkdir()

        args = argparse.Namespace(key=self.key, repo=self.repo, spec=self.write_spec(BASE_SPEC), files=[f'{self.input_file("x")}=usr/bin/x'])
        with mock.patch.object(package2.Signer, 'sign', autospec=True, side_effect=sign_then_race), \
                self.assertRaisesRegex(package2.PackageError, 'nichts ueberschrieben'):
            package2.package(args)
        self.assertEqual(list(final.iterdir()), [])
        self.assertEqual([p.name for p in (self.repo / 'app').iterdir()], ['1.0.0-r1'])

    def test_rename_no_replace_with_and_without_renameat2(self):
        for platform in (sys.platform, 'other'):
            with self.subTest(platform=platform), mock.patch.object(package2.sys, 'platform', platform):
                source, target = self.dir / f'src-{platform}', self.dir / f'dst-{platform}'
                source.mkdir()
                target.mkdir()
                with self.assertRaisesRegex(package2.PackageError, 'nichts ueberschrieben'):
                    package2.rename_no_replace(source, target)
                target.rmdir()
                package2.rename_no_replace(source, target)
                self.assertTrue(target.is_dir() and not source.exists())

    def test_rejects_conflicting_paths(self):
        cases = [
            ('', ['usr/bin/App', 'usr/bin/app'], 'Doppelter Pfad'),
            ('', ['usr/lib', 'usr/lib/x'], 'Ordner eines anderen'),
            ('', ['usr/LIB', 'usr/lib/x'], 'Ordner eines anderen'),
            ('[link]\n"usr/bin/X" = "y"\n', ['usr/bin/x', 'usr/bin/y'], 'Doppelter Pfad'),
            ('[link]\n"usr/bin" = "../etc/x"\n', ['usr/bin/x', 'etc/x'], 'Ordner eines anderen'),
        ]
        for extra, files, message in cases:
            with self.subTest(files=files):
                result = self.build(BASE_SPEC + extra, *files, expect=1)
                self.assertIn(message, result.stderr)
                self.assert_nothing_written()

    def test_link_rules(self):
        accepted = ['"usr/lib/a.so" = "../../lib/x"', '"etc/a" = "../usr/bin/x"', '"usr/bin/l" = "x"']
        for link in accepted:
            with self.subTest(link=link):
                self.repo = self.dir / f'ok-{accepted.index(link)}'
                self.build(BASE_SPEC + f'[link]\n{link}\n', 'usr/bin/x', 'lib/x')
        rejected = ['"usr/bin/l" = "../../../x"', '"usr/bin/l" = "/etc/x"', '"usr/bin/l" = "../x/../y"', '"usr/bin/l" = "./x"',
                    '"usr/bin/l" = "x/"', '"usr/bin/l" = ""', '"usr/bin/l" = "../../dev/zehos"', '"usr/bin/l" = "l"',
                    '"usr/bin/l" = "../bin"', '"usr/bin/l" = "l/x"', '"/usr/bin/l" = "x"', '"usr/bin/../l" = "x"',
                    '"usr/bin/l" = "x\\u001b"']
        self.repo = self.dir / 'repo'
        for link in rejected:
            with self.subTest(link=link):
                self.build(BASE_SPEC + f'[link]\n{link}\n', 'usr/bin/x', expect=1)
                self.assert_nothing_written()

    def test_link_cycles_and_chain_limit(self):
        pair = '[link]\n"usr/bin/a" = "b"\n"usr/bin/b" = "a"\n'
        self.assertIn('sich selbst', self.build(BASE_SPEC + pair, 'usr/bin/x', expect=1).stderr)
        # Wie im Parser: Links werden nach Pfad geprueft, deshalb trifft ein Kreis zuerst seinen eigenen Start.
        cycle = '[link]\n"usr/bin/z" = "a"\n"usr/bin/a" = "b"\n"usr/bin/b" = "a"\n'
        self.assertIn('sich selbst', self.build(BASE_SPEC + cycle, 'usr/bin/x', expect=1).stderr)
        folder = '[link]\n"usr/a" = "b"\n"usr/b" = "a/x"\n'
        self.assertIn('eigenen Ordner', self.build(BASE_SPEC + folder, 'usr/bin/x', expect=1).stderr)

        def chain(count):
            # l0 -> l1 -> ... -> l(count-1) -> x: der Parser folgt count-1 weiteren Links.
            lines = [f'"usr/bin/l{i:02d}" = "l{i + 1:02d}"' for i in range(count - 1)] + [f'"usr/bin/l{count - 1:02d}" = "x"']
            return BASE_SPEC + '[link]\n' + '\n'.join(lines) + '\n'

        self.build(chain(41), 'usr/bin/x')
        self.repo = self.dir / 'repo2'
        self.assertIn('40 Links', self.build(chain(42), 'usr/bin/x', expect=1).stderr)
        self.assert_nothing_written()

    def test_spec_validation(self):
        entry = 'entry = "usr/bin/x"\nexec = ["usr/bin/x"]\n'
        cases = [
            ('id = "app"\n', 'Pflichtfeld'),
            (BASE_SPEC + 'unknown = 1\n', 'Unbekanntes Feld'),
            (BASE_SPEC + 'file = ["usr/bin/x"]\n', 'Unbekanntes Feld'),
            (BASE_SPEC.replace('release = 1', 'release = true'), 'ganze Zahl'),
            (BASE_SPEC.replace('release = 1', 'release = "1"'), 'ganze Zahl'),
            (BASE_SPEC.replace('release = 1', 'release = 0'), 'Paketrevision'),
            (BASE_SPEC.replace('release = 1', 'release = 10000'), 'Paketrevision'),
            (BASE_SPEC.replace('linux-x64-v1', 'zehos-wasm32-v1'), 'runtime'),
            (BASE_SPEC.replace('"1.0.0"', '"01.0.0"'), 'Paketversion'),
            (BASE_SPEC.replace('"1.0.0"', '"1.0"'), 'Paketversion'),
            (BASE_SPEC.replace('"app"', '"app\\n"'), 'Paketkennung'),
            (BASE_SPEC.replace('"app"', '"App"'), 'Paketkennung'),
            (BASE_SPEC.replace('"MIT"', '"MIT/X11"'), 'Lizenz'),
            (BASE_SPEC.replace('"MIT"', '""'), 'Lizenz'),
            (BASE_SPEC + 'name = "App"\n', 'nur zusammen mit entry'),
            (BASE_SPEC + 'memory-mib = 64\n', 'memory-mib'),
            (BASE_SPEC + entry + 'memory-mib = 4097\n', 'memory-mib'),
            (BASE_SPEC + entry + 'name = " App"\n', 'Anzeigename'),
            (BASE_SPEC + 'entry = "usr/bin/x"\n', 'exec-Datei'),
            (BASE_SPEC + 'exec = ["usr/bin/y"]\n', 'exec nennt'),
            (BASE_SPEC + 'exec = ["usr/bin/x", "usr/bin/x"]\n', 'Doppelter Eintrag'),
            (BASE_SPEC + 'capability = ["root"]\n', 'Faehigkeit'),
            (BASE_SPEC + 'capability = ["net", "net"]\n', 'Doppelter Eintrag'),
            (BASE_SPEC + 'min-kernel = "0.5"\n', 'Mindest-Kernel'),
            (BASE_SPEC + 'depends = ["app>=1.0.0-r1"]\n', 'sich selbst'),
            (BASE_SPEC + 'depends = ["musl>=1.0.0-r1", "musl>=1.1.0-r1"]\n', 'Doppelte Abhaengigkeit'),
            (BASE_SPEC + 'depends = ["musl>=1.0.0"]\n', 'Abhaengigkeit'),
            (BASE_SPEC + 'depends = ["musl>=1.0.0-r01"]\n', 'Abhaengigkeit'),
            (BASE_SPEC + 'depends = "musl>=1.0.0-r1"\n', 'Liste'),
            (BASE_SPEC + 'depends = [' + ', '.join(f'"d{i:02d}>=1.0.0-r1"' for i in range(65)) + ']\n', '64'),
            (BASE_SPEC + 'provides = ["libc.so"]\n', 'provides'),
            (BASE_SPEC + 'provides = ["so:lib c.so"]\n', 'provides'),
            (BASE_SPEC + 'provides = [' + ', '.join(f'"so:l{i:02d}.so"' for i in range(65)) + ']\n', '64'),
            (BASE_SPEC + 'link = ["usr/bin/l"]\n', 'Tabelle'),
            (BASE_SPEC + 'id = "x"\n', 'TOML'),
            ('id = [\n', 'TOML'),
        ]
        for spec, message in cases:
            with self.subTest(spec=spec[-60:]):
                result = self.build(spec, 'usr/bin/x', expect=1)
                self.assertIn(message, result.stderr)
                self.assert_nothing_written()
        self.build(BASE_SPEC + 'depends = [' + ', '.join(f'"d{i:02d}>=1.0.0-r1"' for i in range(64)) + ']\n', 'usr/bin/x')

    def test_error_messages_filter_input(self):
        evil = '\\u001b[31m' + 'x' * 200
        result = self.build(BASE_SPEC + f'capability = ["{evil}"]\n', 'usr/bin/x', expect=1)
        message = result.stderr.rstrip('\n')
        self.assertTrue(all(' ' <= c <= '~' for c in message), message)
        self.assertLess(len(message), 200, message)
        result = self.run_tool('package', '--key', self.key, '--repo', self.repo, '--spec', self.write_spec(BASE_SPEC),
                               f'{self.input_file("x")}=usr/bin/\x1b[2J')
        self.assertNotIn('\x1b', result.stderr)
        # Die Meldung von tomllib zitiert Eingabetext; weitergegeben wird nur die Zeile.
        result = self.build('id = "\x1b[31m\n', 'usr/bin/x', expect=1)
        self.assertNotIn('\x1b', result.stderr)

    def test_key_never_becomes_content_or_published(self):
        result = self.run_tool('package', '--key', self.key, '--repo', self.repo, '--spec', self.write_spec(BASE_SPEC),
                               f'{self.key}=etc/key')
        self.assertEqual(result.returncode, 1)
        self.assertIn('Schluessel', result.stderr)
        self.assertNotIn('PRIVATE', result.stderr + result.stdout)
        self.assert_nothing_written()
        inside = self.repo / 'private.pem'
        self.repo.mkdir()
        inside.write_bytes(self.key.read_bytes())
        result = self.run_tool('package', '--key', inside, '--repo', self.repo, '--spec', self.write_spec(BASE_SPEC),
                               f'{self.input_file("x")}=usr/bin/x')
        self.assertEqual(result.returncode, 1)
        self.assertIn('im Repository', result.stderr)
        self.assertEqual(self.run_tool('catalog', '--key', inside, '--repo', self.repo, '--generation', 1).returncode, 1)
        self.assertFalse((self.repo / 'catalog2').exists())

    def test_limits_match_parser(self):
        def files(count, size=0, path=lambda i: f'usr/f{i:05d}'):
            return [(path(i), size, '0' * 64, False) for i in range(count)]

        base = {'id': 'app', 'version': '1.0.0', 'release': 1, 'license': 'MIT'}
        package2.Manifest(base, files(4096))
        with self.assertRaisesRegex(package2.PackageError, '4096 Dateien'):
            package2.Manifest(base, files(4097))
        package2.Manifest(base, [('usr/a', package2.MAX_CONTENT, '0' * 64, False)])
        with self.assertRaisesRegex(package2.PackageError, '1 GiB'):
            package2.Manifest(base, [('usr/a', package2.MAX_CONTENT, '0' * 64, False), ('usr/b', 1, '0' * 64, False)])
        # 4096 Dateien mit 200 Zeichen langen Pfaden ergeben mehr als 1 MiB Manifest.
        with self.assertRaisesRegex(package2.PackageError, '1 MiB'):
            package2.Manifest(base, files(4096, path=lambda i: f'usr/{i:05d}' + 'p' * 191))
        links = {f'usr/l{i:05d}': f'f{i:05d}' for i in range(4097)}
        with self.assertRaisesRegex(package2.PackageError, '4096 Links'):
            package2.Manifest({**base, 'link': links}, files(1))
        # Die Kopie bricht ab, sobald das Budget ueberschritten ist, und meldet die Groesse der kopierten Bytes.
        source = self.input_file('big', b'x' * 10)
        with self.assertRaisesRegex(package2.PackageError, '1 GiB'):
            package2.copy_hashed(source, self.dir / 'copy1', 9)
        self.assertEqual(package2.copy_hashed(source, self.dir / 'copy2', 10), (10, hashlib.sha256(b'x' * 10).hexdigest()))

    def test_parse_manifest_requires_canonical_form(self):
        self.build(BASE_SPEC + 'capability = ["net"]\n[link]\n"usr/bin/l" = "x"\n', 'usr/bin/x', 'usr/bin/y')
        data = (self.repo / 'app' / '1.0.0-r1' / 'manifest').read_bytes()
        package2.parse_manifest(data)
        lines = data.split(b'\n')
        variants = [
            data.replace(b'\n', b'\r\n'),
            data + b'x\n',
            data[:-1],
            data.replace(b'capability=net\n', b'capability=net\ncapability=net\n'),
            b'\n'.join([lines[0], lines[2], lines[1], *lines[3:]]),
            data.replace(b'usr/bin/x', b'usr/bin/z'),
            data.replace(b'release=1', b'release=01'),
            data.replace(b'\nend\n', b'\nfoo=bar\nend\n'),
            data.replace(b'zehos-package 2', b'zehos-package 1'),
            b'\xef\xbb\xbf' + data,
        ]
        for variant in variants:
            with self.subTest(variant=variant[:40]), self.assertRaises(package2.PackageError):
                package2.parse_manifest(variant)

    # --- Katalog -------------------------------------------------------------------------------------------------------

    def test_catalog_takes_highest_version_and_release(self):
        for version, release in (('1.9.0', 7), ('1.10.0', 2), ('1.10.0', 10), ('1.10.0', 9)):
            spec = BASE_SPEC.replace('"1.0.0"', f'"{version}"').replace('release = 1', f'release = {release}')
            self.build(spec, 'usr/bin/x')
        (self.repo / 'app' / '.neu-0000').mkdir()       # Rest eines abgebrochenen Laufs
        (self.repo / 'notes').write_text('kein Paket\n', encoding='ascii')
        self.catalog(1)
        lines = (self.repo / 'catalog2').read_text(encoding='ascii').split('\n')
        manifest = (self.repo / 'app' / '1.10.0-r10' / 'manifest').read_bytes()
        self.assertEqual(lines, ['zehos-catalog 2', 'generation=1', f'package=app 1.10.0-r10 {hashlib.sha256(manifest).hexdigest()} 9',
                                 'end', ''])
        self.assertTrue(openssl_verify(self.public, self.repo / 'catalog2', self.repo / 'catalog2.sig'))

    def test_catalog_generation_must_increase(self):
        self.build(BASE_SPEC, 'usr/bin/x')
        self.catalog(2)
        before = (self.repo / 'catalog2').read_bytes(), (self.repo / 'catalog2.sig').read_bytes()
        for generation in (2, 1, 0, -1, 10 ** 10):
            with self.subTest(generation=generation):
                self.catalog(generation, expect=1)
                self.assertEqual(((self.repo / 'catalog2').read_bytes(), (self.repo / 'catalog2.sig').read_bytes()), before)
        self.catalog(3)
        self.assertEqual(sorted(p.name for p in self.repo.iterdir()), ['app', 'catalog2', 'catalog2.sig'])
        (self.repo / 'catalog2').write_bytes(b'zehos-catalog 2\ngeneration=x\nend\n')
        self.assertIn('unlesbar', self.catalog(9, expect=1).stderr)

    def test_catalog_rejects_tampered_versions(self):
        def fresh(name):
            self.repo = self.dir / name
            self.build(BASE_SPEC, 'usr/bin/x')
            return self.repo / 'app' / '1.0.0-r1'

        version = fresh('signature')
        other = self.dir / 'other.pem'
        subprocess.run(['openssl', 'genpkey', '-algorithm', 'ed25519', '-out', str(other)], check=True, capture_output=True)
        subprocess.run(['openssl', 'pkeyutl', '-sign', '-inkey', str(other), '-rawin', '-in', str(version / 'manifest'),
                        '-out', str(version / 'manifest.sig')], check=True)
        self.assertIn('Signatur ungueltig', self.catalog(1, expect=1).stderr)
        self.assertFalse((self.repo / 'catalog2').exists())

        version = fresh('content')
        (version / 'files' / 'usr' / 'bin' / 'x').write_bytes(b'anders!!!')
        self.assertIn('weicht', self.catalog(1, expect=1).stderr)

        version = fresh('extra')
        (version / 'files' / 'usr' / 'bin' / 'y').write_bytes(b'')
        self.assertIn('passen nicht', self.catalog(1, expect=1).stderr)

        version = fresh('link')
        (version / 'files' / 'usr' / 'bin' / 'x').unlink()
        (version / 'files' / 'usr' / 'bin' / 'x').symlink_to(self.key)
        self.assertIn('symbolischer Link', self.catalog(1, expect=1).stderr)

        version = fresh('renamed')
        os.rename(version, version.with_name('1.0.0-r2'))
        self.assertIn('andere Kennung oder Version', self.catalog(1, expect=1).stderr)
        self.assertEqual([p.name for p in self.repo.iterdir()], ['app'])

    def test_catalog_rejects_symlinked_package_or_version(self):
        self.build(BASE_SPEC, 'usr/bin/x')
        copy = self.dir / 'copy'
        os.rename(self.repo / 'app' / '1.0.0-r1', copy)
        (self.repo / 'app' / '9.0.0-r1').symlink_to(copy, target_is_directory=True)
        self.assertIn('ymbolischer Link', self.catalog(1, expect=1).stderr)
        (self.repo / 'app' / '9.0.0-r1').unlink()
        os.rename(self.repo / 'app', self.dir / 'app')
        (self.repo / 'app').symlink_to(self.dir / 'app', target_is_directory=True)
        self.assertIn('ymbolischer Link', self.catalog(1, expect=1).stderr)
        self.assertFalse((self.repo / 'catalog2').exists())

    def test_catalog_requires_satisfiable_dependencies(self):
        self.build(BASE_SPEC + 'depends = ["musl>=1.2.5-r2"]\n', 'usr/bin/x')
        self.assertIn('fehlt im Katalog oder ist zu alt', self.catalog(1, expect=1).stderr)
        musl = BASE_SPEC.replace('"app"', '"musl"').replace('"1.0.0"', '"1.2.5"')
        self.build(musl, 'lib/ld')
        self.assertIn('zu alt', self.catalog(1, expect=1).stderr)
        self.build(musl.replace('release = 1', 'release = 2'), 'lib/ld')
        self.catalog(1)
        text = (self.repo / 'catalog2').read_text(encoding='ascii')
        self.assertIn('package=musl 1.2.5-r2 ', text)
        self.assertIn('\ndepends=musl>=1.2.5-r2\npackage=musl', text)

    def test_main_reports_openssl_failure_without_command_line(self):
        self.build(BASE_SPEC, 'usr/bin/x')
        args = ['catalog', '--key', str(self.key), '--repo', str(self.repo), '--generation', '1']
        stderr = io.StringIO()
        with mock.patch.object(package2.build_package, 'sign', side_effect=subprocess.CalledProcessError(1, ['openssl', str(self.key)])), \
                redirect_stderr(stderr), redirect_stdout(io.StringIO()):
            self.assertEqual(package2.main(args), 1)
        self.assertIn('OpenSSL', stderr.getvalue())
        self.assertNotIn(str(self.key), stderr.getvalue())
        self.assertEqual(sorted(p.name for p in self.repo.iterdir()), ['app'])


if __name__ == '__main__':
    unittest.main()
