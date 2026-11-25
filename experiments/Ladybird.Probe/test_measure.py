"""Tests für measure.py; laufen ohne Ladybird (Attrappen). Aufruf: python3 -m unittest"""
import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest

HIER = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HIER)
import measure  # noqa: E402


def _load(p):
    with open(p) as f:
        return json.load(f)


class MeasureTests(unittest.TestCase):
    def test_tree_collects_children(self):
        # Ohne "wait" ersetzt sh sich per exec durch das letzte sleep; dann wären es nur 2.
        p = subprocess.Popen(["sh", "-c", "sleep 5 & sleep 5; wait"])
        try:
            deadline = time.monotonic() + 3
            tree = []
            while time.monotonic() < deadline:
                tree = measure.process_tree(p.pid)
                if len(tree) == 3:
                    break
                time.sleep(0.05)
            self.assertEqual(len(tree), 3, tree)
            self.assertEqual(tree[0], p.pid)
            sample = measure.sample_process(p.pid)
            self.assertGreaterEqual(sample["threads"], 1)
            self.assertGreater(sample["vmsize_kib"], 0)
        finally:
            for pid in measure.process_tree(p.pid):
                try:
                    os.kill(pid, signal.SIGKILL)
                except OSError:
                    pass
            p.wait()

    def test_pss_parsed(self):
        text = ("55d0c0a00000-7ffd5b3ff000 ---p 00000000 00:00 0   [rollup]\n"
                "Rss:                2048 kB\n"
                "Pss:                1234 kB\n"
                "Pss_Anon:            900 kB\n")
        self.assertEqual(measure.parse_pss(text), 1234)
        self.assertIsNone(measure.parse_pss("Rss: 1 kB\n"))

    def test_median_of_three(self):
        runs = [{"screenshot_ms": 100}, {"screenshot_ms": 300}, {"screenshot_ms": 200}]
        self.assertEqual(measure.median_of(runs)["screenshot_ms"], 200)

    def test_timeout_exit_code(self):
        with tempfile.TemporaryDirectory() as tmp:
            fake = os.path.join(tmp, "fake-ladybird")
            with open(fake, "w") as f:
                f.write("#!/bin/sh\nexec sleep 30\n")
            os.chmod(fake, 0o755)
            out = os.path.join(tmp, "out.json")
            page = os.path.join(HIER, "pages", "static.html")
            t0 = time.monotonic()
            code = measure.main(["--binary", fake, "--page", page, "--runs", "1",
                                 "--timeout", "1", "--out", out])
            self.assertLess(time.monotonic() - t0, 10)
            self.assertEqual(code, 2)
            with open(out) as f:
                data = json.load(f)
            self.assertEqual(data["runs"][0]["error"], "timeout")

    def test_tracker_sums_per_instant(self):
        tr = measure.Tracker()
        mk = lambda pid, pss: {"pid": pid, "name": "x", "threads": 1, "vmsize_kib": 10,
                               "vmrss_kib": 1, "pss_kib": pss}
        tr.add([mk(1, 100), mk(2, 10)])
        tr.add([mk(1, 10), mk(2, 100)])
        # Summe der Maxima wäre 200; gleichzeitig waren es höchstens 110.
        self.assertEqual(tr.result()["total_max_pss_kib"], 110)
        self.assertEqual(tr.result()["process_count"], 2)

    def test_alive_false_for_dead_or_unknown_start(self):
        self.assertFalse(measure._alive(999999, None))
        self.assertFalse(measure._alive(999999, "1"))
        p = subprocess.Popen(["sleep", "5"])
        try:
            self.assertTrue(measure._alive(p.pid, measure.proc_start(p.pid)))
            self.assertFalse(measure._alive(p.pid, None))
        finally:
            p.kill()
            p.wait()

    def test_signal_skips_reused_pid(self):
        p = subprocess.Popen(["sleep", "5"])
        try:
            measure._signal_all({p.pid: "0"}, signal.SIGKILL)  # falsche Startzeit
            time.sleep(0.2)
            self.assertIsNone(p.poll())
            measure._signal_all({p.pid: measure.proc_start(p.pid)}, signal.SIGKILL)
            self.assertEqual(p.wait(timeout=3), -signal.SIGKILL)
        finally:
            p.kill()
            p.wait()

    def _fake(self, tmp, body):
        fake = os.path.join(tmp, "fake")
        with open(fake, "w") as f:
            f.write("#!/bin/sh\n" + body)
        os.chmod(fake, 0o755)
        return fake

    def test_run_success_and_no_screenshot(self):
        page = os.path.join(HIER, "pages", "static.html")
        with tempfile.TemporaryDirectory() as tmp:
            ok = self._fake(tmp, 'while [ "$1" != --screenshot-path ]; do shift; done\n'
                                 'sleep 0.3; echo x > "$2"; sleep 0.3\n')
            out = os.path.join(tmp, "o.json")
            self.assertEqual(measure.main(["--binary", ok, "--page", page, "--runs", "2",
                                           "--out", out]), 0)
            data = _load(out)
            self.assertEqual(len(data["runs"]), 2)
            self.assertGreater(data["median"]["screenshot_ms"], 0)
            none = self._fake(tmp, "sleep 0.3\n")
            self.assertEqual(measure.main(["--binary", none, "--page", page, "--runs", "1",
                                           "--out", out]), 1)
            self.assertEqual(_load(out)["runs"][0]["error"], "no_screenshot")

    def test_missing_binary_and_bad_runs(self):
        page = os.path.join(HIER, "pages", "static.html")
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "o.json")
            self.assertEqual(measure.main(["--binary", os.path.join(tmp, "nein"), "--page", page,
                                           "--out", out]), 1)
            self.assertEqual(_load(out)["runs"][0]["error"], "start_failed")
            with self.assertRaises(SystemExit) as c:
                measure.main(["--binary", "x", "--page", page, "--runs", "0", "--out", out])
            self.assertEqual(c.exception.code, 64)


if __name__ == "__main__":
    unittest.main()
