import importlib.util
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


build = load("wasi_testsuite_build", "experiments/WasiTestsuite/build.py")
guest = load("wasi_testsuite_guest", "scripts/test-wasi-testsuite.py")
tools = load("package_guest_tools", "scripts/test-wasm-doom-desktop.py")
package = load("build_package_tool", "scripts/build-package.py")


class BuildTests(unittest.TestCase):
    def test_sources_are_pinned(self):
        self.assertRegex(build.COMMIT, r"^[0-9a-f]{40}$")
        for name, digest in build.FILES.items():
            self.assertRegex(digest, r"^[0-9a-f]{64}$", name)
        self.assertIn("LICENSE", build.FILES)

    def test_every_test_has_valid_package_identity(self):
        names = build.tests()
        self.assertEqual(14, len(names))
        for test in names:
            self.assertRegex(build.package_id(test), package.ID, test)
            self.assertTrue(package.valid_path(f"{test}.wasm"), test)
            self.assertLessEqual(len(f"WT {test}"), 32, test)
        self.assertEqual(len(names), len({build.package_id(test) for test in names}))

    def test_out_of_scope_entries_are_tests(self):
        self.assertLessEqual(set(build.OUT_OF_SCOPE), set(build.tests()))

    def test_fixture_paths_are_valid_package_paths(self):
        for name in build.FILES:
            if name.startswith(build.FIXTURE_DIR + "/"):
                self.assertTrue(package.valid_path(name[len(build.FIXTURE_DIR) + 1:]), name)


class OutcomeTests(unittest.TestCase):
    def outcome(self, content, start=0):
        return guest.outcome(content, "wasits-lseek", start, tools.find_marker)

    def test_pass_on_regular_end(self):
        content = "x\nAPP wasits-lseek beendet\nWASM Speicher frei=1 KiB\n"
        result = self.outcome(content)
        self.assertEqual("PASS", result[0])
        self.assertEqual(content.index("\nWASM"), result[2])

    def test_fail_with_message(self):
        content = "APP wasits-lseek Ende: App beendet (Exitcode 134): Assertion failed\n"
        self.assertEqual(("FAIL", "App beendet (Exitcode 134): Assertion failed"),
                         self.outcome(content)[:2])

    def test_incomplete_failure_line_waits(self):
        self.assertIsNone(self.outcome("APP wasits-lseek Ende: App abge"))

    def test_other_package_and_earlier_lines_are_ignored(self):
        content = "APP wasits-lseek beendet\nAPP wasits-stat-dev-ino Ende: x\n"
        self.assertIsNone(self.outcome(content, start=len("APP wasits-lseek beendet\n")))

    def test_split_marker_is_found(self):
        content = "APP wasits-ls\n[LowLevelMonitor] x\neek beendet\n"
        self.assertEqual("PASS", self.outcome(content)[0])

    def test_table_marks_out_of_scope(self):
        entries = [{"test": "lseek", "scope": "in", "reason": ""},
                   {"test": "sock_shutdown-not_sock", "scope": "out", "reason": "Sockets (sock_*)"}]
        text = guest.table(entries, {"lseek": ("PASS", ""), "sock_shutdown-not_sock": ("FAIL", "a|b")})
        self.assertIn("| `lseek` | ja | PASS |", text)
        self.assertIn("| `sock_shutdown-not_sock` | nein: Sockets (sock_*) | AUSSERHALB (FAIL) | a/b |", text)
        self.assertTrue(re.search(r"nicht gelaufen", guest.table(entries[:1], {})))


if __name__ == "__main__":
    unittest.main()
