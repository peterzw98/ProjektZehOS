import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "test-storage-filesystems.py"
SPEC = importlib.util.spec_from_file_location("storage_filesystems", SCRIPT)
STORAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(STORAGE)


class Ext2MetadataTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.image = Path(self.directory.name) / "partition.img"

    def write_superblock(self, log_block_size=2, blocks=33554432):
        data = bytearray(2048)
        struct.pack_into("<I", data, 1028, blocks)
        struct.pack_into("<I", data, 1048, log_block_size)
        struct.pack_into("<H", data, 1080, 0xEF53)
        struct.pack_into("<III", data, 1116, 0x80000000, 0x12345678, 0xFFFFFFFF)
        self.image.write_bytes(data)

    def test_reads_sizes_and_preserves_unknown_feature_bits(self):
        for log_size, block_size in enumerate((1024, 2048, 4096)):
            with self.subTest(block_size=block_size):
                self.write_superblock(log_size)
                self.assertEqual({
                    "block_count": 33554432,
                    "block_size": block_size,
                    "feature_compat": 0x80000000,
                    "feature_incompat": 0x12345678,
                    "feature_ro_compat": 0xFFFFFFFF,
                }, STORAGE.read_ext2_metadata(self.image))

    def test_reading_does_not_modify_image(self):
        self.write_superblock()
        original = hashlib.sha256(self.image.read_bytes()).digest()
        STORAGE.read_ext2_metadata(self.image)
        self.assertEqual(original, hashlib.sha256(self.image.read_bytes()).digest())

    def test_rejects_truncated_superblock(self):
        self.image.write_bytes(bytes(2047))
        with self.assertRaisesRegex(ValueError, "Superblock"):
            STORAGE.read_ext2_metadata(self.image)

    def test_rejects_wrong_magic(self):
        self.write_superblock()
        with self.image.open("r+b") as stream:
            stream.seek(1080)
            stream.write(b"\x00\x00")
        with self.assertRaisesRegex(ValueError, "ext2"):
            STORAGE.read_ext2_metadata(self.image)

    def test_rejects_unsupported_block_sizes(self):
        for log_size in (3, 0xFFFFFFFF):
            with self.subTest(log_size=log_size):
                self.write_superblock(log_size)
                with self.assertRaisesRegex(ValueError, "Blockgroesse"):
                    STORAGE.read_ext2_metadata(self.image)

    def test_rejects_zero_block_count(self):
        self.write_superblock(blocks=0)
        with self.assertRaisesRegex(ValueError, "Blockanzahl"):
            STORAGE.read_ext2_metadata(self.image)

    def test_cli_prints_metadata_as_json(self):
        self.write_superblock()
        result = subprocess.run([sys.executable, str(SCRIPT), str(self.image)],
                                capture_output=True, text=True, check=False)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(4096, json.loads(result.stdout)["block_size"])

    def test_cli_reports_invalid_image_without_traceback(self):
        self.image.write_bytes(b"invalid")
        result = subprocess.run([sys.executable, str(SCRIPT), str(self.image)],
                                capture_output=True, text=True, check=False)
        self.assertEqual(1, result.returncode)
        self.assertEqual("", result.stdout)
        self.assertIn("Superblock", result.stderr)
        self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
