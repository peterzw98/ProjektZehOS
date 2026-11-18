import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "test-storage-migrate-guest.py"
spec = importlib.util.spec_from_file_location("storage_migrate_guest", SCRIPT)
guest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guest)


def sfdisk(label: str, count: int) -> subprocess.CompletedProcess:
    table = {"partitiontable": {"label": label, "partitions": [{"start": 2048, "size": 1000}] * count}}
    return subprocess.CompletedProcess([], 0, stdout=json.dumps(table))


class SourcePartitionTests(unittest.TestCase):
    def test_accepts_single_mbr_partition(self):
        with mock.patch.object(guest.subprocess, "run", return_value=sfdisk("dos", 1)):
            self.assertEqual(guest.source_partition(Path("source.img"))["start"], 2048)

    def test_rejects_gpt_and_other_partition_counts(self):
        for label, count in (("gpt", 1), ("dos", 0), ("dos", 2)):
            with self.subTest(label=label, count=count), \
                    mock.patch.object(guest.subprocess, "run", return_value=sfdisk(label, count)), \
                    self.assertRaises(ValueError):
                guest.source_partition(Path("source.img"))


class SeedTests(unittest.TestCase):
    def test_targets_follow_the_migration_mapping(self):
        # Spec E4.4 §2: Home des Kontos nach /home/<name>, alles ohne Zuordnung nach /srv/legacy-data.
        for relative, (target, contents) in guest.SEED.items():
            with self.subTest(relative=relative):
                self.assertTrue(contents)
                if relative.startswith(f"home/{guest.tools.TEST_USER}/"):
                    self.assertEqual(target, "/" + relative)
                else:
                    self.assertEqual(target, "/srv/legacy-data/" + relative)

    def test_seed_avoids_system_and_excluded_names(self):
        for relative in guest.SEED:
            with self.subTest(relative=relative):
                self.assertFalse(any(part.startswith(".zehos-") for part in relative.split("/")))
                self.assertNotEqual(relative.split("/")[0].lower(), "boot")


class BulkTests(unittest.TestCase):
    def test_contents_have_fixed_size_and_differ(self):
        samples = [guest.bulk_contents(index) for index in (0, 1, guest.BULK_COUNT - 1)]
        self.assertTrue(all(len(sample) == guest.BULK_SIZE for sample in samples))
        self.assertEqual(len(set(samples)), len(samples))

    def listing(self, count: int, size: int) -> str:
        lines = [" 12  40755 (2)      0      0    4096 18-Nov-2026 11:00 .",
                 " 11  40755 (2)      0      0    4096 18-Nov-2026 11:00 .."]
        lines += [f" {20 + index}  100644 (1)      0      0   {size} 18-Nov-2026 11:00 {guest.bulk_name(index)}"
                  for index in range(count)]
        return "\n".join(lines) + "\n"

    def run_check(self, listing: str, contents=None):
        def fake_run(command, **kwargs):
            if command[2].startswith("ls -l"):
                return subprocess.CompletedProcess(command, 0, stdout=listing)
            index = int(command[2].rsplit("-", 1)[-1].split(".")[0])
            return subprocess.CompletedProcess(command, 0, stdout=(contents or guest.bulk_contents)(index))
        with mock.patch.object(guest.subprocess, "run", side_effect=fake_run):
            guest.check_bulk(Path("root.img"), "test")

    def test_accepts_complete_listing(self):
        self.run_check(self.listing(guest.BULK_COUNT, guest.BULK_SIZE))

    def test_rejects_missing_short_or_changed_files(self):
        cases = {"fehlend": (self.listing(guest.BULK_COUNT - 1, guest.BULK_SIZE), None),
                 "kurz": (self.listing(guest.BULK_COUNT, guest.BULK_SIZE - 1), None),
                 "inhalt": (self.listing(guest.BULK_COUNT, guest.BULK_SIZE), lambda index: b"x" * guest.BULK_SIZE)}
        for name, (listing, contents) in cases.items():
            with self.subTest(name=name), self.assertRaises(RuntimeError):
                self.run_check(listing, contents)


