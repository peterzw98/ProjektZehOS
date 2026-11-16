#!/usr/bin/env python3
"""AOT-Hostprobe (Etappe 5b): misst eine Wasm-App unter verschiedenen Laufzeiten auf dem Linux-Host.

Jeder Lauf dauert eine feste App-Zeit (ZEHOS_EXIT_MS); gezaehlt werden die present-Aufrufe ("Bilder", aus der
Abschlusszeile von zehos_host_core.c) und die CPU-Zeit des Kindprozesses. Kennzahl ist Bilder je CPU-Sekunde,
weil Apps dazwischen schlafen koennen. Die Laufzeiten bekommen dieselbe Hostlogik (zehos_host_core.c).
"""
import argparse
import os
import re
import resource
import statistics
import subprocess
from pathlib import Path

FRAMES = re.compile(r"should_exit nach (\d+) ms, (\d+) Bilder")


def run(command: list[str], app_ms: int, timeout: float) -> tuple[int, int, float]:
    before = resource.getrusage(resource.RUSAGE_CHILDREN)
    env = dict(os.environ, ZEHOS_EXIT_MS=str(app_ms))
    done = subprocess.run(command, env=env, capture_output=True, text=True, timeout=timeout)
    after = resource.getrusage(resource.RUSAGE_CHILDREN)
    cpu = (after.ru_utime - before.ru_utime) + (after.ru_stime - before.ru_stime)
    found = FRAMES.search(done.stderr)
    if found is None:
        raise RuntimeError(f"keine Abschlusszeile (Exit {done.returncode}): {done.stderr[-400:]}")
    return int(found.group(1)), int(found.group(2)), cpu


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--name", required=True)
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--app-ms", type=int, default=20000)
    parser.add_argument("command", nargs=argparse.REMAINDER, help="Laufzeitaufruf nach --")
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    rates = []
    for index in range(args.runs):
        wall, frames, cpu = run(command, args.app_ms, args.app_ms / 1000 * 5 + 60)
        rates.append(frames / cpu)
        print(f"{args.name} Lauf {index + 1}: {frames} Bilder in {wall} ms, CPU {cpu:.2f} s, {frames / cpu:.1f} Bilder/CPU-s", flush=True)
    print(f"{args.name} Median: {statistics.median(rates):.1f} Bilder/CPU-s", flush=True)


if __name__ == "__main__":
    main()
