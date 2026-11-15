#!/usr/bin/env python3
"""Erzeugt signierte ZehOS-Pakete und den Repositorykatalog (Format: docs/PACKAGE_FORMAT.md).

  keygen <Ordner>                         neues Ed25519-Schluesselpaar: private.pem und public.b64
  package --key K --repo R --id I --version V --name N --entry E QUELLE=ZIEL ...
  catalog --key K --repo R --generation G hoechste Version je Paket in R/catalog(.sig)

Der private Schluessel bleibt ausserhalb des Repositorys. Signiert wird mit OpenSSL (pkeyutl -rawin), wie bei
scripts/sign-update.py. Das Werkzeug fuehrt keine Paketinhalte aus und packt nichts in Archive.
"""

import argparse
import base64
import hashlib
import re
import shutil
import subprocess
import sys
from pathlib import Path

ID = re.compile(r"^[a-z0-9][a-z0-9-]{0,47}$")
VERSION = re.compile(r"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$")
COMPONENT = re.compile(r"^[A-Za-z0-9._-]+$")
MAX_FILES, MAX_CONTENT, MAX_MANIFEST = 128, 128 * 1024 * 1024, 16 * 1024


def valid_path(path: str) -> bool:
    parts = path.split("/")
    return 0 < len(path) <= 200 and all(p not in ("", ".", "..") and not p.endswith(".") and COMPONENT.match(p) for p in parts)


def sign(key: Path, content: Path, signature: Path) -> None:
    subprocess.run(["openssl", "pkeyutl", "-sign", "-inkey", str(key), "-rawin", "-in", str(content), "-out", str(signature)], check=True)
    if signature.stat().st_size != 64:
        raise ValueError("Der Schluessel erzeugt keine rohe Ed25519-Signatur.")


def keygen(directory: Path) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    private = directory / "private.pem"
    if private.exists():
        raise ValueError(f"{private} existiert bereits; vorhandene Schluessel werden nicht ueberschrieben.")
    subprocess.run(["openssl", "genpkey", "-algorithm", "ed25519", "-out", str(private)], check=True)
    der = subprocess.run(["openssl", "pkey", "-in", str(private), "-pubout", "-outform", "DER"], check=True, capture_output=True).stdout
    (directory / "public.b64").write_text(base64.b64encode(der[-32:]).decode("ascii") + "\n", encoding="ascii")


def version_key(text: str) -> tuple[int, ...]:
    return tuple(int(part) for part in text.split("."))


def package(args: argparse.Namespace) -> None:
    if not ID.match(args.id) or not VERSION.match(args.version):
        raise ValueError("Ungueltige Kennung oder Version.")
    if not (1 <= len(args.name) <= 32 and args.name == args.name.strip() and all(" " <= c <= "~" for c in args.name)):
        raise ValueError("Anzeigename: 1-32 druckbare ASCII-Zeichen.")
    files: dict[str, Path] = {}
    for item in args.files:
        source, _, target = item.partition("=")
        if not valid_path(target) or target.lower() in {name.lower() for name in files}:
            raise ValueError(f"Ungueltiger oder doppelter Zielpfad: {target}")
        files[target] = Path(source)
    if args.entry not in files or not args.entry.endswith(".wasm") or len(files) > MAX_FILES:
        raise ValueError("Einstiegspunkt muss eine gelistete .wasm-Datei sein; hoechstens 128 Dateien.")
    if sum(path.stat().st_size for path in files.values()) > MAX_CONTENT:
        raise ValueError("Paketinhalt groesser als 128 MiB.")
    target = args.repo / args.id / args.version
    if target.exists():
        raise ValueError(f"{target} existiert bereits; veroeffentlichte Versionen sind unveraenderlich.")
    lines = ["zehos-package 1", f"id={args.id}", f"version={args.version}", "runtime=zehos-wasm32-v1", f"name={args.name}", f"entry={args.entry}"]
    if args.network:
        lines.append("capability=net")
    for name in sorted(files):
        data = files[name].read_bytes()
        lines.append(f"file={hashlib.sha256(data).hexdigest()} {len(data)} {name}")
        destination = target / "files" / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(files[name], destination)
    manifest = ("\n".join(lines) + "\nend\n").encode("utf-8")
    if len(manifest) > MAX_MANIFEST:
        raise ValueError("Manifest groesser als 16 KiB.")
    (target / "manifest").write_bytes(manifest)
    sign(args.key, target / "manifest", target / "manifest.sig")
    print(f"{args.id} {args.version}: {hashlib.sha256(manifest).hexdigest()}")


def catalog(args: argparse.Namespace) -> None:
    if args.generation < 1:
        raise ValueError("Generation muss mindestens 1 sein.")
    existing = args.repo / "catalog"
    if existing.exists():
        known = re.search(r"^generation=(\d+)$", existing.read_text(encoding="utf-8"), re.MULTILINE)
        # ZehOS nimmt keinen aelteren Katalog an; ein neuer Stand braucht immer eine hoehere Generation.
        if known and args.generation <= int(known.group(1)):
            raise ValueError(f"Generation muss groesser als die vorhandene ({known.group(1)}) sein.")
    entries = []
    for directory in sorted(p for p in args.repo.iterdir() if p.is_dir() and ID.match(p.name)):
        versions = [p for p in directory.iterdir() if VERSION.match(p.name) and (p / "manifest").exists()]
        if versions:
            newest = max(versions, key=lambda p: version_key(p.name))
            entries.append(f"package={directory.name} {newest.name} {hashlib.sha256((newest / 'manifest').read_bytes()).hexdigest()}")
    (args.repo / "catalog").write_bytes(("\n".join(["zehos-catalog 1", f"generation={args.generation}", *entries, "end"]) + "\n").encode("utf-8"))
    sign(args.key, args.repo / "catalog", args.repo / "catalog.sig")
    print(f"Katalog Generation {args.generation}: {len(entries)} Pakete")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("keygen").add_argument("directory", type=Path)
    build = commands.add_parser("package")
    for name in ("--key", "--repo"):
        build.add_argument(name, type=Path, required=True)
    for name in ("--id", "--version", "--name", "--entry"):
        build.add_argument(name, required=True)
    build.add_argument("--network", action="store_true", help="Netzwerkzugriff im signierten Manifest erlauben")
    build.add_argument("files", nargs="+", help="QUELLE=ZIELPFAD im Paket")
    index = commands.add_parser("catalog")
    index.add_argument("--key", type=Path, required=True)
    index.add_argument("--repo", type=Path, required=True)
    index.add_argument("--generation", type=int, required=True)
    args = parser.parse_args()
    try:
        {"keygen": lambda: keygen(args.directory), "package": lambda: package(args), "catalog": lambda: catalog(args)}[args.command]()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Paketfehler: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
