"""Tests für syscalls.py mit festen strace-Beispielzeilen (N1-C1)."""

import json
import os
import subprocess
import sys
import tempfile
import unittest

import syscalls

HIER = os.path.dirname(os.path.abspath(__file__))

HAUPT = """\
execve("/usr/bin/js", ["js", "-e", "1+1"], 0x7ffd1c0 /* 3 vars */) = 0
openat(AT_FDCWD, "/etc/ld.so.cache", O_RDONLY|O_CLOEXEC) = 3</etc/ld.so.cache>
openat(AT_FDCWD, "/proc/self/maps", O_RDONLY) = -1 ENOENT (No such file or directory)
openat(AT_FDCWD, "/sys/devices/x", O_RDONLY) = -1 ENOENT (No such file or directory)
clone3({flags=CLONE_VM|CLONE_FS|CLONE_FILES|CLONE_THREAD, exit_signal=0, stack=0x7f00, stack_size=0x7fff40} => {parent_tid=[42]}, 88) = 42
futex(0x7f0010, FUTEX_WAIT_PRIVATE, 2, NULL <unfinished ...>
--- SIGCHLD {si_signo=SIGCHLD, si_code=CLD_EXITED, si_pid=42, si_uid=0, si_status=0, si_utime=0, si_stime=0} ---
<... futex resumed>)                    = -1 EAGAIN (Resource temporarily unavailable)
futex(0x7f0010, FUTEX_WAKE_PRIVATE, 1)  = 0
getrandom("\\x01\\x02", 8, GRND_NONBLOCK) = 8
io_uring_setup(8, {flags=0}) = -1 ENOSYS (Function not implemented)
connect(5<UNIX:[1234]>, {sa_family=AF_UNIX, sun_path="/tmp/ipc.sock"}, 110) = 0
connect(6<TCP:[1->2]>, {sa_family=AF_INET, sin_port=htons(80), sin_addr=inet_addr("127.0.0.1")}, 16) = 0
read(0, "abc", 3) = ? ERESTARTSYS (To be restarted if SA_RESTART is set)
+++ exited with 0 +++
openat(AT_FDCWD, "/etc/pas"""


def werte(text, name="t.100"):
    proc = syscalls.Prozess(name)
    pfade, sockets = set(), set()
    syscalls.auswerten_zeilen(text.splitlines(True), proc, pfade, sockets)
    return proc.als_json(), pfade, sockets


