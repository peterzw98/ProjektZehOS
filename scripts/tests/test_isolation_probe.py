import importlib.util
from pathlib import Path
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('isolation', SCRIPTS / 'test-isolation-probe.py')
isolation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(isolation)

PASS_LOG = '\n'.join([
    'ISOLATION_PROBE start',
    # Die Reihenfolge der Zeilen wertet evaluate nicht aus; die Arena- und frei-Zeilen stehen gesammelt.
    'ISOLATION_PROBE frei_vor=401000',
    'ISOLATION_PROBE arena=1',
    'ISOLATION_PROBE arena=1',
    'CPU EXCEPTION: Divide by Zero (#DE)',
    'System halted.',
    'WASM CPU-Ausnahme im App-Code, Worker-Thread 4 aufgegeben (Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=de handled=1 vector=0',
    'CPU EXCEPTION: Page Fault (#PF)',
    'WASM CPU-Ausnahme im App-Code, Worker-Thread 7 aufgegeben (Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=pf handled=1 vector=14',
    'ISOLATION_PROBE gc ok',
    'ISOLATION_PROBE none returned',
    'ISOLATION_PROBE frei_nach=400200',
    'ISOLATION_PROBE DONE',
])


class EvaluateTests(unittest.TestCase):
    def test_complete_run_passes_despite_cosmos_halt_text(self):
        self.assertEqual(isolation.evaluate(PASS_LOG, negative=False), [])

    def test_missing_isolation_is_reported(self):
        log = PASS_LOG.replace('ISOLATION_PROBE fault=pf handled=1 vector=14', '')
        self.assertIn('pf nicht isoliert', isolation.evaluate(log, negative=False))

    def test_main_loop_must_end_both_runs(self):
        log = PASS_LOG.replace('WASM CPU-Ausnahme im App-Code, Worker-Thread 7', 'x')
        self.assertIn('Hauptschleife hat nicht beide Laeufe beendet', isolation.evaluate(log, negative=False))

    def test_negative_needs_halt_after_marker(self):
        halted = PASS_LOG + '\nISOLATION_PROBE negative pf on probe thread\nCPU EXCEPTION: Page Fault (#PF)\nSystem halted.'
        self.assertEqual(isolation.evaluate(halted, negative=True), [])
        survived = PASS_LOG + '\nISOLATION_PROBE negative pf on probe thread\nISOLATION_PROBE negative NOT HALTED'
        self.assertIn('Gegenprobe hielt nicht an', isolation.evaluate(survived, negative=True))

    def test_wrong_vector_points_to_layout_drift(self):
        log = PASS_LOG.replace('fault=de handled=1 vector=0', 'fault=de handled=1 vector=113')
        self.assertIn('de mit Vektor 113 statt 0 (IRQContext-Versatz?)', isolation.evaluate(log, negative=False))

    def test_arena_must_run_in_both_fault_runs(self):
        log = PASS_LOG.replace('ISOLATION_PROBE arena=1', 'ISOLATION_PROBE arena=0', 1)
        self.assertIn('Arena nicht in beiden Fehlerlaeufen gestartet', isolation.evaluate(log, negative=False))

    def test_arena_blocks_must_be_released(self):
        # 32 MiB weniger: Die 2 x 16 MiB der aufgegebenen Laeufe blieben belegt.
        log = PASS_LOG.replace('frei_nach=400200', 'frei_nach=368232')
        self.assertIn('Arena nach den CPU-Fehlern nicht freigegeben (frei_vor/frei_nach)', isolation.evaluate(log, negative=False))
        at_limit = PASS_LOG.replace('frei_nach=400200', 'frei_nach=392808')
        self.assertEqual(isolation.evaluate(at_limit, negative=False), [])
        missing = PASS_LOG.replace('ISOLATION_PROBE frei_nach=400200\n', '')
        self.assertIn('Arena nach den CPU-Fehlern nicht freigegeben (frei_vor/frei_nach)', isolation.evaluate(missing, negative=False))


NMI_HALT = PASS_LOG + '\n'.join([
    '',
    'ISOLATION_PROBE nmi window open',
    'CPU EXCEPTION: Non-Maskable Interrupt',
    'Interrupt Vector: 2',
    'ZehOS: Thread 9, Wasm-Worker 9, Gastcode 1',
    'System halted.',
])


class NmiTests(unittest.TestCase):
    def test_halt_on_worker_in_guest_code_passes(self):
        self.assertEqual(isolation.evaluate(NMI_HALT, negative=False, nmi=True), [])

    def test_isolated_nmi_fails(self):
        log = NMI_HALT + '\nWASM CPU-Ausnahme im App-Code (Vektor 2, RIP 0x1), Worker-Thread 9 aufgegeben'
        self.assertIn('NMI wurde isoliert statt anzuhalten', isolation.evaluate(log, negative=False, nmi=True))

    def test_nmi_on_other_thread_is_inconclusive(self):
        log = NMI_HALT.replace('Thread 9, Wasm-Worker 9', 'Thread 1, Wasm-Worker 9')
        self.assertIn('NMI traf nicht den Worker im Gastcode (Gegenprobe ohne Aussage, wiederholen)',
                      isolation.evaluate(log, negative=False, nmi=True))

    def test_missing_nmi_is_reported(self):
        log = PASS_LOG + '\nISOLATION_PROBE nmi window open\nISOLATION_PROBE nmi window closed'
        self.assertEqual(isolation.evaluate(log, negative=False, nmi=True), ['kein NMI (Vektor 2) im Log'])

    def test_modes_exclude_each_other(self):
        with self.assertRaises(SystemExit):
            isolation.parse_args(['--iso', 'x.iso', '--negative', '--nmi'])


if __name__ == '__main__':
    unittest.main()
