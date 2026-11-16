import hashlib
import http.client
import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest import mock
import urllib.error

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
        (build.HERE / "Makefile.config").write_text("override NETSURF_USE_DUKTAPE := NO\n")
        archive = build.TOOLS / "dl/source.tar.gz"
        with tarfile.open(archive, "w:gz") as tar:
            data = b"one\n"
            info = tarfile.TarInfo("source/value.txt")
            info.size = len(data)
            tar.addfile(info, io.BytesIO(data))
        build.ARCHIVES = {"netsurf": ("https://example.invalid/source.tar.gz",
            hashlib.sha256(archive.read_bytes()).hexdigest(), "source"),
            **{name: self.original[2][name] for name in build.GENERATORS}}

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

    def test_generator_change_uses_fresh_sources(self):
        for name in build.GENERATORS:
            first = build.fetch("netsurf")
            url, _, folder = build.ARCHIVES[name]
            build.ARCHIVES[name] = (url, "0" * 64, folder)
            self.assertNotEqual(build.fetch("netsurf"), first, name)

    def test_config_change_uses_fresh_sources(self):
        first = build.fetch("netsurf")
        (build.HERE / "Makefile.config").write_text("override NETSURF_USE_DUKTAPE := YES\n")
        self.assertNotEqual(build.fetch("netsurf"), first)

    def test_failed_patch_does_not_poison_next_build(self):
        self.patch("missing", "two")
        with self.assertRaises(SystemExit):
            build.fetch("netsurf")
        self.patch("one", "three")
        source = build.fetch("netsurf")
        self.assertEqual((source / "value.txt").read_text(), "three\n")


class NetSurfDownloadTests(unittest.TestCase):
    DATA = b"archive"

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.archive = Path(self.temp.name) / "gperf.tar.gz"
        digest = hashlib.sha256(self.DATA).hexdigest()
        patches = [mock.patch.object(build, "ARCHIVES", {"gperf": ("https://main/a", digest, "gperf")}),
                   mock.patch.object(build, "MIRRORS", {"gperf": ["https://mirror/a"]}),
                   mock.patch.object(build.time, "sleep")]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)
        self.calls = []

    def serve(self, answers):
        def urlopen(url, timeout):
            self.calls.append(url)
            answer = answers[url].pop(0)
            if isinstance(answer, Exception):
                raise answer
            return io.BytesIO(answer)
        return mock.patch.object(build.urllib.request, "urlopen", urlopen)

    def test_retries_main_source_after_error(self):
        answers = [urllib.error.URLError("weg"), http.client.IncompleteRead(b"arc"), self.DATA[:3], self.DATA]
        with mock.patch.object(build, "ATTEMPTS", 4), self.serve({"https://main/a": answers}):
            build.download("gperf", self.archive)
        self.assertEqual(self.archive.read_bytes(), self.DATA)
        self.assertEqual(self.calls, ["https://main/a"] * 4)

    def test_failing_main_source_switches_to_mirror(self):
        with self.serve({"https://main/a": [TimeoutError()] * 3, "https://mirror/a": [self.DATA]}):
            build.download("gperf", self.archive)
        self.assertEqual(self.archive.read_bytes(), self.DATA)
        self.assertEqual(self.calls, ["https://main/a"] * 3 + ["https://mirror/a"])

    def test_all_sources_failing_exits_without_leftovers(self):
        error = urllib.error.HTTPError("https://main/a", 503, "busy", {}, None)
        with self.serve({"https://main/a": [error] * 3, "https://mirror/a": [b"kaputt"] * 3}):
            with self.assertRaises(SystemExit) as raised:
                build.download("gperf", self.archive)
        self.assertIn("https://mirror/a", str(raised.exception))
        self.assertEqual(len(self.calls), 2 * build.ATTEMPTS)
        self.assertEqual(list(Path(self.temp.name).iterdir()), [])


if __name__ == "__main__":
    unittest.main()
