import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "test-storage-matrix.py"
spec = importlib.util.spec_from_file_location("storage_matrix_guest", SCRIPT)
matrix = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matrix)

REQUIRED = ["--work", "w", "--iso", "i", "--payload", "p", "--version", "0", "--boot-config", "b",
            "--ovmf-code", "c", "--ovmf-vars", "v"]


class ResultsTests(unittest.TestCase):
    def test_only_errors_mark_the_run_failed(self):
        results = matrix.Results()
        with mock.patch("builtins.print"):
            results.report("PASS", "a")
            results.report("OFFEN", "b")
            results.report("BILD", "c")
            self.assertFalse(results.failed)
            results.report("FEHLER", "d")
        self.assertTrue(results.failed)

    def test_check_turns_exceptions_into_error_lines(self):
        results = matrix.Results()
        with mock.patch("builtins.print") as printed:
            results.check("Fall", lambda: (_ for _ in ()).throw(RuntimeError("kaputt")))
        self.assertTrue(results.failed)
        self.assertEqual(printed.call_args.args[0], "FEHLER Fall: kaputt")


class ArgumentTests(unittest.TestCase):
    def parse_error(self, *extra: str) -> None:
        with mock.patch.object(sys, "argv", ["test-storage-matrix.py", *REQUIRED, *extra]), \
                mock.patch("sys.stderr"), self.assertRaises(SystemExit) as raised:
            matrix.main()
        self.assertEqual(raised.exception.code, 2)

    def test_rejects_sizes_outside_the_matrix(self):
        self.parse_error("--sizes", "16")

    def test_inode_limit_needs_the_32_gib_installation(self):
        self.parse_error("--sizes", "64", "--inode-limit")

    def test_legacy_needs_both_isos(self):
        self.parse_error("--legacy", "--iso-normal", "n")


def debugfs_fake(missing: int = 0):
    """Simuliert debugfs: Schreiben gelingt; "ls -p /home/inodeN" listet so viele Dateien, wie angelegt sein sollten."""
    per_dir = -(-matrix.INODE_FILES // matrix.INODE_DIRS)

    def run(command, **_):
        if "-R" not in command:
            return matrix.subprocess.CompletedProcess(command, 0, stdout="")
        directory = int(command[command.index("-R") + 1].rsplit("inode", 1)[1])
        count = min(per_dir, matrix.INODE_FILES - directory * per_dir) - (missing if directory == 0 else 0)
        lines = ["/12/040755/0/0/.//", "/2/040755/0/0/..//"] + [f"/{20 + i}/100644/0/0/f{i}/0/" for i in range(count)]
        return matrix.subprocess.CompletedProcess(command, 0, stdout="\n".join(lines) + "\n")
    return run


class FillInodesTests(unittest.TestCase):
    def test_files_stay_below_the_directory_limit(self):
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(matrix.subprocess, "run", side_effect=debugfs_fake()) as run:
            work = Path(folder)
            matrix.fill_inodes(work / "root.img", work)
            commands = (work / "debugfs.cmd").read_text().splitlines()
        self.assertEqual(run.call_count, 1 + matrix.INODE_DIRS)
        # /home zuerst: die frische Root hat es noch nicht.
        self.assertEqual(commands[0], "mkdir /home")
        self.assertEqual(sum(line.startswith("mkdir /home/inode") for line in commands), matrix.INODE_DIRS)
        writes = [line.split()[-1] for line in commands if line.startswith("write ")]
        self.assertEqual(len(writes), matrix.INODE_FILES)
        self.assertEqual(len(set(writes)), matrix.INODE_FILES)
        # Ueber der Inode-Cache-Grenze (17519), je Verzeichnis unter der ZehOS-Grenze von 8189 Eintraegen.
        self.assertGreater(matrix.INODE_FILES, 17519)
        for directory in range(matrix.INODE_DIRS):
            self.assertLessEqual(sum(name.startswith(f"/home/inode{directory}/") for name in writes), 8189)

    def test_missing_files_fail_instead_of_passing_silently(self):
        # debugfs endet auch nach gescheiterten Befehlen mit 0; fehlende Dateien muessen auffallen.
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(matrix.subprocess, "run", side_effect=debugfs_fake(missing=1)), \
                self.assertRaises(RuntimeError):
            matrix.fill_inodes(Path(folder) / "root.img", Path(folder))


@unittest.skipUnless(hasattr(os, "SEEK_DATA"), "SEEK_DATA/SEEK_HOLE nur unter Linux")
class WriteBackTests(unittest.TestCase):
    def test_writes_data_regions_and_keeps_holes(self):
        with tempfile.TemporaryDirectory() as folder:
            extract, image = Path(folder) / "part.img", Path(folder) / "disk.img"
            with extract.open("wb") as stream:
                stream.truncate(64 * 1024 * 1024)
                stream.seek(4096)
                stream.write(b"A" * 4096)
                stream.seek(40 * 1024 * 1024)
                stream.write(b"B" * 4096)
            with image.open("wb") as stream:
                stream.truncate(128 * 1024 * 1024)
            matrix.write_back(extract, image, 1024 * 1024)
            with image.open("rb") as stream:
                self.assertEqual(os.pread(stream.fileno(), 4096, 1024 * 1024 + 4096), b"A" * 4096)
                self.assertEqual(os.pread(stream.fileno(), 4096, 1024 * 1024 + 40 * 1024 * 1024), b"B" * 4096)
            # Eine dichte Kopie belegte 64 MiB; zurueckgeschrieben sind nur die zwei Datenbloecke.
            self.assertLess(image.stat().st_blocks * 512, 4 * 1024 * 1024)

    def test_rejects_a_partition_beyond_the_image(self):
        with tempfile.TemporaryDirectory() as folder:
            extract, image = Path(folder) / "part.img", Path(folder) / "disk.img"
            extract.write_bytes(b"x" * 4096)
            image.write_bytes(b"\0" * 4096)
            with self.assertRaises(ValueError):
                matrix.write_back(extract, image, 1)


class OfflineCheckTests(unittest.TestCase):
    def test_expect_text_compares_trimmed_content(self):
        with mock.patch.object(matrix, "debugfs_cat", return_value=b"tok-1\n"):
            self.assertEqual(matrix.expect_text(Path("r"), "/a", "tok-1")(), "tok-1")
            with self.assertRaises(RuntimeError):
                matrix.expect_text(Path("r"), "/a", "tok-2")()

    def test_clean_state_must_be_one(self):
        with mock.patch.object(matrix.power, "read_s_state", return_value=1):
            self.assertEqual(matrix.expect_clean_state(Path("i")), "1")
        with mock.patch.object(matrix.power, "read_s_state", return_value=0), self.assertRaises(RuntimeError):
            matrix.expect_clean_state(Path("i"))


if __name__ == "__main__":
    unittest.main()
