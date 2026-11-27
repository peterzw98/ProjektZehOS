#!/usr/bin/env python3
"""Gastprobe Ladybird V3: statische Linux-Programme im ZehOS-Ring-3, Ausgabe und Status gegen die Erwartungen von LinuxAbi.

Zwei Schritte:

  scripts/test-linux-run.py bundle <buendel> <programm>...
      Packt Programme in ein Buendel (Format ZLR1: Anzahl, je Eintrag Name und Inhalt). Gebaut wird dann mit
      -p:ZehOSLinuxRunProbe=true -p:ZehOSLinuxRunBundle=<buendel>; der Kernel startet beim Boot jedes Programm mit den
      Argumenten und der Umgebung von experiments/LinuxAbi/run-linux.py und haelt danach an.

  scripts/test-linux-run.py run --iso <iso> --expected <dir> [--expected <dir>...] [--status name=status...]
      Startet QEMU, liest die LINUX_RUN-Zeilen des Seriallogs und vergleicht je Programm Ausgabe (<name>.txt) und Status
      (<name>.status) aus dem ersten Erwartungsordner, der das Programm kennt; --status gibt den Status fuer Programme ohne
      Erwartung vor (etwa spin=signal 9). Exit 1 bei Abweichung, fehlendem Programm, fehlender Schlusszeile oder Zeitlimit.

Grenze: Der Vergleich ist zeilenweise (das Terminal kennt nur Zeilen); eine Ausgabe ohne abschliessendes Zeilenende gilt als
gleich mit derselben Ausgabe mit Zeilenende.
"""

import argparse
import importlib.util
from pathlib import Path
import re
import struct
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
MAGIC = b"ZLR1"
BEGIN = re.compile(r"LINUX_RUN begin (\S+) size=(\d+)")
OUT = re.compile(r"LINUX_RUN out (.*)$")
STATUS = re.compile(r"LINUX_RUN status (\S+) (exit \d+|signal \d+|notrun)(?: \((.*)\))?\s*$")
DONE = re.compile(r"LINUX_RUN done count=(\d+)")
_probe = None


def probe():
    """Helfer aus test-user-mode-probe.py (QEMU-Start, eigene Sitzung, Beenden)."""
    global _probe
    if _probe is None:
        spec = importlib.util.spec_from_file_location("user_mode_probe", ROOT / "scripts/test-user-mode-probe.py")
        _probe = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(_probe)
    return _probe


def make_bundle(target: Path, programs: list[Path]) -> None:
    data = bytearray(MAGIC + struct.pack("<I", len(programs)))
    names = set()
    for program in programs:
        name = program.name.encode()
        if program.name in names:
            raise ValueError(f"Name doppelt: {program.name}")
        names.add(program.name)
        content = program.read_bytes()
        data += struct.pack("<I", len(name)) + name + struct.pack("<I", len(content)) + content
    target.write_bytes(bytes(data))


def parse(log: str) -> tuple[dict[str, dict], bool]:
    """Je Programm Ausgabezeilen, Status und Zusatz; dazu, ob die Schlusszeile da ist."""
    results: dict[str, dict] = {}
    current = None
    for line in log.splitlines():
        line = line.rstrip("\r")
        if m := BEGIN.search(line):
            current = results.setdefault(m.group(1), {"out": [], "status": None, "detail": ""})
        elif (m := OUT.search(line)) and current is not None:
            current["out"].append(m.group(1))
        elif m := STATUS.search(line):
            entry = results.setdefault(m.group(1), {"out": [], "status": None, "detail": ""})
            entry["status"] = m.group(2)
            entry["detail"] = m.group(3) or ""
            current = None
    return results, DONE.search(log) is not None


def expectations(dirs: list[Path], statuses: dict[str, str], names) -> dict[str, tuple[list[str] | None, str]]:
    wanted = {}
    for name in names:
        for directory in dirs:
            status = directory / f"{name}.status"
            if status.exists():
                text = (directory / f"{name}.txt").read_bytes().decode("latin-1")
                lines = text.split("\n")
                if lines and lines[-1] == "":
                    lines.pop()
                wanted[name] = (lines, status.read_text().strip())
                break
        else:
            if name in statuses:
                wanted[name] = (None, statuses[name])
    return wanted