class TargetWrittenTests(unittest.TestCase):
    def test_reads_write_bytes_of_the_target_drive(self):
        qmp = mock.Mock()
        qmp.command.return_value = {"return": [{"device": "d0", "stats": {"wr_bytes": 7}},
                                               {"device": "d1", "stats": {"wr_bytes": 4096}}]}
        self.assertEqual(guest.target_written(qmp), 4096)

    def test_missing_target_drive_is_an_error(self):
        qmp = mock.Mock()
        qmp.command.return_value = {"return": [{"device": "d0", "stats": {"wr_bytes": 7}}]}
        with self.assertRaises(RuntimeError):
            guest.target_written(qmp)


class WaitMigrationTests(unittest.TestCase):
    def run_with(self, log: str, mark: int = 0, timeout: float = 5):
        with tempfile.TemporaryDirectory() as folder:
            run = Path(folder)
            (run / "serial.log").write_text(log, encoding="utf-8")
            with mock.patch.object(guest.time, "sleep"):
                return guest.wait_migration(run, mark, timeout)

    def test_returns_result_line(self):
        log = "MIGRATE Ziel sata1: ok\nMIGRATE Migration abgeschlossen: 3 Dateien; Quellinventar ab.\nweiter\n"
        self.assertEqual(self.run_with(log), "MIGRATE Migration abgeschlossen: 3 Dateien; Quellinventar ab.")

    def test_failure_lines_stop_with_their_text(self):
        for line in ("MIGRATE Migrationsauftrag fehlgeschlagen: kaputt", "MIGRATE Fehler: Ziel liess sich nicht aushaengen.",
                     "MIGRATE cause='ForeignEntry' message='Fremder Eintrag auf dem Ziel: /lost+found'"):
            with self.subTest(line=line), self.assertRaisesRegex(RuntimeError, line.split(":")[0]):
                self.run_with(line + "\n")

    def test_lines_before_mark_do_not_count(self):
        old = "MIGRATE Migration abgeschlossen: 1 Dateien; Quellinventar x.\n"
        with self.assertRaises(TimeoutError):
            self.run_with(old + "neu\n", mark=len(old), timeout=0)


class MainTests(unittest.TestCase):
    def call(self, *extra: str) -> int:
        with tempfile.TemporaryDirectory() as folder:
            argv = ["test-storage-migrate-guest.py", "--run", "g4", "--work", str(Path(folder) / "work"), "--version", "0.0.0"]
            for name in ("iso-installer", "iso-security", "iso-migrate", "payload", "boot-config", "ovmf-code", "ovmf-vars"):
                argv += [f"--{name}", str(Path(folder) / name)]
            with mock.patch.object(sys, "argv", argv + list(extra)):
                return guest.main()

    def test_missing_tools_are_open_not_failure(self):
        with mock.patch.object(guest.shutil, "which", return_value=None), mock.patch("builtins.print"):
            self.assertEqual(self.call(), 2)

    def test_abort_bytes_outside_the_bulk_data_are_rejected(self):
        for value in ("0", str(guest.BULK_COUNT * guest.BULK_SIZE)):
            with self.subTest(value=value), mock.patch.object(guest.shutil, "which", return_value="/bin/true"), \
                    mock.patch("sys.stderr"), self.assertRaises(SystemExit):
                self.call("--abort-bytes", value)

    def test_missing_files_are_rejected_before_any_guest(self):
        with mock.patch.object(guest.shutil, "which", return_value="/bin/true"), \
                mock.patch.object(guest.storage, "boot") as boot, mock.patch("sys.stderr"), self.assertRaises(SystemExit):
            self.call()
        boot.assert_not_called()


if __name__ == "__main__":
    unittest.main()
