#!/usr/bin/env python3
"""Arbeitet eine Liste von Gastlaeufen (Testskripte mit QEMU) im Hintergrund und parallel ab.

SUITE ist eine JSON-Liste aus {"name", "cmd", "env"?, "repeat"?, "timeout"?}. In "cmd" gelten {work} (Ordner des Laufs),
{root} (Repository-Stamm), {python} (dieser Interpreter) und die per --var NAME=WERT gesetzten Platzhalter.
Je Lauf entstehen DIR/<name>-<i>/log.txt; nach jedem Lauf werden DIR/summary.json und DIR/summary.md neu geschrieben.
  run-guest-suite.py SUITE --out DIR [--parallel N] [--var iso=PFAD ...] [--detach]
  run-guest-suite.py --status DIR      (Exit 0 alles pass, 1 Fehlschlag, 2 laeuft noch)
"""
import argparse
import concurrent.futures
import json
import os
import re
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PLACEHOLDER = re.compile(r"\{(\w+)\}")


def load_suite(path: Path) -> list[dict]:
    entries = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(entries, list):
        raise ValueError("Die Suite muss eine JSON-Liste sein")
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("name"), str) or not entry["name"]:
            raise ValueError(f"Eintrag ohne Namen: {entry!r}")
        cmd = entry.get("cmd")
        if not isinstance(cmd, list) or not cmd or not all(isinstance(part, str) for part in cmd):
            raise ValueError(f"{entry['name']}: cmd muss eine nichtleere Liste von Zeichenketten sein")
        if int(entry.get("repeat", 1)) < 1:
            raise ValueError(f"{entry['name']}: repeat muss mindestens 1 sein")
    return entries


def expand(cmd: list[str], values: dict[str, str], name: str) -> list[str]:
    missing = sorted({key for part in cmd for key in PLACEHOLDER.findall(part) if key not in values})
    if missing:
        raise ValueError(f"{name}: Platzhalter ohne Wert: {', '.join(missing)} (--var NAME=WERT)")
    return [PLACEHOLDER.sub(lambda match: values[match.group(1)], part) for part in cmd]


def plan_runs(entries: list[dict], out: Path, variables: dict[str, str]) -> list[dict]:
    runs = []
    for entry in entries:
        for index in range(1, int(entry.get("repeat", 1)) + 1):
            work = out / f"{entry['name']}-{index}"
            values = dict(variables, work=str(work), root=str(ROOT), python=sys.executable)
            runs.append({"name": entry["name"], "repeat": index, "status": "pending", "exit": None, "seconds": None,
                         "log": str(work / "log.txt"), "tail": [], "work": work,
                         "cmd": expand(entry["cmd"], values, entry["name"]),
                         "env": {key: str(value) for key, value in entry.get("env", {}).items()},
                         "timeout": float(entry.get("timeout", 3600))})
    return runs


def kill_tree(process: subprocess.Popen) -> None:
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    process.kill()
    process.wait()


def execute(run: dict) -> None:
    run["work"].mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    with open(run["log"], "wb") as log:
        try:
            process = subprocess.Popen(run["cmd"], cwd=ROOT, env=dict(os.environ, **run["env"]), stdout=log,
                                       stderr=subprocess.STDOUT, start_new_session=os.name != "nt")
        except OSError as error:
            log.write(f"Start fehlgeschlagen: {error}\n".encode())
            run["status"], run["exit"] = "fail", -1
        else:
            try:
                run["exit"] = process.wait(timeout=run["timeout"])
                run["status"] = "pass" if run["exit"] == 0 else "fail"
            except subprocess.TimeoutExpired:
                kill_tree(process)
                run["status"], run["exit"] = "timeout", process.returncode
    run["seconds"] = round(time.monotonic() - started, 1)
    if run["status"] != "pass":
        run["tail"] = Path(run["log"]).read_text(encoding="utf-8", errors="replace").splitlines()[-5:]


def write_atomic(path: Path, text: str) -> None:
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(text, encoding="utf-8")
    os.replace(temporary, path)


