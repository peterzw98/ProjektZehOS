import importlib.util
from pathlib import Path
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('abort_probe', SCRIPTS / 'test-abort-probe.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)

PASS_LOG = '\n'.join([
    'ABORT_PROBE start',
    'ABORT_PROBE case=1 status=Ok ms=12 ',
    'WASM Abbruch: App reagierte 5 s nicht auf das Schliessen',
    'ABORT_PROBE case=2 status=Aborted ms=5031 App beendet: Sie reagierte nicht auf das Schließen.',
    'ABORT_PROBE idle=1',
    'ABORT_PROBE case=3 status=Ok result=7 ',
    'ABORT_PROBE PASS',
])


class EvaluateTests(unittest.TestCase):
    def test_complete_run_passes(self):
        self.assertEqual(probe.evaluate(PASS_LOG), [])

    def test_obedient_app_must_end_itself_quickly(self):
        log = PASS_LOG.replace('case=1 status=Ok ms=12', 'case=1 status=Aborted ms=5012')
        self.assertIn('Fall 1 endete nicht Ok in unter 1 s', probe.evaluate(log))
        log = PASS_LOG.replace('ms=12', 'ms=1000')
        self.assertIn('Fall 1 endete nicht Ok in unter 1 s', probe.evaluate(log))

    def test_deaf_app_must_end_inside_window(self):
        for elapsed in ('4999', '6000'):
            with self.subTest(elapsed=elapsed):
                log = PASS_LOG.replace('ms=5031', 'ms=' + elapsed)
                self.assertIn('Fall 2 endete nicht Aborted nach 5 bis 6 s', probe.evaluate(log))
        log = PASS_LOG.replace('case=2 status=Aborted', 'case=2 status=Timeout')
        self.assertIn('Fall 2 endete nicht Aborted nach 5 bis 6 s', probe.evaluate(log))

    def test_main_loop_line_exactly_once(self):
        log = PASS_LOG.replace('WASM Abbruch: App reagierte 5 s nicht auf das Schliessen\n', '')
        self.assertIn('Abbruchzeile der Hauptschleife nicht genau einmal', probe.evaluate(log))
        log = PASS_LOG + '\nWASM Abbruch: App reagierte 5 s nicht auf das Schliessen'
        self.assertIn('Abbruchzeile der Hauptschleife nicht genau einmal', probe.evaluate(log))

    def test_worker_must_be_free_and_reusable(self):
        self.assertIn('Worker nach dem Abbruch nicht frei', probe.evaluate(PASS_LOG.replace('idle=1', 'idle=0')))
        self.assertIn('dritter Lauf nicht Ok', probe.evaluate(PASS_LOG.replace('result=7', 'result=0')))

    def test_cpu_exception_fails(self):
        self.assertIn('CPU-Ausnahme im Lauf', probe.evaluate(PASS_LOG + '\nCPU EXCEPTION: Page Fault (#PF)'))


if __name__ == '__main__':
    unittest.main()
