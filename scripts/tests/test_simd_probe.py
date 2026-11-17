import importlib.util
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('simd_probe', SCRIPTS / 'test-simd-probe.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class SimdProbeLogTests(unittest.TestCase):
    def test_pass_needs_the_final_line(self):
        self.assertEqual(probe.classify('SIMD_PROBE isa Sse2=True\nSIMD_PROBE int-dot scalar PASS\n'), 'RUNNING')
        self.assertEqual(probe.classify('SIMD_PROBE isa Sse2=True\nSIMD_PROBE PASS\n'), 'PASS')

    def test_any_failed_stage_is_fail(self):
        self.assertEqual(probe.classify('SIMD_PROBE int-dot avx2 FAIL erwartet=1 erhalten=2\n'), 'FAIL')
        self.assertEqual(probe.classify('SIMD_PROBE FAIL InvalidOperationException: x\n'), 'FAIL')
        self.assertEqual(probe.classify('SIMD_PROBE INFO int-dot avx2 speedup_permille=900 NOT_FASTER\nSIMD_PROBE PASS\n'), 'PASS')

    def test_last_stage_names_the_crash_location(self):
        log = 'SIMD_PROBE begin int-dot vector128\nSIMD_PROBE int-dot vector128 PASS\nSIMD_PROBE begin int-dot avx2\n'
        self.assertEqual(probe.last_stage(log), 'int-dot avx2')
        self.assertEqual(probe.last_stage(''), '(keine)')


if __name__ == '__main__':
    unittest.main()
