#!/usr/bin/env python3
"""Misst die Bildzeit einer Paket-App und ihre Anteile (frametimes on) in einer frischen QEMU-Platte.

Standard ist das veroeffentlichte Doom 1.1.0 aus dem Branch packages; das ISO muss dessen Herausgeberschluessel
vertrauen (normaler Build). Doom laeuft ohne Eingabe im Titel-/Demoablauf, damit die Messung reproduzierbar bleibt.
Ausgabe: die seriellen Zeilen "Bildzeit ..." und "Anteile ..." je 60-s-Fenster. Bilder/Protokoll bleiben unter
.tools/frametimes/. Vorhandene VM-Images werden nicht verwendet.
"""
import argparse
import importlib.util
from pathlib import Path
import re
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="kvm")
    parser.add_argument("--seconds", type=int, default=130, help="Laufzeit der App; je 60 s entsteht ein Messfenster")
    parser.add_argument("--ref", default="origin/packages", help="Git-Stand, aus dem doom/1.1.0 gelesen wird")
    args = parser.parse_args()
    work = ROOT / ".tools/frametimes" / uuid.uuid4().hex[:12]
    packages = work / "seed/pkgs"
    packages.mkdir(parents=True)
    # Nur lesend aus Git: das signierte Paket unveraendert auf die frische Testplatte legen.
    archive = subprocess.run(["git", "-C", str(ROOT), "archive", args.ref, "doom/1.1.0"], check=True, capture_output=True).stdout
    subprocess.run(["tar", "-x", "-C", str(packages)], input=archive, check=True)
    tools.create_disk(work / "seed", work / "data.qcow2")
    port = tools.free_port()
    vm = tools.start_qemu(args.iso.resolve(), work / "data.qcow2", work, port, args.accel, None)
    guest = tools.Guest(work, port, "", time.monotonic())
    try:
        guest.wait("Desktop ist bereit", 180)
        guest.connect()
        guest.login(first=True)
        guest.shell("frametimes on")
        guest.pkg("install-local /data/pkgs/doom/1.1.0", "doom 1.1.0 installiert.")
        guest.desktop()
        mark = guest.mark()
        guest.start_app("doom")
        time.sleep(args.seconds)
        guest.shot("ende")
        log = (work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]
        lines = re.findall(r"(Bildzeit n=[^\r\n]*|Anteile \(ms[^\r\n]*)", log)
        for line in lines:
            print(line, flush=True)
        print(f"Protokoll: {work / 'serial.log'}", flush=True)
        return 0 if any(line.startswith("Anteile") for line in lines) else 1
    finally:
        try:
            guest.qmp.quit()
        except Exception:
            vm.kill()
        vm.wait(30)


if __name__ == "__main__":
    raise SystemExit(main())
