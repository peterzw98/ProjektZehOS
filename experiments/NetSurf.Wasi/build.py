#!/usr/bin/env python3
"""Baut NetSurf 3.11 (Framebuffer-Frontend, ZehOS-Oberfläche) als WASIp1-Modul und stellt den Paketinhalt zusammen.

Quellen und Werkzeuge liegen unter .tools/netsurf/ und werden nicht eingecheckt; feste SHA-256-Werte machen den
Build nachvollziehbar. Ergebnis: experiments/NetSurf.Wasi/bin/package/ mit netsurf.wasm, res/, pages/ und licenses/.
Benötigt auf dem Linux-Host: make, cc, perl, gperf, curl/urllib-Zugang beim ersten Lauf.
"""

import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import tarfile
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
    target = TOOLS / folder
    if not target.exists():
        with tarfile.open(archive) as tar:
            tar.extractall(TOOLS, filter="data")
        if name == "netsurf":
            for patch in sorted((HERE / "patches").glob("*.patch")):
                run(["patch", "-p1", "-i", str(patch)], target, os.environ.copy(), TOOLS / f"{patch.stem}.log")
    return target


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jobs", type=int, default=os.cpu_count() or 4)
    args = parser.parse_args()

    sdk = fetch("wasi-sdk")
    source = fetch("netsurf")
    zlib = fetch("zlib")
    prefix = source / "inst-wasi"
    logs = TOOLS / "logs"
    logs.mkdir(exist_ok=True)

    cc = f"{sdk / 'bin' / 'clang'} --target=wasm32-wasip1 --sysroot={sdk / 'share' / 'wasi-sysroot'}"
    env = os.environ.copy()
    env.update(PATH=f"{sdk / 'bin'}:{prefix / 'bin'}:{env['PATH']}",
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
    print(f"Paketinhalt: {package}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
