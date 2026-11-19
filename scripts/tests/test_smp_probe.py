import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("smp_probe", Path(__file__).resolve().parents[1] / "test-smp-probe.py")
smp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smp)

class SmpProbeTests(unittest.TestCase):
    def test_pass_requires_all_steps_and_terminal(self):
        log = "\n".join(f"SMP_PROBE {step} PASS" for step in smp.STEPS) + "\nSMP_PROBE PASS\n"
        self.assertEqual("PASS", smp.classify(log))
        self.assertEqual("RUNNING", smp.classify(log.replace("SMP_PROBE PASS\n", "")))

    def test_failure_takes_priority_over_pass(self):
        self.assertEqual("FAIL", smp.classify("SMP_PROBE locks FAIL lost\nSMP_PROBE PASS\n"))

    def test_duplicate_or_conflicting_step_fails(self):
        self.assertEqual("FAIL", smp.classify("SMP_PROBE gc PASS\nSMP_PROBE gc PASS\n"))

if __name__ == "__main__":
    unittest.main()
