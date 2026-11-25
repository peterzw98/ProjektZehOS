"""Prueft die AOT-Sperre nach einer CPU-Ausnahme mit der echten C-Bruecke (benoetigt gcc)."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
KERNEL = ROOT / "ProjektZeh"
WAMR = KERNEL / "ThirdParty/wamr/core"


@unittest.skipIf(shutil.which("gcc") is None, "gcc fehlt")
class AotGuardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory()
        cls.program = Path(cls.directory.name) / "aot-guard"
        subprocess.run(["gcc", "-std=gnu11", "-O1", "-DZEHOS_WASM_AOT", "-no-pie",
                        "-I", KERNEL / "Native/Wasm/host", "-I", KERNEL / "Native/Wasm/wamr-platform",
                        "-I", WAMR / "iwasm/include", "-I", WAMR / "shared/platform/include", "-I", WAMR,
                        ROOT / "scripts/tests/aot-guard-host.c", KERNEL / "Native/Wasm/host/zehos_host_wamr.c",
                        "-Wl,--unresolved-symbols=ignore-all", "-o", cls.program], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.directory.cleanup()

    def check(self, crash):
        result = subprocess.run([self.program, crash], capture_output=True, text=True, timeout=30)
        self.assertEqual((result.returncode, result.stdout.strip()), (0, "OK"), result.stdout + result.stderr)

    def test_crash_while_starting_wamr_blocks_aot(self):
        self.check("init")

    def test_crash_during_run_blocks_aot(self):
        self.check("run")


if __name__ == "__main__":
    unittest.main()
