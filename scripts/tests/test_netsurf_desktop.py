import ast
import itertools
from pathlib import Path
import struct
import tempfile
import textwrap
from types import SimpleNamespace
import unittest
from unittest.mock import Mock
import zlib


SCRIPT = Path(__file__).resolve().parents[1] / "test-netsurf-desktop.py"
COLOR = bytes.fromhex("d8ecff")
WIDTH, HEIGHT = 100, 50
POINTS = ((0, 49), (99, 25), (99, 49))
READY = COLOR * (WIDTH * HEIGHT)


def helpers(guest, work, clock):
    # Die echten lokalen Helfer ausfuehren, ohne den VM-Start in main aufzurufen.
    main = next(node for node in ast.parse(SCRIPT.read_text(encoding="utf-8")).body
                if isinstance(node, ast.FunctionDef) and node.name == "main")
    functions = [node for node in main.body
                 if isinstance(node, ast.FunctionDef) and node.name in ("capture", "page")]
    source = "def factory(guest, work):\n    step = 0\n"
    source += textwrap.indent("\n".join(ast.unparse(node) for node in functions), "    ")
    source += "\n    return capture, page\n"
    namespace = dict(time=clock, struct=struct, zlib=zlib)
    exec(compile(source, str(SCRIPT), "exec"), namespace)
    return namespace["factory"](guest, work)


class NetSurfDesktopTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.work = Path(directory.name)
        self.clock = SimpleNamespace(monotonic=Mock(side_effect=itertools.count(step=10)), sleep=Mock())

    def guest(self, frames, size=(WIDTH, HEIGHT)):
        frames = iter(frames)

        def command(name, arguments):
            self.assertEqual("screendump", name)
            width, height = size
            Path(arguments["filename"]).write_bytes(
                f"P6\n{width} {height}\n255\n".encode() + next(frames))

        qmp = SimpleNamespace(command=Mock(side_effect=command),
                              shot=Mock(side_effect=AssertionError("Zweiter Screendump")))
        return SimpleNamespace(qmp=qmp, size=size)

    def test_maximize_waits_for_every_edge_in_same_frame(self):
        for x, y in POINTS:
            with self.subTest(point=(x, y)):
                old = bytearray(READY)
                offset = (y * WIDTH + x) * 3
                old[offset:offset + 3] = bytes(3)
                guest = self.guest([bytes(old), READY])
                _, page = helpers(guest, self.work, self.clock)
                self.assertEqual(READY, page("maximiert", "d8ecff", POINTS))
                self.assertEqual(2, guest.qmp.command.call_count)

    def test_color_and_edges_must_be_ready_together(self):
        old = bytearray(READY)
        old[(49 * WIDTH) * 3:(49 * WIDTH) * 3 + 3] = bytes(3)
        guest = self.guest([bytes(old), b"\x11" * len(READY), READY])
        _, page = helpers(guest, self.work, self.clock)
        self.assertEqual(READY, page("maximiert", "d8ecff", POINTS))
        self.assertEqual(3, guest.qmp.command.call_count)

    def test_black_or_outside_edges_timeout(self):
        for points in (POINTS, ((WIDTH, 0),), ((0, HEIGHT),), ((-1, 0),)):
            with self.subTest(points=points):
                clock = SimpleNamespace(monotonic=Mock(side_effect=itertools.count(step=10)), sleep=Mock())
                pixels = bytearray(READY)
                pixels[(49 * WIDTH) * 3:(49 * WIDTH) * 3 + 3] = bytes(3)
                guest = self.guest(itertools.repeat(bytes(pixels)))
                _, page = helpers(guest, self.work, clock)
                with self.assertRaises(AssertionError):
                    page("maximiert", "d8ecff", points)

    def test_ordinary_page_still_checks_color(self):
        guest = self.guest([bytes(len(READY)), READY])
        _, page = helpers(guest, self.work, self.clock)
        self.assertEqual(READY, page("start", "d8ecff"))
        self.assertEqual(2, guest.qmp.command.call_count)

    def test_png_contains_exact_checked_frame_from_single_screendump(self):
        pixels = bytes(range(18))
        guest = self.guest([pixels], size=(3, 2))
        capture, _ = helpers(guest, self.work, self.clock)
        self.assertEqual(pixels, capture("probe"))
        self.assertEqual(1, guest.qmp.command.call_count)
        guest.qmp.shot.assert_not_called()
        png = (self.work / "01-probe.png").read_bytes()
        self.assertEqual(b"\x89PNG\r\n\x1a\n", png[:8])
        offset, compressed = 8, b""
        while offset < len(png):
            length, kind = struct.unpack(">I4s", png[offset:offset + 8])
            data = png[offset + 8:offset + 8 + length]
            if kind == b"IHDR":
                self.assertEqual((3, 2, 8, 2, 0, 0, 0), struct.unpack(">IIBBBBB", data))
            if kind == b"IDAT":
                compressed += data
            offset += length + 12
        self.assertEqual(b"\0" + pixels[:9] + b"\0" + pixels[9:], zlib.decompress(compressed))


if __name__ == "__main__":
    unittest.main()
