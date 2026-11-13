"""Hostpruefungen fuer Dateierhalt und Schutzgrenzen der FAT32-Testkopie."""

import hashlib
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "full_copy", Path(__file__).parents[1] / "create-full-fat32-test-copy.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class FullFat32CopyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.source = Path(self.temporary.name) / "source.img"
        self.target = Path(self.temporary.name) / "full.img"
        self.offset = 2048 * 512
        self.fat_size = 1024 * 512
        self.fat_start = self.offset + 32 * 512
        self.data_start = self.fat_start + 2 * self.fat_size
        self.cluster_count = (64 * 1024 * 1024 - self.offset) // 512 - 32 - 2048
        with self.source.open("xb") as stream:
            stream.truncate(64 * 1024 * 1024)
            stream.seek(446)
            entry = bytearray(16)
            entry[4] = 0x0C
            struct.pack_into("<II", entry, 8, 2048, (64 * 1024 * 1024 - self.offset) // 512)
            stream.write(entry)
            stream.seek(510)
            stream.write(b"\x55\xaa")
            boot = bytearray(512)
            struct.pack_into("<H", boot, 11, 512)
            boot[13] = 1
            struct.pack_into("<H", boot, 14, 32)
            boot[16] = 2
            struct.pack_into("<I", boot, 32, (64 * 1024 * 1024 - self.offset) // 512)
            struct.pack_into("<I", boot, 36, 1024)
            struct.pack_into("<I", boot, 44, 2)
            struct.pack_into("<HH", boot, 48, 1, 6)
            boot[510:512] = b"\x55\xaa"
            stream.seek(self.offset)
            stream.write(boot)
            info = bytearray(512)
            info[:4] = b"RRaA"
            info[484:488] = b"rrAa"
            info[508:512] = b"\x00\x00\x55\xaa"
            stream.seek(self.offset + 512)
            stream.write(info)
            for index in range(2):
                stream.seek(self.fat_start + index * self.fat_size)
                stream.write(struct.pack("<IIII", 0x0FFFFFF8, 0x0FFFFFFF, 0x0FFFFFFF, 0x0FFFFFFF))
            file_entry = bytearray(32)
            file_entry[:11] = b"ORIGINALTXT"
            file_entry[11] = 0x20
            struct.pack_into("<H", file_entry, 26, 3)
            struct.pack_into("<I", file_entry, 28, 6)
            stream.seek(self.data_start)
            stream.write(file_entry)
            stream.seek(self.data_start + 512)
            stream.write(b"vorher")

    def test_full_copy_preserves_source_and_original_and_occupies_every_free_cluster(self):
        before = hashlib.sha256(self.source.read_bytes()).digest()
        module.create_copy(self.source, self.target)
        self.assertEqual(before, hashlib.sha256(self.source.read_bytes()).digest())
        image = self.target.read_bytes()
        self.assertEqual(b"vorher", image[self.data_start + 512:self.data_start + 518])
        self.assertEqual(self.source.read_bytes()[self.data_start:self.data_start + 32],
                         image[self.data_start:self.data_start + 32])
        fat = image[self.fat_start:self.fat_start + self.fat_size]
        self.assertEqual(fat, image[self.fat_start + self.fat_size:self.data_start])
        filler = image[self.data_start + 32:self.data_start + 64]
        self.assertEqual(b"FILLER  BIN", filler[:11])
        self.assertEqual((self.cluster_count - 2) * 512, struct.unpack_from("<I", filler, 28)[0])
        cluster = struct.unpack_from("<H", filler, 26)[0] | struct.unpack_from("<H", filler, 20)[0] << 16
        visited = set()
        while cluster < 0x0FFFFFF8:
            self.assertNotIn(cluster, visited)
            self.assertGreaterEqual(cluster, 4)
            self.assertLess(cluster, self.cluster_count + 2)
            visited.add(cluster)
            cluster = struct.unpack_from("<I", fat, 4 * cluster)[0] & 0x0FFFFFFF
        self.assertEqual(self.cluster_count - 2, len(visited))
        self.assertTrue(all(struct.unpack_from("<I", fat, n * 4)[0] & 0x0FFFFFFF
                            for n in range(2, self.cluster_count + 2)))
        self.assertEqual((0, 0xFFFFFFFF), struct.unpack_from("<II", image, self.offset + 512 + 488))
        self.assertEqual(bytes(512), image[self.offset + 7 * 512:self.offset + 8 * 512])

    def test_existing_destination_and_source_as_destination_are_never_overwritten(self):
        self.target.write_bytes(b"erhalten")
        before = hashlib.sha256(self.source.read_bytes()).digest()
        with self.assertRaises(FileExistsError):
            module.create_copy(self.source, self.target)
        with self.assertRaises(FileExistsError):
            module.create_copy(self.source, self.source)
        self.assertEqual(b"erhalten", self.target.read_bytes())
        self.assertEqual(before, hashlib.sha256(self.source.read_bytes()).digest())

    def test_invalid_source_does_not_create_destination(self):
        self.source.write_bytes(b"keine Platte")
        with self.assertRaises(ValueError):
            module.create_copy(self.source, self.target)
        self.assertFalse(self.target.exists())

    def test_different_fat_copies_are_rejected_before_writing(self):
        with self.source.open("r+b") as stream:
            stream.seek(self.fat_start + self.fat_size + 16)
            stream.write(b"\xff\xff\xff\x0f")
        with self.assertRaises(ValueError):
            module.create_copy(self.source, self.target)
        self.assertFalse(self.target.exists())

    def test_end_marker_keeps_stale_entries_hidden(self):
        with self.source.open("r+b") as stream:
            stream.seek(self.data_start + 64)
            stream.write(b"STALE   TXT" + bytes(21))
        module.create_copy(self.source, self.target)
        image = self.target.read_bytes()
        self.assertEqual(0, image[self.data_start + 64])

    def test_deleted_slot_preserves_live_following_entry(self):
        with self.source.open("r+b") as stream:
            stream.seek(self.data_start)
            entry = stream.read(32)
            stream.seek(self.data_start)
            stream.write(b"\xe5" + bytes(31))
            stream.write(entry)
        module.create_copy(self.source, self.target)
        image = self.target.read_bytes()
        self.assertEqual(entry, image[self.data_start + 32:self.data_start + 64])

    def test_valid_backup_fsinfo_is_updated(self):
        with self.source.open("r+b") as stream:
            stream.seek(self.offset + 512)
            info = stream.read(512)
            stream.seek(self.offset + 7 * 512)
            stream.write(info)
        module.create_copy(self.source, self.target)
        image = self.target.read_bytes()
        self.assertEqual((0, 0xFFFFFFFF), struct.unpack_from("<II", image, self.offset + 7 * 512 + 488))


if __name__ == "__main__":
    unittest.main()
