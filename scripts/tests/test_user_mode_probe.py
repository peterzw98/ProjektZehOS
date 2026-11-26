import importlib.util
import os
import re
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('user_probe', SCRIPTS / 'test-user-mode-probe.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)

CASES = ['loop', 'spin', 'fpu', 'fault_de']
GOOD = '\n'.join([
    'ZehOS boot',
    *[f'USER_PROBE {name} ok=1 calls=1000000' for name in CASES],
    'noise USER_PROBE_OTHER ok=0',
    'USER_PROBE done failed=0',
])


class EvaluateTests(unittest.TestCase):
    def test_all_good(self):
        self.assertEqual(probe.evaluate(GOOD, CASES), [])

    def test_one_failure(self):
        log = GOOD.replace('USER_PROBE fpu ok=1', 'USER_PROBE fpu ok=0').replace('failed=0', 'failed=1')
        result = probe.evaluate(log, CASES)
        self.assertIn('Fall fpu ok=0', result)
        self.assertIn('Schlusszeile meldet failed=1', result)

    def test_done_line_must_not_hide_a_failure(self):
        log = GOOD.replace('USER_PROBE fpu ok=1', 'USER_PROBE fpu ok=0')
        self.assertIn('Schlusszeile meldet failed=0, obwohl ein Fall ok=0 hat', probe.evaluate(log, CASES))

    def test_missing_final_line(self):
        log = GOOD.replace('USER_PROBE done failed=0', '')
        self.assertEqual(probe.evaluate(log, CASES), ["Schlusszeile 'USER_PROBE done' fehlt"])

    def test_missing_case(self):
        log = GOOD.replace('USER_PROBE spin ok=1 calls=1000000', '')
        self.assertEqual(probe.evaluate(log, CASES), ['Fall spin fehlt'])

    def test_ok_zero_after_ok_one_still_fails(self):
        self.assertIn('Fall loop ok=0', probe.evaluate(GOOD + '\nUSER_PROBE loop ok=0', CASES))

    def test_prefix_of_case_name_is_not_a_match(self):
        log = GOOD.replace('USER_PROBE fault_de ok=1', 'USER_PROBE fault_de_x ok=1')
        self.assertIn('Fall fault_de fehlt', probe.evaluate(log, CASES))

    def test_other_lines_are_ignored(self):
        log = 'CPU EXCEPTION ok=0\nISOLATION_PROBE x ok=0\n' + GOOD
        self.assertEqual(probe.evaluate(log, CASES), [])

    def test_empty_log(self):
        result = probe.evaluate('', [])
        self.assertIn('kein Fall im Log', result)
        self.assertIn("Schlusszeile 'USER_PROBE done' fehlt", result)


class NegativeTests(unittest.TestCase):
    START = 'USER_PROBE gegenprobe pf in kernel thread ring3=1\n'
    # Ausschnitt aus dem Seriallog eines echten Laufs (KVM, 1 CPU, Pin 4027f1c9).
    PF = ('\n========================================\nCPU EXCEPTION: Page Fault (#PF)\n'
          '========================================\nInterrupt Vector: 14\nCPU Flags (ESR): 0x10246\n\n'
          'Registers:\n  RIP: 0xFFFFFFFF8030AC02  RSP: 0xFFFF80001E13AE40\n  RFL: 0x10246\n  CR2: 0x0\n\nSystem halted.\n')

    def test_halt_with_page_fault_passes(self):
        self.assertEqual(probe.evaluate_negative(self.START + self.PF), [])

    def test_missing_start_or_ring3_fails(self):
        self.assertTrue(probe.evaluate_negative(self.PF))
        self.assertTrue(probe.evaluate_negative(self.START.replace('ring3=1', 'ring3=0') + self.PF))

    def test_exception_before_the_start_does_not_count(self):
        self.assertTrue(probe.evaluate_negative(self.PF + self.START))

    def test_running_on_fails(self):
        self.assertIn('Kernelthread lief nach dem #PF weiter',
                      probe.evaluate_negative(self.START + self.PF + 'USER_PROBE gegenprobe NOT HALTED\n'))

    def test_other_exception_fails(self):
        other = self.PF.replace('Page Fault (#PF)', 'General Protection Fault (#GP)').replace('Vector: 14', 'Vector: 13')
        result = probe.evaluate_negative(self.START + other)
        self.assertIn('keine #PF-Meldung', result)
        self.assertIn('Vektor nicht 14', result)

    def test_other_address_fails(self):
        self.assertEqual(probe.evaluate_negative(self.START + self.PF.replace('CR2: 0x0', 'CR2: 0x10')), ['CR2 nicht 0'])

    def test_without_halt_fails(self):
        self.assertEqual(probe.evaluate_negative(self.START + self.PF.replace('System halted.', '')), ['keine Haltmeldung'])


class NoteTests(unittest.TestCase):
    INFO = '  USER_PROBE info cpus=1 base=0x8000000000 cr4=0x620 avx={} tsc_hz=2502998000\n'
    AVX = '  USER_PROBE fault_avx ok=1 vector=6 error=0x0 address=0x0 faults=1 osxsave=0 after=1 ms=0\n'

    def test_avx_case_without_avx_is_marked_empty(self):
        self.assertEqual(len(probe.notes(self.INFO.format(0) + self.AVX)), 1)

    def test_avx_case_with_avx_has_no_note(self):
        self.assertEqual(probe.notes(self.INFO.format(1) + self.AVX), [])

    def test_note_does_not_change_the_result(self):
        self.assertEqual(probe.evaluate(self.INFO.format(0) + self.AVX + 'USER_PROBE done failed=0\n', ['fault_avx']), [])


class WaitTests(unittest.TestCase):
    def test_stops_when_process_is_gone(self):
        self.assertFalse(probe.wait_for_done(lambda: '', 1000, clock=lambda: 0.0, sleep=lambda _: None, alive=lambda: False))

    def test_timeout_without_final_line(self):
        now = [0.0]
        sleeps = []

        def sleep(seconds):
            sleeps.append(seconds)
            now[0] += seconds

        self.assertFalse(probe.wait_for_done(lambda: 'USER_PROBE loop ok=1', 5, clock=lambda: now[0], sleep=sleep))
        self.assertEqual(len(sleeps), 5)

    def test_final_line_arrives_late(self):
        logs = iter(['', 'USER_PROBE loop ok=1', 'USER_PROBE done failed=0'])
        self.assertTrue(probe.wait_for_done(lambda: next(logs), 60, clock=lambda: 0.0, sleep=lambda _: None))


class FakeTools:
    def __init__(self, log='', vm_alive=True, disk_error=None):
        self.log, self.vm_alive, self.disk_error = log, vm_alive, disk_error
        self.vm = mock.Mock()
        self.vm.poll = lambda: None if self.vm_alive else 1

    def create_disk(self, seed, target):
        if self.disk_error:
            raise self.disk_error

    def free_port(self):
        return 1

    def start_qemu(self, iso, disk, work, port, accel, loadvm):
        (work / 'serial.log').write_text(self.log)
        return self.vm


class RunTests(unittest.TestCase):
    def run_with(self, fake, argv, clock=None):
        args = probe.parse_args(['--iso', 'x.iso', *argv])
        out = mock.MagicMock()
        with tempfile.TemporaryDirectory() as root, \
                mock.patch.object(probe, 'ROOT', Path(root)), \
                mock.patch.object(probe, 'tools', lambda: fake), \
                mock.patch.object(probe, 'own_session', lambda smp: mock.MagicMock()), \
                mock.patch.object(probe, 'terminate') as terminate, \
                mock.patch.object(probe.time, 'sleep', lambda _: None), \
                mock.patch('builtins.print', out):
            result = probe.run(args, 1)
        return result, ' '.join(str(call.args[0]) for call in out.call_args_list)

    def test_timeout_is_reported_as_timeout_even_with_all_cases(self):
        # Alle Faelle ok, nur die Schlusszeile fehlt: ohne Zeitlimitzweig stuende nur "Schlusszeile fehlt" im Text.
        log = '\n'.join(f'USER_PROBE {name} ok=1' for name in CASES)
        now = iter(x * 0.5 for x in range(10000))
        with mock.patch.object(probe.time, 'monotonic', lambda: next(now)):
            result, text = self.run_with(FakeTools(log), ['--timeout', '3', '--expect', ','.join(CASES)])
        self.assertFalse(result)
        self.assertIn('Zeitlimit 3 s ohne Schlusszeile', text)

    def test_early_qemu_exit_does_not_wait_for_timeout(self):
        result, text = self.run_with(FakeTools('', vm_alive=False), ['--timeout', '100000'])
        self.assertFalse(result)
        self.assertIn('QEMU vorzeitig beendet', text)

    def test_complete_log_passes(self):
        result, _ = self.run_with(FakeTools(GOOD), ['--expect', ','.join(CASES)])
        self.assertTrue(result)

    def test_disk_error_is_a_fail_line_not_a_traceback(self):
        error = subprocess.CalledProcessError(1, 'qemu-img')
        result, text = self.run_with(FakeTools(disk_error=error), [])
        self.assertFalse(result)
        self.assertIn('CalledProcessError', text)

    def test_own_session_sets_and_restores_environment(self):
        fake_subprocess = mock.Mock(Popen=lambda *a, **k: None)
        original = fake_subprocess.Popen
        with mock.patch.object(probe, 'tools', lambda: mock.Mock(subprocess=fake_subprocess)), \
                mock.patch.dict(os.environ, {'ZEHOS_QEMU_SMP': 'alt'}):
            with probe.own_session(4):
                self.assertEqual(os.environ['ZEHOS_QEMU_SMP'], '4')
                self.assertIsNot(fake_subprocess.Popen, original)
                self.assertTrue(fake_subprocess.Popen.keywords['start_new_session'])
            self.assertEqual(os.environ['ZEHOS_QEMU_SMP'], 'alt')
        self.assertIs(fake_subprocess.Popen, original)

    def test_main_exit_codes(self):
        with mock.patch.object(probe, 'run', side_effect=[True, True]):
            self.assertEqual(probe.main(['--iso', 'x', '--runs', '2']), 0)
        with mock.patch.object(probe, 'run', side_effect=[True, False]):
            self.assertEqual(probe.main(['--iso', 'x', '--runs', '2']), 1)

    def test_expect_parsing(self):
        args = probe.parse_args(['--iso', 'x', '--expect', 'a,b,,c', '--smp', '4'])
        self.assertEqual((args.expect, args.smp), (['a', 'b', 'c'], 4))
        with self.assertRaises(SystemExit):
            probe.parse_args(['--iso', 'x', '--smp', '0'])

    @unittest.skipUnless(hasattr(os, 'killpg'), 'nur POSIX')
    def test_terminate_kills_child_of_wrapper(self):
        vm = subprocess.Popen(['sh', '-c', 'sleep 60 & echo $!; wait'], stdout=subprocess.PIPE, text=True,
                              start_new_session=True)
        child = int(vm.stdout.readline())
        probe.terminate(vm)
        vm.stdout.close()
        time.sleep(0.2)
        with self.assertRaises(ProcessLookupError):
            os.kill(child, 0)


@unittest.skipUnless(shutil.which('as') and shutil.which('ld') and shutil.which('readelf') and shutil.which('objdump')
                     and sys.platform.startswith('linux'), 'binutils fehlen')
class ProbeImageTests(unittest.TestCase):
    NAMES = ['loop', 'spin', 'fpu', 'fault_de', 'fault_ud', 'fault_gp_cli', 'fault_gp_hlt', 'fault_gp_in',
             'fault_pf_read', 'fault_pf_write', 'fault_pf_exec', 'badret', 'leave', 'fault_avx', 'fpu2', 'switch',
             'isolate', 'readat', 'execat', 'sparse', 'device']

    def test_entry_table_is_fixed_and_position_independent(self):
        build = SCRIPTS.parent / 'ProjektZeh/Native/UserProbe/build.sh'
        with tempfile.TemporaryDirectory() as out:
            subprocess.run(['bash', str(build), out], check=True, capture_output=True)
            image = (Path(out) / 'probe.bin').read_bytes()
            listing = (Path(out) / 'probe.dis').read_text()
        magic, count, size, capacity = struct.unpack_from('<4sIII', image, 0)
        self.assertEqual((magic, count, size, capacity), (b'ZUP1', len(self.NAMES), 32, 64))
        self.assertLessEqual(16 + 32 * capacity, 0x1000, 'Tabelle muss vor den Slots enden')
        # Slot -> Rumpf laut Disassembly: jede Tabellenzeile (Name, Versatz) muss auf body_<Name> springen.
        jumps = {int(m.group(1), 16): m.group(2)
                 for m in re.finditer(r'^\s*([0-9a-f]+):\s+e9 [0-9a-f ]+\s*jmp\s+[0-9a-f]+ <body_(\w+)>', listing, re.M)}
        table = {}
        for index in range(count):
            raw, offset = struct.unpack_from('<24sI', image, 16 + 32 * index)
            name = raw.rstrip(b'\0').decode()
            table[name] = offset
            self.assertEqual(offset, 0x1000 + 0x10 * index)
            self.assertEqual(image[offset], 0xE9, 'Slot beginnt mit jmp rel32')
            self.assertEqual(jumps.get(offset), name)
        self.assertEqual(list(table), self.NAMES)
        self.assertEqual(len(jumps), len(self.NAMES))


if __name__ == '__main__':
    unittest.main()
