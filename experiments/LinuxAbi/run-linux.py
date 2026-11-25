#!/usr/bin/env python3
"""Vergleicht die Ausgabe der Linux-Vergleichsprogramme mit den Erwartungen.

  run-linux.py --bin out/s1 --expected expected/s1 [--update] --json bericht.json

Jedes ausführbare Programm in --bin läuft einmal mit festen Argumenten, fester
Umgebung, leerem stdin und eigenem leeren Arbeitsverzeichnis. Verglichen werden
stdout+stderr (Bytes) und der Status (`exit <n>` oder `signal <n>`) mit
<expected>/<name>.txt und <name>.status. Danach läuft jedes Programm ein
zweites Mal unter `strace -f -c`; die dort gezählten Systemaufrufe landen im
JSON-Bericht (sie gehören nicht zum Vergleich). Exit 0: alles gleich, Exit 1:
mindestens eine Abweichung (auch fehlende Erwartung oder fehlendes Programm),
Exit 2: falsche Benutzung. --update schreibt die Erwartungen neu und ist nur auf
Linux erlaubt.
"""
import argparse
import json
import os
import platform
import re
import resource
import shutil
import signal
import subprocess
import sys
import tempfile

ARGS = ["a1", "b 2"]
ENV = {"ZEHOS_ABI": "1", "LANG": "C", "PATH": "/usr/bin:/bin"}
TIMEOUT = 60
STRACE_PROBLEMS = []  # Meldungen, wenn strace nichts liefert; landen im Bericht (kein Vergleichsfehler)


def status_text(returncode):
    if returncode < 0:
        return "signal %d" % -returncode
    return "exit %d" % returncode


def _child_setup():
    os.umask(0o022)
    # Kein Coredump: spart Zeit und Platz bei den gewollten Abstürzen.
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))


def run_program(path):
    """Startet ein Programm in einem frischen Verzeichnis; liefert (Ausgabe, Status)."""
    with tempfile.TemporaryDirectory(prefix="linuxabi-") as work:
        try:
            p = subprocess.Popen(
                [path] + ARGS, cwd=work, env=ENV, stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                preexec_fn=_child_setup, start_new_session=True)
        except OSError as e:
            return ("Start fehlgeschlagen: %s\n" % e).encode(), "error"
        try:
            out, _ = p.communicate(timeout=TIMEOUT)
        except subprocess.TimeoutExpired:
            # Eigene Sitzung: auch Kindprozesse des Programms beenden.
            try:
                os.killpg(p.pid, signal.SIGKILL)
            except OSError:
                pass
            p.communicate()
            return b"", "timeout"
        return out, status_text(p.returncode)


def strace_calls(path):
    """Systemaufrufe (Name -> Anzahl) aus `strace -f -c`, oder None ohne strace."""
    if shutil.which("strace") is None:
        return None
    with tempfile.TemporaryDirectory(prefix="linuxabi-st-") as work:
        out = os.path.join(work, "strace.txt")
        run_dir = os.path.join(work, "cwd")
        os.mkdir(run_dir)
        try:
            r = subprocess.run(["strace", "-f", "-c", "-o", out, "--", path] + ARGS, cwd=run_dir, env=ENV,
                               stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                               timeout=TIMEOUT, preexec_fn=_child_setup, start_new_session=True)
            with open(out, encoding="utf-8", errors="replace") as f:
                text = f.read()
        except (subprocess.TimeoutExpired, OSError) as e:
            STRACE_PROBLEMS.append("strace für %s nicht auswertbar: %s" % (os.path.basename(path), e))
            return None
        names = {}
        for line in text.splitlines():
            parts = line.split()
            # Zeile: % time, seconds, usecs/call, calls, [errors,] syscall
            if len(parts) >= 5 and re.match(r"^[0-9.]+$", parts[0]) and parts[-1] != "total" and parts[3].isdigit():
                names[parts[-1]] = int(parts[3])
        if not names:
            # Leere Tabelle: meist verbotenes ptrace (Container) oder strace-Fehler.
            err = r.stderr.decode("utf-8", "replace").strip().splitlines()
            STRACE_PROBLEMS.append("strace für %s lieferte keine Aufrufe (Exit %d): %s" % (
                os.path.basename(path), r.returncode, err[0] if err else "keine Meldung"))
            return None
        return names


def list_programs(bindir):
    names = []
    for n in sorted(os.listdir(bindir)):
        p = os.path.join(bindir, n)
        if os.path.isfile(p) and os.access(p, os.X_OK):
            names.append(n)
    return names


def read_expected(expdir, name):
    t = os.path.join(expdir, name + ".txt")
    s = os.path.join(expdir, name + ".status")
    if not (os.path.isfile(t) and os.path.isfile(s)):
        return None
    with open(t, "rb") as f:
        text = f.read()
    with open(s, encoding="utf-8") as f:
        status = f.read().strip()
    return text, status


