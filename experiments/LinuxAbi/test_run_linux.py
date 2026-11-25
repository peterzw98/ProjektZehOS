#!/usr/bin/env python3
"""Tests des Vergleichers run-linux.py (python3 -m unittest, im Ordner LinuxAbi).

Die Tests brauchen weder musl noch strace: Programme sind kleine Shell-Skripte,
die Aufrufliste wird durch eine Attrappe ersetzt."""
import importlib.util
import json
import os
import platform
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("run_linux", os.path.join(HERE, "run-linux.py"))
rl = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rl)

def slurp(path, mode="r"):
    with open(path, mode) as f:
        return f.read()


NO_CALLS = lambda path: {"write": 1}


@unittest.skipUnless(platform.system() == "Linux", "Vergleicher läuft nur auf Linux")
class CompareTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.bin = os.path.join(self.tmp.name, "bin")
        self.exp = os.path.join(self.tmp.name, "exp")
        os.mkdir(self.bin)
        os.mkdir(self.exp)

    def program(self, name, script):
        p = os.path.join(self.bin, name)
        with open(p, "w") as f:
            f.write("#!/bin/sh\n" + script + "\n")
        os.chmod(p, os.stat(p).st_mode | stat.S_IXUSR)

    def expect(self, name, text, status="exit 0"):
        with open(os.path.join(self.exp, name + ".txt"), "wb") as f:
            f.write(text)
        with open(os.path.join(self.exp, name + ".status"), "w") as f:
            f.write(status + "\n")

    def run_compare(self, update=False):
        return rl.compare(self.bin, self.exp, update, calls=NO_CALLS)

    def test_equal(self):
        self.program("p", 'echo "hallo $1"')
        self.expect("p", b"hallo a1\n")
        report, ok = self.run_compare()
        self.assertTrue(ok)
        self.assertEqual(report["programs"]["p"]["result"], "ok")
        self.assertEqual(report["syscalls"], {"write": 1})

    def test_other_output(self):
        self.program("p", "echo anders")
        self.expect("p", b"hallo\n")
        report, ok = self.run_compare()
        self.assertFalse(ok)
        self.assertEqual(report["programs"]["p"]["result"], "wrong-output")
        self.assertIn("Zeile 1", report["programs"]["p"]["diff"])

    def test_wrong_status(self):
        self.program("p", "echo x; exit 3")
        self.expect("p", b"x\n", "exit 0")
        report, ok = self.run_compare()
        self.assertFalse(ok)
        self.assertEqual(report["programs"]["p"]["result"], "wrong-status")
        self.assertEqual(report["programs"]["p"]["status"], "exit 3")

    def test_signal_status(self):
        self.program("p", "echo x; kill -SEGV $$")
        self.expect("p", b"x\n", "signal 11")
        _, ok = self.run_compare()
        self.assertTrue(ok)

    def test_missing_expectation(self):
        self.program("p", "echo x")
        report, ok = self.run_compare()
        self.assertFalse(ok)
        self.assertEqual(report["programs"]["p"]["result"], "missing-expectation")

    def test_expectation_without_program(self):
        self.program("p", "echo x")
        self.expect("p", b"x\n")
        self.expect("alt", b"y\n")
        report, ok = self.run_compare()
        self.assertFalse(ok)
        self.assertTrue(any(s.startswith("alt:") for s in report["problems"]))

    def test_no_programs(self):
        _, ok = self.run_compare()
        self.assertFalse(ok)

    def test_stderr_counts_and_environment_is_fixed(self):
        self.program("p", 'echo "$ZEHOS_ABI|$2" >&2; pwd | grep -c linuxabi-; umask')
        self.expect("p", b"1|b 2\n1\n0022\n")
        _, ok = self.run_compare()
        self.assertTrue(ok)

    def test_update_writes_and_then_passes(self):
        self.program("p", "echo x; kill -SEGV $$")
        _, ok = self.run_compare(update=True)
        self.assertTrue(ok)
        self.assertEqual(slurp(os.path.join(self.exp, "p.status")), "signal 11\n")
        self.assertEqual(slurp(os.path.join(self.exp, "p.txt"), "rb"), b"x\n")
        _, ok = self.run_compare()
        self.assertTrue(ok)

    def test_update_refuses_failed_runs(self):
        self.program("neu", "echo 'NEED fehlgeschlagen: x Zeile 1 errno=0'; exit 2")
        self.program("haengt", "sleep 5")
        old = rl.TIMEOUT
        rl.TIMEOUT = 0.3
        self.addCleanup(setattr, rl, "TIMEOUT", old)
        report, ok = self.run_compare(update=True)
        self.assertFalse(ok)
        self.assertEqual(os.listdir(self.exp), [])
        self.assertEqual(report["programs"]["neu"]["result"], "failed-run")

    def test_timeout_kills_children(self):
        mark = os.path.join(self.tmp.name, "kind")
        self.program("p", "(sleep 1; echo x > %s) & sleep 5" % mark)
        self.expect("p", b"")
        old = rl.TIMEOUT
        rl.TIMEOUT = 0.3
        self.addCleanup(setattr, rl, "TIMEOUT", old)
        self.run_compare()
        import time
        time.sleep(1.5)
        self.assertFalse(os.path.exists(mark))

    def test_strace_warning_is_reported(self):
        self.program("p", "echo x")
        self.expect("p", b"x\n")
        def leer(path):
            rl.STRACE_PROBLEMS.append("leer")
            return None
        report, ok = rl.compare(self.bin, self.exp, False, calls=leer)
        self.assertTrue(ok)
        self.assertEqual(report["strace_warnings"], ["leer"])
        self.assertIsNone(report["programs"]["p"]["syscalls"])

    def test_timeout_is_a_deviation(self):
        self.program("p", "sleep 5")
        self.expect("p", b"")
        old = rl.TIMEOUT
        rl.TIMEOUT = 0.3
        self.addCleanup(setattr, rl, "TIMEOUT", old)
        report, ok = self.run_compare()
        self.assertFalse(ok)
        self.assertEqual(report["programs"]["p"]["status"], "timeout")


