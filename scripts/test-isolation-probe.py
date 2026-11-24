#!/usr/bin/env python3
"""Gastprobe Roadmap 12.4-I1: CPU-Ausnahmen im Wasm-Worker beenden nur den Lauf, das System bleibt bedienbar.

Braucht ein ISO mit -p:ZehOSIsolationProbe=true (Erwartung: #DE und #PF isoliert, GC und ein weiterer Lauf, danach
Anmeldung und Textshell) oder mit -p:ZehOSIsolationProbeNegative=true und --negative (Erwartung: dieselbe Ausnahme im
Probe-Thread haelt das System wie bisher an) oder mit -p:ZehOSIsolationProbeNmi=true und --nmi (Erwartung: ein per QMP
ausgeloester NMI waehrend Gastcode des Workers haelt an, statt isoliert zu werden). Jeder Lauf nutzt eine frische
Wegwerfplatte unter .tools/isolation-probe/.
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

MARKERS = re.compile(r"[^\r\n]*(?:ISOLATION_PROBE|CPU EXCEPTION|WASM CPU-Ausnahme|Interrupt Vector|ZehOS: Thread)[^\r\n]*")
# I2-C: erlaubter Rueckgang des freien Speichers zwischen frei_vor (nach zwei Desktopbildern und dem Lauf base) und
# frei_nach. Bis E6-G (#523) bleiben die beiden aufgegebenen 8-MiB-Worker-Stacks belegt (16 MiB), dazu 4 MiB Reserve.
# Blieben die 2 x 16 MiB der Arena liegen, waeren es 48 MiB (nach E6-G 32 MiB); beides liegt ueber der Grenze.
ARENA_MAX_DROP_KIB = 20 * 1024
# Der gehaltene Block muss frei sichtbar senken (16 MiB; Reserve fuer den Ersatzstack im zweiten Lauf nach oben).
ARENA_MIN_HELD_KIB = 12 * 1024
# Erwarteter Vektor je Probe, wie ihn der Cosmos-Ausnahme-Hook meldet.
VECTORS = {"de": 0, "pf": 14}
NMI_WINDOW = "ISOLATION_PROBE nmi window open"


def parse_args(argv=None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--accel", choices=["kvm", "tcg", "whpx"], default="kvm")
    parser.add_argument("--runs", type=int, default=1)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--negative", action="store_true", help="ISO mit ZehOSIsolationProbeNegative: Halt erwartet")
    mode.add_argument("--nmi", action="store_true", help="ISO mit ZehOSIsolationProbeNmi: NMI per QMP, Halt erwartet")
    return parser.parse_args(argv)


def evaluate(log: str, negative: bool, nmi: bool = False) -> list[str]:
    """Fehlende Belege eines Laufs; leer heisst bestanden (ohne die Anmeldung, die run() selbst prueft)."""
    missing = []
    for kind, vector in VECTORS.items():
        handled = re.search(rf"ISOLATION_PROBE fault={kind} handled=1 vector=(-?\d+)", log)
        if not handled:
            missing.append(f"{kind} nicht isoliert")
        elif int(handled.group(1)) != vector:
            missing.append(f"{kind} mit Vektor {handled.group(1)} statt {vector} (IRQContext-Versatz?)")
    if log.count("WASM CPU-Ausnahme im App-Code") < 2:
        missing.append("Hauptschleife hat nicht beide Laeufe beendet")
    # I2-C: Jeder Fehlerlauf hielt 16 MiB in der Laufarena; PollFaultIsolation gibt sie frei (Zaehler der Arena 0 und
    # der freie Speicher sinkt um hoechstens ARENA_MAX_DROP_KIB).
    held = [int(m.group(1)) for m in re.finditer(r"ISOLATION_PROBE arena=1 used=[1-9]\d* frei=(\d+)", log)]
    if len(held) != 2:
        missing.append("Arena nicht in beiden Fehlerlaeufen gestartet")
    for kind in VECTORS:
        if not re.search(rf"ISOLATION_PROBE arena_after={kind} used=0\b", log):
            missing.append(f"Arena nach {kind} nicht freigegeben")
    before = re.search(r"ISOLATION_PROBE frei_vor=(\d+)", log)
    after = re.search(r"ISOLATION_PROBE frei_nach=(\d+)", log)
    if not before or not after or int(before.group(1)) - int(after.group(1)) > ARENA_MAX_DROP_KIB:
        missing.append("Arena nach den CPU-Fehlern nicht freigegeben (frei_vor/frei_nach)")
    elif any(int(before.group(1)) - value < ARENA_MIN_HELD_KIB for value in held):
        missing.append("Arenablock senkt frei nicht (Messung ohne Aussage)")
    for text in ("ISOLATION_PROBE gc ok", "ISOLATION_PROBE none returned", "ISOLATION_PROBE DONE"):
        if text not in log:
            missing.append(text + " fehlt")
    if negative:
        tail = log.split("ISOLATION_PROBE negative pf on probe thread", 1)
        if len(tail) < 2 or "CPU EXCEPTION" not in tail[1] or "NOT HALTED" in tail[1]:
            missing.append("Gegenprobe hielt nicht an")
    if nmi:
        missing += evaluate_nmi(log)
    return missing


def evaluate_nmi(log: str) -> list[str]:
    """Der NMI muss den Worker im Gastcode treffen und das System anhalten, ohne dass die Hauptschleife isoliert."""
    tail = log.split(NMI_WINDOW, 1)
    if len(tail) < 2:
        return ["NMI-Fenster nicht geoeffnet"]
    after = tail[1]
    if "Interrupt Vector: 2" not in after:
        return ["kein NMI (Vektor 2) im Log"]
    missing = []
    target = re.search(r"ZehOS: Thread (-?\d+), Wasm-Worker (-?\d+), Gastcode (\d)", after.split("Interrupt Vector: 2", 1)[1])
    if not target or target.group(1) != target.group(2) or target.group(3) != "1":
        missing.append("NMI traf nicht den Worker im Gastcode (Gegenprobe ohne Aussage, wiederholen)")
    if "WASM CPU-Ausnahme im App-Code" in after:
        missing.append("NMI wurde isoliert statt anzuhalten")
    if "nmi window closed" in after:
        missing.append("Worker lief nach dem NMI weiter")
    return missing


def run(args: argparse.Namespace, number: int) -> bool:
    work = ROOT / ".tools/isolation-probe" / uuid.uuid4().hex[:12]
    (work / "seed").mkdir(parents=True)
    tools.create_disk(work / "seed", work / "data.qcow2")
    port = tools.free_port()
    vm = tools.start_qemu(args.iso.resolve(), work / "data.qcow2", work, port, args.accel, None)
    guest = tools.Guest(work, port, "", time.monotonic())
    serial = work / "serial.log"

    def text() -> str:
        return serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""

    try:
        # Nach einer isolierten Ausnahme laeuft das System weiter ("Thread abandoned."); erst DONE oder TIMEOUT beendet das Warten.
        end = ("ISOLATION_PROBE negative pf on probe thread" if args.negative
               else NMI_WINDOW if args.nmi else "ISOLATION_PROBE DONE")
        started = time.monotonic()
        while time.monotonic() - started < 300 and end not in text() and "TIMEOUT" not in text():
            time.sleep(1)
        if args.nmi and NMI_WINDOW in text():
            time.sleep(2)
            qmp = tools.Qmp(port)
            qmp.command("inject-nmi")
        if args.negative or args.nmi:
            time.sleep(10)
        log = text()
        missing = evaluate(log, args.negative, args.nmi)
        if not missing and not args.negative and not args.nmi:
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
    print(f"ISOLATION {args.accel} negative={args.negative} nmi={args.nmi}: {passed}/{args.runs} bestanden", flush=True)
    return 0 if passed == args.runs else 1


if __name__ == "__main__":
    sys.exit(main())
