import importlib.util
from pathlib import Path
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('isolation', SCRIPTS / 'test-isolation-probe.py')
isolation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(isolation)


def aot_log(rounds=3):
    """AOT-Teil (#582): ein sauberer Lauf, dann je Runde #DE und #PF im AOT-Code und wieder ein sauberer Lauf."""
    lines = ['ISOLATION_PROBE aot kind=ok round=0 status=Ok result=42 isolated=0 vector=-1 arena_used=0 ',
             'ISOLATION_PROBE aot frei_vor=880000']
    thread = 20
    for number in range(1, rounds + 1):
        for kind, vector in (('de', 0), ('pf', 14)):
            thread += 3
            lines += ['WASM nativer Lauf nach CPU-Fehler: Aot Released EndArena',
                      f'WASM CPU-Ausnahme im App-Code (Vektor {vector}, RIP 0x4000{thread:02x}), Worker-Thread {thread} aufgegeben '
                      '(Thread und Stack frei, nativ frei, Ressourcen frei); neuer Worker gestartet.',
                      f'ISOLATION_PROBE aot kind={kind} round={number} status=CpuFault result=0 isolated=1 vector={vector} arena_used=0 '
                      'CPU-Fehler im App-Code; die App wurde beendet. Neustart empfohlen.']
        lines.append(f'ISOLATION_PROBE aot kind=ok round={number} status=Ok result=42 isolated=0 vector=-1 arena_used=0 ')
        lines.append(f'ISOLATION_PROBE aot round={number} frei={879600 - number}')
    return lines + ['ISOLATION_PROBE aot frei_nach=879500']


AOT_LOG = aot_log(isolation.AOT_ROUNDS)

