#!/usr/bin/env python3
"""Gastprobe Roadmap 12.4-I2-A/B: Schliessen beendet eine Paket-App nach 5 s auch ohne should_exit.

Braucht ein ISO mit -p:ZehOSAbortProbe=true. Erwartung: Fall 1 (fragt should_exit) endet Ok in unter 1 s, Fall 2
(fragt nie, ruft sleep_ms) und Fall 3 (Schleife ganz ohne Aufruf) enden Aborted nach 5 bis 6 s, je mit der Zeile der
Hauptschleife; danach ist der Worker frei und ein vierter Lauf endet Ok; anschliessend Anmeldung und Textshell. Jeder Lauf nutzt eine frische Wegwerfplatte unter .tools/abort-probe/.
"""

import argparse
import importlib.util
from pathlib import Path
import re
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)

MARKERS = re.compile(r"[^\r\n]*(?:ABORT_PROBE|WASM Abbruch|CPU EXCEPTION)[^\r\n]*")
MAIN_LOOP_LINE = "WASM Abbruch: App reagierte 5 s nicht auf das Schliessen"


def parse_args(argv=None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--accel", choices=["kvm", "tcg", "whpx"], default="kvm")
    parser.add_argument("--runs", type=int, default=1)
    return parser.parse_args(argv)


def evaluate(log: str) -> list[str]:
    """Fehlende Belege eines Laufs; leer heisst bestanden (ohne die Anmeldung, die run() selbst prueft)."""
    missing = []
    first = re.search(r"ABORT_PROBE case=1 status=(\w+) ms=(-?\d+)", log)
    if not first or first.group(1) != "Ok" or not 0 <= int(first.group(2)) < 1000:
        missing.append("Fall 1 endete nicht Ok in unter 1 s")
    for case in (2, 3):
        aborted = re.search(rf"ABORT_PROBE case={case} status=(\w+) ms=(-?\d+)", log)
        if not aborted or aborted.group(1) != "Aborted" or not 5000 <= int(aborted.group(2)) < 6000:
            missing.append(f"Fall {case} endete nicht Aborted nach 5 bis 6 s")
    if log.count(MAIN_LOOP_LINE) != 2:
        missing.append("Abbruchzeile der Hauptschleife nicht genau zweimal")
    if "ABORT_PROBE idle=1" not in log:
        missing.append("Worker nach dem Abbruch nicht frei")
    if not re.search(r"ABORT_PROBE case=4 status=Ok result=7", log):
        missing.append("vierter Lauf nicht Ok")
    if "ABORT_PROBE PASS" not in log:
        missing.append("ABORT_PROBE PASS fehlt")
    if "CPU EXCEPTION" in log:
        missing.append("CPU-Ausnahme im Lauf")
    return missing


def run(args: argparse.Namespace, number: int) -> bool:
    work = ROOT / ".tools/abort-probe" / uuid.uuid4().hex[:12]
    (work / "seed").mkdir(parents=True)
    tools.create_disk(work / "seed", work / "data.qcow2")
    port = tools.free_port()
    vm = tools.start_qemu(args.iso.resolve(), work / "data.qcow2", work, port, args.accel, None)
    guest = tools.Guest(work, port, "", time.monotonic())
    serial = work / "serial.log"

    def text() -> str:
        return serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""

    try:
        started = time.monotonic()
        while (time.monotonic() - started < 300 and "ABORT_PROBE PASS" not in text()
               and "ABORT_PROBE FAIL" not in text()):
            time.sleep(1)
        log = text()
        missing = evaluate(log)
        if not missing:
            guest.wait("Desktop ist bereit", 120)
            guest.connect()
            guest.login(first=True)
            guest.shell()
        for line in MARKERS.findall(log):
            print("  " + line.strip(), flush=True)
        print(f"Lauf {number} {args.accel}: {'PASS' if not missing else 'FAIL ' + '; '.join(missing)} ({work})", flush=True)
        return not missing
    except (RuntimeError, TimeoutError, ConnectionError) as error:
        print(f"Lauf {number} {args.accel}: FAIL {type(error).__name__}: {error} ({work})", flush=True)
        return False
    finally:
        if vm.poll() is None:
            vm.kill()
            vm.wait()


def main() -> int:
    args = parse_args()
    passed = sum(run(args, number) for number in range(1, args.runs + 1))
    print(f"ABORT {args.accel}: {passed}/{args.runs} bestanden", flush=True)
    return 0 if passed == args.runs else 1


if __name__ == "__main__":
    sys.exit(main())
