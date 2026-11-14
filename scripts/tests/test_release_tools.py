import base64
import importlib.util
from http.client import HTTPConnection
from http.server import ThreadingHTTPServer
from pathlib import Path
import subprocess
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

    def test_manifest_rejects_traversal_and_symlink(self):
        with self.assertRaises(ValueError):
            mirror.kernel_name(b'kernel=../secret\n')
        with tempfile.TemporaryDirectory() as temp, tempfile.TemporaryDirectory() as other:
            directory = Path(temp)
            (directory / 'update-manifest.txt').write_text('kernel=ProjektZeh-1.2.3.elf\n')
            (directory / 'update-manifest.sig').write_bytes(b'sig')
            outside = Path(other) / 'file'
            outside.write_bytes(b'secret')
            (directory / 'ProjektZeh-1.2.3.elf').symlink_to(outside)
            with self.assertRaises(ValueError):
                mirror.local_files(directory)

    def test_invalid_version_fails(self):
        for version in ('v1.2.3', '1.2', '1.02.3', '1.2.3/other'):
            with self.subTest(version=version):
                result = subprocess.run([str(SCRIPTS / 'build-release.sh'), version], capture_output=True)
                self.assertNotEqual(result.returncode, 0)


if __name__ == '__main__':
    unittest.main()
