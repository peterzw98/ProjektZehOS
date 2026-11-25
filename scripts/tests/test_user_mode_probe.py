import importlib.util
import os
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


class WaitTests(unittest.TestCase):
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


class RunTests(unittest.TestCase):
    def test_timeout_is_a_failure_with_exit_1(self):
        class Tools:
            def create_disk(self, seed, target): pass
            def free_port(self): return 1
            def start_qemu(self, *args): return mock.Mock(poll=lambda: 0, wait=lambda: 0)

        args = probe.parse_args(['--iso', 'x.iso', '--timeout', '0.01'])
        with tempfile.TemporaryDirectory() as root, \
                mock.patch.object(probe, 'ROOT', Path(root)), \
                mock.patch.object(probe, 'tools', lambda: Tools()), \
                mock.patch.object(probe, 'own_session', lambda smp: mock.MagicMock()), \
                mock.patch.object(probe, 'terminate') as terminate, \
                mock.patch.object(probe.time, 'sleep', lambda _: None):
            self.assertFalse(probe.run(args, 1))
            terminate.assert_called_once()

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
             'fault_pf_read', 'fault_pf_write', 'fault_pf_exec', 'badret', 'leave']

    def test_entry_table_is_fixed_and_position_independent(self):
        build = SCRIPTS.parent / 'ProjektZeh/Native/UserProbe/build.sh'
        with tempfile.TemporaryDirectory() as out:
            subprocess.run(['bash', str(build), out], check=True, capture_output=True)
            image = (Path(out) / 'probe.bin').read_bytes()
        magic, count, size = struct.unpack_from('<4sII', image, 0)
        self.assertEqual((magic, count, size), (b'ZUP1', len(self.NAMES), 32))
        for index, name in enumerate(self.NAMES):
            raw, offset = struct.unpack_from('<24sI', image, 16 + 32 * index)
            self.assertEqual(raw.rstrip(b'\0').decode(), name)
            self.assertEqual(offset, 0x200 + 0x40 * index)
            self.assertEqual(image[offset], 0xE9, 'Slot beginnt mit jmp rel32')


if __name__ == '__main__':
    unittest.main()
