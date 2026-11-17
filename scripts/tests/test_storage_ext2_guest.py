import importlib.util
import shutil
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('ext2_guest', SCRIPTS / 'test-storage-ext2-guest.py')
guest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guest)


class Ext2GuestHostTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir)

    def test_new_work_dir_is_created(self):
        self.assertTrue(guest.new_work_dir(self.dir / 'neu' / 'a').is_dir())

    def test_existing_path_aborts_and_stays_untouched(self):
        (self.dir / 'datei').write_bytes(b'x')
        for path in (self.dir, self.dir / 'datei'):
            with self.subTest(path=path.name):
                with self.assertRaises(FileExistsError):
                    guest.new_work_dir(path)
        self.assertEqual((self.dir / 'datei').read_bytes(), b'x')

    def test_image_is_sparse_64_mib(self):
        image = self.dir / 'a.img'
        guest.create_image(image)
        self.assertEqual(image.stat().st_size, 64 * 1024 * 1024)

    def test_image_rejects_bad_size_and_never_overwrites(self):
        for size in (63 * 1024 * 1024, 64 * 1024 * 1024 + 1):
            with self.subTest(size=size), self.assertRaises(ValueError):
                guest.create_image(self.dir / 'b.img', size)
        existing = self.dir / 'c.img'
        existing.write_bytes(b'daten')
        with self.assertRaises(FileExistsError):
            guest.create_image(existing)
        self.assertEqual(existing.read_bytes(), b'daten')

    def test_evaluate(self):
        log = 'x\nSTORAGE_PROBE mount PASS\nSTORAGE_PROBE write FAIL boom\nSTORAGE_PROBE DONE\n'
        self.assertEqual(guest.evaluate(log), (1, 1, True))
        self.assertEqual(guest.evaluate('STORAGE_PROBE mount PASS\n'), (1, 0, False))


if __name__ == '__main__':
    unittest.main()
