#!/usr/bin/env python3
"""Baut ClassiCube mit der ZehOS-Plattformschicht als WASIp1-Modul (classicube.wasm).

Die ClassiCube-Quellen werden nicht eingecheckt; --source zeigt auf eine Arbeitskopie am festgelegten Commit.
build.py kopiert deren src/ in einen Bauordner, wendet patches/ an und uebersetzt dort. Die Arbeitskopie bleibt
unveraendert.
"""

import argparse
import hashlib
import importlib.util
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

COMMIT = "2b01bee867fe81331d0a898e7ad40a71882131de"
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
WRAPPED = ["Gfx_DrawVb_IndexedTris_Range", "Gfx_DrawVb_IndexedTris", "Gfx_DrawIndexedTris_T2fC4b", "Gfx_ClearBuffers",
           "Gfx_EndFrame", "Builder_MakeChunk", "Gen_Start", "Gen_IsDone"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="ClassiCube-Arbeitskopie")
    parser.add_argument("--wasi-sdk", type=Path, default=ROOT / ".tools" / "wasi-sdk-25.0-x86_64-windows")
    parser.add_argument("--build-dir", type=Path, default=ROOT / ".tools" / "classicube-build")
    parser.add_argument("--output", type=Path, default=HERE / "bin" / "classicube.wasm")
    # SoftMin hat keinen Alphatest (Schrift und Oberflaeche werden zu Balken); nur zum Vergleich der Rasterkosten.
    parser.add_argument("--gfx", choices=["softgpu", "softmin"], default="softgpu")
    # Messanzeige im Chat (platform/Measure_ZehOS.c); fuer veroeffentlichte Pakete abschalten.
    parser.add_argument("--no-measure", action="store_true")
    args = parser.parse_args()

    head = subprocess.run(["git", "-C", str(args.source), "rev-parse", "HEAD"], capture_output=True, text=True)
    if head.returncode != 0 or head.stdout.strip() != COMMIT:
        print(f"ClassiCube muss am Commit {COMMIT} stehen.", file=sys.stderr)
        return 1

    build = args.build_dir.resolve()
    # Nur den eigenen Unterordner loeschen; ein vertippter --build-dir darf keine Arbeitskopie treffen.
    if (build / "src").exists():
        shutil.rmtree(build / "src")
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
    sources += sorted(str(path) for path in (HERE / "platform").glob("*.c")
                      if not (args.no_measure and path.name == "Measure_ZehOS.c"))
    wrap = [] if args.no_measure else ["-Wl,--wrap=" + ",--wrap=".join(WRAPPED)]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    command = [str(clang), "--target=wasm32-wasip1", f"--sysroot={args.wasi_sdk / 'share' / 'wasi-sysroot'}",
               "-O2", "-D__ZEHOS__", f"-DCC_GFX_BACKEND=CC_GFX_BACKEND_{args.gfx.upper()}", "-D_WASI_EMULATED_SIGNAL", "-D_WASI_EMULATED_PROCESS_CLOCKS",
               "-Wno-everything", "-I", str(build / "src"),
               # Ohne Abbildung landen absolute Baupfade (Benutzername) ueber __FILE__ im Paket.
               f"-ffile-prefix-map={build}=/classicube", f"-ffile-prefix-map={HERE}=/zehos",
               *sources, "-lwasi-emulated-signal", "-lwasi-emulated-process-clocks",
               # 1 MiB Stack wie der Loader; mehr als 256 MiB linearer Speicher wird ohnehin abgelehnt.
               "-Wl,-z,stack-size=1048576", "-Wl,--max-memory=268435456",
               # DWARF aus libc und compiler-rt (rund 200 KB) braucht das Paket nicht.
               "-Wl,--strip-debug",
               # Messpunkte fuer den Entscheidungspunkt (platform/Measure_ZehOS.c), ohne den Kern zu patchen.
               *wrap, "-o", str(args.output)]
    result = subprocess.run(command)
    if result.returncode != 0:
        return result.returncode
    digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
    print(f"{args.output} ({args.output.stat().st_size} Bytes, SHA-256 {digest})")
    assemble_package(args.source, args.output, args.output.parent / "package")
    return 0


def recipe_reference(recipe: Path, *inputs: Path) -> list[str]:
    """Rezeptverweis mit Commit; dieselbe Pruefung (lokale Aenderungen, Commit auf master) wie beim NetSurf-Rezept."""
    spec = importlib.util.spec_from_file_location("netsurf_build", ROOT / "experiments" / "NetSurf.Wasi" / "build.py")
    netsurf = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(netsurf)
    return netsurf.recipe_reference(recipe, *inputs)


def assemble_package(source: Path, module: Path, package: Path) -> None:
    """Legt den unsignierten Paketinhalt an: Modul, Texturpaket und alle Lizenz- und Quellenangaben."""
    if package.exists():
        shutil.rmtree(package)
    (package / "texpacks").mkdir(parents=True)
    shutil.copyfile(module, package / "classicube.wasm")
    # ClassiCube sucht texpacks/default.zip; feste Zeitstempel halten das Archiv bytegleich.
    with zipfile.ZipFile(package / "texpacks" / "default.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted((HERE / "texpack").glob("*.png")):
            archive.writestr(zipfile.ZipInfo(path.name, date_time=(2026, 11, 16, 0, 0, 0)), path.read_bytes(),
                             compress_type=zipfile.ZIP_DEFLATED)
    licenses = package / "licenses"
    licenses.mkdir()
    shutil.copyfile(source / "license.txt", licenses / "classicube-license.txt")
    for path in sorted((HERE / "texpack").glob("*.txt")):
        shutil.copyfile(path, licenses / f"texpack-{path.name}")
    # Statisch gelinkte wasi-libc und compiler-rt; die Texte liegen bereits beim NetSurf-Rezept.
    for path in sorted((ROOT / "experiments" / "NetSurf.Wasi" / "licenses").iterdir()):
        if path.name.startswith(("wasi-libc", "compiler-rt")):
            shutil.copyfile(path, licenses / path.name)
    (licenses / "SOURCES.txt").write_text("\n".join([
        "ZehOS ClassiCube source and build recipe",
        "",
        f"ClassiCube: https://github.com/ClassiCube/ClassiCube commit {COMMIT}",
        "Recipe (build.py, patches/, platform/, texpack/):",
        *recipe_reference(HERE, ROOT / "experiments" / "NetSurf.Wasi" / "licenses"),
        "Toolchain: wasi-sdk 25.0 (https://github.com/WebAssembly/wasi-sdk/releases/tag/wasi-sdk-25)",
    ]) + "\n", encoding="utf-8")
    print(f"Paketinhalt: {package}")


if __name__ == "__main__":
    sys.exit(main())
