#!/usr/bin/env python3
"""Baut ClassiCube mit der ZehOS-Plattformschicht als WASIp1-Modul (classicube.wasm).

Die ClassiCube-Quellen werden nicht eingecheckt; --source zeigt auf eine Arbeitskopie am festgelegten Commit.
build.py kopiert deren src/ in einen Bauordner, wendet patches/ an und uebersetzt dort. Die Arbeitskopie bleibt
unveraendert.
"""

import argparse
import hashlib
import os
import shutil
import subprocess
import sys
from pathlib import Path

COMMIT = "2b01bee867fe81331d0a898e7ad40a71882131de"
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="ClassiCube-Arbeitskopie")
    parser.add_argument("--wasi-sdk", type=Path, default=ROOT / ".tools" / "wasi-sdk-25.0-x86_64-windows")
    parser.add_argument("--build-dir", type=Path, default=ROOT / ".tools" / "classicube-build")
    parser.add_argument("--output", type=Path, default=HERE / "bin" / "classicube.wasm")
    args = parser.parse_args()

    head = subprocess.run(["git", "-C", str(args.source), "rev-parse", "HEAD"], capture_output=True, text=True)
    if head.returncode != 0 or head.stdout.strip() != COMMIT:
        print(f"ClassiCube muss am Commit {COMMIT} stehen.", file=sys.stderr)
        return 1

    build = args.build_dir.resolve()
    if build.exists():
        shutil.rmtree(build)
    shutil.copytree(args.source / "src", build / "src")
    # Der Bauordner liegt meist in einem Git-Arbeitsbaum; ohne Grenze wuerde git apply Pfade relativ zu dessen
    # Wurzel deuten.
    environment = dict(os.environ, GIT_CEILING_DIRECTORIES=str(build.parent))
    for patch in sorted((HERE / "patches").glob("*.patch")):
        if subprocess.run(["git", "apply", str(patch)], cwd=build, env=environment).returncode != 0:
            print(f"Patch {patch.name} passt nicht.", file=sys.stderr)
            return 1

    clang = args.wasi_sdk / "bin" / ("clang.exe" if sys.platform == "win32" else "clang")
    sources = sorted(str(path) for path in (build / "src").glob("*.c"))
    sources += sorted(str(path) for path in (HERE / "platform").glob("*.c"))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    command = [str(clang), "--target=wasm32-wasip1", f"--sysroot={args.wasi_sdk / 'share' / 'wasi-sysroot'}",
               "-O2", "-D__ZEHOS__", "-D_WASI_EMULATED_SIGNAL", "-D_WASI_EMULATED_PROCESS_CLOCKS",
               "-Wno-everything", "-I", str(build / "src"),
               # Ohne Abbildung landen absolute Baupfade (Benutzername) ueber __FILE__ im Paket.
               f"-ffile-prefix-map={build}=/classicube", f"-ffile-prefix-map={HERE}=/zehos",
               *sources, "-lwasi-emulated-signal", "-lwasi-emulated-process-clocks",
               # 1 MiB Stack wie der Loader; mehr als 256 MiB linearer Speicher wird ohnehin abgelehnt.
               "-Wl,-z,stack-size=1048576", "-Wl,--max-memory=268435456", "-o", str(args.output)]
    result = subprocess.run(command)
    if result.returncode == 0:
        digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
        print(f"{args.output} ({args.output.stat().st_size} Bytes, SHA-256 {digest})")
    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
