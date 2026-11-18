#!/usr/bin/env python3
"""Fuehrt die Python-Skripttests (scripts/tests) parallel aus: je Modul ein Prozess, die langsamen Module je Test.

Es entfaellt keine Pruefung; jeder Test laeuft genau einmal wie mit `python -m unittest discover -s scripts/tests`.
Exit 0 nur, wenn jede Einheit erfolgreich war. Ausgaben fehlgeschlagener Einheiten stehen am Ende.
"""
import concurrent.futures
import os
import re
import subprocess
import sys
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TESTS = ROOT / "scripts" / "tests"
# Module mit langsamen Einzeltests (bash-/pwsh-Prozesse unter Windows): je Test ein Prozess, damit sie sich ueberlappen.
SPLIT = {"test_storage_matrix", "test_start_qemu_data_image"}


def test_ids(module: str) -> list[str]:
    def flatten(suite):
        for item in suite:
            yield from flatten(item) if isinstance(item, unittest.TestSuite) else [item.id()]
    return list(flatten(unittest.defaultTestLoader.loadTestsFromName(module)))


def units() -> list[str]:
    result: list[str] = []
    for path in sorted(TESTS.glob("test_*.py")):
        module = path.stem
        result.extend(test_ids(module) if module in SPLIT else [module])
    return result


def run(unit: str) -> tuple[str, int, float, str, int]:
    started = time.monotonic()
    environment = dict(os.environ, PYTHONPATH=str(TESTS) + os.pathsep + os.environ.get("PYTHONPATH", ""))
    done = subprocess.run([sys.executable, "-m", "unittest", unit], cwd=ROOT, env=environment, capture_output=True, text=True)
    output = done.stdout + done.stderr
    ran = re.search(r"^Ran (\d+) test", output, re.MULTILINE)
    return unit, done.returncode, time.monotonic() - started, output, int(ran.group(1)) if ran else 0


def main() -> int:
    sys.path.insert(0, str(TESTS))
    sys.path.insert(0, str(ROOT))
    names = units()
    started = time.monotonic()
    workers = min(8, os.cpu_count() or 2)
    with concurrent.futures.ThreadPoolExecutor(workers) as pool:
        results = list(pool.map(run, names))
    failed = [result for result in results if result[1] != 0]
    for unit, code, seconds, _, _ in sorted(results, key=lambda r: -r[2])[:5]:
        print(f"{seconds:7.1f} s  {'ok  ' if code == 0 else 'FAIL'} {unit}")
    for unit, _, _, output, _ in failed:
        print(f"\n=== {unit} ===\n{output}")
    total = sum(result[4] for result in results)
    print(f"\n{total} Tests in {len(results)} Einheiten, {len(failed)} fehlgeschlagen, {time.monotonic() - started:.1f} s ({workers} Prozesse)")
    return 1 if failed or total == 0 else 0


if __name__ == "__main__":
    sys.exit(main())
