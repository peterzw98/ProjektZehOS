#!/usr/bin/env python3
"""Misst Prozesse, Threads, PSS, VmSize und Zeit bis zum Screenshot eines headless Ladybird.

Nur Linux (/proc). Gehört nicht in den Kernel- oder Paketbau.
Exit-Codes: 0 ok, 1 Lauf ohne Screenshot ("no_screenshot") oder Start fehlgeschlagen
("start_failed"), 2 Zeitlimit ("timeout"), 64 falsche Aufrufoptionen.
Grenzen: Der Prozessbaum wird nur alle 100 ms und nur über die Wurzel abgetastet; Prozesse,
die kürzer als etwa 100 ms leben, fehlen. Nach dem Ende der Wurzel werden bereits gesehene
Nachzügler weiter gemessen.
Die "Zeit bis Screenshot" ist KEINE Zeit bis zum ersten Bild: Ladybird löst den Screenshot
über einen festen Timer (--screenshot-delay, hier 5 s) aus.
"""
import argparse
import json
import os
import pathlib
import signal
import statistics
import subprocess
import sys
import tempfile
import time

SAMPLE_INTERVAL = 0.1      # Abtastung des Prozessbaums
SHOT_POLL_INTERVAL = 0.02  # Prüfung auf den Screenshot (feiner als die Abtastung)
TERM_AFTER = 10.0          # Nachlauf nach dem Screenshot: SIGTERM
KILL_AFTER = 5.0           # danach SIGKILL


def parse_kib(text, key):
    """Liest '<key>: N kB' aus status/smaps_rollup-Text; None, wenn die Zeile fehlt."""
    for line in text.splitlines():
        if line.startswith(key + ":"):
            parts = line.split(":", 1)[1].split()
            if parts:
                return int(parts[0])
    return None


def parse_pss(text):
    return parse_kib(text, "Pss")


def _read(path):
    try:
        with open(path, "r", errors="replace") as f:
            return f.read()
    except OSError:
        return None


def children_of(pid):
    """Kindprozesse über /proc/<pid>/task/*/children (braucht CONFIG_PROC_CHILDREN)."""
    result = []
    try:
        tids = os.listdir(f"/proc/{pid}/task")
    except OSError:
        return result
    for tid in tids:
        text = _read(f"/proc/{pid}/task/{tid}/children")
        if text:
            result.extend(int(x) for x in text.split())
    return result


def proc_start(pid):
    """Startzeit (Feld 22 von /proc/<pid>/stat) oder None, wenn der Prozess fehlt oder Zombie ist.

    Zusammen mit der PID erkennt sie PID-Wiederverwendung, bevor ein Signal gesendet wird.
    """
    text = _read(f"/proc/{pid}/stat")
    if not text:
        return None
    # comm steht in Klammern und darf Leerzeichen enthalten: ab der letzten ')' zerlegen.
    rest = text[text.rfind(")") + 2:].split()
    if len(rest) < 20 or rest[0] == "Z":
        return None
    return rest[19]


PNG_SIGNATURE = bytes.fromhex("89504e470d0a1a0a")
PNG_IEND = bytes.fromhex("0000000049454e44ae426082")  # Länge 0, "IEND", feste CRC


def png_complete(path):
    """True, wenn die Datei mit PNG-Signatur beginnt und mit dem IEND-Chunk endet (kein halbes PNG)."""
    try:
        with open(path, "rb") as f:
            head = f.read(8)
            f.seek(0, os.SEEK_END)
            size = f.tell()
            if head != PNG_SIGNATURE or size < 8 + len(PNG_IEND):
                return False
            f.seek(-len(PNG_IEND), os.SEEK_END)
            return f.read() == PNG_IEND
    except OSError:
        return False


def group_members(pgid):
    """Lebende (keine Zombies) Prozesse der Prozessgruppe pgid."""
    result = []
    try:
        names = os.listdir("/proc")
    except OSError:
        return result
    for n in names:
        if not n.isdigit():
            continue
        text = _read(f"/proc/{n}/stat")
        if not text:
            continue
        rest = text[text.rfind(")") + 2:].split()
        if len(rest) > 2 and rest[0] != "Z" and rest[2] == str(pgid):
            result.append(int(n))
    return result


