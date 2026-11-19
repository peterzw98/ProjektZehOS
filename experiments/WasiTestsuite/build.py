#!/usr/bin/env python3
"""Baut die C-Tests aus WebAssembly/wasi-testsuite unveraendert als ZehOS-Pakete (ein Paket je Test).

Quellen: feste Dateien eines festen Commits (COMMIT, je Datei SHA-256), geholt nach .tools/wasi-testsuite/<commit>,
nicht ins Repository kopiert. Werkzeug: wasi-sdk 25 ueber denselben Abruf wie experiments/NetSurf.Wasi/build.py.
Ergebnis: experiments/WasiTestsuite/bin/packages/<test>/ mit <test>.wasm, LICENSE.txt (Apache-2.0 der Suite), licenses/
(wasi-libc, compiler-rt, SOURCES.txt) und, wenn die Suite fuer den Test eine <test>.json mit "root" hat, dem Inhalt von
fs-tests.dir als Paketwurzel (das nur lesende Preopen "/" auf fd 3 ersetzt das Preopen "." der Suite).
bin/packages/tests.json listet Kennung, Anzeigename, Einstieg und Umfang je Test fuer scripts/test-wasi-testsuite.py.
Signiert wird erst im Testlauf mit dem Testschluessel (scripts/build-package.py).
"""

import hashlib
import importlib.util
import json
import shutil
import subprocess
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
NETSURF = ROOT / "experiments" / "NetSurf.Wasi"
OUTPUT = HERE / "bin" / "packages"
REPOSITORY = "https://raw.githubusercontent.com/WebAssembly/wasi-testsuite"
COMMIT = "de7fbe267210b52f63893270403177239b4f4a9b"
TOOLS = ROOT / ".tools" / "wasi-testsuite" / COMMIT

# Pfad relativ zum Repository der Suite -> SHA-256 (gelesen am 19.11.2026 aus dem Commit oben).
FILES = {
    "LICENSE": "58d1e17ffe5109a7ae296caafcadfdbe6a7d176f0bc4ab01e12a689b0499d8bd",
    "tests/c/src/clock_getres-monotonic.c": "dbcc41c6d96b80b5f9016e17d70839b02c5716bf3e45efc821aef749af4d0a4b",
    "tests/c/src/clock_getres-realtime.c": "fc77e7c42d9b7cfbce6d91e292424b9391c08e41c41d42cbdd063b80562e9688",
    "tests/c/src/clock_gettime-monotonic.c": "93ddd7ea632085d53cd320c8ef2f7607d35a373156072dac6614c5780349cf5a",
    "tests/c/src/clock_gettime-realtime.c": "669cefbeb09e762de0fa726ec9eef5acea3eaf9d24c47dfe955d3fd7386b54e6",
    "tests/c/src/fdopendir-with-access.c": "12dcab46536ca8274c4c1c93ac850fb85c024609a2bf604682faa5e0e8945267",
    "tests/c/src/fdopendir-with-access.json": "ee3eb5f323581c2fec24aa18bbd06c318ad0009cb428e955111cb01412c82d8d",
    "tests/c/src/fopen-with-access.c": "6fba409e1290a9b6d246e9e320b059f9a8d57f1cb0f2816420814b0f1b9503a4",
    "tests/c/src/fopen-with-access.json": "ee3eb5f323581c2fec24aa18bbd06c318ad0009cb428e955111cb01412c82d8d",
    "tests/c/src/fopen-with-no-access.c": "7ef4888565f125d837567f28fca98a7f958101d2ae9faf46f6bb2db4aeccbb88",
    "tests/c/src/fs-tests.dir/file": "7f83b1657ff1fc53b92dc18148a1d65dfc2d4b1fa3d677284addd200126d9069",
    "tests/c/src/fs-tests.dir/fopendir.dir/file-0": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    "tests/c/src/fs-tests.dir/fopendir.dir/file-1": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    "tests/c/src/fs-tests.dir/lseek.txt": "924592b9b103f14f833faafb67f480691f01988aa457c0061769f58cd47311bc",
    "tests/c/src/fs-tests.dir/pread.txt": "f29a78e2b8fd00d8a58cf6ad12636eacd28bdc44cb6353ee31987eaeac60af66",
    "tests/c/src/fs-tests.dir/writeable/.gitignore": "240a3e0d37d2e86b614063f5347eb02d4f99ca6c254de6b82871ff8d95532a7d",
    "tests/c/src/lseek.c": "e1b0be4df20574a1264deacec2c650ff3322fda82b9f008139afd81db43d906a",
    "tests/c/src/lseek.json": "ee3eb5f323581c2fec24aa18bbd06c318ad0009cb428e955111cb01412c82d8d",
    "tests/c/src/pread-with-access.c": "1aaf8001f9f1b6c90f122410249af713373927fc4b1c6b92bb8db52eb0b9afec",
    "tests/c/src/pread-with-access.json": "ee3eb5f323581c2fec24aa18bbd06c318ad0009cb428e955111cb01412c82d8d",
    "tests/c/src/pwrite-with-access.c": "28198121a3d76e6156f66c47c68a7242048af1e7d0b50689559975302e2f05b9",
    "tests/c/src/pwrite-with-access.json": "ee3eb5f323581c2fec24aa18bbd06c318ad0009cb428e955111cb01412c82d8d",
    "tests/c/src/pwrite-with-append.c": "60b4c7a765cee58e277b8963a228d802a209895a17d5e96a054c7f939b90f156",
    "tests/c/src/pwrite-with-append.json": "ee3eb5f323581c2fec24aa18bbd06c318ad0009cb428e955111cb01412c82d8d",
    "tests/c/src/sock_shutdown-invalid_fd.c": "68aff5011c03937b56190e1c7af160ce9edea1bf6cce2af088f87d2e663330e0",
    "tests/c/src/sock_shutdown-not_sock.c": "2bddda9ade613ed11b339d5b10c196731d6e69b44390b47cb0adb7ff5efa48fb",
    "tests/c/src/stat-dev-ino.c": "88894fa050c43f4854f77135a03bffbff68bf7604390d502a2c48164f4c68f82",
    "tests/c/src/stat-dev-ino.json": "ee3eb5f323581c2fec24aa18bbd06c318ad0009cb428e955111cb01412c82d8d",
}

