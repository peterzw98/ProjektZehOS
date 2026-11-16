#!/usr/bin/env python3
"""Baut die Rechner-App als WASIp1-Modul und stellt den Paketinhalt zusammen.

Ergebnis: experiments/Rechner.Wasi/bin/package/ mit rechner.wasm, LICENSE.txt (GPL-3.0, Lizenz des Repositorys) und
licenses/ (statisch gelinkte wasi-libc und compiler-rt, SOURCES.txt mit Quellverweis).
wasi-sdk 25 kommt mit festem SHA-256 ueber denselben Abruf wie der NetSurf-Build (.tools/netsurf/).
Vorher laufen die Hosttests der Rechner-Logik mit dem nativen C-Compiler (cc).
"""

import importlib.util
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
PACKAGE = HERE / "bin" / "package"
SOURCES = [HERE / "src" / "calc.c", HERE / "src" / "app.c"]


NETSURF = ROOT / "experiments" / "NetSurf.Wasi"


def netsurf_build():
    # wasi-sdk-Abruf, Quellverweis und Laufzeit-Herkunft teilt der Rechner mit dem NetSurf-Build.
    spec = importlib.util.spec_from_file_location("netsurf_build", NETSURF / "build.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def host_tests() -> None:
    binary = HERE / "bin" / "calc_test"
    binary.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["cc", "-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", "-I", str(HERE / "src"),
                    str(HERE / "src" / "calc.c"), str(HERE / "tests" / "calc_test.c"), "-lm", "-o", str(binary)], check=True)
    subprocess.run([str(binary)], check=True)


def main() -> int:
    host_tests()
    netsurf = netsurf_build()
    sdk = netsurf.fetch("wasi-sdk")
    if PACKAGE.exists():
        shutil.rmtree(PACKAGE)
    PACKAGE.mkdir(parents=True)
    output = PACKAGE / "rechner.wasm"
    subprocess.run([str(sdk / "bin" / "clang"), "--target=wasm32-wasip1", f"--sysroot={sdk / 'share' / 'wasi-sysroot'}",
                    "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror",
                    # Ohne Abbildung landen absolute Baupfade (Benutzername) ueber __FILE__ im Paket.
                    f"-ffile-prefix-map={HERE}=/rechner", *map(str, SOURCES), "-lm", "-o", str(output)], check=True)
    shutil.copyfile(ROOT / "LICENSE", PACKAGE / "LICENSE.txt")
    # wasi-libc (mit musl-Teilen) und compiler-rt sind statisch gelinkt; ihre Texte liefert wasi-sdk 25 nicht mit.
    licenses = PACKAGE / "licenses"
    licenses.mkdir()
    for path in sorted((NETSURF / "licenses").iterdir()):
        if path.name.startswith(("wasi-libc-", "compiler-rt-")):
            shutil.copyfile(path, licenses / path.name)
    source_lines = ["ZehOS Rechner source and build recipe", ""] + netsurf.recipe_reference(HERE)
    source_lines.extend(netsurf.wasi_runtime_sources())
    (licenses / "SOURCES.txt").write_text("\n".join(source_lines) + "\n", encoding="utf-8")
    data = output.read_bytes()
    for marker in (b"/home/", b"/Users/", str(ROOT).encode()):
        if marker in data:
            sys.exit(f"{output.name} enthaelt einen lokalen Pfad ({marker.decode()}).")
    print(f"{output} ({len(data)} Bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
