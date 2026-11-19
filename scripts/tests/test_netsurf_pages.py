"""Prueft die paketeigenen NetSurf-Testseiten (11.5/11.7) ohne Gast: Links, Markerfarben, GIF/BMP-Bilddaten."""
from html.parser import HTMLParser
from pathlib import Path
import re
import struct
import unittest

PAGES = Path(__file__).resolve().parents[2] / "experiments/NetSurf.Wasi/pages"
SCRIPT = Path(__file__).resolve().parents[1] / "test-netsurf-desktop.py"


class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.hrefs = []

    def handle_starttag(self, tag, attrs):
        if tag == "a" and dict(attrs).get("href"):
            self.hrefs.append(dict(attrs)["href"])


def gif_pixels(data):
    """Dekodiert ein einbildiges GIF (nur Palette/LZW) zu Palettenindizes."""
    width, height, flags = struct.unpack("<HHB", data[6:11])
    pos = 13 + 3 * (2 << (flags & 7)) if flags & 0x80 else 13
    assert data[pos] == 0x2c
    pos += 10
    size = data[pos]
    pos += 1
    packed = b""
    while data[pos]:
        packed += data[pos + 1:pos + 1 + data[pos]]
        pos += data[pos] + 1
    clear, end = 1 << size, (1 << size) + 1
    bits = int.from_bytes(packed, "little")
    table, width_bits, offset, out, previous = None, size + 1, 0, [], None
    while offset + width_bits <= len(packed) * 8:
        code = (bits >> offset) & ((1 << width_bits) - 1)
        offset += width_bits
        if code == clear:
            table, width_bits, previous = [[i] for i in range(clear)] + [None, None], size + 1, None
            continue
        if code == end:
            return width, height, out
        entry = table[code] if code < len(table) else previous + [previous[0]]
        out += entry
        if previous is not None:
            table.append(previous + [entry[0]])
            if len(table) == 1 << width_bits and width_bits < 12:
                width_bits += 1
        previous = entry
    raise AssertionError("GIF-Datenstrom ohne Ende-Code")


class NetSurfPagesTests(unittest.TestCase):
    def test_index_links_exist_and_guest_clicks_target_them(self):
        parser = Links()
        parser.feed((PAGES / "index.html").read_text(encoding="utf-8"))
        # Reihenfolge entspricht den Klickzeilen y=209,231,252,273,294,315 im Gastskript.
        self.assertEqual(["ziel.html", "lang.html", "formular.html", "fehlt.html", "kaputt.html", "bild.html"], parser.hrefs)
        for name in parser.hrefs:
            if name != "fehlt.html":
                self.assertTrue((PAGES / name).is_file(), name)
        script = SCRIPT.read_text(encoding="utf-8")
        for y in (294, 315):
            self.assertIn(f"frame(99,{y})", script)

    def test_broken_page_is_really_broken_but_has_marker_background(self):
        text = (PAGES / "kaputt.html").read_text(encoding="utf-8")
        self.assertIn("background: #ffd8d8", text)
        self.assertGreater(text.count("<b>") + text.count("<table>"), text.count("</b>") + text.count("</table>"))

    def test_image_page_references_existing_images(self):
        text = (PAGES / "bild.html").read_text(encoding="utf-8")
        self.assertIn("background: #d8fff0", text)
        for name in re.findall(r'<img src="([^"]+)"', text):
            self.assertTrue((PAGES / name).is_file(), name)

    def test_gif_is_solid_red_40x40(self):
        width, height, pixels = gif_pixels((PAGES / "bild.gif").read_bytes())
        self.assertEqual((40, 40, [0] * 1600), (width, height, pixels))
        self.assertEqual(b"\xc0\x00\x00", (PAGES / "bild.gif").read_bytes()[13:16])

    def test_bmp_is_solid_blue_40x40(self):
        data = (PAGES / "bild.bmp").read_bytes()
        self.assertEqual(b"BM", data[:2])
        offset, = struct.unpack("<I", data[10:14])
        width, height, planes, depth = struct.unpack("<iiHH", data[18:30])
        self.assertEqual((40, 40, 24), (width, height, depth))
        pixels = data[offset:]
        self.assertEqual(40 * 40 * 3, len(pixels))
        self.assertEqual(b"\xc0\x00\x00" * 1600, pixels)  # BGR: Blau 0000c0


if __name__ == "__main__":
    unittest.main()
