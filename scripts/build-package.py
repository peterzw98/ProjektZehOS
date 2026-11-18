#!/usr/bin/env python3
"""Erzeugt signierte ZehOS-Pakete und den Repositorykatalog (Format: docs/PACKAGE_FORMAT.md).

  keygen <Ordner>                         neues Ed25519-Schluesselpaar: private.pem und public.b64
  package --key K --repo R --id I --version V --name N --entry E [--network] [--storage] [--min-kernel X.Y.Z] [--aot-file A] QUELLE=ZIEL ...
  aot WAMRC EINGABE.wasm -o AUSGABE.aot   mit dem zugelassenen wamrc 2.4.5 uebersetzen, Beleg AUSGABE.aot.json; ohne Schluessel
  catalog --key K --repo R --generation G hoechste Version je Paket in R/catalog(.sig)

Der private Schluessel bleibt ausserhalb des Repositorys. Signiert wird mit OpenSSL (pkeyutl -rawin), wie bei
scripts/sign-update.py. Das Werkzeug fuehrt keine Paketinhalte aus und packt nichts in Archive.
"""

import argparse
import base64
import hashlib
import importlib.util
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ID = re.compile(r"^[a-z0-9][a-z0-9-]{0,47}$")
VERSION = re.compile(r"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$")
COMPONENT = re.compile(r"^[A-Za-z0-9._-]+$")
MAX_FILES, MAX_CONTENT, MAX_MANIFEST = 128, 128 * 1024 * 1024, 16 * 1024
# AOT (Plan docs/superpowers/plans/2026-11-17-aot-kernel.md, Etappe E): Nur dieses wamrc ist zugelassen, weil der Kernel
# Speicher- und Stackpruefungen eines .aot nicht selbst feststellen kann (Sicherheitsdokument, Abschnitt 3.2 und 5).
# Zugelassen ist nur das wamrc 2.4.5 mit dem Patch fixture/wamrc-noredzone.patch (alle erzeugten Funktionen ohne
# Red Zone; Cosmos betritt Interrupts ohne Stackwechsel und ueberschrieb sonst Werte unter RSP, WASM_FEASIBILITY.md).
# Das fruehere wamrc ohne Patch (6b400e9c...) ist bewusst nicht mehr zugelassen. Ein anderes wamrc ist eine eigene Aufgabe.
WAMRC_VERSION = re.compile(r"(?<![0-9.])2\.4\.5(?![0-9.])")
WAMRC_SHA256 = frozenset({"5809858be58ab78b00d970b5d50cae40e1a38d2a99abe16f8b952f74e128902c"})
# Gleich fixture/compile-aot.sh desselben Branches: feste Zielmaschine (TCG-tauglich), Pruefungen im erzeugten Code,
# kein SIMD. Weitere Schalter nimmt das Werkzeug bewusst nicht an.
WAMRC_FLAGS = ("--target=x86_64", "--cpu=x86-64", "--bounds-checks=1", "--stack-bounds-checks=1", "--size-level=0", "--disable-simd")
# WAMR-Version, AOT-Formatversion (AOT_CURRENT_VERSION 5 in 2.4.5), Ziel, Pruefschalter und nrz (ohne Red Zone);
# muss der Kernelkonstante gleichen. Kernel mit dieser Kennung laden .aot des alten wamrc nicht mehr.
AOT_ABI = "wamr245-aot5-x64-bc-nrz"
_redzone_spec = importlib.util.spec_from_file_location("check_aot_redzone", Path(__file__).with_name("check-aot-redzone.py"))
redzone = importlib.util.module_from_spec(_redzone_spec)
_redzone_spec.loader.exec_module(redzone)


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


def compile_aot(args: argparse.Namespace) -> None:
    # Getrennt von package, weil wamrc fremde .wasm liest und ohne Zugriff auf den Schluessel laufen soll
    # (Sicherheitsdokument, Abschnitt 5, Punkt 2).
    if args.output.resolve() == args.wasm.resolve():
        raise ValueError("Ausgabe und Eingabe sind dieselbe Datei.")
    with tempfile.TemporaryDirectory() as scratch:
        # Geprueft und ausgefuehrt wird dieselbe Kopie mit absolutem Pfad: Kein Austausch der Datei zwischen Pruefung
        # und Start, keine Suche im PATH. Ein fremdes wamrc laeuft nicht einmal fuer --version.
        tool = Path(scratch) / args.wamrc.name
        shutil.copyfile(args.wamrc, tool)
        tool.chmod(0o700)
        wamrc_sha = hashlib.sha256(tool.read_bytes()).hexdigest()
        if wamrc_sha not in WAMRC_SHA256:
            raise ValueError(f"{args.wamrc} ist nicht das zugelassene wamrc (SHA-256 unbekannt).")
        version = subprocess.run([str(tool), "--version"], check=True, capture_output=True, text=True).stdout.partition("\n")[0]
        if not WAMRC_VERSION.search(version):
            raise ValueError(f"wamrc 2.4.5 erwartet, gefunden: {version}")
        # Auch das .wasm als Kopie: Der Beleg nennt genau die Bytes, die wamrc gelesen hat.
        wasm = Path(scratch) / "input.wasm"
        shutil.copyfile(args.wasm, wasm)
        try:
            subprocess.run([str(tool), *WAMRC_FLAGS, "-o", str(args.output), str(wasm)], check=True)
            check_redzone(args.output)
        except (subprocess.CalledProcessError, ValueError):
            args.output.unlink(missing_ok=True)
            raise
        receipt = {"aot_abi": AOT_ABI, "wamrc_version": version, "wamrc_sha256": wamrc_sha, "flags": list(WAMRC_FLAGS),
                   "wasm_sha256": hashlib.sha256(wasm.read_bytes()).hexdigest(),
                   "aot_sha256": hashlib.sha256(args.output.read_bytes()).hexdigest()}
    receipt_path(args.output).write_text(json.dumps(receipt, indent=1) + "\n", encoding="utf-8")
    print(f"{args.output}: {receipt['aot_sha256']}")