@unittest.skipUnless(platform.system() == "Linux", "Vergleicher läuft nur auf Linux")
@unittest.skipUnless(shutil.which("sh"), "sh fehlt")
class BuildScriptTests(unittest.TestCase):
    def test_rejects_bad_set_names(self):
        with tempfile.TemporaryDirectory() as t:
            os.makedirs(os.path.join(t, "src", "s1"))
            keep = os.path.join(t, "keep")
            os.mkdir(keep)
            shutil.copy(os.path.join(HERE, "build.sh"), t)
            for bad in ("../keep", "..", "a/b"):
                r = subprocess.run(["sh", os.path.join(t, "build.sh"), bad], env=dict(os.environ, CC="true"),
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                self.assertEqual(r.returncode, 1, bad)
                self.assertIn("ungültiger Satzname", r.stderr)
            self.assertTrue(os.path.isdir(keep))


@unittest.skipUnless(platform.system() == "Linux", "Vergleicher läuft nur auf Linux")
class CliTests(unittest.TestCase):
    def cli(self, *args):
        return subprocess.run([sys.executable, os.path.join(HERE, "run-linux.py")] + list(args),
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def test_exit_codes_and_json(self):
        with tempfile.TemporaryDirectory() as t:
            b, e, j = os.path.join(t, "b"), os.path.join(t, "e"), os.path.join(t, "r.json")
            os.mkdir(b)
            p = os.path.join(b, "p")
            with open(p, "w") as f:
                f.write("#!/bin/sh\necho x\n")
            os.chmod(p, 0o700)
            r = self.cli("--bin", b, "--expected", e, "--json", j)
            self.assertEqual(r.returncode, 1)
            self.assertIn("Erwartung fehlt", r.stderr)
            self.assertFalse(json.loads(slurp(j))["ok"])
            self.assertEqual(self.cli("--bin", b, "--expected", e, "--update", "--json", j).returncode, 0)
            self.assertEqual(self.cli("--bin", b, "--expected", e, "--json", j).returncode, 0)
            self.assertTrue(json.loads(slurp(j))["ok"])
            self.assertEqual(self.cli("--bin", os.path.join(t, "fehlt"), "--expected", e, "--json", j).returncode, 2)


if __name__ == "__main__":
    unittest.main()
