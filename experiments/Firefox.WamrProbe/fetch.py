#!/usr/bin/env python3
"""Lädt die Eingaben der Firefox-Machbarkeitsprobe nach .tools/firefox-probe/.

gecko.wasm wird nicht eingecheckt (150 MB, fremder Build); feste Hashes und Commits machen die
Messungen nachvollziehbar. Bei abweichendem Hash bricht das Skript ab.
"""

import hashlib
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

GECKO_URL = "https://github.com/HeyPuter/firefox-wasm/releases/download/v0.0.1/gecko.js-v0.0.1.tar.gz"
GECKO_SHA256 = "bac28056a00ef6e5fb4345117e73d3629e75aca00dac71389fabbce39c018a73"
WAMR_URL = "https://github.com/bytecodealliance/wasm-micro-runtime.git"
WAMR_COMMIT = "25bd7eb63e828e4bd242cc9b38d260b4b31c6605"  # Tag WAMR-2.4.5
OUT = Path(__file__).resolve().parents[2] / ".tools" / "firefox-probe"


def fetch_gecko() -> None:
    archive = OUT / "gecko.js-v0.0.1.tar.gz"
    if not archive.exists():
        urllib.request.urlretrieve(GECKO_URL, archive)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    if digest != GECKO_SHA256:
        archive.unlink()
        sys.exit(f"Falscher SHA-256 für {archive.name}: {digest}")
    member = "gecko.js/dist/gecko.wasm.zst"
    with tarfile.open(archive) as tar:
        tar.extract(member, OUT / "release", filter="data")
    # zstd steht als Werkzeug bereit; eine Python-Abhängigkeit nur dafür lohnt nicht.
    subprocess.run(["zstd", "-d", "-q", "-f", str(OUT / "release" / member), "-o", str(OUT / "gecko.wasm")],
                   check=True)


def fetch_wamr() -> None:
    wamr = OUT / "wamr"
    if not (wamr / ".git").exists():
        subprocess.run(["git", "init", "-q", str(wamr)], check=True)
        subprocess.run(["git", "-C", str(wamr), "fetch", "-q", "--depth", "1", WAMR_URL, WAMR_COMMIT], check=True)
        subprocess.run(["git", "-C", str(wamr), "checkout", "-q", "--detach", "FETCH_HEAD"], check=True)
    head = subprocess.run(["git", "-C", str(wamr), "rev-parse", "HEAD"], capture_output=True, text=True, check=True)
    if head.stdout.strip() != WAMR_COMMIT:
        sys.exit(f"WAMR steht nicht auf {WAMR_COMMIT}.")


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    fetch_gecko()
    fetch_wamr()
    print(f"Bereit unter {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
