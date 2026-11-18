#!/usr/bin/env python3
"""Startet das SIMD-Probe-ISO (-p:ZehOSSimdProbe=true) unter QEMU und wertet die seriellen SIMD_PROBE-Zeilen aus.

Matrix (S1): --accel tcg mit --cpu qemu64 (Grundausstattung) und --cpu max; --accel kvm mit --cpu host. Exit 0 nur bei
"SIMD_PROBE PASS". Die Zeilen "INFO ... speedup_permille" sind Zeiten und kein Fehlerkriterium. Bei Absturz oder Timeout
steht die letzte "begin ..."-Stufe im Protokoll (vermutlich die Stufe, die den Gast beendet hat).
"""
import os
import argparse
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path


def qemu_binary() -> str:
    found = shutil.which("qemu-system-x86_64") or str(Path.home() / ".cosmos/tools/qemu/qemu-system-x86_64")
    if not Path(found).is_file():
        sys.exit("qemu-system-x86_64 nicht gefunden")
    return found


def classify(log: str) -> str:
    """'PASS', 'FAIL' oder 'RUNNING' nach den seriellen Zeilen."""
    if re.search(r"^SIMD_PROBE FAIL", log, re.MULTILINE) or re.search(r"SIMD_PROBE \S+.* FAIL", log):
        return "FAIL"
    return "PASS" if re.search(r"^SIMD_PROBE PASS", log, re.MULTILINE) else "RUNNING"


def last_stage(log: str) -> str:
    stages = re.findall(r"^SIMD_PROBE begin (.+)$", log, re.MULTILINE)
    return stages[-1] if stages else "(keine)"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--accel", choices=["tcg", "kvm"], default="tcg")
    parser.add_argument("--cpu", default=None, help="QEMU-CPU-Modell; Standard: qemu64 (tcg) bzw. host (kvm)")
    parser.add_argument("--timeout", type=float, default=300)
    parser.add_argument("--work", type=Path, help="NEUER Ausgabeordner (darf nicht existieren); Standard: temporaer")
    args = parser.parse_args()
    if not args.iso.is_file():
        sys.exit(f"ISO nicht gefunden: {args.iso}")
    cpu = args.cpu or ("host" if args.accel == "kvm" else "qemu64")
    if args.work:
        if args.work.exists():
            sys.exit(f"Ausgabeordner existiert bereits: {args.work}")
        args.work.mkdir(parents=True)
        work = args.work
    else:
        work = Path(tempfile.mkdtemp(prefix="simd-probe-"))
    serial = work / "serial.log"
    qemu = qemu_binary()
    share = Path(qemu).resolve().parent.parent / "share" / "qemu"
    command = [qemu, *(["-L", str(share)] if share.is_dir() else []), "-accel", args.accel, "-cpu", cpu, "-machine", "q35",
               "-m", "512M", "-vga", "std", "-boot", "d", "-cdrom", str(args.iso.resolve()), "-display", "none",
               "-serial", f"file:{serial}", "-monitor", "none", "-action", "shutdown=poweroff,reboot=reset"]
    if os.environ.get("ZEHOS_QEMU_SMP"):
        command.extend(["-smp", os.environ["ZEHOS_QEMU_SMP"]])
    print(f"QEMU: accel={args.accel} cpu={cpu}; Protokoll: {serial}", flush=True)
    with (work / "stderr.log").open("w") as stderr:
        guest = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=stderr)
    verdict = "RUNNING"
    try:
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            log = serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""
            verdict = classify(log)
            if verdict != "RUNNING" or guest.poll() is not None:
                break
            time.sleep(0.5)
        log = serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""
        verdict = classify(log)
    finally:
        if guest.poll() is None:
            guest.terminate()
            try:
                guest.wait(timeout=10)
            except subprocess.TimeoutExpired:
                guest.kill()
    for line in log.splitlines():
        if line.startswith("SIMD_PROBE"):
            print(line)
    if verdict != "PASS":
        print(f"Ergebnis {verdict}; letzte Stufe: {last_stage(log)}; QEMU-Exit: {guest.returncode}", file=sys.stderr)
        return 1
    print("SIMD-Probe bestanden.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
