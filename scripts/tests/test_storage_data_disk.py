import argparse
import importlib.util
import shutil
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('data_disk', SCRIPTS / 'test-storage-data-disk.py')
disk = importlib.util.module_from_spec(spec)
spec.loader.exec_module(disk)


class DataDiskHostTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir)

    def test_work_dir_new_only(self):
        self.assertTrue(disk.new_work_dir(self.dir / 'neu').is_dir())
        (self.dir / 'datei').write_bytes(b'x')
        for path in (self.dir, self.dir / 'datei'):
            with self.subTest(path=path.name), self.assertRaises(FileExistsError):
                disk.new_work_dir(path)
        self.assertEqual((self.dir / 'datei').read_bytes(), b'x')

    def test_parse_size(self):
        self.assertEqual(disk.parse_size('64M'), 64 * disk.MIB)
        self.assertEqual(disk.parse_size('5g'), 5 * disk.GIB)
        self.assertEqual(disk.parse_size('128G'), 128 * disk.GIB)
        for bad in ('1M', '129G', '32', '4T', '-1G', ''):
            with self.subTest(bad=bad), self.assertRaises(argparse.ArgumentTypeError):
                disk.parse_size(bad)
        self.assertEqual(disk.size_label(32 * disk.GIB), '32g')
        self.assertEqual(disk.size_label(64 * disk.MIB), '64m')

    def test_sparse_image_sizes(self):
        for size in (64 * disk.MIB, 5 * disk.GIB, 128 * disk.GIB):
            path = self.dir / f'{size}.img'
            disk.create_sparse(path, size)
            self.assertEqual(path.stat().st_size, size)
        for size in (16 * disk.MIB, 129 * disk.GIB, 32 * disk.GIB + 1):
            with self.subTest(size=size), self.assertRaises(ValueError):
                disk.create_sparse(self.dir / 'x.img', size)

    def test_sparse_image_never_overwrites(self):
        existing = self.dir / 'e.img'
        existing.write_bytes(b'daten')
        with self.assertRaises(FileExistsError):
            disk.create_sparse(existing, disk.LEGACY_SIZE)
        self.assertEqual(existing.read_bytes(), b'daten')

    def test_legacy_check(self):
        good = self.dir / 'alt.img'
        good.write_bytes(b'\0' * disk.LEGACY_SIZE)
        self.assertEqual(disk.check_legacy(good), good.resolve())
        small = self.dir / 'klein.img'
        small.write_bytes(b'\0' * 512)
        for path in (small, self.dir / 'fehlt.img', self.dir):
            with self.subTest(path=path.name), self.assertRaises(ValueError):
                disk.check_legacy(path)

    def test_sha256_and_device_line(self):
        path = self.dir / 'h'
        path.write_bytes(b'abc')
        self.assertEqual(disk.sha256(path), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
        self.assertEqual(disk.device_line('sata1', 32 * disk.GIB, 1), 'Laufwerk sata1: 34359738368 Bytes, 1 Partition(en)')

    def test_machine_has_no_fixed_app_disk(self):
        self.assertNotIn('ide-hd,drive=appdisk,bus=ahci.0', disk.MACHINE_BASE)
        self.assertIn('ahci,id=ahci', disk.MACHINE_BASE)


if __name__ == '__main__':
    unittest.main()
