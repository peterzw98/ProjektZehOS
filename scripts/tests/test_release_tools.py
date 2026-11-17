import base64
import importlib.util
import os
from http.client import HTTPConnection
from http.server import ThreadingHTTPServer
from pathlib import Path
import subprocess
import shutil
import sys
import tempfile
import threading
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('update_mirror', SCRIPTS / 'update-mirror.py')
mirror = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mirror)


class ReleaseToolsTests(unittest.TestCase):
    def test_signature_roundtrip_and_tampering(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            key = directory / 'private.pem'
            public = directory / 'public.der'
            manifest = directory / 'update-manifest.txt'
            manifest.write_bytes(b'zehos-update 1\n')
            subprocess.run(['openssl', 'genpkey', '-algorithm', 'Ed25519', '-out', str(key)], check=True, capture_output=True)
            subprocess.run(['openssl', 'pkey', '-in', str(key), '-pubout', '-outform', 'DER', '-out', str(public)], check=True, capture_output=True)
            pub = base64.b64encode(public.read_bytes()[-32:]).decode()
            script = str(SCRIPTS / 'sign-update.py')
            subprocess.run([sys.executable, script, str(manifest), str(key)], check=True, capture_output=True)
            signature = directory / 'update-manifest.sig'
            self.assertEqual(signature.stat().st_size, 64)
            verify = [sys.executable, script, 'verify', str(manifest), str(signature), pub]
            self.assertEqual(subprocess.run(verify, capture_output=True).returncode, 0)
            manifest.write_bytes(b'changed')
            self.assertNotEqual(subprocess.run(verify, capture_output=True).returncode, 0)

    def test_local_http_allowlist_and_traversal(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            (directory / 'update-manifest.txt').write_text('zehos-update 1\nkernel=ProjektZeh-1.2.3.elf\n')
            (directory / 'update-manifest.sig').write_bytes(b'sig')
            (directory / 'ProjektZeh-1.2.3.elf').write_bytes(b'kernel')
            (directory / 'secret').write_bytes(b'secret')
            with ThreadingHTTPServer(('127.0.0.1', 0), mirror.handler_for(mirror.local_files(directory))) as server:
                thread = threading.Thread(target=server.serve_forever)
                thread.start()
                try:
                    for path, expected in [('/ProjektZeh-1.2.3.elf', 200), ('/secret', 404), ('/../secret', 404), ('/%2e%2e/secret', 404), ('/update-manifest.txt?x=1', 404)]:
                        with self.subTest(path=path):
                            connection = HTTPConnection('127.0.0.1', server.server_port)
                            connection.request('GET', path)
                            response = connection.getresponse()
                            self.assertEqual(response.status, expected)
                            response.read()
                            connection.close()
                finally:
                    server.shutdown()
                    thread.join()

    def test_manifest_rejects_traversal(self):
        with self.assertRaises(ValueError):
            mirror.kernel_name(b'kernel=../secret\n')

    def test_manifest_rejects_symlink(self):
        with tempfile.TemporaryDirectory() as temp, tempfile.TemporaryDirectory() as other:
            directory = Path(temp)
            (directory / 'update-manifest.txt').write_text('kernel=ProjektZeh-1.2.3.elf\n')
            (directory / 'update-manifest.sig').write_bytes(b'sig')
            outside = Path(other) / 'file'
            outside.write_bytes(b'secret')
            try:
                (directory / 'ProjektZeh-1.2.3.elf').symlink_to(outside)
            except OSError as error:
                if getattr(error, 'winerror', None) == 1314:
                    self.skipTest('Windows erlaubt diesem Benutzer keine symbolischen Links')
                raise
            with self.assertRaises(ValueError):
                mirror.local_files(directory)

    def test_invalid_version_fails(self):
        bash = shutil.which('bash')
        if bash is None:
            self.skipTest('Bash wird fuer build-release.sh benoetigt')
        for version in ('v1.2.3', '1.2', '1.02.3', '1.2.3/other'):
            with self.subTest(version=version):
                result = subprocess.run([bash, str(SCRIPTS / 'build-release.sh'), version], capture_output=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(b'Aufruf: build-release.sh', result.stderr)

    def test_release_drops_inherited_zehos_properties_for_both_builds(self):
        bash = shutil.which('bash')
        if bash is None and Path('C:/Program Files/Git/bin/bash.exe').exists():
            bash = 'C:/Program Files/Git/bin/bash.exe'
        if bash is None:
            self.skipTest('Bash wird fuer build-release.sh benoetigt')
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'scripts').mkdir()
            (root / 'ProjektZeh').mkdir()
            script = root / 'scripts/build-release.sh'
            shutil.copyfile(SCRIPTS / 'build-release.sh', script)
            (root / 'ProjektZeh/ProjektZeh.csproj').write_text('<ZehOSInstaller>true</ZehOSInstaller>')
            boot = root / 'packages/cosmos.build.common/3.0.97/tools/Limine/BOOTX64.EFI'
            boot.parent.mkdir(parents=True)
            boot.write_bytes(b'boot')
            # Nur dotnet ersetzen: der echte Releaseablauf muss beide Builds sicher aufrufen.
            harness = r'''
dotnet() {
    if [[ "$1" == build ]]; then
        if env | grep -qi -E '^(zehos|defineconstants=)'; then
            echo 'Release wuerde geerbte ZehOS-Properties verwenden' >&2
            return 77
        fi
        printf '%s\n' "$*" >> release-builds.log
        mkdir -p ProjektZeh/bin/Release/net10.0/linux-x64/cosmos
        printf iso > ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso
        printf kernel > ProjektZeh/bin/Release/net10.0/linux-x64/ProjektZeh.elf
    else
        printf 'global-packages: %s/packages\n' "$PWD"
    fi
}
export -f dotnet
"$BASH" "$1" 1.2.3 "$2"
'''
            environment = dict(os.environ, ZehOSUpdateTestKeyPath='/inherited/test.pub',
                               ZEHOSUPDATETESTKEYPATH='/inherited/other-case.pub',
                               ZehOSPackageKeyPath='/inherited/package.pub', zehosrecovery='true',
                               DefineConstants='ZEHOS_RECOVERY')
            result = subprocess.run([bash, '-c', harness, 'release-test', script.as_posix(),
                                     (root / 'release').as_posix()], capture_output=True, text=True,
                                    env=environment, timeout=30)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(2, len((root / 'release-builds.log').read_text().splitlines()))
            self.assertTrue((root / 'release/update-manifest.txt').is_file())


if __name__ == '__main__':
    unittest.main()
