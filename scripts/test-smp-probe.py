#!/usr/bin/env python3
"""Startet das SMP-Probe-ISO (-p:ZehOSSmpProbe=true) unter QEMU und wertet die seriellen SMP_PROBE-Zeilen aus (E6-D).

Die CPU-Topologie setzt ZEHOS_QEMU_SMP wie in den anderen Gastskripten (leer = eine CPU). Bestanden heisst: alle Schritte
PASS (SKIP nur bei weniger als zwei Worker-CPUs, sonst FAIL), Abschluss "SMP_PROBE PASS", die gemeldete CPU-Zahl gleich der ersten Zahl aus ZEHOS_QEMU_SMP und unter KVM
mit mindestens zwei Worker-CPUs "parallel speedup_permille" >= 1500 (unter TCG nur Information). Die Test-VM ist plattenlos.

Mit --panic (ISO mit -p:ZehOSSmpProbePanic=true, E7-B): Ein Thread schreibt alle 50 ms "SMP_PROBE tick", ein Thread auf
einer anderen CPU loest einen #PF aus. Bestanden heisst: mindestens MIN_TICKS tick-Zeilen von der Tick-CPU vor dem Fehler,
danach "System halted." und PANIC_QUIET_S Sekunden lang keine weitere tick-Zeile.
"""
import argparse
import importlib.util
import os
import re
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("thread_probe", ROOT / "scripts/test-thread-probe.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)

STEPS = ("cpus", "counters", "parallel", "migrate", "locks", "gc", "threads", "timedwait")
MARKER = re.compile(r"SMP_PROBE[ \t]+(?:(\w+)[ \t]+)?(PASS|FAIL|SKIP)(?:[ \t]+[^\r\n]*)?$", re.MULTILINE)
MIN_SPEEDUP_PERMILLE = 1500
MIN_TICKS = 5
PANIC_QUIET_S = 2.0
HALTED = "System halted."
FAULT = re.compile(r"SMP_PROBE panic fault cpu=(\d+) tick_cpu=(\d+)")
TICK = re.compile(r"SMP_PROBE tick (\d+) cpu=(\d+)")


def classify(log):
    seen = set()
    terminal = False
    for match in MARKER.finditer(log):
        step, result = match.groups()
        if result == "FAIL":
            return "FAIL"
        if step is None:
            if terminal or result != "PASS":
                return "FAIL"
            terminal = True
        elif step not in STEPS or step in seen:
            return "FAIL"
        else:
            seen.add(step)
    return "PASS" if terminal and seen == set(STEPS) else "RUNNING"


def expected_cpus(smp):
    """Erste Zahl aus ZEHOS_QEMU_SMP ("4,cores=2,threads=2" -> 4); leer heisst eine CPU."""
    match = re.match(r"\s*(\d+)", smp or "")
    return int(match.group(1)) if match else 1


def evaluate(log, accel, smp):
    """Fehlende Belege ueber classify hinaus; leer heisst bestanden."""
    missing = []
    if classify(log) != "PASS":
        missing.append(f"Probe nicht bestanden ({classify(log)})")
    cpus = re.search(r"SMP_PROBE INFO cpus count=(\d+) online=(\d+) workers=(\d+)", log)
    if not cpus:
        return missing + ["INFO cpus fehlt"]
    want = expected_cpus(smp)
    if int(cpus.group(1)) != want:
        missing.append(f"CPU-Zahl {cpus.group(1)} statt {want}")
    if int(cpus.group(2)) != want:
        missing.append(f"{cpus.group(2)} CPUs online statt {want}")
    if int(cpus.group(3)) >= 2:
        for step in re.findall(r"SMP_PROBE[ \t]+(\w+)[ \t]+SKIP", log):
            missing.append(f"Schritt {step} uebersprungen trotz {cpus.group(3)} Worker-CPUs")
    speedup = re.search(r"SMP_PROBE INFO parallel speedup_permille=(\d+)", log)
    if accel == "kvm" and int(cpus.group(3)) >= 2:
        if not speedup:
            missing.append("INFO parallel speedup_permille fehlt")
        elif int(speedup.group(1)) < MIN_SPEEDUP_PERMILLE:
            missing.append(f"speedup_permille {speedup.group(1)} unter {MIN_SPEEDUP_PERMILLE}")
    return missing


def evaluate_panic(log):
    """Fehlende Belege eines --panic-Laufs; leer heisst bestanden. Das Ruhefenster stellt run_once sicher."""
    if re.search(r"SMP_PROBE[ \t]+panic[ \t]+FAIL", log):
        return ["Probe meldet panic FAIL"]
    fault = FAULT.search(log)
    if not fault:
        return ["Fehlerzeile 'panic fault' fehlt"]
    fault_cpu, tick_cpu = int(fault.group(1)), int(fault.group(2))
    missing = []
    if fault_cpu == tick_cpu:
        missing.append(f"Fehler und Ticks auf derselben CPU {fault_cpu}")
    before = log[:fault.start()]
    ticks = TICK.findall(before)
    if len(ticks) < MIN_TICKS:
        missing.append(f"nur {len(ticks)} tick-Zeilen vor dem Fehler (mindestens {MIN_TICKS})")
    if any(int(cpu) != tick_cpu for _, cpu in ticks):
        missing.append(f"tick-Zeilen nicht nur von CPU {tick_cpu}")
    halted = log.find(HALTED, fault.end())
    if halted < 0:
        return missing + [f"'{HALTED}' fehlt nach dem Fehler"]
    if "SMP_PROBE tick" in log[halted:]:
        missing.append(f"tick-Zeile nach '{HALTED}'")
    return missing


def run_panic(args, number):
    work = ROOT / ".tools" / "smp-probe" / uuid.uuid4().hex
    work.mkdir(parents=True)
    serial = work / "serial.log"
    arguments = base.command(base.qemu_binary(), args.iso.resolve(), serial, args.accel)
    with (work / "stderr.log").open("w") as stderr:
        guest = subprocess.Popen(arguments, stdout=subprocess.DEVNULL, stderr=stderr)
    log = ""
    halted_at = None
    try:
        deadline = time.monotonic() + args.timeout
        # Nach "System halted." laeuft QEMU weiter; erst nach dem Ruhefenster wird ausgewertet.
        while guest.poll() is None and time.monotonic() < deadline:
            time.sleep(0.1)
            log = serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""
            if halted_at is None and FAULT.search(log) and HALTED in log[FAULT.search(log).end():]:
                halted_at = time.monotonic()
            if halted_at is not None and time.monotonic() - halted_at >= PANIC_QUIET_S:
                break
            if re.search(r"SMP_PROBE[ \t]+panic[ \t]+FAIL", log):
                break
        log = serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""
    finally:
        if guest.poll() is None:
            guest.kill()
            guest.wait()
    ticks = TICK.findall(log)
    for line in log.splitlines():
        if "SMP_PROBE" in line and "SMP_PROBE tick" not in line or HALTED in line:
            print("  " + line.strip(), flush=True)
    print(f"  {len(ticks)} tick-Zeilen, letzte {ticks[-1][0] if ticks else '-'}", flush=True)
    missing = evaluate_panic(log)
    if halted_at is None and not missing:
        missing.append("Ruhefenster nicht erreicht (Zeitlimit)")
    print(f"Lauf {number} {args.accel} panic smp={os.environ.get('ZEHOS_QEMU_SMP', '') or '-'}: "
          f"{'PASS' if not missing else 'FAIL ' + '; '.join(missing)} ({serial})", flush=True)
    return not missing


def run_once(args, number):
    work = ROOT / ".tools" / "smp-probe" / uuid.uuid4().hex
    work.mkdir(parents=True)
    serial = work / "serial.log"
    arguments = base.command(base.qemu_binary(), args.iso.resolve(), serial, args.accel)
    with (work / "stderr.log").open("w") as stderr:
        guest = subprocess.Popen(arguments, stdout=subprocess.DEVNULL, stderr=stderr)
    log = ""
    try:
        deadline = time.monotonic() + args.timeout
        while guest.poll() is None and time.monotonic() < deadline and classify(log) == "RUNNING":
            time.sleep(0.2)
            log = serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""
        log = serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""
    finally:
        if guest.poll() is None:
            guest.kill()
            guest.wait()
    for line in log.splitlines():
        if "SMP_PROBE" in line:
            print("  " + line.strip(), flush=True)
    missing = evaluate(log, args.accel, os.environ.get("ZEHOS_QEMU_SMP", ""))
    print(f"Lauf {number} {args.accel} smp={os.environ.get('ZEHOS_QEMU_SMP', '') or '-'}: "
          f"{'PASS' if not missing else 'FAIL ' + '; '.join(missing)} ({serial})", flush=True)
    return not missing


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--accel", choices=["kvm", "tcg"], default="kvm")
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--panic", action="store_true", help="ISO mit ZehOSSmpProbePanic: Halt aller CPUs erwartet")
    args = parser.parse_args()
    if not args.iso.is_file():
        sys.exit(f"ISO nicht gefunden: {args.iso}")
    run = run_panic if args.panic else run_once
    passed = sum(run(args, n) for n in range(1, args.runs + 1))
    print(f"SMP_PROBE {args.accel}{' panic' if args.panic else ''}: {passed}/{args.runs} bestanden", flush=True)
    return 0 if passed == args.runs else 1


if __name__ == "__main__":
    sys.exit(main())
