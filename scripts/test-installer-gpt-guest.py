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
import re
from pathlib import Path
import shutil
import subprocess
import sys
import uuid
import zlib

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


def check_gpt(image: Path) -> None:
    result = subprocess.run(["sgdisk", "-v", str(image)], capture_output=True, text=True, check=False)
    if result.returncode or "No problems found" not in result.stdout or "warning" in (result.stdout + result.stderr).lower():
        raise RuntimeError("sgdisk meldet Fehler oder Warnungen: " + result.stdout + result.stderr)


def check_ext2(image: Path) -> None:
    result = subprocess.run(["e2fsck", "-fn", str(image)], capture_output=True, text=True, check=False)
    output = result.stdout + result.stderr
    # -n kann trotz nicht behobener Fehler Exit 0 liefern; die Ausgabe ist Teil der Pruefung.
    if result.returncode or re.search(r"fix\?\s*no|wrong|corrupt|error|warning|unexpected|not clean", output, re.IGNORECASE):
        raise RuntimeError("e2fsck meldet Fehler oder Warnungen: " + output)


def expected_markers(table: dict, root: dict) -> tuple[bytes, bytes]:
    disk_guid = str(uuid.UUID(table["partitiontable"]["id"]))
    root_guid = str(uuid.UUID(root["uuid"]))
    body = (f"zehos-install 1\ndisk={disk_guid}\nroot={root_guid}\n"
            f"root-first={root['start']}\nroot-sectors={root['size']}\n").encode()
    esp = body + f"crc32={zlib.crc32(body):08x}\n".encode()
    body = f"format=1\nstate=fresh\ndisk={disk_guid}\n".encode()
    return esp, body + f"crc32={zlib.crc32(body):08x}\n".encode()


def inspect_image(image: Path, work: Path, payload: Path, version: str, config: Path, marker_expected: bool) -> None:
    check_gpt(image)
    result = subprocess.run(["sfdisk", "-J", str(image)], capture_output=True, text=True, check=True)
    table = json.loads(result.stdout)
    esp, root = partitions(table, image.stat().st_size)
    esp_image, root_image = work / "esp.img", work / "root.img"
    extract_sparse(image, esp["start"] * SECTOR, esp["size"] * SECTOR, esp_image)
    extract_sparse(image, root["start"] * SECTOR, root["size"] * SECTOR, root_image)
    check_tool("fsck.fat", "-n", str(esp_image))
    check_ext2(root_image)
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
    present = any(line.rstrip("/").rsplit("/", 1)[-1].lower() == ".zehos-install.id" for line in listing.stdout.splitlines())
    if present != marker_expected:
        raise ValueError("ESP-Marker vorhanden" if present else "ESP-Marker fehlt")
    if marker_expected:
        expected_marker, expected_root = expected_markers(table, root)
        actual = subprocess.run(["mcopy", "-i", str(esp_image), "::/boot/.zehos-install.id", "-"],
                                capture_output=True, check=True).stdout
        if actual != expected_marker:
            raise ValueError("ESP-Marker passt nicht zur GPT")
        actual_root = subprocess.run(["debugfs", "-R", "cat /etc/zehos/.zehos-layout", str(root_image)],
                                     capture_output=True, check=True).stdout
        if actual_root != expected_root:
            raise ValueError("Root-Marker fehlt oder passt nicht zur GPT")


def new_sparse(path: Path, size: int) -> None:
    if size not in (31 * GIB, 32 * GIB, 129 * GIB):
        raise ValueError("Nur die vorgesehenen G6-Groessen sind erlaubt")
    with path.open("xb") as image:
        if os.name == "nt":
            storage._set_sparse_windows(image.fileno())
            image.seek(size - 1)
            image.write(b"\0")
        else:
            image.truncate(size)


def assert_blank(path: Path) -> None:
    # Bei sparse RAW ist jedes vom Gast geschriebene Byte als belegter Bereich erkennbar.
    with path.open("rb") as image:
        try:
            os.lseek(image.fileno(), 0, os.SEEK_DATA)
        except OSError as error:
            if error.errno == errno.ENXIO:
                return
            raise
    raise ValueError(f"Unerwarteter Schreibzugriff auf {path}")


def run_g6_basic(args, work: Path) -> None:
    disks = [work / "small.img", work / "large.img", work / "valid.img"]
    for path, size in zip(disks, (31 * GIB, 129 * GIB, 32 * GIB), strict=True):
        new_sparse(path, size)
    run = storage.new_work_dir(work / "guest-g6")
    process = None
    try:
        process, qmp = storage.boot(args, run, disks)
        cases = [("setup install sata0 --layout gpt --erase", "GPT-Layout nur fuer 32 bis 128 GiB"),
                 ("setup install sata1 --layout gpt --erase", "GPT-Layout nur fuer 32 bis 128 GiB"),
                 ("setup install sata2 --layout gpt", "--erase erforderlich")]
        for command, reason in cases:
            mark = len((run / "serial.log").read_text(encoding="utf-8", errors="replace"))
            qmp.line(command)
            storage.tools.wait_serial(run, "GPT_TEST RESULT ERROR " + reason, args.timeout, mark)
        storage.stop(process, qmp, run)
        process = None
        for disk in disks:
            assert_blank(disk)
        print("PASS G6 Teil: 31/129 GiB und fehlendes --erase abgewiesen, keine Schreibzugriffe", flush=True)
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)