def first_diff(a, b):
    la, lb = a.decode("utf-8", "replace").splitlines(), b.decode("utf-8", "replace").splitlines()
    for i in range(max(len(la), len(lb))):
        x = la[i] if i < len(la) else "<fehlt>"
        y = lb[i] if i < len(lb) else "<fehlt>"
        if x != y:
            return "Zeile %d: erwartet %r, erhalten %r" % (i + 1, x, y)
    return "Unterschied in Zeilenenden oder Kodierung"


def compare(bindir, expdir, update, calls=strace_calls, runner=run_program):
    """Liefert (Bericht, ok)."""
    programs = list_programs(bindir)
    report = {"kernel": platform.release(), "programs": {}, "syscalls": {}, "problems": []}
    ok = True
    if update:
        os.makedirs(expdir, exist_ok=True)
    del STRACE_PROBLEMS[:]
    for name in programs:
        path = os.path.abspath(os.path.join(bindir, name))
        output, status = runner(path)
        entry = {"status": status, "result": "ok"}
        if update and (status in ("timeout", "error") or output.startswith(b"NEED fehlgeschlagen")):
            # Fehllauf nie als Erwartung speichern.
            entry["result"] = "failed-run"
            report["problems"].append("%s: Lauf fehlgeschlagen (%s), Erwartung nicht geschrieben" % (name, status))
        elif update:
            with open(os.path.join(expdir, name + ".txt"), "wb") as f:
                f.write(output)
            with open(os.path.join(expdir, name + ".status"), "w", encoding="utf-8", newline="\n") as f:
                f.write(status + "\n")
            entry["result"] = "updated"
        else:
            exp = read_expected(expdir, name)
            if exp is None:
                entry["result"] = "missing-expectation"
                report["problems"].append("%s: Erwartung fehlt (%s.txt/.status)" % (name, name))
            else:
                if exp[1] != status:
                    entry["result"] = "wrong-status"
                    entry["expected_status"] = exp[1]
                    report["problems"].append("%s: Status %s, erwartet %s" % (name, status, exp[1]))
                if exp[0] != output:
                    entry["result"] = "wrong-output" if entry["result"] == "ok" else entry["result"] + "+wrong-output"
                    entry["diff"] = first_diff(exp[0], output)
                    report["problems"].append("%s: Ausgabe weicht ab (%s)" % (name, entry["diff"]))
        if entry["result"] not in ("ok", "updated"):
            ok = False
        c = calls(path)
        entry["syscalls"] = None if c is None else sorted(c)
        for k, v in (c or {}).items():
            report["syscalls"][k] = report["syscalls"].get(k, 0) + v
        report["programs"][name] = entry
    if os.path.isdir(expdir) and not update:
        known = set(programs)
        for f in sorted(os.listdir(expdir)):
            base, ext = os.path.splitext(f)
            if ext in (".txt", ".status") and base not in known:
                ok = False
                report["problems"].append("%s: Erwartung ohne Programm in %s" % (base, bindir))
    if not programs:
        ok = False
        report["problems"].append("keine Programme in %s" % bindir)
    report["strace_warnings"] = list(STRACE_PROBLEMS)
    report["syscalls"] = dict(sorted(report["syscalls"].items()))
    report["ok"] = ok
    return report, ok


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bin", required=True, help="Ordner mit den gebauten Programmen")
    ap.add_argument("--expected", required=True, help="Ordner mit <name>.txt und <name>.status")
    ap.add_argument("--update", action="store_true", help="Erwartungen neu schreiben (nur Linux)")
    ap.add_argument("--json", required=True, help="Bericht (JSON)")
    a = ap.parse_args(argv)
    if not os.path.isdir(a.bin):
        print("run-linux: --bin %s ist kein Ordner (build.sh ausgeführt?)" % a.bin, file=sys.stderr)
        return 2
    if a.update and platform.system() != "Linux":
        print("run-linux: --update nur auf Linux", file=sys.stderr)
        return 2
    report, ok = compare(a.bin, a.expected, a.update)
    with open(a.json, "w", encoding="utf-8", newline="\n") as f:
        json.dump(report, f, indent=2, sort_keys=True)
        f.write("\n")
    for n, e in report["programs"].items():
        print("%-18s %-10s %s" % (n, e["status"], e["result"]))
    for w in report["strace_warnings"]:
        print("WARNUNG:", w, file=sys.stderr)
    for p in report["problems"]:
        print("ABWEICHUNG:", p, file=sys.stderr)
    print("%d Programme, %d verschiedene Systemaufrufe, %s" % (
        len(report["programs"]), len(report["syscalls"]), "gleich" if ok else "ABWEICHUNG"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
