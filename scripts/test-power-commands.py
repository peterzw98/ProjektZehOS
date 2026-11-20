#!/usr/bin/env python3
"""Gastabnahme der Befehle reboot und shutdown fuer Linux/KVM (Gegenstueck zu scripts/test-power-commands.ps1).

Ablauf wie in der ps1: Gast bis zur Textshell starten, "reboot" tippen, die Shell muss in derselben VM ein zweites Mal
bereit sein (zweite Zeile "Textshell ist bereit."), die VM laeuft weiter; danach "shutdown": QEMU endet mit Exitcode 0.
Der Gast hat keine Datenplatte und damit keine Anmeldung (Desktop und Shell starten ohne Konto). SMP wie bei den anderen
Gastskripten ueber ZEHOS_QEMU_SMP (start_qemu liest die Variable).

Lastfall (E7-C): --load stoesst vor dem reboot zwei busy-Hintergrundaufgaben ("tasks start busy") an. Doom-Last
geht hier nicht: ohne Datenplatte ist das Konto gesperrt, ein Paketinstall also nicht moeglich (offen, braucht
Root-/Datenplatte). Der zweite Start wird ohne Last auf Shell und sauberes shutdown geprueft.
--runs N wiederholt den Lauf, jeder in einem neuen Unterordner von --work.

Exitcodes: 0 = alle Laeufe bestanden, 1 = mindestens ein Lauf gescheitert.
"""
import argparse
import importlib.util
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def load(name: str, file: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


power = load("power_guest", "test-power-guest.py")
storage, tools = power.storage, power.tools
READY = "Textshell ist bereit."
REBOOT_TIMEOUT = 60


def count(run: Path, text: str) -> int:
    log = run / "serial.log"
    return log.read_text(encoding="utf-8", errors="replace").count(text) if log.exists() else 0


def wait_count(guest, text: str, wanted: int, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while count(guest.run, text) < wanted:
        if guest.process.poll() is not None:
            raise RuntimeError(f"QEMU beendet (Exit {guest.process.returncode}) vor dem {wanted}. '{text}'")
        if time.monotonic() > deadline:
            raise TimeoutError(f"'{text}' nicht {wanted}-mal nach {timeout:.0f} s (Bootlog {guest.run / 'serial.log'})")
        time.sleep(0.1)


def start_load(guest) -> None:
    """Zwei busy-Hintergrundaufgaben in der Textshell; die Ausgabe von tasks list bleibt als Bild im Laufordner."""
    for _ in range(2):
        guest.qmp.line("tasks start busy")
        time.sleep(1)
    guest.qmp.line("tasks list")
    time.sleep(2)
    guest.qmp.shot(guest.run / "tasks.png")


def one_run(args, work: Path) -> None:
    run = storage.new_work_dir(work)
    guest = power.Guest(args, run, [], args.iso, from_disk=False, reboot_exits=False)
    try:
        wait_count(guest, READY, 1, args.timeout)
        guest.ready(args.timeout)
        guest.to_textshell()
        if args.load:
            start_load(guest)
        reboot_mark = guest.mark()
        guest.power("reboot", expect_exit=False)
        wait_count(guest, READY, 2, REBOOT_TIMEOUT + (60 if args.load else 0))
        print("  PASS: reboot hat den Gast in derselben VM erneut bis zur Shell gestartet.", flush=True)
        if guest.process.poll() is not None:
            raise RuntimeError("QEMU lief nach reboot nicht weiter")
        guest.ready(args.timeout, start=reboot_mark)
        guest.to_textshell()
        guest.power("shutdown", expect_exit=True, timeout=30)
        if guest.process.returncode != 0:
            raise RuntimeError(f"QEMU Exit {guest.process.returncode} nach shutdown")
        print("  PASS: shutdown hat QEMU sauber beendet (Exitcode 0).", flush=True)
    finally:
        guest.kill()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--work", required=True, type=Path, help="neuer Arbeitsordner (je Lauf ein Unterordner)")
    parser.add_argument("--iso", required=True, type=Path)
    parser.add_argument("--iso-recovery", type=Path, default=None, help="unbenutzt, Kompatibilitaet zu Guest")
    parser.add_argument("--accel", choices=["tcg", "kvm"], default="kvm")
    parser.add_argument("--timeout", type=float, default=300)
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--load", action="store_true", help="vor reboot zwei busy-Aufgaben")
    args = parser.parse_args()
    work = storage.new_work_dir(args.work)
    failed = 0
    for number in range(1, args.runs + 1):
        started = time.monotonic()
        try:
            print(f"== Lauf {number}/{args.runs}", flush=True)
            one_run(args, work / f"lauf-{number}")
            print(f"PASS Lauf {number} ({time.monotonic() - started:.0f} s)", flush=True)
        except Exception as error:
            failed += 1
            print(f"FAIL Lauf {number}: {error}", flush=True)
    print(f"{args.runs - failed}/{args.runs} bestanden", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
