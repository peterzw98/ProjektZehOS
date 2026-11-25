#!/usr/bin/env python3
"""Misst Prozesse, Threads, PSS, VmSize und Zeit bis zum Screenshot eines headless Ladybird.

Nur Linux (/proc). Gehört nicht in den Kernel- oder Paketbau.
Die "Zeit bis Screenshot" ist KEINE Zeit bis zum ersten Bild: Ladybird löst den Screenshot
über einen festen Timer (--screenshot-delay, hier 5 s) aus.
"""
import argparse
import json
import os
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
    return [p for p in seen if sample_process(p) is not None]


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


def _alive(pid):
    return sample_process(pid) is not None


def _signal_all(pids, sig):
    for pid in pids:
        try:
            os.kill(pid, sig)
        except OSError:
            pass


def build_command(binary, page, width, height, shot):
    return [binary, "--headless=screenshot", "--force-cpu-painting",
            "--window-width", str(width), "--window-height", str(height),
            "--screenshot-delay", "5", "--screenshot-path", shot,
            "file://" + os.path.abspath(page)]


def run_once(cmd, shot, timeout):
    """Ein Lauf. Rückgabe (Ergebnis-Dict, Fehlerart oder None)."""
    tracker = Tracker()
    start = time.monotonic()
    proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    known = {proc.pid}
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
                    if os.path.getsize(shot) > 0:
                        shot_ms = round((now - start) * 1000)
                        shot_time = now
                except OSError:
                    pass
            if now - last_sample >= SAMPLE_INTERVAL:
                last_sample = now
                tree = process_tree(proc.pid) if proc.poll() is None else []
                samples = [s for s in map(sample_process, tree) if s]
                tracker.add(samples)
                known.update(s["pid"] for s in samples)
            running = proc.poll() is None
            # Nachzügler, die nach dem Ende der Wurzel noch leben (an init umgehängt)
            stragglers = [p for p in known if p != proc.pid and _alive(p)]
            if not running and not stragglers:
                break
            if shot_time is not None:
                after = now - shot_time
                if after > TERM_AFTER and not term_sent:
                    _signal_all([p for p in known if _alive(p)], signal.SIGTERM)
                    term_sent = True
                if after > TERM_AFTER + KILL_AFTER and not kill_sent:
                    _signal_all([p for p in known if _alive(p)], signal.SIGKILL)
                    kill_sent = True
            if now - start > timeout and shot_ms is None:
                error = "timeout"
                break
            if now - start > timeout + TERM_AFTER + KILL_AFTER + 5:
                error = "timeout"
                break
            time.sleep(SHOT_POLL_INTERVAL)
    finally:
        if proc.poll() is None or any(_alive(p) for p in known if p != proc.pid):
            _signal_all([p for p in known if _alive(p)], signal.SIGKILL)
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


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--binary", required=True)
    ap.add_argument("--page", required=True)
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--width", type=int, default=798)
    ap.add_argument("--height", type=int, default=519)
    ap.add_argument("--timeout", type=float, default=120)
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)

    runs = []
    exit_code = 0
    with tempfile.TemporaryDirectory(prefix="ladybird-measure-") as tmp:
        for i in range(a.runs):
            shot = os.path.join(tmp, f"shot{i}.png")
            cmd = build_command(a.binary, a.page, a.width, a.height, shot)
            run, error = run_once(cmd, shot, a.timeout)
            runs.append(run)
            if error:
                exit_code = 2 if error == "timeout" else 1
                break
    result = {"page": os.path.abspath(a.page), "runs": runs, "median": median_of(runs)}
    with open(a.out, "w") as f:
        json.dump(result, f, indent=2)
        f.write("\n")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
