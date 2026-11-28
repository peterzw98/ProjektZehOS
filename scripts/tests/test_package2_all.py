import contextlib
import importlib.util
import io
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "test-package2-all.py"
spec = importlib.util.spec_from_file_location("package2_all", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Package2AllTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.dir = Path(self.temp.name)
        self.key = self.dir / "private.pem"
        self.key.write_text("test", encoding="utf-8")
        self.iso = self.dir / "z.iso"
        self.iso.write_bytes(b"iso")

    def main(self, *argv, results=None):
        calls = []

        def fake_run(cmd, log, env=None, timeout=None):
            probe = Path(str(cmd[2])).name
            calls.append((probe, env.get("ZEHOS_QEMU_SMP"), [str(c) for c in cmd]))
            rc = (results or {}).get((probe, env.get("ZEHOS_QEMU_SMP")), 0)
            marker = next(m for s, m in module.PROBES.values() if Path(s).name == probe)
            Path(log).write_text(f"{marker} {'PASS' if rc == 0 else 'FAIL kaputt'}\n", encoding="utf-8")
            return rc

        out = io.StringIO()
        with mock.patch.object(module, "run", fake_run), mock.patch.object(module, "GUEST_WORK", self.dir / "gw"), \
                contextlib.redirect_stdout(out):
            rc = module.main(["--key", str(self.key), "--iso", str(self.iso), "--work", str(self.dir / "w"), *argv])
        return rc, calls, out.getvalue()

    def test_configs(self):
        self.assertEqual(module.parse_configs(["kvm:1", "tcg:4"]), [("kvm", 1), ("tcg", 4)])
        for bad in ("kvm", "xen:1", "kvm:0"):
            with self.assertRaises(ValueError):
                module.parse_configs([bad])

    def test_all_pass_sets_smp_per_stage(self):
        rc, calls, out = self.main("--configs", "kvm:1", "kvm:4", "--ld-musl", str(self.key))
        self.assertEqual(rc, 0, out)
        self.assertEqual([(p.split("-", 2)[2], smp) for p, smp, _ in calls],
                         [("guest.py", "1"), ("dyn-guest.py", "1"), ("upgrade-live-guest.py", "1"),
                          ("guest.py", "4"), ("dyn-guest.py", "4"), ("upgrade-live-guest.py", "4")])
        self.assertIn("6/6 Laeufe PASS", out)
        self.assertTrue(all("--accel" in c and c[c.index("--accel") + 1] == "kvm" for _, _, c in calls))

    def test_dyn_without_musl_is_skipped_not_passed(self):
        rc, calls, out = self.main("--configs", "kvm:4")
        self.assertEqual(rc, 0, out)
        self.assertNotIn("test-package2-dyn-guest.py", [p for p, _, _ in calls])
        self.assertIn("| dyn | - |", out)
        self.assertIn("entfaellt dyn kvm4: --ld-musl fehlt", out)
        self.assertIn("2/2 Laeufe PASS", out)

    def test_failure_gives_exit_1_with_reason(self):
        rc, _, out = self.main("--configs", "kvm:1", "--probes", "live",
                               results={("test-package2-upgrade-live-guest.py", "1"): 1})
        self.assertEqual(rc, 1)
        self.assertIn("FAIL live kvm1: PACKAGE2_UPGRADE_LIVE FAIL kaputt", out)

    def test_only_skipped_runs_is_not_success(self):
        rc, _, out = self.main("--configs", "kvm:1", "--probes", "dyn")
        self.assertEqual(rc, 1, out)

    def test_live_sleep_passed_through(self):
        _, calls, _ = self.main("--configs", "tcg:1", "--probes", "live", "--live-sleep", "600")
        cmd = calls[0][2]
        self.assertEqual(cmd[cmd.index("--sleep") + 1], "600")
        self.assertEqual(cmd[cmd.index("--accel") + 1], "tcg")

    def test_run_kills_process_group_on_timeout(self):
        log = self.dir / "t.log"
        pidfile = self.dir / "grandchild.pid"
        # Das Kind startet einen Enkel (wie die Probe QEMU) und haengt; nach dem Zeitlimit darf der Enkel nicht weiterleben.
        code = ("import subprocess,sys,time;p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)']);"
                f"open({str(pidfile)!r},'w').write(str(p.pid));time.sleep(60)")
        rc = module.run([sys.executable, "-c", code], log, env=dict(os.environ), timeout=2)
        self.assertEqual(rc, 124)
        pid = int(pidfile.read_text())
        with self.assertRaises(ProcessLookupError):
            for _ in range(50):
                os.kill(pid, 0)
                import time
                time.sleep(0.1)
        self.assertIn("Zeitlimit 2 s", log.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
