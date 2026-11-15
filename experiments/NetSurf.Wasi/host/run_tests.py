#!/usr/bin/env python3
"""Spielt Eingabeszenarien über den Linux-Prüfhost ab und prüft das letzte Bild anhand der Markerfarben der Testseiten.

Aufruf vom Repository-Stamm: python3 experiments/NetSurf.Wasi/host/run_tests.py --iwasm <iwasm> --host <libzehos_host.so>
Voraussetzung: build.py ist gelaufen. Ereignisse folgen dem poll_event-ABI: <ms> <typ> <code> <x> <y>.
Die Klickpositionen gelten für die Startseite im 632×392-Bild.
"""

import argparse
import os
import subprocess
import sys
import tempfile
from pathlib import Path

PACKAGE = Path(__file__).resolve().parents[1] / "bin" / "package"
START, TARGET, FORM, LONG = (0x00, 0x50, 0xa0), (0x00, 0x80, 0x00), (0x60, 0x00, 0xa0), (0xa0, 0x60, 0x00)
LONG_BACKGROUND = (0xff, 0xf4, 0xd0)
CLICK_TARGET, CLICK_LONG, CLICK_FORM, CLICK_BROKEN = (80, 209), (80, 230), (80, 251), (80, 272)


def click(at: int, position: tuple[int, int]) -> list[str]:
    x, y = position
    return [f"{at} 5 1 {x} {y}", f"{at + 50} 6 1 {x} {y}"]


# Name, Ereignisse, erwartete Farben, verbotene Farben
SCENARIOS = [
    ("start", [], [START], [TARGET]),
    ("klick-ohne-bewegung", click(1500, CLICK_TARGET), [TARGET], [START]),
    ("druck-dann-reset", ["1500 4 0 80 209", "1550 5 1 80 209", "1600 8 0 0 0", "2500 4 0 300 300"], [START], [TARGET]),
    ("reset-dann-klick", ["1500 5 1 80 209", "1600 8 0 0 0", *click(1700, CLICK_FORM)], [FORM], [TARGET]),
    ("kaputter-link", click(1500, CLICK_BROKEN), [], [START, TARGET, FORM, LONG]),
    ("rad-abwaerts", [*click(1500, CLICK_LONG), "2500 7 -5 300 200"], [LONG_BACKGROUND], [LONG]),
]


def colours(path: Path) -> set[tuple[int, int, int]]:
    data = path.read_bytes()
    header, width_height, depth, pixels = data.split(b"\n", 3)
    if header != b"P6" or depth != b"255":
        raise ValueError(f"{path} ist kein P6-Bild")
    return {tuple(pixels[i:i + 3]) for i in range(0, len(pixels), 3)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iwasm", type=Path, required=True)
    parser.add_argument("--host", type=Path, required=True)
    args = parser.parse_args()
    failures = 0
    with tempfile.TemporaryDirectory() as work:
        for name, events, expected, forbidden in SCENARIOS:
            script, frame = Path(work) / f"{name}.txt", Path(work) / f"{name}.ppm"
            script.write_text("".join(f"{line}\n" for line in events))
            env = dict(os.environ, ZEHOS_EVENTS=str(script), ZEHOS_PPM=str(frame), ZEHOS_EXIT_MS="3500")
            run = subprocess.run([str(args.iwasm), f"--native-lib={args.host.resolve()}", f"--map-dir=/::{PACKAGE}",
                                  "--stack-size=1048576", str(PACKAGE / "netsurf.wasm")],
                                 env=env, capture_output=True, text=True, timeout=60)
            seen = colours(frame) if frame.exists() else set()
            problems = []
            if run.returncode != 0:
                problems.append(f"Exitcode {run.returncode}")
            problems += [f"Farbe {c} fehlt" for c in expected if c not in seen]
            problems += [f"Farbe {c} unerwartet" for c in forbidden if c in seen]
            failures += bool(problems)
            print(f"{'FAIL' if problems else 'ok  '} {name}" + (f": {', '.join(problems)}" if problems else ""))
    print(f"{len(SCENARIOS) - failures}/{len(SCENARIOS)} bestanden")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
