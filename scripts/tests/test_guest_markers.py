import importlib.util
from pathlib import Path
import time
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('guest_test', SCRIPTS / 'test-wasm-doom-desktop.py')
guest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guest)

TEXT = 'PKG Katalog geladen'


class GuestMarkerTests(unittest.TestCase):
    def test_finds_contiguous_marker(self):
        content = '[SCHED] a\nPKG Katalog geladen (Generation 1).\n'
        self.assertEqual(guest.find_marker(content, TEXT, 0), content.index('PKG') + len(TEXT))

    def test_finds_marker_split_by_debug_lines(self):
        # Beobachtet unter WHPX: ein Threadwechsel schiebt den Rest einer Debugzeile und weitere Zeilen ein.
        content = ('[LowLevelMonitor] Acquire ENPKG KataD\n'
                   '[LowLevelMonitor] LowLevelMonitor_TimedWait: BEGIN, timeout=1ms\n'
                   'log geladen (Generation 1, 2 Pakete).\n')
        self.assertGreater(guest.find_marker(content, TEXT, 0), 0)

    def test_rejects_different_result_and_respects_start(self):
        self.assertEqual(guest.find_marker('PKG Katalog nicht geladen\n[SCHED] x\n', TEXT, 0), -1)
        self.assertEqual(guest.find_marker('PKG Kxatalog geladen', TEXT, 0), -1)
        content = 'PKG Katalog geladen\n'
        self.assertEqual(guest.find_marker(content, TEXT, 1), -1)

    def test_large_log_stays_fast(self):
        noise = '[LowLevelMonitor] Acquire BEGIN\nPKG doom 1.0.0 installiert.\n' * 50000
        started = time.monotonic()
        self.assertEqual(guest.find_marker(noise, 'PKG Katalog geladen', 0), -1)
        self.assertGreater(guest.find_marker(noise + TEXT, TEXT, 0), 0)
        self.assertLess(time.monotonic() - started, 5)


if __name__ == '__main__':
    unittest.main()
