#!/usr/bin/env python3
"""Erzeugt das freie ClassiCube-Texturpaket (default.png, terrain.png) aus festgelegten Quellen.

Quellen (vor dem Aufruf selbst holen; jede verwendete Datei wird per SHA-256 geprueft):
  - minetest_game, Commit 9e895af6e9feb98332fa1a778001442f5669bc20
      git clone https://github.com/minetest/minetest_game.git && git -C minetest_game checkout 9e895af6...
  - font8x8, Commit 8e279d2d864e79128e96188a6b9526cfa3fbfef9 (Datei font8x8_basic.h, Public Domain)
      git clone https://github.com/dhepper/font8x8.git && git -C font8x8 checkout 8e279d2d...

Aufruf: python3 build_texpack.py <minetest_game> <font8x8_basic.h> <ausgabeordner>
Nur Python-Standardbibliothek. Lizenzen und Urheber: TEXTURES.txt neben diesem Skript.
"""
import hashlib
import re
import struct
import sys
import zlib
from pathlib import Path

TILE = 16

# Erwartete SHA-256 je Quelldatei (sources.sha256); eine Abweichung bricht ab, damit das Paket reproduzierbar bleibt.
SOURCES: dict[str, str] = {}  # aus sources.sha256 gelesen
PINNED_FILE = Path(__file__).with_name("sources.sha256")