PASS_LOG = '\n'.join([
    'ISOLATION_PROBE start',
    # Die Reihenfolge der Zeilen wertet evaluate nicht aus; die Arena- und frei-Zeilen stehen gesammelt.
    'ISOLATION_PROBE frei_vor=401000',
    'ISOLATION_PROBE arena=1 used=16777263 frei=384600',
    'ISOLATION_PROBE arena_after=de used=0',
    'ISOLATION_PROBE arena=1 used=16777263 frei=376400',
    'ISOLATION_PROBE arena_after=pf used=0',
    'ISOLATION_PROBE arena=1 used=16777263 frei=368000',
    'ISOLATION_PROBE arena_after=gp used=0',
    'ISOLATION_PROBE arena=1 used=16777263 frei=367000',
    'ISOLATION_PROBE arena_after=ud used=0',
    'CPU EXCEPTION: Divide by Zero (#DE)',
    'System halted.',
    'WASM CPU-Ausnahme im App-Code (Vektor 0, RIP 0x1), Worker-Thread 4 aufgegeben (Thread und Stack frei, Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=de handled=1 vector=0',
    'ISOLATION_PROBE memory kind=de released=1 free_before_kib=900100 free_kib=900000',
    'CPU EXCEPTION: Page Fault (#PF)',
    'WASM CPU-Ausnahme im App-Code (Vektor 14, RIP 0x2), Worker-Thread 7 aufgegeben (Thread und Stack frei, Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=pf handled=1 vector=14',
    'ISOLATION_PROBE memory kind=pf released=1 free_before_kib=900000 free_kib=899500',
    'CPU EXCEPTION: General Protection Fault (#GP)',
    'WASM CPU-Ausnahme im App-Code (Vektor 13, RIP 0x10), Worker-Thread 10 aufgegeben (Thread und Stack frei, Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=gp handled=1 vector=13',
    'ISOLATION_PROBE memory kind=gp released=1 free_before_kib=899500 free_kib=899000',
    'CPU EXCEPTION: Invalid Opcode (#UD)',
    'WASM CPU-Ausnahme im App-Code (Vektor 6, RIP 0x13), Worker-Thread 13 aufgegeben (Thread und Stack frei, Ressourcen frei); neuer Worker gestartet.',
    'ISOLATION_PROBE fault=ud handled=1 vector=6',
    'ISOLATION_PROBE memory kind=ud released=1 free_before_kib=899000 free_kib=898500',
    'ISOLATION_PROBE gc ok',
    'ISOLATION_PROBE none returned',
    'ISOLATION_PROBE frei_nach=400800',
    *AOT_LOG,
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
        log = PASS_LOG.replace('WASM CPU-Ausnahme im App-Code (Vektor 14', 'x')
        self.assertIn('Hauptschleife hat nicht alle Laeufe beendet', isolation.evaluate(log, negative=False))

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

    def test_gp_and_ud_worker_must_be_reaped(self):
        # Auch die neuen Arten (I1-N) muessen ihren Worker abraeumen und den Stack zurueckgeben (E6-G).
        for kind, thread in (('gp', 10), ('ud', 13)):
            log = PASS_LOG.replace(f'Worker-Thread {thread} aufgegeben (Thread und Stack frei', f'Worker-Thread {thread} aufgegeben (Thread und Stack belegt')
            self.assertEqual([f'{kind}: Worker nicht abgeraeumt'], isolation.evaluate(log, negative=False))
            log = PASS_LOG.replace(f'memory kind={kind} released=1', f'memory kind={kind} released=0')
            self.assertEqual([f'{kind}: kein Stack freigegeben'], isolation.evaluate(log, negative=False))

    def test_gp_reported_as_pf_is_wrong_vector(self):
        log = PASS_LOG.replace('fault=gp handled=1 vector=13', 'fault=gp handled=1 vector=14')
        self.assertIn('gp mit Vektor 14 statt 13 (IRQContext-Versatz?)', isolation.evaluate(log, negative=False))

    def test_wrong_vector_points_to_layout_drift(self):
        log = PASS_LOG.replace('fault=de handled=1 vector=0', 'fault=de handled=1 vector=113')
        self.assertIn('de mit Vektor 113 statt 0 (IRQContext-Versatz?)', isolation.evaluate(log, negative=False))

    def test_arena_must_run_in_all_fault_runs(self):
        log = PASS_LOG.replace('ISOLATION_PROBE arena=1 used=16777263 frei=384600', 'ISOLATION_PROBE arena=0 used=0 frei=401000')
        self.assertIn('Arena nicht in allen Fehlerlaeufen gestartet', isolation.evaluate(log, negative=False))

    def test_arena_counter_must_be_zero_after_gp_and_ud(self):
        for kind in ('gp', 'ud'):
            log = PASS_LOG.replace(f'arena_after={kind} used=0', f'arena_after={kind} used=16777263')
            self.assertEqual([f'Arena nach {kind} nicht freigegeben'], isolation.evaluate(log, negative=False))

    def test_arena_counter_must_be_zero_after_each_isolation(self):
        log = PASS_LOG.replace('arena_after=pf used=0', 'arena_after=pf used=16777263')
        self.assertEqual(['Arena nach pf nicht freigegeben'], isolation.evaluate(log, negative=False))

    def test_free_memory_may_drop_by_reserve_but_not_by_stack(self):
        # Genau 4 MiB Reserve sind erlaubt, 5000 KiB nicht (ein Worker-Stack waere 8192 KiB).
        log = PASS_LOG.replace('frei_nach=400800', 'frei_nach=396000')
        self.assertIn('Arena nach den CPU-Fehlern nicht freigegeben (frei_vor/frei_nach)', isolation.evaluate(log, negative=False))
        at_limit = PASS_LOG.replace('frei_nach=400800', 'frei_nach=396904')
        self.assertEqual(isolation.evaluate(at_limit, negative=False), [])
        missing = PASS_LOG.replace('ISOLATION_PROBE frei_nach=400800\n', '')
        self.assertIn('Arena nach den CPU-Fehlern nicht freigegeben (frei_vor/frei_nach)', isolation.evaluate(missing, negative=False))

    def test_held_block_must_lower_free_memory(self):
        log = PASS_LOG.replace('used=16777263 frei=367000', 'used=16777263 frei=400000')
        self.assertEqual(['Arenablock senkt frei nicht (Messung ohne Aussage)'], isolation.evaluate(log, negative=False))


class AotTests(unittest.TestCase):
    """#582: AOT-Laeufe nach #DE und #PF im AOT-Code."""

    def test_locked_aot_after_fault_fails(self):
        # Vor #582: nach der ersten Isolation endete jeder AOT-Lauf mit LoadFailed ("AOT nach CPU-Fehler ... gesperrt").
        log = PASS_LOG.replace('kind=ok round=1 status=Ok result=42', 'kind=ok round=1 status=LoadFailed result=0')
        self.assertEqual(['aot ok 1: LoadFailed/0 statt Ok/42 (AOT gesperrt?)'], isolation.evaluate(log, negative=False))

    def test_kept_instance_fails(self):
        log = PASS_LOG.replace('Aot Released EndArena', 'Aot Kept KeepArena', 1).replace('nativ frei', 'nativ belegt', 1)
        missing = isolation.evaluate(log, negative=False)
        self.assertIn('aot: Isolation ohne Freigabe von Stack oder nativem Lauf', missing)
        self.assertIn('aot: 19 statt 20 Laeufe ueber WAMR abgebaut', missing)

    def test_leaked_memory_fails(self):
        log = PASS_LOG.replace('aot frei_nach=879500', 'aot frei_nach=872000')
        self.assertEqual(['aot: freier Speicher 872000 KiB statt etwa 880000 KiB (Instanz nicht frei?)'],
                         isolation.evaluate(log, negative=False))
        at_limit = PASS_LOG.replace('aot frei_nach=879500', 'aot frei_nach=878976')
        self.assertEqual([], isolation.evaluate(at_limit, negative=False))

    def test_falling_trend_fails_even_within_total_limit(self):
        # Review #582, P3-2: 30 KiB je Runde bleiben unter der Gesamtgrenze, der Verlauf faellt aber um 270 KiB.
        log = PASS_LOG
        for number in range(1, isolation.AOT_ROUNDS + 1):
            log = log.replace(f'aot round={number} frei={879600 - number}', f'aot round={number} frei={879600 - 30 * number}')
        self.assertEqual(['aot: freier Speicher sinkt von Runde 1 bis 10 um 270 KiB (Rest je Isolation?)'],
                         isolation.evaluate(log, negative=False))

    def test_missing_round_memory_fails(self):
        log = PASS_LOG.replace('ISOLATION_PROBE aot round=4 frei=879596\n', '')
        self.assertEqual(['aot: freier Speicher je Runde fehlt (9 statt 10)'], isolation.evaluate(log, negative=False))

    def test_wrong_vector_and_missing_round_fail(self):
        log = PASS_LOG.replace('kind=pf round=2 status=CpuFault result=0 isolated=1 vector=14',
                               'kind=pf round=2 status=CpuFault result=0 isolated=1 vector=6')
        self.assertEqual(['aot pf 2 mit Vektor 6 statt 14'], isolation.evaluate(log, negative=False))
        log = PASS_LOG.replace('ISOLATION_PROBE aot kind=de round=3', 'x')
        self.assertIn('aot de 3 fehlt', isolation.evaluate(log, negative=False))

    def test_arena_must_be_empty_after_aot_isolation(self):
        log = PASS_LOG.replace('kind=de round=1 status=CpuFault result=0 isolated=1 vector=0 arena_used=0',
                               'kind=de round=1 status=CpuFault result=0 isolated=1 vector=0 arena_used=1310720')
        self.assertEqual(['aot de 1: Arena zaehlt noch 1310720 Bytes'], isolation.evaluate(log, negative=False))

    def test_missing_aot_section_fails(self):
        log = '\n'.join(line for line in PASS_LOG.splitlines() if line not in AOT_LOG)
        missing = isolation.evaluate(log, negative=False)
        self.assertIn('aot ok 0 fehlt', missing)
        self.assertIn('aot: Speicherwerte fehlen', missing)


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
