import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'test-linux-run.py'
spec = importlib.util.spec_from_file_location('test_linux_run', SCRIPT)
run = importlib.util.module_from_spec(spec)
spec.loader.exec_module(run)


class DecodeSerialTests(unittest.TestCase):
    def test_roh_latin1_wird_zu_umlaut(self):
        # Beleg aus dem Seriallog: 'ausf\xfchrbar' (0xFC allein, kein gueltiges UTF-8)
        self.assertEqual(run.decode_serial(b'nicht ausf\xfchrbar'), 'nicht ausführbar')

    def test_utf8_wird_zu_umlaut(self):
        self.assertEqual(run.decode_serial('nicht ausführbar'.encode('utf-8')), 'nicht ausführbar')

    def test_ascii(self):
        self.assertEqual(run.decode_serial(b'abc'), 'abc')


if __name__ == '__main__':
    unittest.main()
