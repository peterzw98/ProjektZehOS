import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "test-installer-gpt-guest.py"
spec = importlib.util.spec_from_file_location("installer_gpt_guest", SCRIPT)
guest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guest)


def table():
    return {"partitiontable": {"label": "gpt", "sectorsize": 512, "partitions": [
        {"type": guest.ESP_TYPE, "start": 2048, "size": 2048},
        {"type": guest.ROOT_TYPE, "start": 4096, "size": 4096},
    ]}}


class PartitionTests(unittest.TestCase):
    def test_accepts_bounded_nonoverlapping_partitions(self):
        esp, root = guest.partitions(table(), 8192 * 512)
        self.assertEqual(esp["start"], 2048)
        self.assertEqual(root["start"], 4096)

    def test_rejects_overlaps_and_out_of_bounds(self):
        for start, size in ((4095, 4096), (4096, 4097), (4096, 0)):
            data = table()
            data["partitiontable"]["partitions"][1].update(start=start, size=size)
            with self.subTest(start=start, size=size), self.assertRaises(ValueError):
                guest.partitions(data, 8192 * 512)

    def test_rejects_bad_sector_size_and_partition_count(self):
        for change in ("sector", "count"):
            data = table()
            if change == "sector":
                data["partitiontable"]["sectorsize"] = 4096
            else:
                data["partitiontable"]["partitions"].pop()
            with self.subTest(change=change), self.assertRaises(ValueError):
                guest.partitions(data, 8192 * 512)

    def test_rejects_wrong_type(self):
        data = table()
        data["partitiontable"]["partitions"][0]["type"] = guest.ROOT_TYPE
        with self.assertRaises(ValueError):
            guest.partitions(data, 8192 * 512)


class MarkerTests(unittest.TestCase):
    def test_markers_use_partition_identity_and_crc(self):
        data = table()
        data["partitiontable"]["id"] = "12121212-1212-4212-8212-121212121212"
        root = data["partitiontable"]["partitions"][1]
        root["uuid"] = "34343434-3434-4434-8434-343434343434"
        esp, fresh = guest.expected_markers(data, root)
        self.assertIn(b"root-first=4096\nroot-sectors=4096\n", esp)
        self.assertIn(b"state=fresh\ndisk=12121212-1212-4212-8212-121212121212\n", fresh)
        self.assertEqual(esp.splitlines()[-1], b"crc32=7d5d4721")
        self.assertEqual(fresh.splitlines()[-1], b"crc32=f3e83252")


class OfflineCheckTests(unittest.TestCase):
    def test_e2fsck_exit_zero_with_unfixed_error_fails(self):
        result = mock.Mock(returncode=0, stdout="Free blocks count wrong. Fix? no\n", stderr="")
        with mock.patch.object(guest.subprocess, "run", return_value=result):
            with self.assertRaises(RuntimeError):
                guest.check_ext2(Path("root.img"))

    def test_sgdisk_exit_zero_with_warning_fails(self):
        result = mock.Mock(returncode=0, stdout="Warning: problem found", stderr="")
        with mock.patch.object(guest.subprocess, "run", return_value=result):
            with self.assertRaises(RuntimeError):
                guest.check_gpt(Path("disk.img"))


class ExtractTests(unittest.TestCase):
    def test_window_and_existing_target(self):
        with tempfile.TemporaryDirectory() as directory:
            source, target = Path(directory) / "source", Path(directory) / "target"
            source.write_bytes(b"0123456789")
            guest.extract_sparse(source, 2, 5, target)
            self.assertEqual(target.read_bytes(), b"23456")
            with self.assertRaises(FileExistsError):
                guest.extract_sparse(source, 0, 5, target)
            self.assertEqual(target.read_bytes(), b"23456")

    def test_g6_image_is_new_and_writes_are_detected(self):
        with tempfile.TemporaryDirectory() as directory:
            image = Path(directory) / "small.img"
            guest.new_sparse(image, 31 * guest.GIB)
            guest.assert_blank(image)
            with self.assertRaises(FileExistsError):
                guest.new_sparse(image, 31 * guest.GIB)
            with image.open("r+b") as target:
                target.write(b"x")
            with self.assertRaises(ValueError):
                guest.assert_blank(image)

    def test_unsupported_sparse_seek_is_error(self):
        with tempfile.TemporaryDirectory() as directory:
            source, target = Path(directory) / "source", Path(directory) / "target"
            source.write_bytes(b"x" * 100)
            with mock.patch.object(guest.os, "lseek", side_effect=OSError(22, "unsupported")):
                with self.assertRaises(OSError):
                    guest.extract_sparse(source, 0, 100, target)


if __name__ == "__main__":
    unittest.main()
