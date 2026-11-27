"""Tests fuer scripts/fetch-data-disk-file.py: Datei aus einer mit linux-root-disk.py gebauten qcow2-Platte holen."""
import importlib.util
import io
from pathlib import Path
import shutil
import tarfile
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


lrd = load("linux_root_disk", "linux-root-disk.py")
fdf = load("fetch_data_disk_file", "fetch-data-disk-file.py")
TOOLS = all(shutil.which(tool) for tool in ("qemu-img", "mcopy", "mdir"))
PNG = b"\x89PNG\r\n\x1a\n" + bytes(range(256)) * 40


@unittest.skipUnless(TOOLS, "qemu-img/mtools fehlen")
class FetchTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, True)
        bundle = self.dir / "b.tar"
        with tarfile.open(bundle, "w") as archive:
            for name, data in (("bin/Hub", b"x"), ("shot.png", PNG)):
                info = tarfile.TarInfo(name)
                info.size, info.mode, info.mtime = len(data), 0o755, 1764201600
                archive.addfile(info, io.BytesIO(data))
        self.disk = self.dir / "d.qcow2"
        lrd.build(bundle, self.disk, "lb")

    def test_holt_datei_bytegleich(self):
        target = self.dir / "out" / "x.png"
        self.assertEqual(0, fdf.fetch(self.disk, "lb/shot.png", target))
        self.assertEqual(PNG, target.read_bytes())

    def test_fehlende_datei_ist_exit_1_und_legt_nichts_an(self):
        target = self.dir / "y.png"
        self.assertEqual(1, fdf.fetch(self.disk, "lb/fehlt.png", target))
        self.assertFalse(target.exists())


if __name__ == "__main__":
    unittest.main()
