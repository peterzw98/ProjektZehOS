#!/usr/bin/env python3
"""Baut NetSurf 3.11 (Framebuffer-Frontend, ZehOS-Oberfläche) als WASIp1-Modul und stellt den Paketinhalt zusammen.

Quellen und Werkzeuge liegen unter .tools/netsurf/ und werden nicht eingecheckt; feste SHA-256-Werte machen den
Build nachvollziehbar. Ergebnis: experiments/NetSurf.Wasi/bin/package/ mit netsurf.wasm, res/, pages/ und licenses/.
Benötigt auf dem Linux-Host: make, cc, c++, perl, bison, flex, patch, libpng- und zlib-Entwicklungsdateien sowie
Netzzugang beim ersten Lauf.
"""

import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
TOOLS = ROOT / ".tools" / "netsurf"

ARCHIVES = {
    # wasi-sdk 25 veröffentlicht keine Prüfsumme; der Wert stammt aus dem ersten Bezug am 15. November 2026.
    "wasi-sdk": ("https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-25/wasi-sdk-25.0-x86_64-linux.tar.gz",
                 "52640dde13599bf127a95499e61d6d640256119456d1af8897ab6725bcf3d89c", "wasi-sdk-25.0-x86_64-linux"),
    "netsurf": ("https://download.netsurf-browser.org/netsurf/releases/source-full/netsurf-all-3.11.tar.gz",
                "4dea880ff3c2f698bfd62c982b259340f9abcd7f67e6c8eb2b32c61f71644b7b", "netsurf-all-3.11"),
    "zlib": ("https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz",
             "9a93b2b7dfdac77ceba5a558a580e74667dd6fede4585b91eefb60f03b72df23", "zlib-1.3.1"),
    # Fest statt Host-gperf: 3.1 und 3.3 erzeugen unterschiedlichen Generatorcode und damit andere Module.
    "gperf": ("https://ftp.gnu.org/gnu/gperf/gperf-3.1.tar.gz",
              "588546b945bba4b70b6a3a616e80b4ab466e3f33024a352fc2198112cdbb3ae2", "gperf-3.1"),
}
# Reihenfolge nach Abhängigkeiten; libsvgtiny fehlt, weil es setjmp/longjmp braucht.
LIBRARIES = ["buildsystem", "libnslog", "libwapcaplet", "libparserutils", "libcss", "libhubbub", "libdom",
             "libnsbmp", "libnsgif", "libnsutils", "libutf8proc", "libnspsl", "libnsfb"]
RESOURCES = ["Messages", "default.css", "quirks.css", "internal.css", "adblock.css", "welcome.html",
             "credits.html", "licence.html"]


def run(command: list[str], cwd: Path, env: dict[str, str], log: Path) -> None:
    with log.open("w") as output:
        result = subprocess.run(command, cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=output,
                                stderr=subprocess.STDOUT)
    if result.returncode != 0:
        sys.exit(f"Fehlgeschlagen ({result.returncode}): {' '.join(command[:4])} … – Protokoll: {log}")


