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


if __name__ == "__main__":
    unittest.main()
