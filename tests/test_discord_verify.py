import base64
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest

VERIFY = Path(__file__).resolve().parents[1] / 'verify.sh'


@unittest.skipUnless(shutil.which('sh') and shutil.which('gpg'), 'verify.sh braucht sh und gpg')
class DiscordVerifyTests(unittest.TestCase):
    """verify.sh entscheidet über Freigaben; jede Zeile ohne gültige Bridge-Signatur muss abgelehnt werden.

    Der Gutfall bräuchte den privaten Bridge-Schlüssel oder einen überschreibbaren Fingerabdruck; beides bleibt bewusst
    außen vor, damit das Prüfskript keinen Testschalter bekommt.
    """

    def verify(self, line):
        with tempfile.TemporaryDirectory() as home:
            # Eigener Schlüsselring, damit der Test weder vom Rechner abhängt noch ihn verändert.
            environment = dict(os.environ, GNUPGHOME=home)
            return subprocess.run(['sh', str(VERIFY), '#general'], input=line + '\n', capture_output=True, text=True,
                                  env=environment, timeout=30)

    def assert_rejected(self, line, verdict):
        result = self.verify(line)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual(verdict, result.stdout.strip())

    def test_line_without_signature_is_unsigned(self):
        self.assert_rejected('<Lars_Eric> agent-x: merge 1', 'unsigned')

    def test_old_timestamp_is_stale(self):
        stamp = int(time.time()) - 3600
        self.assert_rejected(f'<Lars_Eric> agent-x: merge 1 [id=137636974927413248 t={stamp} sig=AAAA]', 'stale')

    def test_future_timestamp_is_stale(self):
        stamp = int(time.time()) + 3600
        self.assert_rejected(f'<Lars_Eric> agent-x: merge 1 [id=137636974927413248 t={stamp} sig=AAAA]', 'stale')

    def test_random_signature_is_invalid(self):
        signature = base64.b64encode(os.urandom(96)).decode()
        stamp = int(time.time())
        self.assert_rejected(f'<Lars_Eric> agent-x: merge 1 [id=137636974927413248 t={stamp} sig={signature}]', 'INVALID')


if __name__ == '__main__':
    unittest.main()
