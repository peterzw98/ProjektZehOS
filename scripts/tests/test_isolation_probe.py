import importlib.util
from pathlib import Path
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('isolation', SCRIPTS / 'test-isolation-probe.py')
isolation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(isolation)

PASS_LOG = '\n'.join([
    'ISOLATION_PROBE start',
    'CPU EXCEPTION: Divide by Zero (#DE)',
    'System halted.',
    'WASM CPU-Ausnahme im App-Code, Worker-Thread 4 aufgegeben (Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=de handled=1',
    'CPU EXCEPTION: Page Fault (#PF)',
    'WASM CPU-Ausnahme im App-Code, Worker-Thread 7 aufgegeben (Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=pf handled=1',
    'ISOLATION_PROBE gc ok',
    'ISOLATION_PROBE none returned',
    'ISOLATION_PROBE DONE',
])


class EvaluateTests(unittest.TestCase):
    def test_complete_run_passes_despite_cosmos_halt_text(self):
        self.assertEqual(isolation.evaluate(PASS_LOG, negative=False), [])

    def test_missing_isolation_is_reported(self):
        log = PASS_LOG.replace('ISOLATION_PROBE fault=pf handled=1', '')
        self.assertIn('pf nicht isoliert', isolation.evaluate(log, negative=False))

    def test_main_loop_must_end_both_runs(self):
        log = PASS_LOG.replace('WASM CPU-Ausnahme im App-Code, Worker-Thread 7', 'x')
        self.assertIn('Hauptschleife hat nicht beide Laeufe beendet', isolation.evaluate(log, negative=False))

    def test_negative_needs_halt_after_marker(self):
        halted = PASS_LOG + '\nISOLATION_PROBE negative pf on probe thread\nCPU EXCEPTION: Page Fault (#PF)\nSystem halted.'
        self.assertEqual(isolation.evaluate(halted, negative=True), [])
        survived = PASS_LOG + '\nISOLATION_PROBE negative pf on probe thread\nISOLATION_PROBE negative NOT HALTED'
        self.assertIn('Gegenprobe hielt nicht an', isolation.evaluate(survived, negative=True))


if __name__ == '__main__':
    unittest.main()
