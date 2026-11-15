#!/usr/bin/env python3
"""Startet ZehOS in VMware Workstation (Linux) wiederholt per Hardware-Reset und zaehlt das Ergebnis der PS/2-Tastaturbindung.

Die VM bootet nur vom ISO, ohne Datenlaufwerk und ohne Netz. Nach jedem Start wartet das Skript auf die serielle Zeile
"ps2:kbd offer Ps2KeyboardDriver -> ..." und loest danach "vmrun reset hard" aus. Am Ende steht je Ergebnis die Anzahl;
der Exitcode ist 1, wenn mindestens eine Bindung nicht "bound" war. Der Arbeitsordner (Standard .tools/vmware-ps2-reset)
wird bei jedem Lauf neu angelegt; serial.log bleibt fuer die Auswertung erhalten.
"""

import argparse
import collections
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OFFER = re.compile(r"ps2:kbd offer Ps2KeyboardDriver -> ([^\r\n]*)")
# Ein Start bis zur Tastaturbindung dauert in VMware wenige Sekunden; die Frist faengt haengende Starts ab.
BOOT_TIMEOUT = 120

VMX = """.encoding = "UTF-8"
config.version = "8"
virtualHW.version = "21"
displayName = "ZehOS PS2 Reset"
guestOS = "other-64"
memsize = "1024"
numvcpus = "1"
firmware = "efi"
efi.secureBoot.enabled = "FALSE"
ide1:0.present = "TRUE"
ide1:0.deviceType = "cdrom-image"
ide1:0.fileName = "{iso}"
ide1:0.startConnected = "TRUE"
floppy0.present = "FALSE"
ethernet0.present = "FALSE"
sound.present = "FALSE"
serial0.present = "TRUE"
serial0.fileType = "file"
serial0.fileName = "{serial}"
serial0.startConnected = "TRUE"
msg.autoAnswer = "TRUE"
"""


def vmrun(*arguments: str) -> None:
    subprocess.run(["vmrun", "-T", "ws", *arguments], check=True, stdout=subprocess.DEVNULL)


def wait_for_offer(serial: Path, seen: int) -> str | None:
    """Liefert das Ergebnis der naechsten noch nicht gezaehlten Bindung oder None nach der Frist."""
    deadline = time.monotonic() + BOOT_TIMEOUT
    while time.monotonic() < deadline:
        if serial.is_file():
            offers = OFFER.findall(serial.read_text(encoding="utf-8", errors="replace"))
            if len(offers) > seen:
                return offers[seen].strip()
        time.sleep(1)
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--runs", type=int, default=50, help="Anzahl der Starts (erster Start plus Resets)")
    parser.add_argument("--work", type=Path, default=ROOT / ".tools" / "vmware-ps2-reset")
    args = parser.parse_args()
    if shutil.which("vmrun") is None:
        sys.exit("vmrun nicht gefunden (VMware Workstation unter Linux)")

    work = args.work.resolve()
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True)
    serial = work / "serial.log"
    vmx = work / "ps2-reset.vmx"
    vmx.write_text(VMX.format(iso=args.iso.resolve(), serial=serial), encoding="utf-8")

    results: collections.Counter[str] = collections.Counter()
    vmrun("start", str(vmx), "nogui")
    try:
        for run in range(args.runs):
            result = wait_for_offer(serial, run)
            if result is None:
                results["kein Ergebnis (Frist abgelaufen)"] += 1
                print(f"Start {run + 1}: kein Ergebnis", flush=True)
                break
            results[result] += 1
            print(f"Start {run + 1}: {result}", flush=True)
            if run + 1 < args.runs:
                vmrun("reset", str(vmx), "hard")
    except (subprocess.CalledProcessError, KeyboardInterrupt) as error:
        print(f"Abbruch: {error}", file=sys.stderr)
    # Die VM wird auch nach einem Fehler beendet, damit kein Testgast weiterlaeuft.
    subprocess.run(["vmrun", "-T", "ws", "stop", str(vmx), "hard"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    for result, count in results.most_common():
        print(f"{count:4d}  {result}")
    print(f"Protokoll: {serial}")
    return 0 if set(results) == {"bound"} else 1


if __name__ == "__main__":
    sys.exit(main())
