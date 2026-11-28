import contextlib
import importlib.util
import io
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "test-package2-all.py"
spec = importlib.util.spec_from_file_location("package2_all", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def alive(pid):
    """Lebt der Prozess? Ein Zombie zaehlt als beendet: In einem Container ohne init raeumt ihn niemand ab, und
    os.kill(pid, 0) meldete ihn weiter als lebend."""
    try:
        stat = Path(f"/proc/{pid}/stat").read_text()
    except FileNotFoundError:
        return False
    except OSError:
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        return True
    return stat.rsplit(")", 1)[1].split()[0] not in ("Z", "X")


def wait_dead(pid, seconds=5.0):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if not alive(pid):
            return True
        time.sleep(0.1)
    return False


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
        self.assertTrue(wait_dead(pid), f"Enkel {pid} lebt nach dem Zeitlimit weiter")
        self.assertIn("Zeitlimit 2 s", log.read_text(encoding="utf-8"))

    def test_drop_images_only_own_dir(self):
        gw = self.dir / "gw"
        gw.mkdir()
        old = gw / "alt"
        (old / "vm").mkdir(parents=True)
        (old / "vm/data.qcow2").write_bytes(b"x")
        with mock.patch.object(module, "GUEST_WORK", gw):
            before = module.work_dirs()
            own, foreign = gw / "live-eigen", gw / "live-fremd"
            for d in (own, foreign):
                (d / "vm").mkdir(parents=True)
                (d / "vm/data.qcow2").write_bytes(b"x")
                (d / "data.raw").write_bytes(b"x")
                (d / "serial.log").write_text("log", encoding="utf-8")
            log = self.dir / "live.log"
            log.write_text(f"PACKAGE2_UPGRADE_LIVE PASS; Protokolle: {own}\n", encoding="utf-8")
            left = module.drop_images(before, log)
            self.assertFalse((own / "vm/data.qcow2").exists())
            self.assertFalse((own / "data.raw").exists())
            self.assertTrue((own / "serial.log").exists())
            self.assertTrue((foreign / "vm/data.qcow2").exists())
            self.assertTrue((foreign / "data.raw").exists())
            self.assertTrue((old / "vm/data.qcow2").exists())
            self.assertEqual(left, [foreign])
            # Ohne Abschlusszeile (Zeitlimit) wird nichts geloescht, beide neuen Ordner werden genannt.
            log.write_text("haengt\n", encoding="utf-8")
            self.assertEqual(module.drop_images(before, log), [own, foreign])
            self.assertTrue((foreign / "data.raw").exists())

    def test_sigterm_kills_guest_and_prints_table(self):
        pidfile = self.dir / "qemu.pid"
        probe = self.dir / "probe.py"
        # Falsche Probe: startet einen "QEMU"-Enkel und haengt, wie eine Probe mit laufendem Gast.
        probe.write_text("import subprocess,sys,time\n"
                         "p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(120)'])\n"
                         f"open({str(pidfile)!r},'w').write(str(p.pid))\ntime.sleep(120)\n", encoding="utf-8")
        driver = ("import importlib.util,sys\n"
                  f"spec=importlib.util.spec_from_file_location('p2a',{str(SCRIPT)!r})\n"
                  "m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)\n"
                  f"m.PROBES['guest']=({str(probe)!r},'PACKAGE2_GUEST')\n"
                  f"m.GUEST_WORK=m.Path({str(self.dir / 'gw')!r})\n"
                  f"sys.exit(m.main(['--key',{str(self.key)!r},'--iso',{str(self.iso)!r},'--work',{str(self.dir / 'w')!r},"
                  "'--configs','kvm:1','--probes','guest']))\n")
        proc = subprocess.Popen([sys.executable, "-c", driver], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        self.addCleanup(lambda: proc.poll() is None and proc.kill())
        end = time.monotonic() + 20
        while not (pidfile.exists() and pidfile.read_text()) and time.monotonic() < end:
            time.sleep(0.05)
        pid = int(pidfile.read_text())
        self.assertTrue(alive(pid))
        proc.send_signal(signal.SIGTERM)
        out, _ = proc.communicate(timeout=20)
        self.assertEqual(proc.returncode, 128 + signal.SIGTERM, out)
        self.assertTrue(wait_dead(pid), f"QEMU-Enkel {pid} lebt nach SIGTERM weiter")
        self.assertIn("Abbruch durch Signal 15", out)
        self.assertIn("| guest |", out)


if __name__ == "__main__":
    unittest.main()
