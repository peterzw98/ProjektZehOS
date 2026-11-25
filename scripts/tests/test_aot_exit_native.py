"""Prueft den Ausstieg aus einer AOT-Schleife mit den echten Assemblerroutinen (#423, R8; gcc, Linux x86-64)."""
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
HOST = ROOT / "ProjektZeh/Native/Wasm/host"


@unittest.skipUnless(shutil.which("gcc") and sys.platform == "linux" and platform.machine() == "x86_64", "gcc auf Linux x86-64 noetig")
class AotExitTests(unittest.TestCase):
    def test_tick_leaves_loop_and_restores_caller(self):
        for level in ("-O0", "-O2"):
            with self.subTest(level), tempfile.TemporaryDirectory() as folder:
                program = Path(folder) / "aot-exit"
                build = subprocess.run(["gcc", level, "-Wall", "-mno-red-zone", "-no-pie", "-DZEHOS_WASM_AOT", f"-I{HOST}",
                                        ROOT / "scripts/tests/aot-exit-host.c", HOST / "zehos_host_aot_exit.c", "-o", program],
                                       capture_output=True, text=True)
                self.assertEqual(0, build.returncode, build.stderr)
                run = subprocess.run([program], capture_output=True, text=True, timeout=30)
                self.assertEqual((0, "OK"), (run.returncode, run.stdout.strip()), run.stdout)


if __name__ == "__main__":
    unittest.main()
