"""Prueft end_call der WAMR-Bindung bei einem Abbruch ohne Nutzerdaten des Laufs (Startfunktion, benoetigt gcc)."""
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(shutil.which("gcc") and os.name == "posix" and platform.machine() == "x86_64", "gcc auf x86-64 noetig")
class WamrAbortTests(unittest.TestCase):
    def test_abort_without_run_data_does_not_touch_null(self):
        host = ROOT / "ProjektZeh/Native/Wasm/host"
        wamr = ROOT / "ProjektZeh/ThirdParty/wamr/core"
        includes = [host, host.parent / "wamr-platform", wamr / "shared/platform/include", wamr / "shared/utils", wamr / "iwasm/include"]
        with tempfile.TemporaryDirectory() as folder:
            program = Path(folder) / "wamr-abort"
            build = subprocess.run(
                ["gcc", "-O1", "-Wall", "-ffunction-sections", "-fdata-sections", "-Wl,--gc-sections", "-DZEHOS_WASM_AOT",
                 "-DBH_PLATFORM_ZEHOS", "-DBUILD_TARGET_X86_64", "-DWASM_ENABLE_AOT=1", *(f"-I{path}" for path in includes), ROOT / "scripts/tests/wamr-abort-host.c", host / "zehos_host_core.c", "-o", program],
                capture_output=True, text=True)
            self.assertEqual(0, build.returncode, build.stderr)
            run = subprocess.run([program], capture_output=True, text=True, timeout=30)
        # Ohne die Pruefung endet der Lauf mit SIGSEGV (-11).
        self.assertEqual(0, run.returncode, run.stdout)
        self.assertEqual("with_run=1 without_run=1", run.stdout.strip())


if __name__ == "__main__":
    unittest.main()