def read_png(path: Path) -> list[list[tuple[int, int, int, int]]]:
    """Dekodiert nicht verschachtelte PNGs (Graustufe, RGB, Palette, mit Alpha; 1-8 Bit) zu RGBA-Zeilen."""
    data = path.read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", path
    pos, idat, palette, trns = 8, b"", [], None
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            assert interlace == 0 and depth <= 8, path
        elif kind == b"PLTE":
            palette = [tuple(body[i:i + 3]) for i in range(0, len(body), 3)]
        elif kind == b"tRNS":
            trns = body
        elif kind == b"IDAT":
            idat += body
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    bits = depth * channels
    stride = (width * bits + 7) // 8
    step = max(1, bits // 8)
    raw = zlib.decompress(idat)
    rows, previous = [], bytes(stride)
    for y in range(height):
        kind, line = raw[y * (stride + 1)], bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for i in range(stride):
            a = line[i - step] if i >= step else 0
            b, c = previous[i], previous[i - step] if i >= step else 0
            if kind == 1: line[i] = (line[i] + a) & 255
            elif kind == 2: line[i] = (line[i] + b) & 255
            elif kind == 3: line[i] = (line[i] + (a + b) // 2) & 255
            elif kind == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        previous = bytes(line)
        pixels = []
        for x in range(width):
            if depth < 8:
                bit = x * depth
                value = (line[bit // 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1)
                samples = [value]
            else:
                samples = list(line[x * channels:(x + 1) * channels])
            if color == 3:
                r, g, b = palette[samples[0]]
                alpha = trns[samples[0]] if trns and samples[0] < len(trns) else 255
                pixels.append((r, g, b, alpha))
            elif color == 0:
                v = samples[0] * 255 // ((1 << depth) - 1)
                pixels.append((v, v, v, 255))
            elif color == 4:
                pixels.append((samples[0], samples[0], samples[0], samples[1]))
            elif color == 2:
                pixels.append((*samples, 255))
            else:
                pixels.append(tuple(samples))
        rows.append(pixels)
    return rows


def write_png(path: Path, rows) -> None:
    height, width = len(rows), len(rows[0])
    raw = b"".join(b"\0" + bytes(c for px in row for c in px) for row in rows)
    chunk = lambda kind, body: struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def checked(path: Path) -> Path:
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    expected = SOURCES.get(path.name)
    if expected != digest:
        raise SystemExit(f"{path}: SHA-256 {digest} erwartet {expected}")
    return path


def tint(rows, color):
    return [[(r * color[0] // 255, g * color[1] // 255, b * color[2] // 255, a) for r, g, b, a in row] for row in rows]


def over(base, top):
    out = []
    for row_b, row_t in zip(base, top):
        out.append([t if t[3] == 255 else b if t[3] == 0 else
                    tuple((tc * t[3] + bc * (255 - t[3])) // 255 for tc, bc in zip(t[:3], b[:3])) + (255,)
                    for b, t in zip(row_b, row_t)])
    return out


def black_to_alpha(rows):
    # Flammen liegen in Minetest als RGB ohne Alpha vor; Schwarz wird fuer das Sprite durchsichtig.
    return [[(r, g, b, 0 if r + g + b < 24 else a) for r, g, b, a in row] for row in rows]


def rope():
    # Eigene Zeichnung (CC0): zweispaltiges braunes Seil mittig.
    return [[(110, 80, 40, 255) if x in (7, 8) else (0, 0, 0, 0) for x in range(TILE)] for _ in range(TILE)]


def terrain(mtg: Path):
    def tex(mod, name):
        return read_png(checked(mtg / "mods" / mod / "textures" / name))
    d = lambda name: tex("default", f"default_{name}.png")
    wool = lambda name: tex("wool", f"wool_{name}.png")
    stone = d("stone")
    white = wool("white")
    # Kachelnummer -> Bild; Nummern aus ClassiCube src/Block.c (core_blockDefs, Classic- und CPE-Bloecke).
    tiles = {
        0: d("grass"), 1: stone, 2: d("dirt"), 3: over(d("dirt"), d("grass_side")), 4: d("wood"),
        5: d("stone_brick"), 6: d("stone_block"), 7: d("brick"),
        8: tex("tnt", "tnt_side.png"), 9: tex("tnt", "tnt_top.png"), 10: tex("tnt", "tnt_bottom.png"),
        11: rope(), 12: tex("flowers", "flowers_rose.png"), 13: tex("flowers", "flowers_dandelion_yellow.png"),
        14: d("water"), 15: d("sapling"), 16: d("cobble"), 17: tint(stone, (90, 90, 90)),
        18: d("sand"), 19: d("gravel"), 20: d("tree"), 21: d("tree_top"), 22: d("leaves"),
        23: d("steel_block"), 24: d("gold_block"), 25: d("sandstone"), 26: d("desert_stone_block"),
        28: tex("flowers", "flowers_mushroom_red.png"), 29: tex("flowers", "flowers_mushroom_brown.png"),
        30: d("lava"),
        32: over(stone, d("mineral_gold")), 33: over(stone, d("mineral_iron")), 34: over(stone, d("mineral_coal")),
        35: d("bookshelf"), 36: d("mossycobble"), 37: d("obsidian"),
        38: black_to_alpha(tex("fire", "fire_basic_flame.png")),
        39: d("steel_block"), 40: d("gold_block"), 41: d("sandstone"), 42: d("desert_stone_block"),
        48: tint(white, (230, 210, 80)), 49: d("glass"), 50: d("snow"), 51: d("ice"),
        52: d("stone_brick"), 53: tint(d("wood"), (200, 150, 100)), 54: tint(d("stone_block"), (240, 240, 230)),
        55: d("steel_block"), 56: d("gold_block"), 57: d("sandstone"), 58: d("desert_stone_block"),
        64: wool("red"), 65: wool("orange"), 66: wool("yellow"), 67: wool("green"), 68: wool("dark_green"),
        69: tint(white, (40, 170, 140)), 70: tint(white, (90, 200, 230)), 71: wool("cyan"), 72: wool("blue"),
        73: tint(white, (80, 60, 170)), 74: wool("violet"), 75: wool("magenta"), 76: wool("pink"),
        77: wool("black"), 78: wool("grey"), 79: white,
        80: tint(white, (250, 190, 210)), 81: tint(white, (40, 110, 40)), 82: wool("brown"),
        83: tint(white, (30, 50, 150)), 84: tint(white, (60, 210, 190)),
        86: tint(d("lava"), (255, 120, 60)),
    }
    atlas = [[(0, 0, 0, 0)] * (TILE * 16) for _ in range(TILE * 16)]
    for index, rows in tiles.items():
        assert len(rows) == TILE and len(rows[0]) == TILE, index
        ox, oy = index % 16 * TILE, index // 16 * TILE
        for y in range(TILE):
            atlas[oy + y][ox:ox + TILE] = rows[y]
    return atlas


def font(header: Path):
    text = checked(header).read_text(encoding="ascii")
    body = text[text.index("font8x8_basic[128][8]"):]
    glyphs = [[int(v, 16) for v in re.findall(r"0x[0-9A-Fa-f]{2}", line)]
              for line in body.splitlines() if line.strip().startswith("{ 0x")]
    assert len(glyphs) == 128 and all(len(g) == 8 for g in glyphs)
    # ClassiCube: 16x16 Zellen in Zeichencode-Reihenfolge, weiss auf transparent; Bit 0 ist das linke Pixel.
    image = [[(0, 0, 0, 0)] * 128 for _ in range(128)]
    for code, rows in enumerate(glyphs):
        ox, oy = code % 16 * 8, code // 16 * 8
        for y, bits in enumerate(rows):
            for x in range(8):
                if bits >> x & 1:
                    image[oy + y][ox + x] = (255, 255, 255, 255)
    return image


def main() -> None:
    mtg, header, out = map(Path, sys.argv[1:4])
    for line in PINNED_FILE.read_text(encoding="ascii").splitlines():
        if line.strip():
            digest, name = line.split()
            SOURCES[name] = digest
    out.mkdir(parents=True, exist_ok=True)
    write_png(out / "terrain.png", terrain(mtg))
    write_png(out / "default.png", font(header))
    for name in ("terrain.png", "default.png"):
        print(hashlib.sha256((out / name).read_bytes()).hexdigest(), name)


if __name__ == "__main__":
    main()
