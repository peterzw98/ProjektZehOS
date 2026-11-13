#!/usr/bin/env python3
"""Erzeugt eine volle Kopie einer isolierten ZehOS-FAT32-Testplatte, nie am Original."""

import argparse
from pathlib import Path
import struct


def create_copy(source: Path, destination: Path) -> None:
    image = bytearray(source.read_bytes())
    if len(image) != 64 * 1024 * 1024 or image[510:512] != b"\x55\xaa":
        raise ValueError("Erwartet wird eine partitionierte 64-MiB-Testplatte.")
    partitions = [image[pos:pos + 16] for pos in range(446, 510, 16)]
    used = [entry for entry in partitions if entry[4] != 0]
    if len(used) != 1 or used[0][4] != 0x0C:
        raise ValueError("Genau eine FAT32-LBA-Partition ist erforderlich.")
    start, sectors = struct.unpack_from("<II", used[0], 8)
    offset = start * 512
    if start == 0 or sectors == 0 or offset + sectors * 512 > len(image):
        raise ValueError("Die Partition liegt ausserhalb der Testplatte.")
    boot = image[offset:offset + 512]
    sector_size = struct.unpack_from("<H", boot, 11)[0]
    cluster_sectors = boot[13]
    reserved = struct.unpack_from("<H", boot, 14)[0]
    fat_count = boot[16]
    total = struct.unpack_from("<I", boot, 32)[0]
    fat_sectors = struct.unpack_from("<I", boot, 36)[0]
    root_cluster = struct.unpack_from("<I", boot, 44)[0]
    flags = struct.unpack_from("<H", boot, 40)[0]
    if (boot[510:512] != b"\x55\xaa" or sector_size != 512 or
            cluster_sectors == 0 or cluster_sectors & (cluster_sectors - 1) or
            reserved == 0 or fat_count != 2 or flags != 0 or total > sectors or
            struct.unpack_from("<H", boot, 17)[0] != 0 or
            struct.unpack_from("<H", boot, 22)[0] != 0):
        raise ValueError("Nicht unterstuetztes FAT32-Testprofil.")
    cluster_size = cluster_sectors * sector_size
    fat_size = fat_sectors * sector_size
    data_sectors = total - reserved - fat_count * fat_sectors
    cluster_count = data_sectors // cluster_sectors
    if not 65525 <= cluster_count < 0x0FFFFFF5 or (cluster_count + 2) * 4 > fat_size:
        raise ValueError("Ungueltige FAT32-Groessen.")
    fat_start = offset + reserved * sector_size
    data_start = fat_start + fat_count * fat_size
    fat = bytearray(image[fat_start:fat_start + fat_size])
    if fat != image[fat_start + fat_size:fat_start + 2 * fat_size]:
        raise ValueError("Die beiden FAT-Kopien unterscheiden sich.")
    if not 2 <= root_cluster < cluster_count + 2:
        raise ValueError("Ungueltiger Wurzelcluster.")
    if struct.unpack_from("<I", fat, 4 * root_cluster)[0] & 0x0FFFFFFF < 0x0FFFFFF8:
        raise ValueError("Die Probe unterstuetzt nur ein einzelnes Wurzelcluster.")
    root_start = data_start + (root_cluster - 2) * cluster_size
    entries = bytearray(image[root_start:root_start + cluster_size])
    slot = next((pos for pos in range(0, len(entries), 32) if entries[pos] in (0, 0xE5)), None)
    if slot is None or any(entries[pos:pos + 11] == b"FILLER  BIN" for pos in range(0, len(entries), 32)):
        raise ValueError("Kein freier Verzeichniseintrag oder FILLER.BIN bereits vorhanden.")
    free = [n for n in range(2, cluster_count + 2)
            if struct.unpack_from("<I", fat, 4 * n)[0] & 0x0FFFFFFF == 0]
    if not free:
        raise ValueError("Die Ausgangsplatte ist bereits voll.")
    # Die Fuell-Datei belegt alle freien Cluster; vorhandene Dateiketten bleiben unveraendert.
    for index, cluster in enumerate(free):
        following = free[index + 1] if index + 1 < len(free) else 0x0FFFFFFF
        struct.pack_into("<I", fat, 4 * cluster, following)
    # Hinter dem bisherigen Ende koennen alte Eintraege liegen; sie muessen unsichtbar bleiben.
    if entries[slot] == 0 and slot + 32 < len(entries):
        entries[slot + 32] = 0
    entries[slot:slot + 32] = bytes(32)
    entries[slot:slot + 11] = b"FILLER  BIN"
    entries[slot + 11] = 0x20
    struct.pack_into("<H", entries, slot + 20, free[0] >> 16)
    struct.pack_into("<H", entries, slot + 26, free[0] & 0xFFFF)
    struct.pack_into("<I", entries, slot + 28, len(free) * cluster_size)
    for index in range(fat_count):
        begin = fat_start + index * fat_size
        image[begin:begin + fat_size] = fat
    image[root_start:root_start + cluster_size] = entries
    info = struct.unpack_from("<H", boot, 48)[0]
    backup = struct.unpack_from("<H", boot, 50)[0]
    for sector in (info, backup + info):
        if not 0 < sector < reserved:
            raise ValueError("Ungueltiger FSInfo-Sektor.")
        begin = offset + sector * sector_size
        # Cosmos 3.0.94 laesst den FSInfo-Sicherungssektor leer; diesen nicht neu erfinden.
        if sector != info and not any(image[begin:begin + sector_size]):
            continue
        if (image[begin:begin + 4] != b"RRaA" or
                image[begin + 484:begin + 488] != b"rrAa" or
                image[begin + 508:begin + 512] != b"\x00\x00\x55\xaa"):
            raise ValueError("Ungueltige FSInfo-Signaturen.")
        struct.pack_into("<II", image, begin + 488, 0, 0xFFFFFFFF)
    # Erst nach der Validierung exklusiv erzeugen, auch bestehende Zielimages niemals ersetzen.
    with destination.open("xb") as output:
        output.write(image)
    print(f"Volle Testkopie: {destination}; FILLER.BIN: {len(free) * cluster_size} Bytes")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Sauber ausgehaengte isolierte Testplatte")
    parser.add_argument("destination", type=Path, help="Neue, noch nicht vorhandene Kopie")
    arguments = parser.parse_args()
    try:
        create_copy(arguments.source, arguments.destination)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Fehler: {error}\n")
