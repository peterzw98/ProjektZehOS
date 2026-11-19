import importlib.util
import json
import sys
import tempfile
import time
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "run-guest-suite.py"
spec = importlib.util.spec_from_file_location("run_guest_suite", SCRIPT)
suite_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(suite_module)

PY = sys.executable


def entry(name, code, **extra):
    return dict({"name": name, "cmd": [PY, "-c", code]}, **extra)


class RunGuestSuiteTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.dir = Path(self.temp.name)
        self.out = self.dir / "out"

    def run_suite(self, entries, *extra):
        path = self.dir / "suite.json"
        path.write_text(json.dumps(entries), encoding="utf-8")
        code = suite_module.main([str(path), "--out", str(self.out), *extra])
        return code

    def summary(self):
        return json.loads((self.out / "summary.json").read_text(encoding="utf-8"))

    def test_placeholders_and_missing_variable(self):
        values = {"work": "W", "root": "R", "iso": "I"}
        self.assertEqual(suite_module.expand(["a", "{work}/{iso}", "{root}"], values, "x"), ["a", "W/I", "R"])
        with self.assertRaisesRegex(ValueError, "nope"):
            suite_module.expand(["{nope}"], values, "x")

    def test_missing_variable_fails_before_start(self):
        code = self.run_suite([entry("a", "print(1)")] + [{"name": "b", "cmd": [PY, "{iso}"]}])
        self.assertEqual(code, 2)
        self.assertFalse(self.out.exists())

    def test_invalid_suite_is_error(self):
        self.assertEqual(self.run_suite([{"name": "a"}]), 2)

    def test_var_and_work_substituted(self):
        code = self.run_suite([{"name": "a", "cmd": [PY, "-c", "import sys; print(sys.argv[1:])", "{iso}", "{work}"]}],
                              "--var", "iso=X.iso")
        self.assertEqual(code, 0)
        log = (self.out / "a-1" / "log.txt").read_text(encoding="utf-8")
        self.assertIn("X.iso", log)
        self.assertIn("a-1", log)

    def test_repeat_pass_fail_and_summary(self):
        code = self.run_suite([entry("ok", "print('hi')", repeat=2),
                               entry("bad", "import sys; print('l1'); print('boom'); sys.exit(3)")])
        self.assertEqual(code, 1)
        data = self.summary()
        self.assertEqual(data["total"], 3)
        self.assertEqual(data["counts"]["pass"], 2)
        self.assertEqual(data["counts"]["fail"], 1)
        runs = {(run["name"], run["repeat"]): run for run in data["runs"]}
        self.assertEqual(set(runs), {("ok", 1), ("ok", 2), ("bad", 1)})
        self.assertEqual(runs[("bad", 1)]["exit"], 3)
        self.assertEqual(runs[("bad", 1)]["tail"], ["l1", "boom"])
        self.assertEqual(runs[("ok", 1)]["tail"], [])
        self.assertTrue((self.out / "ok-2" / "log.txt").is_file())
        self.assertIn("bad-1", (self.out / "summary.md").read_text(encoding="utf-8"))

    def test_environment_passed(self):
        code = self.run_suite([entry("e", "import os,sys; sys.exit(0 if os.environ['ZEHOS_X']=='7' else 1)", env={"ZEHOS_X": "7"})])
        self.assertEqual(code, 0)

    def test_timeout_kills_child(self):
        marker = self.dir / "marker"
        code = ("import os,subprocess,sys,time;"
                "subprocess.Popen([sys.executable,'-c','import os,time;time.sleep(3);open(os.environ[\"M\"],\"w\").close()']);"
                "time.sleep(60)")
        started = time.monotonic()
        result = self.run_suite([entry("slow", code, timeout=1, env={"M": str(marker)})])
        self.assertEqual(result, 1)
        self.assertLess(time.monotonic() - started, 30)
        self.assertEqual(self.summary()["runs"][0]["status"], "timeout")
        time.sleep(4)
        self.assertFalse(marker.exists(), "Kindprozess lief nach dem Timeout weiter")

    def test_parallel_limit(self):
        code = ("import os,time,pathlib;d=pathlib.Path(os.environ['D']);"
                "m=d/f'{os.getpid()}';m.write_text('x');time.sleep(0.6);"
                "n=len(list(d.iterdir()));m.unlink();open(d.parent/f'seen{os.getpid()}','w').write(str(n))")
        track = self.dir / "track"
        track.mkdir()
        self.run_suite([entry("p", code, repeat=6, env={"D": str(track)})], "--parallel", "2")
        seen = [int(path.read_text()) for path in self.dir.glob("seen*")]
        self.assertEqual(len(seen), 6)
        self.assertLessEqual(max(seen), 2)
        self.assertEqual(max(seen), 2)

    def test_status_exit_codes(self):
        def write(counts, total):
            self.out.mkdir(exist_ok=True)
            (self.out / "summary.json").write_text(json.dumps({"counts": counts, "total": total}))
            (self.out / "summary.md").write_text("md\n")
        zero = {"pending": 0, "running": 0, "pass": 0, "fail": 0, "timeout": 0}
        write(dict(zero, **{"pass": 2}), 2)
        self.assertEqual(suite_module.main(["--status", str(self.out)]), 0)
        write(dict(zero, **{"pass": 1, "fail": 1}), 2)
        self.assertEqual(suite_module.main(["--status", str(self.out)]), 1)
        write(dict(zero, **{"pass": 1, "running": 1}), 2)
        self.assertEqual(suite_module.main(["--status", str(self.out)]), 2)
        write(dict(zero, **{"pass": 1, "pending": 1}), 2)
        self.assertEqual(suite_module.main(["--status", str(self.out)]), 2)
        self.assertEqual(suite_module.main(["--status", str(self.dir / "none")]), 1)

    def test_detach_returns_and_finishes(self):
        path = self.dir / "suite.json"
        path.write_text(json.dumps([entry("d", "import time; time.sleep(1)")]), encoding="utf-8")
        started = time.monotonic()
        self.assertEqual(suite_module.main([str(path), "--out", str(self.out), "--detach"]), 0)
        self.assertLess(time.monotonic() - started, 0.9)
        for _ in range(60):
            time.sleep(0.5)
            if (self.out / "summary.json").exists() and self.summary()["counts"]["pass"] == 1:
                break
        self.assertEqual(self.summary()["counts"]["pass"], 1)
        self.assertTrue((self.out / "runner.log").is_file())


if __name__ == "__main__":
    unittest.main()
