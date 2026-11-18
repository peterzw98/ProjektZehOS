#!/usr/bin/env python3
"""Zaehlt Speicherzugriffe unter RSP (Red Zone) in WAMR-AOT-Dateien; erlaubt sind 0 (WASM_FEASIBILITY.md, Red-Zone-Befund).

  check-aot-redzone.py DATEI ...   .aot direkt; .c-Dateien der AOT-Probe (const uint8_t name[] = {0x..}) je Feld

Cosmos betritt Interrupts ohne Stackwechsel und schreibt dabei unter den RSP des unterbrochenen Codes. Das zugelassene
wamrc (fixture/wamrc-noredzone.patch) erzeugt deshalb keine Zugriffe der Form [rsp-0x..]. Diese Pruefung belegt das am
Ergebnis selbst, unabhaengig von der wamrc-Pruefsumme in build-package.py.

Grenzen: objdump disassembliert die ganze Datei roh, auch Kopf und Daten. Ein Treffer in Daten waere ein Fehlalarm und
muss von Hand geklaert werden; bei den bisherigen nrz-Dateien (Doom, ClassiCube, Probe-Fixtures) gab es keinen. Laeuft
die Dekodierung ueber Daten aus dem Takt, kann sie umgekehrt einen echten Befehl verfehlen; lea [rsp-0x..] zaehlt mit,
obwohl es nur eine Adresse berechnet. Zugriffe ueber ein anderes Basisregister, das RSP kopiert hat, erkennt die
Pruefung nicht.
"""

import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

BELOW_RSP = re.compile(r"\[rsp-0x[0-9a-f]+\]")
C_ARRAY = re.compile(r"const uint8_t (\w+)\[\] = \{(.*?)\};", re.S)


def count_below_rsp(disassembly: str) -> int:
    return len(BELOW_RSP.findall(disassembly))


def disassemble(data: bytes, objdump: str) -> str:
    with tempfile.TemporaryDirectory() as scratch:
        raw = Path(scratch) / "code.bin"
        raw.write_bytes(data)
        return subprocess.run([objdump, "-D", "-M", "intel", "-b", "binary", "-m", "i386:x86-64", str(raw)],
                              check=True, capture_output=True, text=True).stdout


def blobs(path: Path) -> list[tuple[str, bytes]]:
    """Die zu pruefenden Bytes: eine .aot als Ganzes, aus einer Fixture-.c jedes uint8_t-Feld einzeln."""
    if path.suffix != ".c":
        return [(str(path), path.read_bytes())]
    found = [(f"{path}:{name}", bytes(int(value, 16) for value in re.findall(r"0x([0-9a-fA-F]{2})", body)))
             for name, body in C_ARRAY.findall(path.read_text(encoding="utf-8"))]
    if not found:
        raise ValueError(f"{path}: kein uint8_t-Feld gefunden.")
    return found


def find_objdump() -> str:
    objdump = shutil.which("objdump")
    if objdump is None:
        raise ValueError("objdump fehlt (GNU Binutils); ohne ihn laesst sich die Red Zone einer .aot nicht pruefen.")
    return objdump


def check(paths: list[Path]) -> int:
    """Gibt je Datei bzw. Feld den Zaehler aus und liefert die Summe."""
    objdump = find_objdump()
    total = 0
    for path in paths:
        for name, data in blobs(path):
            hits = count_below_rsp(disassemble(data, objdump))
            print(f"{name}: {hits} Zugriffe unter RSP")
            total += hits
    return total


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        total = check([Path(item) for item in argv])
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Fehler: {error}", file=sys.stderr)
        return 2
    if total:
        print(f"Red Zone benutzt ({total} Zugriffe): mit dem zugelassenen wamrc neu uebersetzen.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
