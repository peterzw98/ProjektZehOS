from pathlib import Path
import importlib.util
import json
import subprocess
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
CASES = json.loads((ROOT / "scripts/power_cases.json").read_text(encoding="utf-8"))["cases"]
# Gleiche Liste wie PowerFaultProbe.Points im Fehlerprobe-Build; ein Tippfehler im Fall liesse powerfault scheitern.
FAULT_POINTS = {"efi-sync", "efi-unmount", "root-sync", "root-flush", "reopen", "remount"}


def load_script():
    spec = importlib.util.spec_from_file_location("power_guest", ROOT / "scripts/test-power-guest.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class PowerCaseDataTests(unittest.TestCase):
    def test_ids_are_unique(self):
        ids = [case["id"] for case in CASES]
        self.assertEqual(len(ids), len(set(ids)))

    def test_fault_cases_name_known_points_and_both_files(self):
        faults = [case for case in CASES if "fault" in case]
        self.assertEqual(["P4a", "P4b", "P4c", "P4d", "P4e", "P4f"], [case["id"] for case in faults])
        for case in faults:
            with self.subTest(case=case["id"]):
                self.assertTrue(set(case["fault"].split()) <= FAULT_POINTS)
                self.assertEqual("root", case["disk"])
                self.assertNotEqual(case["write"], case["writeAfterFault"])

    def test_reopen_only_after_an_earlier_fault_and_then_locked(self):
        # "reopen" greift nur in einer Ruecknahme; allein geschaltet bliebe er wirkungslos (lead2, bb28345).
        for case in CASES:
            points = case.get("fault", "").split()
            if "reopen" in points:
                self.assertGreater(len(points), 1, case["id"])
                self.assertTrue(case["rootLockedAfterFault"], case["id"])
                self.assertNotIn("s_state-running", case["checks"], case["id"])


class ExpectRootFileTests(unittest.TestCase):
    def setUp(self):
        self.script = load_script()
        self.image = Path(tempfile.gettempdir()) / "unused-root.img"

    def debugfs(self, stdout):
        return mock.patch.object(self.script.subprocess, "run",
                                 return_value=subprocess.CompletedProcess([], 0, stdout=stdout, stderr=""))

    def test_locked_write_must_be_absent(self):
        with self.debugfs("P4e-nach-power-ok"):
            with self.assertRaises(RuntimeError):
                self.script.expect_root_file(self.image, "/home/tester/p4e-nach.txt", None)
        with self.debugfs(""):
            self.script.expect_root_file(self.image, "/home/tester/p4e-nach.txt", None)

    def test_written_text_must_match(self):
        with self.debugfs("anderes"):
            with self.assertRaises(RuntimeError):
                self.script.expect_root_file(self.image, "/home/tester/p4a-nach.txt", "P4a-nach-power-ok")
        with self.debugfs("P4a-nach-power-ok\n"):
            self.script.expect_root_file(self.image, "/home/tester/p4a-nach.txt", "P4a-nach-power-ok")


if __name__ == "__main__":
    unittest.main()
