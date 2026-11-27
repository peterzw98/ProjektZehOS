import importlib.util
import os
from pathlib import Path
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class DiskCacheTests(unittest.TestCase):
    def setUp(self):
        self.saved = os.environ.pop("ZEHOS_QEMU_DISK_CACHE", None)
        self.addCleanup(self.restore)

    def restore(self):
        os.environ.pop("ZEHOS_QEMU_DISK_CACHE", None)
        if self.saved is not None:
            os.environ["ZEHOS_QEMU_DISK_CACHE"] = self.saved

    def test_default_without_env_is_empty(self):
        self.assertEqual(load("doom", "test-wasm-doom-desktop.py").disk_cache_option(), "")

    def test_guest_script_sets_cache(self):
        guest = load("guest", "test-linux-root-disk-guest.py")
        doom = load("doom2", "test-wasm-doom-desktop.py")
        guest.apply_disk_cache("unsafe")
        self.assertEqual(doom.disk_cache_option(), ",cache=unsafe")
        guest.apply_disk_cache("writeback")
        self.assertEqual(doom.disk_cache_option(), ",cache=writeback")

    def test_option_default_is_unsafe(self):
        text = (SCRIPTS / "test-linux-root-disk-guest.py").read_text()
        self.assertIn('"--disk-cache"', text)
        self.assertIn('default="unsafe"', text)


if __name__ == "__main__":
    unittest.main()
