#!/usr/bin/env python3
"""Vorbereitung der GPT-Installer-Gastabnahme. G8 startet nur mit ausdruecklichem --run-g8.

Alle Images und der Arbeitsordner werden neu angelegt; bereits vorhandene Daten werden nie beschrieben.
Nicht implementierte Faelle und fehlende Werkzeuge sind offen und fuehren zu Exitcode 2.
"""
import argparse
import errno
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("storage_guest", ROOT / "scripts/test-storage-data-disk.py")
storage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage)
SECTOR = 512
GIB = 1024 ** 3
ESP_TYPE = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B"
ROOT_TYPE = "0FC63DAF-8483-4772-8E79-3D69D8477DE4"


def partitions(table: dict, image_size: int) -> tuple[dict, dict]:
    table = table["partitiontable"]
    parts = table["partitions"]
    if table.get("label") != "gpt" or table.get("sectorsize", SECTOR) != SECTOR or len(parts) != 2:
        raise ValueError("GPT mit genau zwei 512-Byte-Partitionen erwartet")
    esp, root = parts
    if esp["type"].upper() != ESP_TYPE or root["type"].upper() != ROOT_TYPE:
        raise ValueError("Unerwartete Partitionstypen")
    previous_end = 0
    for part in parts:
        start, size = part["start"], part["size"]
        if not isinstance(start, int) or not isinstance(size, int) or start <= previous_end or size <= 0 or (start + size) * SECTOR > image_size:
            raise ValueError("Partition ausserhalb des Images oder ueberlappend")
        previous_end = start + size - 1
    return esp, root


def extract_sparse(source: Path, offset: int, length: int, target: Path) -> None:
    """Kopiert nur belegte Bereiche; bei nicht unterstuetztem SEEK_DATA wird nicht stillschweigend Erfolg gemeldet."""
    if offset < 0 or length <= 0 or offset + length > source.stat().st_size:
        raise ValueError("Partitionsfenster ausserhalb des Images")
    with source.open("rb") as src, target.open("xb") as dst:
        dst.truncate(length)
        pos, end = offset, offset + length
        while pos < end:
            try:
                data = os.lseek(src.fileno(), pos, os.SEEK_DATA)
            except OSError as error:
                if error.errno == errno.ENXIO:
                    break
                raise
            if data >= end:
                break
            hole = min(os.lseek(src.fileno(), data, os.SEEK_HOLE), end)
            src.seek(data)
            dst.seek(data - offset)
            while data < hole:
                block = src.read(min(8 * 1024 * 1024, hole - data))
                if not block:
                    raise EOFError("Image waehrend des Kopierens verkuerzt")
                dst.write(block)
                data += len(block)
            pos = hole


def check_tool(name: str, *arguments: str) -> None:
    result = subprocess.run([name, *arguments], capture_output=True, text=True, check=False)
    if result.returncode:
        raise RuntimeError(f"{name} meldet Fehler {result.returncode}: {result.stdout} {result.stderr}")


def inspect_g8(image: Path, work: Path, payload: Path, version: str, config: Path) -> None:
    check_tool("sgdisk", "-v", str(image))
    result = subprocess.run(["sfdisk", "-J", str(image)], capture_output=True, text=True, check=True)
    esp, root = partitions(json.loads(result.stdout), image.stat().st_size)
    esp_image, root_image = work / "esp.img", work / "root.img"
    extract_sparse(image, esp["start"] * SECTOR, esp["size"] * SECTOR, esp_image)
    extract_sparse(image, root["start"] * SECTOR, root["size"] * SECTOR, root_image)
    check_tool("fsck.fat", "-n", str(esp_image))
    check_tool("e2fsck", "-fn", str(root_image))
    expected = {"/EFI/BOOT/BOOTX64.EFI": (payload / "BOOTX64.EFI").read_bytes(),
                "/boot/ProjektZeh.elf": (payload / "ProjektZeh.elf").read_bytes(),
                "/boot/ProjektZeh.elf.bak": (payload / "ProjektZeh.elf").read_bytes(),
                "/boot/zehos-version.txt": (version + "\n").encode(),
                "/boot/limine/limine.conf": config.read_bytes()}
    for path, contents in expected.items():
        result = subprocess.run(["mcopy", "-i", str(esp_image), "::" + path, "-"], capture_output=True, check=True)
        if result.stdout != contents:
            raise ValueError(f"Bootdatei weicht ab: {path}")
    listing = subprocess.run(["mdir", "-a", "-b", "-i", str(esp_image), "::/boot"], capture_output=True, text=True, check=True)
    if any(line.rstrip("/").rsplit("/", 1)[-1].lower() == ".zehos-install.id" for line in listing.stdout.splitlines()):
        raise ValueError("ESP-Marker ist bereits vorhanden")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-g8", action="store_true", help="G8 mit vorbereitetem Recovery-Setup-ISO ausfuehren")
    parser.add_argument("--work", required=True, type=Path)
    parser.add_argument("--iso", type=Path)
    parser.add_argument("--payload", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--boot-config", type=Path)
    parser.add_argument("--ovmf-code", type=Path)
    parser.add_argument("--ovmf-vars", type=Path)
    parser.add_argument("--accel", choices=["tcg", "kvm"], default="tcg")
    parser.add_argument("--timeout", type=float, default=1200)
    args = parser.parse_args()
    print("OFFEN G1-G7 und G9: Gastablaeufe noch nicht implementiert", flush=True)
    if not args.run_g8:
        print("OFFEN G8: --run-g8 nicht angegeben", flush=True)
        return 2
    missing = [name for name in ("sgdisk", "sfdisk", "fsck.fat", "e2fsck", "mcopy", "mdir") if not shutil.which(name)]
    if missing:
        print("OFFEN G8: Werkzeuge fehlen: " + ", ".join(missing), flush=True)
        return 2
    if not all((args.iso, args.payload, args.version, args.boot_config, args.ovmf_code, args.ovmf_vars)):
        parser.error("G8 benoetigt --iso, --payload, --version, --boot-config, --ovmf-code und --ovmf-vars")
    for file in (args.iso, args.boot_config, args.ovmf_code, args.ovmf_vars, args.payload / "ProjektZeh.elf", args.payload / "BOOTX64.EFI"):
        if not file.is_file():
            parser.error(f"Datei fehlt: {file}")
    work = storage.new_work_dir(args.work)
    target = work / "target.img"
    storage.create_sparse(target, 32 * GIB)
    vars_copy = work / "ovmf-vars.fd"
    with args.ovmf_vars.open("rb") as src, vars_copy.open("xb") as dst:
        shutil.copyfileobj(src, dst)
    storage.MACHINE_BASE += ["-drive", f"if=pflash,format=raw,readonly=on,file={args.ovmf_code.resolve()}",
                             "-drive", f"if=pflash,format=raw,file={vars_copy.resolve()}"]
    run = storage.new_work_dir(work / "guest")
    args.iso_recovery = args.iso
    process = None
    try:
        process, qmp = storage.boot(args, run, [target])
        qmp.line("setup install sata0 --layout gpt --erase")
        storage.tools.wait_serial(run, "GPT_G8_BOOT_FLUSHED", args.timeout)
        process.kill()
        process.wait(timeout=30)
        process = None
        inspect_g8(target, work, args.payload, args.version, args.boot_config)
        print("PASS G8: Bootdateien geflusht, Marker fehlt, Offline-Pruefungen bestanden", flush=True)
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)
    return 2  # G1-G7/G9 sind weiterhin offen.


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(f"FEHLER: {error}", file=sys.stderr)
        sys.exit(1)
