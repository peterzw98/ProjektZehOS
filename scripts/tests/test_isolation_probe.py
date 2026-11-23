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
    'ISOLATION_PROBE fault=de handled=1 vector=0',
    'CPU EXCEPTION: Page Fault (#PF)',
    'WASM CPU-Ausnahme im App-Code, Worker-Thread 7 aufgegeben (Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=pf handled=1 vector=14',
    'CPU EXCEPTION: General Protection Fault (#GP)',
    'WASM CPU-Ausnahme im App-Code, Worker-Thread 10 aufgegeben (Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=gp handled=1 vector=13',
    'CPU EXCEPTION: Invalid Opcode (#UD)',
    'WASM CPU-Ausnahme im App-Code, Worker-Thread 13 aufgegeben (Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=ud handled=1 vector=6',
    'ISOLATION_PROBE gc ok',
    'ISOLATION_PROBE none returned',
    'ISOLATION_PROBE DONE',
])


class EvaluateTests(unittest.TestCase):
    def test_complete_run_passes_despite_cosmos_halt_text(self):
        self.assertEqual(isolation.evaluate(PASS_LOG, negative=False), [])

    def test_missing_isolation_is_reported(self):
        log = PASS_LOG.replace('ISOLATION_PROBE fault=pf handled=1 vector=14', '')
        self.assertIn('pf nicht isoliert', isolation.evaluate(log, negative=False))

    def test_missing_gp_and_ud_are_reported(self):
        for kind, vector in (('gp', 13), ('ud', 6)):
            log = PASS_LOG.replace(f'ISOLATION_PROBE fault={kind} handled=1 vector={vector}', '')
            self.assertIn(f'{kind} nicht isoliert', isolation.evaluate(log, negative=False))

    def test_three_handled_faults_are_not_enough(self):
        # Die Probe verlangt vier handled=1: ein Mitschnitt mit nur drei (de, pf, gp) besteht nicht.
        log = PASS_LOG.replace('ISOLATION_PROBE fault=ud handled=1 vector=6', '')
        self.assertEqual(PASS_LOG.count('handled=1'), 4)
        self.assertEqual(log.count('handled=1'), 3)
        self.assertIn('ud nicht isoliert', isolation.evaluate(log, negative=False))

    def test_main_loop_must_end_all_runs(self):
        log = PASS_LOG.replace('WASM CPU-Ausnahme im App-Code, Worker-Thread 7', 'x')
        self.assertIn('Hauptschleife hat nicht alle Laeufe beendet', isolation.evaluate(log, negative=False))

    def test_negative_needs_halt_after_marker(self):
        halted = PASS_LOG + '\nISOLATION_PROBE negative pf on probe thread\nCPU EXCEPTION: Page Fault (#PF)\nSystem halted.'
        self.assertEqual(isolation.evaluate(halted, negative=True), [])
        survived = PASS_LOG + '\nISOLATION_PROBE negative pf on probe thread\nISOLATION_PROBE negative NOT HALTED'
        self.assertIn('Gegenprobe hielt nicht an', isolation.evaluate(survived, negative=True))

    def test_gp_reported_as_pf_is_wrong_vector(self):
        log = PASS_LOG.replace('fault=gp handled=1 vector=13', 'fault=gp handled=1 vector=14')
        self.assertIn('gp mit Vektor 14 statt 13 (IRQContext-Versatz?)', isolation.evaluate(log, negative=False))

    def test_wrong_vector_points_to_layout_drift(self):
        log = PASS_LOG.replace('fault=de handled=1 vector=0', 'fault=de handled=1 vector=113')
        self.assertIn('de mit Vektor 113 statt 0 (IRQContext-Versatz?)', isolation.evaluate(log, negative=False))


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