def check_redzone(aot: Path) -> None:
    """Belegt am Ergebnis, dass kein Code unter RSP zugreift; die wamrc-Pruefsumme allein beweist das nicht."""
    hits = redzone.count_below_rsp(redzone.disassemble(aot.read_bytes(), redzone.find_objdump()))
    if hits:
        raise ValueError(f"{aot} greift {hits}-mal unter RSP zu (Red Zone); mit dem zugelassenen wamrc neu uebersetzen.")


def receipt_path(aot: Path) -> Path:
    return aot.with_name(aot.name + ".json")


def check_receipt(aot: Path, wasm: Path) -> None:
    """Gleicht die .aot mit dem Beleg von aot ab. Schuetzt gegen Verwechslung und veraltete Dateien, nicht gegen Angreifer."""
    try:
        receipt = json.loads(receipt_path(aot).read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise ValueError(f"Beleg {receipt_path(aot)} fehlt; die .aot mit dem Unterbefehl aot erzeugen.") from None
    if not isinstance(receipt, dict):
        raise ValueError("Beleg ist kein JSON-Objekt.")
    if receipt.get("aot_abi") != AOT_ABI:
        raise ValueError("Beleg nennt eine andere AOT-Kennung.")
    if receipt.get("flags") != list(WAMRC_FLAGS):
        raise ValueError("Beleg nennt andere wamrc-Schalter.")
    if receipt.get("wamrc_sha256") not in WAMRC_SHA256:
        raise ValueError("Beleg nennt ein nicht zugelassenes wamrc.")
    if receipt.get("aot_sha256") != hashlib.sha256(aot.read_bytes()).hexdigest():
        raise ValueError("Die .aot passt nicht zu ihrem Beleg.")
    if receipt.get("wasm_sha256") != hashlib.sha256(wasm.read_bytes()).hexdigest():
        raise ValueError("Die .aot wurde aus einem anderen .wasm als dem Einstiegspunkt uebersetzt; neu uebersetzen.")


def version_key(text: str) -> tuple[int, ...]:
    return tuple(int(part) for part in text.split("."))


def package(args: argparse.Namespace) -> None:
    if not ID.match(args.id) or not VERSION.match(args.version):
        raise ValueError("Ungueltige Kennung oder Version.")
    if args.min_kernel is not None and not VERSION.match(args.min_kernel):
        raise ValueError("Ungueltige Mindest-Kernelversion.")
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
    if args.aot_file is not None:
        aot = args.entry.removesuffix(".wasm") + f".{AOT_ABI}.aot"
        if not valid_path(aot):
            raise ValueError(f"AOT-Pfad {aot} ist laenger als 200 Zeichen; den Einstiegspfad kuerzen.")
        if aot.lower() in {name.lower() for name in files}:
            raise ValueError(f"{aot} ist bereits als Paketdatei gelistet.")
        if len(files) >= MAX_FILES:
            raise ValueError("Mit der AOT-Datei waeren es mehr als 128 Dateien.")
        check_receipt(args.aot_file, files[args.entry])
        check_redzone(args.aot_file)
        files[aot] = args.aot_file
    if sum(path.stat().st_size for path in files.values()) > MAX_CONTENT:
        raise ValueError("Paketinhalt groesser als 128 MiB.")
    target = args.repo / args.id / args.version
    if target.exists():
        raise ValueError(f"{target} existiert bereits; veroeffentlichte Versionen sind unveraenderlich.")
    lines = ["zehos-package 1", f"id={args.id}", f"version={args.version}", "runtime=zehos-wasm32-v1", f"name={args.name}", f"entry={args.entry}"]
    if args.network:
        lines.append("capability=net")
    if args.storage:
        lines.append("capability=storage")
    if args.min_kernel is not None:
        lines.append(f"min-kernel={args.min_kernel}")
    try:
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
    except BaseException:
        # Ein halber Versionsordner sperrt die Version (unveraenderlich) und gelangte sonst unsigniert in den Katalog.
        shutil.rmtree(target, ignore_errors=True)
        raise
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
        versions = [p for p in directory.iterdir() if VERSION.match(p.name) and (p / "manifest").exists() and (p / "manifest.sig").exists()]
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
    build.add_argument("--storage", action="store_true", help="Schreibenden App-Speicher /save im signierten Manifest erlauben (13.4)")
    build.add_argument("--min-kernel", help="niedrigste ZehOS-Version X.Y.Z, deren Hostfunktionen das Paket braucht")
    build.add_argument("--aot-file", type=Path, help=f"mit dem Unterbefehl aot erzeugte Datei (Beleg .json daneben) als <entry>.{AOT_ABI}.aot aufnehmen")
    build.add_argument("files", nargs="+", help="QUELLE=ZIELPFAD im Paket")
    translate = commands.add_parser("aot")
    translate.add_argument("wamrc", type=Path)
    translate.add_argument("wasm", type=Path)
    translate.add_argument("-o", "--output", type=Path, required=True)
    index = commands.add_parser("catalog")
    index.add_argument("--key", type=Path, required=True)
    index.add_argument("--repo", type=Path, required=True)
    index.add_argument("--generation", type=int, required=True)
    args = parser.parse_args()
    try:
        {"keygen": lambda: keygen(args.directory), "package": lambda: package(args), "aot": lambda: compile_aot(args), "catalog": lambda: catalog(args)}[args.command]()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Paketfehler: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
