import importlib.util
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('security_probe', SCRIPTS / 'test-security-probe.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class SecurityProbeLogTests(unittest.TestCase):
    def test_stage_lines_are_not_the_final_pass(self):
        self.assertEqual(probe.classify('SECURITY_PROBE RNG completed\n'
                                        'SECURITY_PROBE cleanup PASS success and exception\n'
                                        'SECURITY_PROBE vector completed; production iterations starting\n'), 'RUNNING')

    def test_final_line_passes_also_after_interleaved_output(self):
        line = 'SECURITY_PROBE PASS PBKDF2-SHA256 vector, RNG and 600000 iterations\n'
        self.assertEqual(probe.classify(line), 'PASS')
        self.assertEqual(probe.classify('[SCHED] Thread ' + line), 'PASS')

    def test_fail_line_wins(self):
        self.assertEqual(probe.classify('SECURITY_PROBE FAIL System.Exception: x\n'), 'FAIL')


if __name__ == '__main__':
    unittest.main()
