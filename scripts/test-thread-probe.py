#!/usr/bin/env python3
"""Startet das Thread-Probe-ISO (-p:ZehOSThreadProbe=true) unter QEMU und wertet die seriellen Zeilen aus.

Python-Fassung von test-thread-probe.ps1 fuer Linux-Hosts ohne pwsh (gleiche QEMU-Argumente, gleiches Timeout).
Exit 0 nur, wenn "THREAD_PROBE PASS" und "MESSAGES_PROBE PASS" im seriellen Protokoll stehen; die Nachrichtenprobe laeuft
im Kernel vor der abschliessenden THREAD_PROBE-PASS-Zeile (CosmosThreadProbe.Run). ZEHOS_QEMU_SMP setzt wie in den
anderen Gastskripten die CPU-Topologie. Die Test-VM ist plattenlos; bestehende Images werden nicht beruehrt.
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def qemu_binary() -> str:
    found = shutil.which("qemu-system-x86_64") or str(Path.home() / ".cosmos/tools/qemu/qemu-system-x86_64")
    if not Path(found).is_file():
        sys.exit("qemu-system-x86_64 nicht gefunden")
    return found


def classify(log: str) -> str:
    """'PASS', 'FAIL' oder 'RUNNING' nach den seriellen Zeilen."""
    # Nicht am Zeilenanfang verankert: Unter SMP kann eine mehrteilig geschriebene Cosmos-Zeile (etwa "[SCHED] Thread ")
    # vor der Marke stehen. Das Zeilenende bleibt verankert, damit Stufenzeilen wie "THREAD_PROBE preemption PASS ..."
    # nicht als Abschluss gelten.
    if re.search(r"(THREAD|MESSAGES)_PROBE FAIL", log):
        return "FAIL"
    if re.search(r"THREAD_PROBE PASS\r?$", log, re.MULTILINE):
        # Fehlt die Nachrichtenprobe trotz Abschlusszeile, ist der Probe-Build unvollstaendig: kein PASS.
        return "PASS" if "MESSAGES_PROBE PASS" in log else "FAIL"
    return "RUNNING"


def command(qemu: str, iso: Path, serial: Path, accel: str) -> list[str]:
    share = Path(qemu).resolve().parent.parent / "share" / "qemu"
    cpu = "host" if accel == "kvm" else "max"
    arguments = [qemu, *(["-L", str(share)] if share.is_dir() else []), "-accel", accel, "-machine", "q35", "-cpu", cpu,
                 "-m", "512M", "-vga", "std", "-boot", "d", "-cdrom", str(iso), "-display", "none",
                 "-serial", f"file:{serial}", "-monitor", "none", "-action", "shutdown=poweroff,reboot=reset"]
    if os.environ.get("ZEHOS_QEMU_SMP"):
        arguments.extend(["-smp", os.environ["ZEHOS_QEMU_SMP"]])
    return arguments


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--accel", choices=["tcg", "kvm"], default="tcg")
    parser.add_argument("--timeout", type=int, default=45, choices=range(1, 121), metavar="1..120")
    parser.add_argument("--dry-run", action="store_true", help="nur den QEMU-Aufruf ausgeben")
    args = parser.parse_args()
    if not args.iso.is_file():
        sys.exit(f"ISO nicht gefunden: {args.iso}")
    work = ROOT / ".tools/thread-probe" / uuid.uuid4().hex
    serial = work / "serial.log"
    arguments = command(qemu_binary(), args.iso.resolve(), serial, args.accel)
    if args.dry_run:
        print(" ".join(arguments))
        return 0
    work.mkdir(parents=True)
    with (work / "stderr.log").open("w") as stderr:
        guest = subprocess.Popen(arguments, stdout=subprocess.DEVNULL, stderr=stderr)
    verdict, log = "RUNNING", ""
    try:
        deadline = time.monotonic() + args.timeout
        while verdict == "RUNNING" and guest.poll() is None and time.monotonic() < deadline:
            time.sleep(0.1)
            log = serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""
            verdict = classify(log)
        log = serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""
        verdict = classify(log)
    finally:
        # Nur die eigene plattenlose Test-VM beenden; bestehende Sitzungen bleiben erhalten.
        if guest.poll() is None:
            guest.kill()
            guest.wait()
    for line in log.splitlines():
        if "THREAD_PROBE" in line or "MESSAGES_PROBE" in line:
            print(line)
    if verdict == "FAIL":
        print(f"Threadprobe fehlgeschlagen. Log: {serial}", file=sys.stderr)
        return 1
    if verdict == "RUNNING":
        print(f"Kein THREAD_PROBE PASS innerhalb von {args.timeout} s (QEMU-Exit {guest.returncode}). Log: {serial}",
              file=sys.stderr)
        return 1
    print(f"Gastprobe bestanden. Log: {serial}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
