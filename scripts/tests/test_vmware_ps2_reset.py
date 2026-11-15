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


class GuestRebootTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.serial = Path(directory.name) / 'serial.log'
        self.guest = ps2_reset.GuestReboot(self.serial, 5974, 'tester', 'geheim123456')
        self.typed = []
        patcher = mock.patch.object(self.guest, 'type', side_effect=lambda *lines: self.typed.append(lines))
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_waits_for_markers_after_current_offer(self):
        # Eine Textshell-Marke vor der aktuellen Bindung darf den naechsten Start nicht vorziehen.
        old = 'Desktop: Textshell aktiv.\n'
        boot = ('[Drivers] ps2:kbd offer Ps2KeyboardDriver -> bound\n'
                'Desktop: Desktop ist bereit.\nSession: Benutzer angemeldet.\nDesktop: Textshell aktiv.\n')
        self.serial.write_text(old + boot)
        self.guest(len(old))
        self.assertEqual([lines[-1] for lines in self.typed], ['key: return', 'key: f4', 'key: return'])
        self.assertIn('text: reboot', self.typed[-1])

    def test_missing_login_aborts(self):
        self.serial.write_text('Desktop: Desktop ist bereit.\n')
        with mock.patch.object(ps2_reset, 'STEP_TIMEOUT', 0), self.assertRaisesRegex(RuntimeError, 'Anmeldung'):
            self.guest(0)

    def test_unbound_keyboard_is_reset_instead_of_typed(self):
        guest = mock.Mock()
        calls = []
        with mock.patch.object(ps2_reset, 'vmrun', side_effect=lambda *a: calls.append(a[0])), \
                mock.patch.object(ps2_reset, 'wait_for_offer', side_effect=['failed: x', 'bound']), \
                mock.patch.object(ps2_reset.subprocess, 'run'):
            results, complete = ps2_reset.run_loop(Path('x.vmx'), self.serial, 2, guest)
        self.assertTrue(complete)
        self.assertEqual(calls, ['start', 'reset'])
        guest.assert_not_called()


if __name__ == '__main__':
    unittest.main()
