"""Fuzz-Test der Hostschicht (Roadmap 12.4, Paket I2-F): jede Funktion aus ZEHOS_HOST_FUNCTIONS wird 10.000-mal mit
Zufallsargumenten gegen einen von Schutzseiten umgebenen Gastspeicher aufgerufen (benoetigt Linux und gcc)."""
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
HOST = ROOT / "ProjektZeh/Native/Wasm/host"
HARNESS = ROOT / "scripts/tests/host-fuzz.c"
SEED = "0x5EED5EED"
CALLS = 10000
COMPILER = shutil.which("gcc")


def compile_harness(core, output):
    """Uebersetzt Harness und Kern; mit Adress- und UB-Pruefung, wo gcc sie kann (sonst mit Hinweis ohne)."""
    base = [COMPILER, "-g", "-O1", "-I", str(HOST), str(HARNESS), str(core), "-o", str(output)]
    sanitize = ["-fsanitize=address,undefined", "-fno-sanitize-recover=undefined"]
    result = subprocess.run(base + sanitize, capture_output=True, text=True)
    if result.returncode == 0:
        return True
    print("Hinweis: gcc ohne -fsanitize=address,undefined; Fuzz-Test laeuft ohne Sanitizer.", file=sys.stderr)
    subprocess.run(base, check=True)
    return False


def run(binary, *args, timeout=170):
    return subprocess.run([str(binary), *map(str, args)], capture_output=True, text=True, timeout=timeout)


@unittest.skipUnless(sys.platform.startswith("linux"), "braucht mmap/mprotect (Linux)")
@unittest.skipIf(COMPILER is None, "gcc fehlt")
@unittest.skipUnless(platform.machine().lower() in ("x86_64", "amd64", "aarch64", "arm64"),
                     "host-fuzz.c ruft ueber einen einheitlichen Funktionszeiger (nur x86-64/aarch64)")
class HostFuzzTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.work = Path(cls.temp.name)
        cls.binary = cls.work / "host-fuzz"
        cls.sanitized = compile_harness(HOST / "zehos_host_core.c", cls.binary)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def mutant(self, name, old, new):
        """Kern mit absichtlich kaputter Bereichspruefung: der Fuzz-Test muss das finden (Pruefung der Pruefung)."""
        source = (HOST / "zehos_host_core.c").read_text(encoding="utf-8")
        self.assertEqual(source.count(old), 1, "Mutationsstelle in zehos_host_core.c nicht eindeutig")
        core = self.work / f"{name}.c"
        core.write_text(source.replace(old, new), encoding="utf-8")
        binary = self.work / name
        sanitized = compile_harness(core, binary)
        return binary, sanitized

    def test_every_host_function_survives_10000_random_calls(self):
        header = (HOST / "zehos_host_core.h").read_text(encoding="utf-8")
        listed = len(re.findall(r"^\s+X\(\w+, \w+, [iv],", header, re.MULTILINE))
        self.assertGreater(listed, 50)
        result = run(self.binary, CALLS, SEED)
        self.assertEqual(result.returncode, 0, result.stdout[-400:] + result.stderr[-2000:])
        summary = re.search(r"FUZZ-OK funktionen=(\d+) aufrufe=(\d+) fortgesetzt=(\d+) trap_speicher=(\d+) trap_exit=(\d+)", result.stdout)
        self.assertIsNotNone(summary, result.stdout[-400:])
        functions, calls, ok, memory, exit_ = map(int, summary.groups())
        # Die Tabelle des Harness kommt aus demselben Makro wie die Bindungen; jede Funktion bekam alle Aufrufe.
        self.assertEqual(functions, listed)
        self.assertEqual(calls, listed * CALLS)
        self.assertEqual(ok + memory + exit_, calls)
        # Beide Wege muessen oft vorkommen, sonst prueft der Zufall nur eine Seite (Speicherfehler oder tiefe Pfade).
        self.assertGreater(ok, calls // 4)
        self.assertGreater(memory, calls // 10)
        self.assertEqual(exit_, CALLS)  # nur proc_exit endet mit TRAP_EXIT

    def test_same_seed_gives_same_result(self):
        first = run(self.binary, 300, SEED)
        second = run(self.binary, 300, SEED)
        other = run(self.binary, 300, "0x1234")
        self.assertEqual(first.stdout, second.stdout)
        self.assertNotEqual(first.stdout, other.stdout)

    def test_guard_page_reports_access_behind_memory(self):
        result = run(self.binary, 1, SEED, "-", -1, "--guard-selbsttest")
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("Zugriff ausserhalb", result.stderr)

    def test_unknown_function_is_an_error(self):
        result = run(self.binary, 1, SEED, "gibt_es_nicht")
        self.assertEqual(result.returncode, 1)
        self.assertIn("unbekannt", result.stderr)

    def assert_found_and_reproducible(self, mutant):
        binary, sanitized = mutant
        # Ohne Sanitizer faengt nur die Schutzseite (SIGSEGV); das gilt als Fund, wird aber im Text gekennzeichnet.
        note = "" if sanitized else " (Mutant ohne Sanitizer uebersetzt)"
        result = run(binary, CALLS, SEED)
        self.assertEqual(result.returncode, 2, "Fuzz-Test fand den eingebauten Fehler nicht" + note + ": " + result.stdout[-300:])
        found = re.search(r"FUZZ-FUND: Zugriff ausserhalb \(SIG\w+\) Funktion=(\w+) Nummer=(\d+) Saat=0x5EED5EED", result.stderr)
        self.assertIsNotNone(found, result.stderr[-500:])
        # Funktion und Nummer allein genuegen, den Fund einzeln zu wiederholen.
        again = run(binary, CALLS, SEED, found.group(1), found.group(2))
        self.assertEqual(again.returncode, 2, again.stderr[-500:])

    def test_off_by_one_in_range_check_is_found(self):
        binary = self.mutant("off-by-one", "length <= (uint64_t)(call->memorySize - offset);",
                             "length <= (uint64_t)(call->memorySize - offset) + 1;")
        self.assert_found_and_reproducible(binary)

    def test_wrapping_32_bit_sum_in_range_check_is_found(self):
        binary = self.mutant("wrap", "return offset <= call->memorySize && length <= (uint64_t)(call->memorySize - offset);",
                             "return (uint32_t)(offset + length) <= call->memorySize;")
        self.assert_found_and_reproducible(binary)


if __name__ == "__main__":
    unittest.main()