def evaluate(results: dict[str, dict], done: bool, wanted: dict) -> list[str]:
    problems = [] if done else ["Schlusszeile LINUX_RUN done fehlt"]
    for name, (lines, status) in wanted.items():
        got = results.get(name)
        if got is None:
            problems.append(f"{name}: nicht gelaufen")
            continue
        if got["status"] != status:
            problems.append(f"{name}: Status {got['status']!r} statt {status!r} {got['detail']}".rstrip())
        if lines is not None and got["out"] != lines:
            problems.append(f"{name}: Ausgabe {got['out']!r} statt {lines!r}")
    for name in results:
        if name not in wanted:
            problems.append(f"{name}: keine Erwartung")
    return problems


def run_once(args, number: int, wanted: dict) -> bool:
    helper = probe()
    tools = helper.tools()
    work = ROOT / ".tools/linux-run" / uuid.uuid4().hex[:12]
    serial = work / "serial.log"
    vm = None
    started = time.monotonic()

    def text() -> str:
        # Der Gast schreibt Bytes (Latin-1), nicht UTF-8; Erwartung wird ebenfalls bytegleich als Latin-1 gelesen.
        return serial.read_bytes().decode("latin-1") if serial.exists() else ""

    try:
        (work / "seed").mkdir(parents=True)
        tools.create_disk(work / "seed", work / "data.qcow2")
        with helper.own_session(args.smp):
            vm = tools.start_qemu(args.iso.resolve(), work / "data.qcow2", work, tools.free_port(), args.accel, None)
        deadline = time.monotonic() + args.timeout
        while not DONE.search(text()) and time.monotonic() < deadline and vm.poll() is None:
            time.sleep(0.5)
        results, done = parse(text())
        problems = evaluate(results, done, wanted)
        for name, got in results.items():
            print(f"  {name}: {got['status']} {got['detail']} out={got['out']!r}".rstrip(), flush=True)
        verdict = "PASS" if not problems else "FAIL " + "; ".join(problems)
        print(f"Lauf {number} {args.accel} smp={args.smp}: {verdict} ({time.monotonic() - started:.0f} s, {work})", flush=True)
        return not problems
    except (RuntimeError, OSError, TimeoutError, ConnectionError, subprocess.CalledProcessError) as error:
        print(f"Lauf {number} {args.accel} smp={args.smp}: FAIL {type(error).__name__}: {error} ({work})", flush=True)
        return False
    finally:
        if vm is not None:
            helper.terminate(vm)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    bundle = sub.add_parser("bundle")
    bundle.add_argument("target", type=Path)
    bundle.add_argument("programs", type=Path, nargs="+")
    run = sub.add_parser("run")
    run.add_argument("--iso", type=Path, required=True)
    run.add_argument("--expected", type=Path, action="append", default=[])
    run.add_argument("--status", action="append", default=[], help="name=status fuer Programme ohne Erwartung")
    run.add_argument("--programs", nargs="+", required=True, help="Namen, die laufen muessen (wie im Buendel)")
    run.add_argument("--accel", choices=["kvm", "tcg", "whpx"], default="kvm")
    run.add_argument("--smp", type=int, default=1)
    run.add_argument("--runs", type=int, default=1)
    run.add_argument("--timeout", type=float, default=300)
    args = parser.parse_args(argv)
    if args.command == "bundle":
        make_bundle(args.target, args.programs)
        print(f"{args.target}: {len(args.programs)} Programme, {args.target.stat().st_size} Byte")
        return 0
    statuses = dict(item.split("=", 1) for item in args.status)
    wanted = expectations(args.expected, statuses, args.programs)
    missing = [name for name in args.programs if name not in wanted]
    if missing:
        parser.error("ohne Erwartung: " + ", ".join(missing))
    passed = sum(run_once(args, number, wanted) for number in range(1, args.runs + 1))
    print(f"LINUX_RUN {args.accel} smp={args.smp}: {passed}/{args.runs} bestanden", flush=True)
    return 0 if passed == args.runs else 1


if __name__ == "__main__":
    sys.exit(main())
