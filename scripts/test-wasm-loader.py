#!/usr/bin/env python3
"""Startet den Wasm- oder AOT-Probekernel in QEMU und wertet WASM_PROBE bzw. AOT_PROBE PASS/FAIL aus.

Die App liegt in einem frisch angelegten Ordner, den QEMU als FAT-Laufwerk mit verworfenen Schreibzugriffen
(vvfat) einbindet. Vorhandene Datenimages werden weder gelesen noch veraendert. Mit --probe aot liegt dort immer
aotprobe.txt fuer die Dateifaelle der AOT-Probe, optional doom.aot mit freedoom1.wad.
"""

import argparse
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def find_qemu() -> Path:
    found = shutil.which("qemu-system-x86_64")
    if found:
        return Path(found)
    fallback = Path.home() / ".cosmos/tools/qemu/qemu-system-x86_64"
    for candidate in (fallback, fallback.with_suffix(".exe")):
        if candidate.exists():
            return candidate
    raise SystemExit("qemu-system-x86_64 nicht gefunden.")


def build_arguments(qemu: Path, iso: Path, disk: Path | None, serial: Path, accel: str) -> list[str]:
    share = qemu.parent.parent / "share" / "qemu"
    # Unter WHPX bricht der Gast mit -cpu max sofort ab (Unexpected VP exit code 4, COSMOS_SUPPORT.md); dort bleibt
    # das Standardmodell.
    cpu = [] if accel.startswith("whpx") else ["-cpu", "max"]
    arguments = [str(qemu), "-L", str(share), "-accel", accel, "-machine", "q35", *cpu, "-m", "512M",
                 "-vga", "std", "-boot", "d", "-cdrom", str(iso), "-display", "none",
                 "-serial", f"file:{serial}", "-monitor", "none",
                 "-action", "shutdown=poweroff,reboot=reset"]
    if disk is not None:
        arguments += ["-drive", f"file=fat:{disk},format=raw,if=none,id=appdisk,snapshot=on",
                      "-device", "ahci,id=ahci", "-device", "ide-hd,drive=appdisk,bus=ahci.0"]
    return arguments


def wait_for_result(guest: subprocess.Popen, serial: Path, timeout: float, marker: str = "WASM_PROBE") -> tuple[bool, str]:
    deadline = time.monotonic() + timeout
    log = ""
    while time.monotonic() < deadline:
        if serial.exists():
            log = serial.read_text(encoding="utf-8", errors="replace")
        if f"{marker} FAIL" in log:
            return False, log
        if f"{marker} PASS" in log:
            return True, log
        if guest.poll() is not None:
            return False, log + "\nQEMU vor dem Pruefergebnis beendet."
        time.sleep(0.2)
    return False, log + f"\nKein Ergebnis innerhalb von {timeout:.0f} s."


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--probe", choices=["wasm", "aot"], default="wasm",
                        help="wasm: -p:ZehOSWasmProbe=true (WASM_PROBE); aot: -p:ZehOSAotProbe=true (AOT_PROBE)")
    parser.add_argument("--doom-aot", type=Path, help="doom.aot fuer --probe aot; wird mit --wad als /data/doom.aot bereitgestellt")
    parser.add_argument("--app", type=Path, help="Wasm-App, die als /data/csprobe.wasm bereitsteht")
    parser.add_argument("--doom", type=Path, help="doom.wasm; wird mit --wad als /data/doom.wasm bereitgestellt")
    parser.add_argument("--wad", type=Path, help="freedoom1.wad fuer die Doom-Probe")
    parser.add_argument("--timeout", type=float, default=180)
    parser.add_argument("--accel", choices=["tcg", "whpx", "whpx,kernel-irqchip=off", "kvm"], default="tcg",
                        help="QEMU-Beschleuniger; Messwerte immer mit dem verwendeten Wert dokumentieren")
    parser.add_argument("--expect-fail", action="store_true",
                        help="Negativpruefung: erwartet FAIL bzw. kein PASS (z. B. ISO ohne Probe)")
    args = parser.parse_args()

    if args.doom_aot is not None and (args.probe != "aot" or args.wad is None):
        parser.error("--doom-aot braucht --probe aot und --wad")
    marker = "AOT_PROBE" if args.probe == "aot" else "WASM_PROBE"
    work = ROOT / ".tools" / "wasm-probe" / uuid.uuid4().hex
    work.mkdir(parents=True)
    disk = None
    if args.probe == "aot":
        disk = work / "disk"
        disk.mkdir()
        (disk / "aotprobe.txt").write_text("ZehOS AOT-Probe\n", encoding="ascii")
        if args.doom_aot is not None:
            shutil.copyfile(args.doom_aot, disk / "doom.aot")
            shutil.copyfile(args.wad, disk / "freedoom1.wad")
    elif args.app is not None:
        disk = work / "disk"
        disk.mkdir()
        shutil.copyfile(args.app, disk / "csprobe.wasm")
        if args.doom is not None:
            shutil.copyfile(args.doom, disk / "doom.wasm")
            shutil.copyfile(args.wad, disk / "freedoom1.wad")
    serial = work / "serial.log"
    guest = subprocess.Popen(build_arguments(find_qemu(), args.iso.resolve(), disk, serial, args.accel),
                             stdout=subprocess.DEVNULL, stderr=(work / "stderr.log").open("w"))
    try:
        passed, log = wait_for_result(guest, serial, args.timeout, marker)
    finally:
        # Nur die eigene Test-VM beenden.
        if guest.poll() is None:
            guest.kill()
            guest.wait()
    for line in log.splitlines():
        if marker in line:
            print(line)
    print(f"Log: {serial}")
    if args.expect_fail:
        return 0 if not passed else 1
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