def kill_group(pgid, sig):
    """Signal an die Gruppe, solange sie Mitglieder hat (die Gruppen-ID kann dann nicht
    neu vergeben sein), auch nach dem Ende der Wurzel."""
    if group_members(pgid):
        try:
            os.killpg(pgid, sig)
        except OSError:
            pass


def process_tree(root_pid):
    """Alle Nachfahren von root_pid einschließlich der Wurzel (nur lebende, keine Zombies)."""
    seen = []
    stack = [root_pid]
    while stack:
        pid = stack.pop()
        if pid in seen:
            continue
        seen.append(pid)
        stack.extend(children_of(pid))
    return [p for p in seen if proc_start(p) is not None]


def sample_process(pid):
    """Einzelmessung eines Prozesses oder None, wenn er fehlt oder Zombie ist."""
    status = _read(f"/proc/{pid}/status")
    if status is None:
        return None
    state = ""
    name = ""
    for line in status.splitlines():
        if line.startswith("State:"):
            state = line.split(":", 1)[1].strip()
        elif line.startswith("Name:"):
            name = line.split(":", 1)[1].strip()
    if state.startswith("Z"):
        return None
    comm = _read(f"/proc/{pid}/comm")
    if comm:
        name = comm.strip()
    smaps = _read(f"/proc/{pid}/smaps_rollup")
    return {
        "pid": pid,
        "name": name,
        "threads": parse_kib(status, "Threads") or 0,  # Zahl, nicht kB; gleiches Zeilenformat
        "vmsize_kib": parse_kib(status, "VmSize") or 0,
        "vmrss_kib": parse_kib(status, "VmRSS") or 0,
        "pss_kib": (parse_pss(smaps) or 0) if smaps else 0,
    }


class Tracker:
    """Sammelt je Prozess die Höchstwerte und je Abtastung die Summen."""

    def __init__(self):
        self.procs = {}
        self.total_pss = 0
        self.total_threads = 0
        self.max_vmsize = 0

    def add(self, samples):
        self.total_pss = max(self.total_pss, sum(s["pss_kib"] for s in samples))
        self.total_threads = max(self.total_threads, sum(s["threads"] for s in samples))
        for s in samples:
            p = self.procs.setdefault(s["pid"], {"name": s["name"], "max_threads": 0,
                                                 "max_pss_kib": 0, "max_vmsize_kib": 0,
                                                 "max_vmrss_kib": 0})
            p["name"] = s["name"]
            p["max_threads"] = max(p["max_threads"], s["threads"])
            p["max_pss_kib"] = max(p["max_pss_kib"], s["pss_kib"])
            p["max_vmsize_kib"] = max(p["max_vmsize_kib"], s["vmsize_kib"])
            p["max_vmrss_kib"] = max(p["max_vmrss_kib"], s["vmrss_kib"])
            self.max_vmsize = max(self.max_vmsize, s["vmsize_kib"])

    def result(self):
        return {
            "processes": [dict(pid=pid, **p) for pid, p in sorted(self.procs.items())],
            "process_count": len(self.procs),
            "total_max_threads": self.total_threads,
            "total_max_pss_kib": self.total_pss,
            "max_vmsize_kib": self.max_vmsize,
        }


def _alive(pid, start):
    return start is not None and proc_start(pid) == start


def _signal_all(known, sig):
    """Signal nur an Prozesse, deren Startzeit noch zur gemerkten passt (PID-Wiederverwendung)."""
    for pid, start in known.items():
        if _alive(pid, start):
            try:
                os.kill(pid, sig)
            except OSError:
                pass


def build_command(binary, page, width, height, shot):
    return [binary, "--headless=screenshot", "--force-cpu-painting",
            "--window-width", str(width), "--window-height", str(height),
            "--screenshot-delay", "5", "--screenshot-path", shot,
            pathlib.Path(page).resolve().as_uri()]


