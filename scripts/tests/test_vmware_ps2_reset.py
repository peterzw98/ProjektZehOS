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

    def test_ignores_partially_written_line(self):
        self.serial.write_text('[Drivers] ps2:kbd offer Ps2KeyboardDriver -> boun')
        with mock.patch.object(ps2_reset, 'BOOT_TIMEOUT', 0):
            self.assertIsNone(ps2_reset.wait_for_offer(self.serial, 0))

    def test_missing_start_ends_after_timeout(self):
        self.serial.write_text('[Drivers] ps2:kbd offer Ps2KeyboardDriver -> bound\n')
        with mock.patch.object(ps2_reset, 'BOOT_TIMEOUT', 0):
            self.assertIsNone(ps2_reset.wait_for_offer(self.serial, 1))


class RunLoopTests(unittest.TestCase):
    def run_loop(self, offers, vmrun_error=None):
        calls = []

        def vmrun(*arguments):
            calls.append(arguments[0])
            if vmrun_error and arguments[0] == vmrun_error:
                raise RuntimeError('vmrun reset hard: Error')

        with mock.patch.object(ps2_reset, 'vmrun', side_effect=vmrun), \
                mock.patch.object(ps2_reset, 'wait_for_offer', side_effect=offers), \
                mock.patch.object(ps2_reset.subprocess, 'run') as stop:
            result = ps2_reset.run_loop(Path('x.vmx'), Path('serial.log'), 3)
        self.assertIn('stop', stop.call_args.args[0])
        return result, calls

    def test_counts_all_starts(self):
        (results, complete), calls = self.run_loop(['bound', 'failed: x', 'bound'])
        self.assertTrue(complete)
        self.assertEqual(results, {'bound': 2, 'failed: x': 1})
        self.assertEqual(calls, ['start', 'reset', 'reset'])

    def test_abort_is_incomplete_and_stops_vm(self):
        (results, complete), _ = self.run_loop(['bound', 'bound'], vmrun_error='reset')
        self.assertFalse(complete)
        self.assertEqual(results, {'bound': 1})


if __name__ == '__main__':
    unittest.main()