def boot_installed(args, case: Path, target: Path) -> None:
    run = storage.new_work_dir(case / "boot-installed")
    machine = list(storage.MACHINE_BASE)
    machine[machine.index("-boot") + 1] = "c"
    machine += ["-drive", f"file={target.resolve()},format=raw,if=none,id=d0",
                "-device", "ide-hd,drive=d0,bus=ahci.0"]
    storage.tools.MACHINE = machine
    unused = run / "unused.qcow2"
    tool, environment = storage.tools.qemu_img()
    subprocess.run([*tool, "create", "-f", "qcow2", str(unused), "1M"], check=True,
                   stdout=subprocess.DEVNULL, env=environment)
    process = storage.tools.start_qemu(args.iso, unused, run, storage.tools.free_port(), args.accel, None)
    try:
        storage.tools.wait_serial(run, "GPT_TEST IDENTITY Healthy SETUP True", args.timeout)
        storage.tools.wait_serial(run, "Desktop ist bereit.", args.timeout)
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=30)


def run_g1_basic(args, work: Path) -> None:
    for size in (32 * GIB, 128 * GIB):
        case = storage.new_work_dir(work / f"g1-{size // GIB}g")
        target = case / "target.img"
        storage.create_sparse(target, size)
        run = storage.new_work_dir(case / "guest")
        process = None
        try:
            process, qmp = storage.boot(args, run, [target])
            mark = len((run / "serial.log").read_text(encoding="utf-8", errors="replace"))
            qmp.line("setup install sata0 --layout gpt --erase")
            storage.tools.wait_serial(run, "GPT_TEST RESULT OK", args.timeout, mark)
            storage.stop(process, qmp, run)
            process = None
            inspect_image(target, case, args.payload, args.version, args.boot_config, marker_expected=True)
            boot_installed(args, case, target)
            print(f"PASS G1 Teil: {size // GIB} GiB installiert, offline geprueft und Healthy gestartet", flush=True)
        finally:
            if process is not None and process.poll() is None:
                process.kill()
                process.wait(timeout=30)
    print("OFFEN G1: Kontoanlage und VMware", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-g8", action="store_true", help="G8 mit vorbereitetem Recovery-Setup-ISO ausfuehren")
    parser.add_argument("--run-g6-basic", action="store_true", help="G6-Groessengrenzen und --erase pruefen")
    parser.add_argument("--run-g1-basic", action="store_true", help="G1-Installation und Offline-Pruefung vorbereiten")
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
    if not args.run_g8 and not args.run_g6_basic and not args.run_g1_basic:
        print("OFFEN G8: --run-g8 nicht angegeben", flush=True)
        return 2
    if sum((args.run_g8, args.run_g6_basic, args.run_g1_basic)) > 1:
        parser.error("G1, G6 und G8 brauchen getrennte Arbeitsordner")
    required = ("sgdisk", "sfdisk", "fsck.fat", "e2fsck", "mcopy", "mdir") if args.run_g8 or args.run_g1_basic else ()
    if args.run_g1_basic:
        required += ("debugfs",)
    missing = [name for name in required if not shutil.which(name)]
    if missing:
        print("OFFEN G8: Werkzeuge fehlen: " + ", ".join(missing), flush=True)
        return 2
    if not all((args.iso, args.ovmf_code, args.ovmf_vars)):
        parser.error("Gastlauf benoetigt --iso, --ovmf-code und --ovmf-vars")
    if (args.run_g8 or args.run_g1_basic) and not all((args.payload, args.version, args.boot_config)):
        parser.error("G8 benoetigt --payload, --version und --boot-config")
    files = [args.iso, args.ovmf_code, args.ovmf_vars]
    if args.run_g8 or args.run_g1_basic:
        files += [args.boot_config, args.payload / "ProjektZeh.elf", args.payload / "BOOTX64.EFI"]
    for file in files:
        if not file.is_file():
            parser.error(f"Datei fehlt: {file}")
    work = storage.new_work_dir(args.work)
    vars_copy = work / "ovmf-vars.fd"
    with args.ovmf_vars.open("rb") as src, vars_copy.open("xb") as dst:
        shutil.copyfileobj(src, dst)
    storage.MACHINE_BASE += ["-drive", f"if=pflash,format=raw,readonly=on,file={args.ovmf_code.resolve()}",
                             "-drive", f"if=pflash,format=raw,file={vars_copy.resolve()}"]
    args.iso_recovery = args.iso
    if args.run_g6_basic:
        run_g6_basic(args, work)
        return 2
    if args.run_g1_basic:
        run_g1_basic(args, work)
        return 2
    target = work / "target.img"
    storage.create_sparse(target, 32 * GIB)
    run = storage.new_work_dir(work / "guest")
    process = None
    try:
        process, qmp = storage.boot(args, run, [target])
        qmp.line("setup install sata0 --layout gpt --erase")
        storage.tools.wait_serial(run, "GPT_G8_BOOT_FLUSHED", args.timeout)
        process.kill()
        process.wait(timeout=30)
        process = None
        inspect_image(target, work, args.payload, args.version, args.boot_config, marker_expected=False)
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
