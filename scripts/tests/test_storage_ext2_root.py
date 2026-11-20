import contextlib
import importlib.util
import io
import shutil
import tempfile
import threading
import time
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('ext2_root', SCRIPTS / 'test-storage-ext2-root.py')
root = importlib.util.module_from_spec(spec)
spec.loader.exec_module(root)

DUMPE2FS = """dumpe2fs 1.47.0 (5-Feb-2023)
Filesystem volume name:   zehos-rootprobe
Filesystem state:         clean
Reserved block count:     419430
Free blocks:              8000000
Free inodes:              2000000
"""


class Ext2RootHostTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir)

    def test_work_dir_new_only(self):
        self.assertTrue(root.new_work_dir(self.dir / 'neu').is_dir())
        with self.assertRaises(FileExistsError):
            root.new_work_dir(self.dir)

    def test_control_sector(self):
        sector = root.control_sector(3)
        self.assertEqual(len(sector), 512)
        self.assertTrue(sector.startswith(b'ZEHOS-ROOTPROBE run=3\n'))
        for bad in (0, 17):
            with self.subTest(run=bad), self.assertRaises(ValueError):
                root.control_sector(bad)

    def test_write_control_is_one_mib(self):
        path = self.dir / 'control.img'
        root.write_control(path, 1)
        root.write_control(path, 5)
        self.assertEqual(path.stat().st_size, root.MIB)
        self.assertTrue(path.read_bytes().startswith(b'ZEHOS-ROOTPROBE run=5'))

    def test_evaluate(self):
        serial = ("STORAGE_ROOT select PASS Lauf 1\nSTORAGE_ROOT a PASS ok\nSTORAGE_ROOT d FAIL /d/x weicht ab\n"
                  "STORAGE_ROOT COUNTERS a free_blocks=1 free_inodes=2 r_blocks=3 state=0\nSTORAGE_ROOT DONE\n")
        self.assertEqual(root.evaluate(serial), (2, ['d'], True))
        self.assertEqual(root.evaluate("STORAGE_ROOT a PASS\n"), (1, [], False))

    def test_counters_last_m_line(self):
        serial = ("STORAGE_ROOT COUNTERS c free_blocks=9 free_inodes=9 r_blocks=9 state=0\n"
                  "STORAGE_ROOT COUNTERS m free_blocks=8000000 free_inodes=2000000 r_blocks=419430 state=1\n")
        guest = root.guest_counters(serial)
        self.assertEqual(guest, {'free_blocks': 8000000, 'free_inodes': 2000000, 'r_blocks': 419430, 'state': 1})
        self.assertIsNone(root.guest_counters("STORAGE_ROOT DONE\n"))

    def test_counters_match(self):
        host = root.parse_dumpe2fs(DUMPE2FS)
        self.assertEqual(host['state'], 'clean')
        guest = {'free_blocks': 8000000, 'free_inodes': 2000000, 'r_blocks': 419430, 'state': 1}
        self.assertEqual(root.counters_match(guest, host), [])
        self.assertEqual(len(root.counters_match({**guest, 'free_blocks': 1}, host)), 1)
        self.assertEqual(len(root.counters_match({**guest, 'state': 0}, host)), 1)
        self.assertEqual(len(root.counters_match(guest, {**host, 'state': 'not clean'})), 1)
        self.assertEqual(root.counters_match(None, host), ['Gast meldete keine COUNTERS m'])

    def test_drives_ports(self):
        arguments = root.drives(Path('/r.qcow2'), 'qcow2', Path('/i.qcow2'), Path('/c.img'))
        joined = ' '.join(arguments)
        for port in (1, 2, 3):
            self.assertIn(f'bus=ahci.{port}', joined)
        self.assertIn('detect-zeroes=unmap', joined)

    def test_prefill_commands_one_gib_files(self):
        free = 8 * root.GIB // 4096
        commands = root.prefill_commands(free)
        self.assertEqual(commands[:2], ['mkdir fill', 'cd fill'])
        sizes = [int(c.split()[-1]) for c in commands if c.startswith('sif ')]
        # Jede Datei hoechstens 1 GiB (kein large_file), zusammen alles ausser dem Hochgruppenrest.
        self.assertTrue(all(size <= root.GIB for size in sizes))
        self.assertEqual(sum(sizes), free * 4096 - root.HIGH_GROUP_FREE)
        self.assertIn('fallocate f0000 0 262143', commands)

    def test_prefill_commands_too_small(self):
        with self.assertRaises(ValueError):
            root.prefill_commands(root.HIGH_GROUP_FREE // 4096)


class RunAbortsTests(unittest.TestCase):
    DELAYS = (0.3, 2.0, 7.0, 20.0)

    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir)
        for disk in ('fat.qcow2', 'inodes.qcow2'):
            (self.dir / disk).write_bytes(b'x')
        self.ctx = object.__new__(root.Context)
        self.ctx.work, self.ctx.report, self.ctx.go, self.ctx.echo = self.dir, [], True, True
        self.active = self.peak = 0
        self.lock = threading.Lock()

    def runner(self, fail=()):
        def fake(ctx, base, delay):
            with self.lock:
                self.active += 1
                self.peak = max(self.peak, self.active)
            time.sleep(0.25 - delay / 100)  # kleine Verzoegerung endet zuletzt
            with self.lock:
                self.active -= 1
            ctx.note(f'lauf {delay:g}', delay not in fail)
        return fake

    def run_aborts(self, parallel, fail=()):
        with contextlib.redirect_stdout(io.StringIO()) as out:
            root.run_aborts(self.ctx, self.dir / 'root.qcow2', self.DELAYS, parallel, self.runner(fail))
        return out.getvalue().splitlines()

    def test_order_is_fixed_despite_finish_order(self):
        printed = self.run_aborts(4)
        expected = [f'lauf {d:g}' for d in self.DELAYS]
        self.assertEqual(printed, expected)
        self.assertEqual(self.ctx.report, expected)
        self.assertTrue(self.ctx.go)

    def test_failure_clears_go_and_all_finish(self):
        self.run_aborts(4, fail=(2.0,))
        self.assertFalse(self.ctx.go)
        self.assertEqual(len(self.ctx.report), 4)

    def test_parallel_one_is_sequential_and_stops_on_failure(self):
        self.run_aborts(1)
        self.assertEqual(self.peak, 1)
        self.ctx.report.clear()

        self.active = self.peak = 0
        for child in self.dir.glob('abort-*'):
            shutil.rmtree(child)
        self.run_aborts(1, fail=(0.3,))
        self.assertEqual(self.ctx.report[-1:], ['lauf 0.3'])
        self.assertFalse(self.ctx.go)

    def test_at_most_n_concurrent(self):
        for parallel in (2, 4):
            with self.subTest(parallel=parallel):
                self.active = self.peak = 0
                for child in self.dir.glob('abort-*'):
                    shutil.rmtree(child)
                self.run_aborts(parallel)
                self.assertEqual(self.peak, parallel)

    def test_exception_counts_as_failure(self):
        def broken(ctx, base, delay):
            raise RuntimeError('kaputt')
        with contextlib.redirect_stdout(io.StringIO()):
            root.run_aborts(self.ctx, self.dir / 'root.qcow2', self.DELAYS, 4, broken)
        self.assertFalse(self.ctx.go)
        self.assertEqual(len(self.ctx.report), 4)

    def test_children_have_own_disks(self):
        self.run_aborts(4)
        for delay in self.DELAYS:
            self.assertTrue((self.dir / f'abort-{delay:g}s' / 'inodes.qcow2').is_file())


if __name__ == '__main__':
    unittest.main()
