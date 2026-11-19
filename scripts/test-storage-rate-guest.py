#!/usr/bin/env python3
"""Hostseite der Ratenmessung (Issue #542): ein QEMU-Lauf mit dem Probe-ISO (-p:ZehOSStorageRateProbe=true).

Legt in einem NEUEN Arbeitsordner zwei Testplatten an: ext2.img (genau 512 MiB, leer; die Probe misst zuerst roh und
formatiert dann) und fat.img (genau 520 MiB, FAT32 per mkfs.fat). Beide haengen an AHCI-Port 1 und 2 hinter der
App-Platte. Gibt die STORAGE_RATE-Zeilen aus; Exit 0 nur mit DONE, ohne FAIL und mit sauberem Gastende.
SMP ueber --smp (setzt ZEHOS_QEMU_SMP).
"""
import argparse
import importlib.util
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)

EXT2_SIZE = 512 * 1024 * 1024
FAT_SIZE = 520 * 1024 * 1024
LINE = re.compile(r"^STORAGE_RATE .*$", re.MULTILINE)


def new_image(path: Path, size: int) -> None:
    with path.open("xb") as image:  # "x": nie ein vorhandenes Image ueberschreiben
        image.truncate(size)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True, help="neuer Arbeitsordner (darf nicht existieren)")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="kvm")
    parser.add_argument("--smp", default="", help="Wert fuer -smp; leer = QEMU-Standard (1 CPU)")
    parser.add_argument("--timeout", type=float, default=2400)
    parser.add_argument("--mkfs-fat", default="mkfs.fat")
    args = parser.parse_args()
    if not args.iso.is_file():
        sys.exit(f"ISO nicht gefunden: {args.iso}")
    mkfs = shutil.which(args.mkfs_fat)
    if not mkfs:
        sys.exit(f"{args.mkfs_fat} nicht gefunden")
    work = args.work.resolve()
    if work.exists() or work.is_symlink():
        sys.exit(f"Arbeitsordner existiert bereits: {work}")
    work.mkdir(parents=True)
    ext2 = work / "ext2.img"
    fat = work / "fat.img"
    new_image(ext2, EXT2_SIZE)
    new_image(fat, FAT_SIZE)
    subprocess.run([mkfs, "-F", "32", "-S", "512", "-n", "ZEHRATE", str(fat)], check=True, stdout=subprocess.DEVNULL)
    appdisk = work / "app.qcow2"
    tool, environment = tools.qemu_img()
    subprocess.run([*tool, "create", "-f", "qcow2", str(appdisk), "64M"], check=True, stdout=subprocess.DEVNULL, env=environment)

    if args.smp:
        os.environ["ZEHOS_QEMU_SMP"] = args.smp
    else:
        os.environ.pop("ZEHOS_QEMU_SMP", None)
    tools.MACHINE = [*tools.MACHINE,
                     "-drive", f"file={ext2},format=raw,if=none,id=ratee2", "-device", "ide-hd,drive=ratee2,bus=ahci.1",
                     "-drive", f"file={fat},format=raw,if=none,id=ratefat", "-device", "ide-hd,drive=ratefat,bus=ahci.2"]
    process = tools.start_qemu(args.iso.resolve(), appdisk, work, tools.free_port(), args.accel, None)
    try:
        tools.wait_serial(work, "STORAGE_RATE DONE", args.timeout)
        process.wait(timeout=120)
    except (TimeoutError, subprocess.TimeoutExpired) as error:
        print(f"{error or 'Gast beendete sich nicht'}", file=sys.stderr)
        process.kill()
        process.wait()
    serial = (work / "serial.log").read_text(encoding="utf-8", errors="replace")
    lines = LINE.findall(serial)
    print("\n".join(lines))
    ok = "STORAGE_RATE DONE" in serial and not any(" FAIL " in line for line in lines) and process.returncode == 0
    print(f"smp={args.smp or 'leer'} qemu-exit={process.returncode} ok={ok}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
