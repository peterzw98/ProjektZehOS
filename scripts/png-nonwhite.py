#!/usr/bin/env python3
"""Zaehlt nicht-weisse Pixel eines PNG (nur Standardbibliothek, kein PIL auf dem MSI).

Nutzung: png-nonwhite.py <bild.png> [--min-anteil 0.001]
Ausgabe: "<breite>x<hoehe> nicht-weiss <n>/<gesamt> (<prozent> %)"; Exit 0, wenn der Anteil nicht-weisser Pixel
mindestens --min-anteil ist, Exit 1 bei (fast) weissem Bild, Exit 2 bei unlesbarer Datei.
Weiss heisst: alle Farbkanaele >= 250 (Ladybird rendert den Seitenhintergrund rein weiss; der Rest sind
Kantenglaettung und Kompressionsreste). Unterstuetzt 8 Bit Graustufen, RGB, Graustufen+Alpha und RGBA ohne Interlace,
alle fuenf Zeilenfilter; Alpha wird ignoriert.
"""
import argparse
import struct
import sys
import zlib
from pathlib import Path

CHANNELS = {0: 1, 2: 3, 4: 2, 6: 4}
COLOR = {0: 1, 2: 3, 4: 1, 6: 3}


def unfilter(raw: bytes, width: int, height: int, bpp: int) -> list[bytes]:
    stride = width * bpp
    rows = []
    previous = bytearray(stride)
    pos = 0
    for _ in range(height):
        kind = raw[pos]
        line = bytearray(raw[pos + 1:pos + 1 + stride])
        pos += 1 + stride
        if kind == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 0xFF
        elif kind == 2:
            line = bytearray((a + b) & 0xFF for a, b in zip(line, previous))
        elif kind == 3:
            for i in range(stride):
                left = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((left + previous[i]) >> 1)) & 0xFF
        elif kind == 4:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b = previous[i]
                c = previous[i - bpp] if i >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 0xFF
        elif kind != 0:
            raise ValueError(f"unbekannter Zeilenfilter {kind}")
        rows.append(bytes(line))
        previous = line
    return rows


def count(path: Path) -> tuple[int, int, int, int]:
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("keine PNG-Signatur")
    pos, idat, header = 8, [], None
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            header = struct.unpack(">IIBBBBB", body)
        elif kind == b"IDAT":
            idat.append(body)
        elif kind == b"IEND":
            break
        pos += 12 + length
    if header is None:
        raise ValueError("IHDR fehlt")
    width, height, depth, color, _, _, interlace = header
    if depth != 8 or color not in CHANNELS or interlace:
        raise ValueError(f"Format nicht unterstuetzt (Tiefe {depth}, Farbtyp {color}, Interlace {interlace})")
    bpp, used = CHANNELS[color], COLOR[color]
    rows = unfilter(zlib.decompress(b"".join(idat)), width, height, bpp)
    nonwhite = 0
    for line in rows:
        for x in range(0, len(line), bpp):
            if min(line[x:x + used]) < 250:
                nonwhite += 1
    return width, height, nonwhite, width * height


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("png", type=Path)
    parser.add_argument("--min-anteil", type=float, default=0.001)
    args = parser.parse_args()
    try:
        width, height, nonwhite, total = count(args.png)
    except (OSError, ValueError, zlib.error, struct.error) as error:
        print(f"PNG unlesbar: {error}")
        return 2
    share = nonwhite / total if total else 0.0
    print(f"{width}x{height} nicht-weiss {nonwhite}/{total} ({share * 100:.2f} %)")
    return 0 if total and share >= args.min_anteil else 1


if __name__ == "__main__":
    sys.exit(main())