# Tests ausserhalb des Umfangs von Plan 12.8 mit Grund; sie werden trotzdem gebaut und im Gast ausgefuehrt.
OUT_OF_SCOPE = {
    "pwrite-with-access": "schreibt ueber das Preopen (writeable/), Paket ist nur lesend",
    "pwrite-with-append": "schreibt ueber das Preopen, Paket ist nur lesend",
    "sock_shutdown-invalid_fd": "Sockets (sock_*)",
    "sock_shutdown-not_sock": "Sockets (sock_*)",
}

SOURCE_DIR = "tests/c/src"
FIXTURE_DIR = SOURCE_DIR + "/fs-tests.dir"


def netsurf_build():
    # wasi-sdk-Abruf, Lizenztexte und Laufzeit-Herkunft teilt die Suite mit dem NetSurf-Build.
    spec = importlib.util.spec_from_file_location("netsurf_build", NETSURF / "build.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def fetch_sources() -> Path:
    """Holt jede Datei einzeln und prueft ihren SHA-256; vorhandene, passende Dateien werden wiederverwendet."""
    for name, digest in FILES.items():
        target = TOOLS / name
        if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == digest:
            continue
        with urllib.request.urlopen(f"{REPOSITORY}/{COMMIT}/{name}", timeout=60) as response:
            data = response.read()
        actual = hashlib.sha256(data).hexdigest()
        if actual != digest:
            sys.exit(f"Falscher SHA-256 fuer {name}: {actual}")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    return TOOLS


def tests() -> list[str]:
    return sorted(Path(name).stem for name in FILES if name.startswith(SOURCE_DIR + "/") and name.endswith(".c"))


def package_id(test: str) -> str:
    # Paketkennungen erlauben nur a-z, 0-9 und '-' (scripts/build-package.py).
    return "wasits-" + test.lower().replace("_", "-")


def main() -> int:
    source = fetch_sources()
    netsurf = netsurf_build()
    sdk = netsurf.fetch("wasi-sdk")
    if OUTPUT.exists():
        shutil.rmtree(OUTPUT)
    OUTPUT.mkdir(parents=True)
    licenses = [path for path in sorted((NETSURF / "licenses").iterdir()) if path.name.startswith(("wasi-libc-", "compiler-rt-"))]
    source_lines = ["wasi-testsuite C tests, built unmodified for ZehOS", f"{REPOSITORY}/{COMMIT}/",
                    *(f"SHA256 {digest} {name}" for name, digest in FILES.items()), ""]
    source_lines += netsurf.recipe_reference(HERE, NETSURF / "licenses")
    source_lines.extend(netsurf.wasi_runtime_sources())
    listing = []
    for test in tests():
        target = OUTPUT / test
        target.mkdir()
        wasm = target / f"{test}.wasm"
        # Upstream baut ohne zusaetzliche Warnungen; fremder Code wird nicht mit -Werror geprueft.
        subprocess.run([str(sdk / "bin" / "clang"), "--target=wasm32-wasip1", f"--sysroot={sdk / 'share' / 'wasi-sysroot'}",
                        "-O2", f"-ffile-prefix-map={source}=/wasi-testsuite", str(source / SOURCE_DIR / f"{test}.c"),
                        "-o", str(wasm)], check=True)
        data = wasm.read_bytes()
        for marker in (b"/home/", b"/Users/", str(ROOT).encode()):
            if marker in data:
                sys.exit(f"{wasm.name} enthaelt einen lokalen Pfad ({marker.decode()}).")
        config = source / SOURCE_DIR / f"{test}.json"
        fixtures = config.exists() and json.loads(config.read_text(encoding="utf-8")).get("root") == "fs-tests.dir"
        if fixtures:
            for name in FILES:
                if name.startswith(FIXTURE_DIR + "/"):
                    destination = target / name[len(FIXTURE_DIR) + 1:]
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(source / name, destination)
        shutil.copyfile(source / "LICENSE", target / "LICENSE.txt")
        (target / "licenses").mkdir()
        for path in licenses:
            shutil.copyfile(path, target / "licenses" / path.name)
        (target / "licenses" / "SOURCES.txt").write_text("\n".join(source_lines) + "\n", encoding="utf-8")
        listing.append({"test": test, "id": package_id(test), "name": f"WT {test}"[:32], "entry": wasm.name,
                        "fixtures": fixtures, "scope": "out" if test in OUT_OF_SCOPE else "in",
                        "reason": OUT_OF_SCOPE.get(test, "")})
        print(f"{test}: {len(data)} Bytes, Paketwurzel {'fs-tests.dir' if fixtures else 'leer'}")
    (OUTPUT / "tests.json").write_text(json.dumps(listing, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