class Zeilen(unittest.TestCase):
    def setUp(self):
        self.proc, self.pfade, self.sockets = werte(HAUPT)
        self.calls = self.proc["syscalls"]

    def test_name_aus_execve(self):
        self.assertEqual(self.proc["name"], "js")

    def test_openat_zaehlt_pfade_und_fehler(self):
        e = self.calls["openat"]
        self.assertEqual(e["count"], 3)  # abgeschnittene vierte Zeile zaehlt nicht
        self.assertEqual(e["errors"], {"ENOENT": 2})
        self.assertEqual(e["flags"], ["AT_FDCWD", "O_CLOEXEC", "O_RDONLY"])
        self.assertIn("/etc/ld.so.cache", self.pfade)
        self.assertIn("/proc/self/maps", self.pfade)
        self.assertNotIn("/etc/pas", self.pfade)

    def test_clone3_flags(self):
        e = self.calls["clone3"]
        self.assertEqual(e["count"], 1)
        self.assertEqual(e["flags"], ["CLONE_FILES", "CLONE_FS", "CLONE_THREAD", "CLONE_VM"])

    def test_futex_unfinished_und_resumed(self):
        e = self.calls["futex"]
        self.assertEqual(e["count"], 2)  # unfinished + resumed sind ein Aufruf
        self.assertEqual(e["errors"], {"EAGAIN": 1})
        self.assertEqual(e["flags"], ["FUTEX_WAIT_PRIVATE", "FUTEX_WAKE_PRIVATE"])

    def test_signalzeile_wird_ignoriert(self):
        self.assertEqual(sorted(self.calls), ["clone3", "connect", "execve", "futex", "getrandom",
                                              "io_uring_setup", "openat", "read"])

    def test_flags_nur_aus_argumenten(self):
        self.assertEqual(self.calls["read"]["flags"], [])  # SA_RESTART steht nur hinter dem Ergebnis

    def test_gekuerzte_pfade_werden_ignoriert(self):
        p, pfade, _ = werte('openat(AT_FDCWD, "/usr/lib/x86_64-linux-gnu/libc.so"..., O_RDONLY) = 3\n')
        self.assertEqual(p["syscalls"]["openat"]["count"], 1)
        self.assertEqual(pfade, set())

    def test_enosys(self):
        self.assertEqual(self.calls["io_uring_setup"]["errors"], {"ENOSYS": 1})

    def test_restart_fehler_mit_fragezeichen(self):
        self.assertEqual(self.calls["read"]["errors"], {"ERESTARTSYS": 1})

    def test_erfolgswert_ist_kein_fehler(self):
        self.assertEqual(self.calls["getrandom"]["errors"], {})
        self.assertEqual(self.calls["getrandom"]["flags"], ["GRND_NONBLOCK"])

    def test_sockets(self):
        self.assertEqual(self.sockets, {"AF_UNIX:/tmp/ipc.sock", "AF_INET:127.0.0.1:80", "UNIX:[1234]", "TCP:[1->2]"})

    def test_unfinished_ohne_resumed_zaehlt(self):
        p, _, _ = werte("futex(0x1, FUTEX_WAIT_PRIVATE, 0, NULL <unfinished ...>\n+++ killed by SIGKILL +++\n")
        self.assertEqual(p["syscalls"]["futex"]["count"], 1)

    def test_resumed_ohne_anfang(self):
        p, _, _ = werte("<... read resumed>\"x\", 8) = -1 EINTR (Interrupted system call)\n")
        self.assertEqual(p["syscalls"]["read"], {"count": 1, "errors": {"EINTR": 1}, "flags": []})

    def test_abgeschnittene_letzte_zeile_allein(self):
        p, pfade, _ = werte('openat(AT_FDCWD, "/etc/pa')
        self.assertEqual(p["syscalls"], {})
        self.assertEqual(pfade, set())

    def test_name_ohne_execve(self):
        p, _, _ = werte("futex(0x1, FUTEX_WAKE_PRIVATE, 1) = 0\n", "t.77")
        self.assertEqual(p["name"], "pid-77")

    def test_pid_praefix(self):
        p, _, _ = werte("[pid 12] futex(0x1, FUTEX_WAKE, 1) = 0\n")
        self.assertEqual(p["syscalls"]["futex"]["count"], 1)


class Kommandozeile(unittest.TestCase):
    def test_ausgabe_json(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, "t.10"), "w") as f:
                f.write(HAUPT)
            with open(os.path.join(d, "t.11"), "w") as f:
                f.write('execve("/bin/true", ["true"], 0x0 /* 0 vars */) = 0\n')
            with open(os.path.join(d, "notiz.txt"), "w") as f:
                f.write("kein Trace")
            out = os.path.join(d, "o.json")
            r = subprocess.run([sys.executable, os.path.join(HIER, "syscalls.py"), "--trace", d, "--out", out],
                               capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stderr)
            with open(out) as f:
                daten = json.load(f)
        self.assertEqual(set(daten), {"processes", "paths", "sockets"})
        self.assertEqual([p["name"] for p in daten["processes"]], ["js", "true"])
        self.assertEqual(daten["processes"][1]["syscalls"]["execve"]["count"], 1)

    def test_fehlendes_verzeichnis(self):
        r = subprocess.run([sys.executable, os.path.join(HIER, "syscalls.py"), "--trace", "/nicht/da",
                            "--out", "/dev/null"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 2)


if __name__ == "__main__":
    unittest.main()