def fetch(name: str) -> Path:
    url, digest, folder = ARCHIVES[name]
    archive = TOOLS / "dl" / url.rsplit("/", 1)[1]
    archive.parent.mkdir(parents=True, exist_ok=True)
    if not archive.exists():
        urllib.request.urlretrieve(url, archive.with_suffix(".part"))
        archive.with_suffix(".part").rename(archive)
    actual = hashlib.sha256(archive.read_bytes()).hexdigest()
    if actual != digest:
        archive.unlink()
        sys.exit(f"Falscher SHA-256 für {archive.name}: {actual}")
    patches = sorted((HERE / "patches").glob("*.patch")) if name == "netsurf" else []
    # Ein neuer Patchsatz oder Generator braucht unveraenderte Quellen, auch nach einem frueher abgebrochenen Build;
    # make erzeugt sonst die gperf-Ausgaben eines aelteren Laufs nicht neu.
    recipe = hashlib.sha256(digest.encode() + ARCHIVES["gperf"][1].encode() + b"".join(p.name.encode() + b"\0" + p.read_bytes() for p in patches)).hexdigest()[:16]
    destination = TOOLS / ("source-" + recipe) if name == "netsurf" else TOOLS
    target = destination / folder
    if not target.exists():
        with tempfile.TemporaryDirectory(dir=TOOLS) as temporary:
            with tarfile.open(archive) as tar:
                tar.extractall(temporary, filter="data")
            extracted = Path(temporary) / folder
            for patch in patches:
                run(["patch", "-p1", "-i", str(patch)], extracted, os.environ.copy(), TOOLS / f"{patch.stem}.log")
            destination.mkdir(parents=True, exist_ok=True)
            shutil.move(str(extracted), target)
    return target


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jobs", type=int, default=os.cpu_count() or 4)
    args = parser.parse_args()

    sdk = fetch("wasi-sdk")
    source = fetch("netsurf")
    zlib = fetch("zlib")
    gperf = fetch("gperf")
    prefix = source / "inst-wasi"
    logs = TOOLS / "logs"
    logs.mkdir(exist_ok=True)

    if not (gperf / "inst" / "bin" / "gperf").exists():
        run(["./configure", f"--prefix={gperf / 'inst'}"], gperf, os.environ.copy(), logs / "gperf-configure.log")
        run(["make", f"-j{args.jobs}", "install"], gperf, os.environ.copy(), logs / "gperf.log")

    input_test = TOOLS / "frontend-input-test"
    run(["cc", "-O2", "-Wno-attributes", "-I", str(source / "libnsfb/include"),
         "-I", str(source / "libnsfb/src"), str(HERE / "tests/input.c"), "-o", str(input_test)],
        HERE, os.environ.copy(), logs / "frontend-input-build.log")
    run([str(input_test)], HERE, os.environ.copy(), logs / "frontend-input.log")

    # Ohne Pfadabbildung landen rund 290 absolute Baupfade (__FILE__, Debuginfo) im Modul; dann unterscheidet es sich je Rechner.
    cc = (f"{sdk / 'bin' / 'clang'} --target=wasm32-wasip1 --sysroot={sdk / 'share' / 'wasi-sysroot'}"
          f" -ffile-prefix-map={TOOLS}=/netsurf-build")
    env = os.environ.copy()
    # Feste Zeit für NetSurfs Build-Testament (patches/netsurf-testament-reproducible.patch), sonst ändert sich das Modul täglich.
    env.setdefault("SOURCE_DATE_EPOCH", "1794700800")
    # Die Perl-Generatoren von libparserutils/hubbub geben Tabellen in Hash-Reihenfolge aus; feste Seeds machen sie stabil.
    env.update(PERL_HASH_SEED="0", PERL_PERTURB_KEYS="0", PYTHONHASHSEED="0")
    env.update(PATH=f"{sdk / 'bin'}:{prefix / 'bin'}:{gperf / 'inst' / 'bin'}:{env['PATH']}",
               PKG_CONFIG_PATH=str(prefix / "lib" / "pkgconfig"), PKG_CONFIG_LIBDIR=str(prefix / "lib" / "pkgconfig"))
    common = [f"HOST=wasm32-wasip1", f"PREFIX={prefix}", f"NSSHARED={source / 'buildsystem'}", f"CC={cc}",
              f"AR={sdk / 'bin' / 'llvm-ar'}", "Q=", "DESTDIR="]

    if not (prefix / "lib" / "libz.a").exists():
        zenv = dict(env, CC=cc, AR=str(sdk / "bin" / "llvm-ar"), RANLIB=str(sdk / "bin" / "llvm-ranlib"), CHOST="wasm32")
        run(["./configure", "--static", f"--prefix={prefix}"], zlib, zenv, logs / "zlib-configure.log")
        run(["make", f"-j{args.jobs}", "libz.a"], zlib, zenv, logs / "zlib.log")
        run(["make", "install"], zlib, zenv, logs / "zlib-install.log")

    shutil.copy2(HERE / "frontend" / "zehos.c", source / "libnsfb" / "src" / "surface" / "zehos.c")
    for library in LIBRARIES:
        run(["make", "install", *common, "WARNFLAGS=-Wall -W -Wno-error", "WITH_EXPAT_BINDING=no"],
            source / library, env, logs / f"{library}.log")

    shutil.copy2(HERE / "Makefile.config", source / "netsurf" / "Makefile.config")
    run(["make", f"-j{args.jobs}", "TARGET=framebuffer", *common, "BUILD_CC=cc"], source / "netsurf", env,
        logs / "netsurf.log")

    package = HERE / "bin" / "package"
    shutil.rmtree(package, ignore_errors=True)
    (package / "res").mkdir(parents=True)
    shutil.copy2(source / "netsurf" / "nsfb", package / "netsurf.wasm")
    resources = source / "netsurf" / "frontends" / "framebuffer" / "res"
    for name in RESOURCES:
        shutil.copyfile(resources / name, package / "res" / name)  # löst die Symlinks des Quellbaums auf
    shutil.copy2(HERE / "res" / "Choices", package / "res" / "Choices")
    shutil.copytree(HERE / "pages", package / "pages")
    licenses = package / "licenses"
    licenses.mkdir()
    shutil.copyfile(source / "netsurf" / "COPYING", licenses / "netsurf-COPYING")
    for library in LIBRARIES[1:]:
        for name in ("COPYING", "LICENSE.md"):
            if (source / library / name).exists():
                shutil.copyfile(source / library / name, licenses / f"{library}-{name}")
    shutil.copyfile(zlib / "LICENSE", licenses / "zlib-LICENSE")
    # Texte, die weder NetSurf noch wasi-sdk mitliefern (PSL unter MPL-2.0, statisch gelinkte wasi-libc und compiler-rt).
    for path in sorted((HERE / "licenses").iterdir()):
        shutil.copyfile(path, licenses / path.name)
    revision = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, check=True, capture_output=True, text=True).stdout.strip()
    source_lines = ["ZehOS NetSurf source and build recipe", "",
        f"https://github.com/peterzw98/ProjektZehOS/tree/{revision}/experiments/NetSurf.Wasi", ""]
    # Das GPL-Quellangebot verweist auf diesen Commit; er muss dauerhaft erreichbar sein. Nach einem Squash-Merge
    # verschwinden Branch-Commits, lokale Aenderungen sind nirgends abrufbar.
    # Unversionierte Dateien zaehlen nur im Rezeptordner: build.py liest patches/ und licenses/ per glob ein.
    dirty = (subprocess.run(["git", "status", "--porcelain", "--untracked-files=no"], cwd=ROOT,
                            capture_output=True, text=True).stdout.strip()
             + subprocess.run(["git", "status", "--porcelain", "--untracked-files=all", "--", str(HERE)], cwd=ROOT,
                              capture_output=True, text=True).stdout.strip())
    on_master = subprocess.run(["git", "merge-base", "--is-ancestor", revision, "origin/master"], cwd=ROOT,
                               capture_output=True).returncode == 0
    if dirty or not on_master:
        warning = ("WARNUNG: Bau aus " + ("lokal geaendertem Stand" if dirty else "Commit ausserhalb von origin/master")
                   + "; der Quellverweis ist nicht dauerhaft. Dieses Paket nicht veroeffentlichen.")
        print(warning, file=sys.stderr)
        source_lines.extend([warning, ""])
    source_lines.extend([
        "Source archives (unmodified upstream plus the patches in the recipe):"])
    for name in ("netsurf", "zlib"):
        url, digest, _ = ARCHIVES[name]
        source_lines.extend([url, f"SHA256 {digest}"])
    source_lines.extend(["", "Statically linked runtime from wasi-sdk 25:", ARCHIVES["wasi-sdk"][0],
        f"SHA256 {ARCHIVES['wasi-sdk'][1]}",
        "https://github.com/WebAssembly/wasi-libc/tree/574b88da481569b65a237cb80daf9a2d5aeaf82d",
        "https://github.com/llvm/llvm-project/tree/ab4b5a2db582958af1ee308a790cfdb42bd24720/compiler-rt"])
    (licenses / "SOURCES.txt").write_text("\n".join(source_lines) + "\n", encoding="utf-8")
    print(f"Paketinhalt: {package}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
