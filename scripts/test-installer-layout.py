#!/usr/bin/env python3
"""7a Schritt 2: prueft den produktiven GPT-Writer mit fremden Werkzeugen (Spec 2026-11-17-storage-7a-hostprobe.md).

Je Groesse schreibt experiments/StorageProbe (layout) ein NEUES sparse Image: GPT mit Backup und ext2-Root. Danach
prueft dieses Skript mit sgdisk -v, sfdisk -J, mkfs.fat/fsck.fat (ESP-Fenster), e2fsck -fn (Root-Fenster) und
dumpe2fs -h; Gegenproben auf Kopien mit gezielt zerstoertem Byte muessen sgdisk und Verify rot melden. Python kennt
nur Aufruf, Offsets und Auswertung, nie die Tabellenbytes (Beweisregel). Exit 0 nur, wenn jede Pruefung gruen ist;
ein fehlendes Werkzeug zaehlt als offen, nie als bestanden.
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

SECTOR = 512
GIB = 1024 ** 3
SIZES = [32 * GIB, 64 * GIB, 128 * GIB]
REFUSED = [32 * GIB - SECTOR, 128 * GIB + SECTOR]
ESP_TYPE = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B"
ROOT_TYPE = "0FC63DAF-8483-4772-8E79-3D69D8477DE4"
PROBE = Path(__file__).resolve().parent.parent / "experiments" / "StorageProbe"
CHUNK = 8 * 1024 * 1024

results: list[tuple[str, str, str]] = []


def record(name: str, status: str, detail: str = "") -> None:
    results.append((name, status, detail))
    print(f"{status:5} {name}" + (f": {detail}" if detail else ""), flush=True)


def check(name: str, condition: bool, detail: str = "") -> bool:
    record(name, "ok" if condition else "rot", "" if condition else detail)
    return condition


def run(command: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, capture_output=True, text=True, check=False)


def probe(*arguments: str) -> tuple[int, dict]:
    done = run(["dotnet", "run", "-c", "Release", "--no-build", "--project", str(PROBE), "--", *arguments])
    try:
        return done.returncode, json.loads(done.stdout.strip().splitlines()[-1])
    except (IndexError, json.JSONDecodeError):
        return done.returncode, {"stdout": done.stdout, "stderr": done.stderr}


def tool(name: str) -> bool:
    return shutil.which(name) is not None


def extract(source: Path, offset: int, length: int, target: Path) -> None:
    """Kopiert ein Fenster sparse (nur Datenbereiche, SEEK_DATA/SEEK_HOLE); target darf noch nicht existieren."""
    with source.open("rb") as src, target.open("xb") as dst:
        dst.truncate(length)
        position, end = offset, offset + length
        while position < end:
            try:
                data = os.lseek(src.fileno(), position, os.SEEK_DATA)
            except OSError:
                break  # ENXIO: keine Daten mehr
            if data >= end:
                break
            hole = min(os.lseek(src.fileno(), data, os.SEEK_HOLE), end)
            src.seek(data)
            dst.seek(data - offset)
            remaining = hole - data
            while remaining > 0:
                block = src.read(min(CHUNK, remaining))
                dst.write(block)
                remaining -= len(block)
            position = hole


def flip(image: Path, offset: int) -> None:
    with image.open("r+b") as stream:
        stream.seek(offset)
        value = stream.read(1)[0]
        stream.seek(offset)
        stream.write(bytes([value ^ 0xFF]))


def v4(guid: str) -> bool:
    text = guid.lower()
    return len(text) == 36 and text[14] == "4" and text[19] in "89ab" and text != "00000000-0000-4000-8000-000000000000"


def check_layout(image: Path, size: int, layout: dict, work: Path, disk_guids: set[str]) -> None:
    tag = f"{size // GIB} GiB"
    blocks = size // SECTOR
    check(f"{tag} layout (Writer + ext2 + Verify)", layout.get("ok") is True, json.dumps(layout))

    if tool("sgdisk"):
        done = run(["sgdisk", "-v", str(image)])
        check(f"{tag} sgdisk -v", done.returncode == 0 and "No problems found" in done.stdout, done.stdout + done.stderr)
    else:
        record(f"{tag} sgdisk -v", "offen", "sgdisk fehlt (gdisk)")

    if tool("sfdisk"):
        done = run(["sfdisk", "-J", str(image)])
        try:
            table = json.loads(done.stdout)["partitiontable"]
        except (json.JSONDecodeError, KeyError):
            check(f"{tag} sfdisk -J", False, done.stdout + done.stderr)
        else:
            parts = table.get("partitions", [])
            last_usable = table.get("lastlba", -1)
            ok = table.get("label") == "gpt" and len(parts) == 2
            if ok:
                esp, root = parts
                root_end = root["start"] + root["size"] - 1
                expected_end = root["start"] + (last_usable - root["start"] + 1) // 8 * 8 - 1
                ok = (esp["type"].upper() == ESP_TYPE and root["type"].upper() == ROOT_TYPE and esp["start"] == 2048
                      and esp["size"] == 1048576 and root["start"] % 2048 == 0 and root["size"] % 8 == 0
                      and root_end == expected_end and table.get("firstlba") == 34 and last_usable == blocks - 34)
                guids = [table.get("id", ""), esp.get("uuid", ""), root.get("uuid", "")]
                check(f"{tag} GUIDs v4, verschieden, nicht null", all(v4(g) for g in guids) and len({g.lower() for g in guids}) == 3, str(guids))
                disk_guids.add(guids[0].lower())
            check(f"{tag} sfdisk: Typen, Ausrichtung, Root-Ende", ok, json.dumps(table))
    else:
        record(f"{tag} sfdisk -J", "offen", "sfdisk fehlt (util-linux)")

    # Schutz-MBR: nur feste Offsets aus UEFI 2.10 5.2.3, keine Tabellenbytes.
    with image.open("rb") as stream:
        mbr = stream.read(SECTOR)
    entry = mbr[446:462]
    check(f"{tag} Schutz-MBR", entry[4] == 0xEE and int.from_bytes(entry[8:12], "little") == 1
          and int.from_bytes(entry[12:16], "little") == min(blocks - 1, 0xFFFFFFFF) and not any(mbr[462:510])
          and mbr[510:512] == b"\x55\xaa", mbr[446:512].hex())

    esp_first, esp_last = layout["esp"]["first"], layout["esp"]["last"]
    if tool("mkfs.fat") and tool("fsck.fat"):
        done = run(["mkfs.fat", "-F", "32", "-n", "ZEHOSBOOT", "--offset", str(esp_first), str(image),
                    str((esp_last - esp_first + 1) * SECTOR // 1024)])
        if check(f"{tag} mkfs.fat im ESP-Fenster", done.returncode == 0, done.stdout + done.stderr):
            esp = work / "esp.img"
            extract(image, esp_first * SECTOR, (esp_last - esp_first + 1) * SECTOR, esp)
            done = run(["fsck.fat", "-n", str(esp)])
            check(f"{tag} fsck.fat -n", done.returncode == 0, done.stdout + done.stderr)
            esp.unlink()
            code, verified = probe("verify", str(image))
            check(f"{tag} Verify nach mkfs.fat (Tabelle unberuehrt)", code == 0, json.dumps(verified))
    else:
        record(f"{tag} ESP mkfs.fat/fsck.fat", "offen", "dosfstools fehlt")

    root_first, root_last = layout["root"]["first"], layout["root"]["last"]
    if tool("e2fsck"):
        root = work / "root.img"
        extract(image, root_first * SECTOR, (root_last - root_first + 1) * SECTOR, root)
        started = time.monotonic()
        done = run(["e2fsck", "-fn", str(root)])
        check(f"{tag} e2fsck -fn Root ({time.monotonic() - started:.1f} s)", done.returncode == 0, done.stdout + done.stderr)
        if tool("dumpe2fs"):
            header = run(["dumpe2fs", "-h", str(root)]).stdout
            check(f"{tag} Root zehos-root, 4096 B", re.search(r"volume name:\s+zehos-root$", header, re.M) is not None and re.search(r"^Block size:\s+4096$", header, re.M) is not None, header[:400])
        root.unlink()
    else:
        record(f"{tag} e2fsck -fn Root", "offen", "e2fsprogs fehlt")


def counterproofs(image: Path, size: int, work: Path) -> None:
    blocks = size // SECTOR
    for name, offset in [("Primaer-CRC", SECTOR + 16), ("Backup-CRC", (blocks - 1) * SECTOR + 16), ("Eintragstabelle (Byte in Eintrag 0, LastLba)", 2 * SECTOR + 40)]:
        copy = work / "gegenprobe.img"
        extract(image, 0, size, copy)
        flip(copy, offset)
        code, verified = probe("verify", str(copy))
        check(f"Gegenprobe {name}: Verify rot", code == 1, json.dumps(verified))
        if tool("sgdisk"):
            done = run(["sgdisk", "-v", str(copy)])
            # sgdisk laedt bei kaputtem Primaer den Backup und meldet das als Warnung; beides zaehlt als erkannt.
            output = (done.stdout + done.stderr).lower()
            noticed = "no problems found" not in output or "mismatch" in output or "invalid" in output or "corrupt" in output
            check(f"Gegenprobe {name}: sgdisk meldet Problem", noticed, done.stdout + done.stderr)
        copy.unlink()


def reproducible(work: Path) -> None:
    first, second = work / "seed-a.img", work / "seed-b.img"
    probe("layout", str(first), "--size", str(SIZES[0]), "--guid-seed", "5a17")
    probe("layout", str(second), "--size", str(SIZES[0]), "--guid-seed", "5a17")
    blocks = SIZES[0] // SECTOR

    def tables(image: Path) -> bytes:
        with image.open("rb") as stream:
            head = stream.read(34 * SECTOR)
            stream.seek((blocks - 33) * SECTOR)
            return head + stream.read(33 * SECTOR)

    check("gleicher --guid-seed: Tabellen bytegleich", first.exists() and second.exists() and tables(first) == tables(second))
    first.unlink(missing_ok=True)
    second.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workdir", type=Path, help="Arbeitsordner, z. B. .tools/installer-layout; je Lauf ein neuer Unterordner")
    args = parser.parse_args()
    if not tool("dotnet"):
        print("dotnet fehlt: Probe nicht ausfuehrbar.", file=sys.stderr)
        return 2
    built = run(["dotnet", "build", "-c", "Release", str(PROBE)])
    if built.returncode != 0:
        print(built.stdout + built.stderr, file=sys.stderr)
        return 2
    args.workdir.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="run.", dir=args.workdir))
    print(f"Lauf: {work}")

    for size in REFUSED:
        image = work / f"refused-{size}.img"
        code, answer = probe("layout", str(image), "--size", str(size))
        # Die Ursache verlangen: ein Absturz oder fehlendes dotnet hiesse sonst ebenfalls "verweigert".
        check(f"{size} B verweigert, keine Datei", code == 1 and answer.get("cause") == "UnsupportedDiskSize" and not image.exists(), json.dumps(answer))

    disk_guids: set[str] = set()
    for size in SIZES:
        image = work / f"layout-{size // GIB}g.img"
        code, layout = probe("layout", str(image), "--size", str(size))
        if code != 0 or not image.exists():
            check(f"{size // GIB} GiB layout", False, json.dumps(layout))
            continue
        check_layout(image, size, layout, work, disk_guids)
        if size == SIZES[0]:
            counterproofs(image, size, work)
        # Grosse sparse Images: nur das eben selbst angelegte Image im eigenen Laufordner loeschen.
        image.unlink()
    if tool("sfdisk"):
        check("Disk-GUIDs ohne Seed je Lauf verschieden", len(disk_guids) == len(SIZES), str(disk_guids))
    reproducible(work)

    red = sum(1 for _, status, _ in results if status == "rot")
    open_ = sum(1 for _, status, _ in results if status == "offen")
    print(f"ERGEBNIS: {len(results) - red - open_} ok, {red} rot, {open_} offen")
    return 0 if red == 0 and open_ == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
