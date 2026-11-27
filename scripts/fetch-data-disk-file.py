#!/usr/bin/env python3
"""Holt eine Datei aus der FAT32-Datenplatte (qcow2 oder raw) nach dem Gastlauf (mtools, ohne root).

Nutzung: scripts/fetch-data-disk-file.py <platte.qcow2> <pfad-auf-der-platte> <ziel> [--list ORDNER]
Der Pfad steht relativ zur Plattenwurzel, z. B. lb/shot.png (im Gast /data/lb/shot.png). Die Platte wird nur gelesen:
qemu-img wandelt sie in eine sparse Raw-Datei im Temp-Ordner, mcopy liest daraus mit dem Offset der ersten MBR-Partition.
Der Gast muss vorher beendet sein (shutdown), sonst fehlen noch nicht geschriebene Blocks.
Exit 0 ok, 1 Datei nicht gefunden oder leer, 2 Werkzeug oder Plattenformat.
"""
import argparse
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

SECTOR = 512


def partition_offset(raw: Path) -> int:
    """Byte-Offset der ersten MBR-Partition (die Datenplatte hat genau eine, linux-root-disk.py)."""
    with raw.open("rb") as handle:
        mbr = handle.read(SECTOR)
    if len(mbr) < SECTOR or mbr[510:512] != b"\x55\xaa":
        raise ValueError("keine MBR-Signatur")
    start = struct.unpack_from("<I", mbr, 446 + 8)[0]
    if start == 0:
        raise ValueError("erste Partition leer")
    return start * SECTOR


def fetch(disk: Path, name: str, target: Path, list_dir: str | None = None) -> int:
    for tool in ("qemu-img", "mcopy", "mdir"):
        if shutil.which(tool) is None:
            print(f"Werkzeug fehlt: {tool}", file=sys.stderr)
            return 2
    with tempfile.TemporaryDirectory(prefix="fetch-disk-") as tmp:
        raw = Path(tmp) / "disk.raw"
        subprocess.run(["qemu-img", "convert", "-f", "qcow2" if disk.suffix == ".qcow2" else "raw", "-O", "raw",
                        str(disk), str(raw)], check=True)
        try:
            image = f"{raw}@@{partition_offset(raw)}"
        except ValueError as e:
            print(f"Plattenformat: {e}", file=sys.stderr)
            return 2
        if list_dir is not None:
            return subprocess.run(["mdir", "-i", image, "::/" + list_dir.strip("/")]).returncode
        target.parent.mkdir(parents=True, exist_ok=True)
        result = subprocess.run(["mcopy", "-n", "-i", image, "::/" + name.strip("/"), str(target)],
                                capture_output=True, text=True)
        if result.returncode != 0 or not target.exists() or target.stat().st_size == 0:
            print(f"nicht gefunden oder leer: {name} ({result.stderr.strip()})", file=sys.stderr)
            target.unlink(missing_ok=True)
            return 1
    print(f"{name} -> {target} ({target.stat().st_size} Bytes)")
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("disk", type=Path)
    parser.add_argument("name", help="Pfad relativ zur Plattenwurzel")
    parser.add_argument("target", type=Path)
    parser.add_argument("--list", dest="list_dir", help="stattdessen diesen Ordner auflisten")
    args = parser.parse_args(argv)
    return fetch(args.disk, args.name, args.target, args.list_dir)


if __name__ == "__main__":
    sys.exit(main())