def run_once(cmd, shot, timeout):
    """Ein Lauf. Rückgabe (Ergebnis-Dict, Fehlerart oder None)."""
    tracker = Tracker()
    start = time.monotonic()
    try:
        # Eigene Sitzung: Aufräumen trifft zusätzlich die ganze Prozessgruppe.
        proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                start_new_session=True)
    except OSError as e:
        return {"screenshot_ms": None, "error": "start_failed", "detail": str(e)}, "start_failed"
    known = {}  # pid -> Startzeit
    shot_ms = None
    error = None
    shot_time = None
    last_sample = 0.0
    term_sent = kill_sent = False
    try:
        while True:
            now = time.monotonic()
            if shot_ms is None:
                try:
                    if png_complete(shot):
                        shot_ms = round((now - start) * 1000)
                        shot_time = now
                except OSError:
                    pass
            running = proc.poll() is None
            if now - last_sample >= SAMPLE_INTERVAL:
                last_sample = now
                pids = process_tree(proc.pid) if running else []
                # Nachzügler (nach dem Ende der Wurzel an init umgehängt) weiter messen
                pids += [p for p, st in known.items() if p not in pids and _alive(p, st)]
                samples = [s for s in map(sample_process, pids) if s]
                tracker.add(samples)
                for s in samples:
                    st = proc_start(s["pid"])
                    if st is not None:  # inzwischen beendet: nicht merken
                        known.setdefault(s["pid"], st)
            stragglers = [p for p, st in known.items() if p != proc.pid and _alive(p, st)]
            if not running and not stragglers:
                break
            if shot_time is not None:
                after = now - shot_time
                if after > TERM_AFTER and not term_sent:
                    _signal_all(known, signal.SIGTERM)
                    kill_group(proc.pid, signal.SIGTERM)
                    term_sent = True
                if after > TERM_AFTER + KILL_AFTER and not kill_sent:
                    _signal_all(known, signal.SIGKILL)
                    kill_group(proc.pid, signal.SIGKILL)
                    kill_sent = True
            if now - start > timeout and shot_ms is None:
                error = "timeout"
                break
            if now - start > timeout + TERM_AFTER + KILL_AFTER + 5:
                error = "timeout"
                break
            time.sleep(SHOT_POLL_INTERVAL)
    finally:
        _signal_all(known, signal.SIGKILL)
        kill_group(proc.pid, signal.SIGKILL)  # Gruppe gehört uns (start_new_session)
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass
    if error is None and shot_ms is None:
        error = "no_screenshot"
    run = tracker.result()
    run["screenshot_ms"] = shot_ms
    if error:
        run["error"] = error
    return run, error


def median_of(runs):
    """Median über die fehlerfreien Läufe; leer, wenn es keine gibt."""
    ok = [r for r in runs if "error" not in r]
    if not ok:
        return {}
    keys = ["screenshot_ms", "process_count", "total_max_threads",
            "total_max_pss_kib", "max_vmsize_kib"]
    out = {}
    for k in keys:
        vals = [r[k] for r in ok if r.get(k) is not None]
        if vals:
            m = statistics.median(vals)
            out[k] = int(m) if float(m).is_integer() else m
    return out


def _write(a, runs):
    result = {"page": os.path.abspath(a.page), "runs": runs, "median": median_of(runs)}
    with open(a.out, "w") as f:
        json.dump(result, f, indent=2)
        f.write("\n")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    # argparse beendet sich sonst mit 2 und kollidiert mit dem Timeout-Code.
    ap.error = lambda msg: (ap.print_usage(sys.stderr),
                            print(f"{ap.prog}: Fehler: {msg}", file=sys.stderr),
                            sys.exit(64))
    ap.add_argument("--binary", required=True)
    ap.add_argument("--page", required=True)
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--width", type=int, default=798)
    ap.add_argument("--height", type=int, default=519)
    ap.add_argument("--timeout", type=float, default=120)
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)
    if a.runs < 1:
        ap.error("--runs muss mindestens 1 sein")

    runs = []
    exit_code = 0
    if not (os.path.isfile(a.binary) and os.access(a.binary, os.X_OK)):
        runs = [{"screenshot_ms": None, "error": "start_failed",
                 "detail": f"Binary nicht ausführbar oder fehlt: {a.binary}"}]
        _write(a, runs)
        print(runs[0]["detail"], file=sys.stderr)
        return 1
    with tempfile.TemporaryDirectory(prefix="ladybird-measure-") as tmp:
        for i in range(a.runs):
            shot = os.path.join(tmp, f"shot{i}.png")
            cmd = build_command(a.binary, a.page, a.width, a.height, shot)
            run, error = run_once(cmd, shot, a.timeout)
            runs.append(run)
            if error:
                exit_code = 2 if error == "timeout" else 1
                break
    _write(a, runs)
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
