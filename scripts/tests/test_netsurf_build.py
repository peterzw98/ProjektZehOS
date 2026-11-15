import hashlib
import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("netsurf_build", ROOT / "experiments/NetSurf.Wasi/build.py")
build = importlib.util.module_from_spec(spec)
spec.loader.exec_module(build)


class NetSurfBuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.original = build.TOOLS, build.HERE, build.ARCHIVES
        self.addCleanup(self.restore)
        base = Path(self.temp.name)
        build.TOOLS, build.HERE = base / "tools", base / "recipe"
        (build.TOOLS / "dl").mkdir(parents=True)
        (build.HERE / "patches").mkdir(parents=True)
        archive = build.TOOLS / "dl/source.tar.gz"
        with tarfile.open(archive, "w:gz") as tar:
            data = b"one\n"
            info = tarfile.TarInfo("source/value.txt")
            info.size = len(data)
            tar.addfile(info, io.BytesIO(data))
        build.ARCHIVES = {"netsurf": ("https://example.invalid/source.tar.gz",
            hashlib.sha256(archive.read_bytes()).hexdigest(), "source")}

    def restore(self):
        build.TOOLS, build.HERE, build.ARCHIVES = self.original

    def patch(self, old, new):
        (build.HERE / "patches/change.patch").write_text(
            f"--- a/value.txt\n+++ b/value.txt\n@@ -1 +1 @@\n-{old}\n+{new}\n")

    def test_recipe_change_uses_fresh_sources_and_keeps_previous_result(self):
        self.patch("one", "two")
        first = build.fetch("netsurf")
        self.assertEqual((first / "value.txt").read_text(), "two\n")
        self.assertEqual(build.fetch("netsurf"), first)
        self.patch("one", "three")
        second = build.fetch("netsurf")
        self.assertEqual((second / "value.txt").read_text(), "three\n")
        self.assertEqual((first / "value.txt").read_text(), "two\n")

    def test_failed_patch_does_not_poison_next_build(self):
        self.patch("missing", "two")
        with self.assertRaises(SystemExit):
            build.fetch("netsurf")
        self.patch("one", "three")
        source = build.fetch("netsurf")
        self.assertEqual((source / "value.txt").read_text(), "three\n")


if __name__ == "__main__":
    unittest.main()
