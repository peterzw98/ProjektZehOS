import importlib.util
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('network_script', SCRIPTS / 'test-network.py')
network = importlib.util.module_from_spec(spec)
spec.loader.exec_module(network)

MARKER = 'Textshell ist bereit.'


class RestartFinishedTests(unittest.TestCase):
    def test_probe_boot_waits_for_second_marker_and_second_pass(self):
        first = f'{MARKER}\nNETWORK_PROBE PASS\n'
        self.assertFalse(network.restart_finished(first, MARKER, 1, 1), 'noch kein Neustart')
        self.assertFalse(network.restart_finished(first + MARKER + '\n', MARKER, 1, 1), 'Probe laeuft noch: Shell reagiert nicht')
        self.assertTrue(network.restart_finished(first + MARKER + '\nNETWORK_PROBE PASS\n', MARKER, 1, 1))

    def test_boot_smoke_without_probe_needs_only_the_marker(self):
        self.assertFalse(network.restart_finished(f'{MARKER}\n', MARKER, 1, 0))
        self.assertTrue(network.restart_finished(f'{MARKER}\n{MARKER}\n', MARKER, 1, 0))


class AdminSetupTests(unittest.TestCase):
    def test_recovery_uses_security_recover_with_one_password(self):
        self.assertEqual(network.admin_setup_lines(True), ['security recover netadmin', 'testpassword12345'])

    def test_normal_build_uses_first_run_setup_with_confirmation(self):
        self.assertEqual(network.admin_setup_lines(False), ['netadmin', 'testpassword12345', 'testpassword12345'])


if __name__ == '__main__':
    unittest.main()