def write_summary(out: Path, runs: list[dict], started: float) -> None:
    counts = {status: sum(run["status"] == status for run in runs) for status in ("pending", "running", "pass", "fail", "timeout")}
    seconds = round(time.monotonic() - started, 1)
    fields = ("name", "repeat", "status", "exit", "seconds", "log", "tail")
    write_atomic(out / "summary.json", json.dumps({"counts": counts, "total": len(runs), "seconds": seconds,
                                                   "runs": [{key: run[key] for key in fields} for run in runs]},
                                                  indent=2, ensure_ascii=False))
    lines = [f"# Gastsuite: {counts['pass']}/{len(runs)} pass, {counts['fail']} fail, {counts['timeout']} timeout, "
             f"{counts['running']} running, {counts['pending']} pending ({seconds} s)", "",
             "| Lauf | Status | Exit | Dauer | Log |", "| --- | --- | --- | --- | --- |"]
    for run in runs:
        lines.append(f"| {run['name']}-{run['repeat']} | {run['status']} | {run['exit']} | {run['seconds']} | {run['log']} |")
    for run in runs:
        if run["tail"]:
            lines += ["", f"## {run['name']}-{run['repeat']} ({run['status']})", "", *[f"    {line}" for line in run["tail"]]]
    write_atomic(out / "summary.md", "\n".join(lines) + "\n")


def run_suite(runs: list[dict], out: Path, parallel: int) -> int:
    out.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    lock = threading.Lock()
    write_summary(out, runs, started)

    def task(run: dict) -> None:
        with lock:
            run["status"] = "running"
            write_summary(out, runs, started)
        execute(run)
        with lock:
            write_summary(out, runs, started)
            print(f"{run['status']:7} {run['name']}-{run['repeat']} ({run['seconds']} s)", flush=True)

    with concurrent.futures.ThreadPoolExecutor(parallel) as pool:
        list(pool.map(task, runs))
    return 0 if all(run["status"] == "pass" for run in runs) else 1


def status(out: Path) -> int:
    try:
        summary = json.loads((out / "summary.json").read_text(encoding="utf-8"))
        print((out / "summary.md").read_text(encoding="utf-8"), end="")
    except (OSError, ValueError) as error:
        print(f"Kein lesbarer Stand in {out}: {error}", file=sys.stderr)
        return 1
    counts = summary["counts"]
    if counts["pending"] or counts["running"]:
        return 2
    return 0 if counts["pass"] == summary["total"] else 1


def detach(args: argparse.Namespace) -> int:
    args.out.mkdir(parents=True, exist_ok=True)
    command = [sys.executable, str(Path(__file__).resolve()), str(args.suite), "--out", str(args.out), "--parallel", str(args.parallel)]
    for var in args.var:
        command += ["--var", var]
    windows = os.name == "nt"
    flags = (subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP) if windows else 0
    with open(args.out / "runner.log", "wb") as log:
        subprocess.Popen(command, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                         creationflags=flags, start_new_session=not windows)
    print(args.out)
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("suite", nargs="?", type=Path)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--parallel", type=int, default=min(4, (os.cpu_count() or 2) // 2 or 1))
    parser.add_argument("--var", action="append", default=[], metavar="NAME=WERT")
    parser.add_argument("--detach", action="store_true")
    parser.add_argument("--status", type=Path, metavar="DIR")
    args = parser.parse_args(argv)
    if args.status:
        return status(args.status)
    if not args.suite or not args.out:
        parser.error("SUITE und --out sind erforderlich")
    if args.parallel < 1:
        parser.error("--parallel muss mindestens 1 sein")
    variables = {}
    for var in args.var:
        name, separator, value = var.partition("=")
        if not separator or not name:
            parser.error(f"--var erwartet NAME=WERT: {var}")
        variables[name] = value
    try:
        runs = plan_runs(load_suite(args.suite), args.out, variables)
    except (OSError, ValueError) as error:
        print(f"Fehler: {error}", file=sys.stderr)
        return 2
    if args.detach:
        return detach(args)
    return run_suite(runs, args.out, args.parallel)


if __name__ == "__main__":
    sys.exit(main())
