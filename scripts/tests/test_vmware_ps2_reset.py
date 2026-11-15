import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('ps2_reset', SCRIPTS / 'test-vmware-ps2-reset.py')
ps2_reset = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ps2_reset)


class WaitForOfferTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.serial = Path(directory.name) / 'serial.log'

    def test_returns_result_of_each_start_in_order(self):
        self.serial.write_text('[Drivers] ps2:kbd offer Ps2KeyboardDriver -> bound\n'
                               '[SCHED] Tick 4\n'
                               '[Drivers] ps2:kbd offer Ps2KeyboardDriver -> failed: enable scanning was not acknowledged\r\n')
        self.assertEqual(ps2_reset.wait_for_offer(self.serial, 0), 'bound')
        self.assertEqual(ps2_reset.wait_for_offer(self.serial, 1), 'failed: enable scanning was not acknowledged')

    def test_missing_start_ends_after_timeout(self):
        self.serial.write_text('[Drivers] ps2:kbd offer Ps2KeyboardDriver -> bound\n')
        with mock.patch.object(ps2_reset, 'BOOT_TIMEOUT', 0):
            self.assertIsNone(ps2_reset.wait_for_offer(self.serial, 1))


if __name__ == '__main__':
    unittest.main()
