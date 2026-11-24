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
    'WASM CPU-Ausnahme im App-Code (Vektor 0, RIP 0x1), Worker-Thread 4 aufgegeben (Thread und Stack frei, Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=de handled=1 vector=0',
    'ISOLATION_PROBE memory kind=de released=1 free_before_kib=900100 free_kib=900000',
    'CPU EXCEPTION: Page Fault (#PF)',
    'WASM CPU-Ausnahme im App-Code (Vektor 14, RIP 0x2), Worker-Thread 7 aufgegeben (Thread und Stack frei, Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=pf handled=1 vector=14',
    'ISOLATION_PROBE memory kind=pf released=1 free_before_kib=900000 free_kib=899500',
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

    def test_main_loop_must_end_both_runs(self):
        log = PASS_LOG.replace('WASM CPU-Ausnahme im App-Code (Vektor 14', 'x')
        self.assertIn('Hauptschleife hat nicht beide Laeufe beendet', isolation.evaluate(log, negative=False))

    def test_negative_needs_halt_after_marker(self):
        halted = PASS_LOG + '\nISOLATION_PROBE negative pf on probe thread\nCPU EXCEPTION: Page Fault (#PF)\nSystem halted.'
        self.assertEqual(isolation.evaluate(halted, negative=True), [])
        survived = PASS_LOG + '\nISOLATION_PROBE negative pf on probe thread\nISOLATION_PROBE negative NOT HALTED'
        self.assertIn('Gegenprobe hielt nicht an', isolation.evaluate(survived, negative=True))

    def test_missing_reap_is_reported(self):
        log = PASS_LOG.replace('ISOLATION_PROBE memory kind=pf released=1 free_before_kib=900000 free_kib=899500', '')
        self.assertIn('pf ohne Speicherbeleg', isolation.evaluate(log, negative=False))

    def test_reap_without_released_stack_fails(self):
        log = PASS_LOG.replace('kind=de released=1', 'kind=de released=0')
        self.assertIn('de: kein Stack freigegeben', isolation.evaluate(log, negative=False))

    def test_main_loop_not_reaped_fails_despite_released(self):
        # released zaehlt systemweit; massgeblich ist die Zeile der Hauptschleife fuer genau diesen Worker.
        log = PASS_LOG.replace('Worker-Thread 7 aufgegeben (Thread und Stack frei', 'Worker-Thread 7 aufgegeben (Thread und Stack belegt')
        self.assertIn('pf: Worker nicht abgeraeumt', isolation.evaluate(log, negative=False))

    def test_lost_worker_stack_fails(self):
        # Ohne Abraeumen fehlt nach der Isolation ein weiterer 8-MiB-Stack, auch schon beim ersten Lauf.
        log = PASS_LOG.replace('free_before_kib=900100 free_kib=900000', 'free_before_kib=900100 free_kib=891908')
        missing = isolation.evaluate(log, negative=False)
        self.assertTrue(any(m.startswith('de:') and 'Stack nicht frei' in m for m in missing), missing)

    def test_wrong_vector_points_to_layout_drift(self):
        log = PASS_LOG.replace('fault=de handled=1 vector=0', 'fault=de handled=1 vector=113')
        self.assertIn('de mit Vektor 113 statt 0 (IRQContext-Versatz?)', isolation.evaluate(log, negative=False))


NMI_HALT = PASS_LOG + '\n'.join([
    '',
    'ISOLATION_PROBE nmi window open cpu=0',
    'ISOLATION_PROBE nmi main parked',
    # Reihenfolge wie im Gast seit panic-stop-cpus: erst die Zeile des Hooks, dann der Bericht (#515).
    'ZehOS: Thread 9, Wasm-Worker 9, Gastcode 1',
    'CPU EXCEPTION: Non-Maskable Interrupt',
    'Interrupt Vector: 2',
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
        log = (PASS_LOG + '\nISOLATION_PROBE nmi window open\nISOLATION_PROBE nmi main parked'
               '\nISOLATION_PROBE nmi window closed')
        self.assertEqual(isolation.evaluate(log, negative=False, nmi=True), ['kein NMI (Vektor 2) im Log'])

    def test_main_loop_not_parked_is_reported(self):
        log = NMI_HALT.replace('ISOLATION_PROBE nmi main parked\n', '')
        self.assertEqual(isolation.evaluate(log, negative=False, nmi=True), ['Hauptschleife nicht geparkt'])

    def test_modes_exclude_each_other(self):
        with self.assertRaises(SystemExit):
            isolation.parse_args(['--iso', 'x.iso', '--negative', '--nmi'])


if __name__ == '__main__':
    unittest.main()
