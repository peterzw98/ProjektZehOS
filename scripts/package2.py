#!/usr/bin/env python3
"""Erzeugt signierte ZehOS-Pakete und den Katalog des Formats 2 (docs/PACKAGE_FORMAT.md, "Format 2").

  package --key K --repo R --spec SPEC.toml QUELLE=PFAD ...   schreibt R/<id>/<version>-r<release>/{manifest,manifest.sig,files/...}
  catalog --key K --repo R --generation N                    hoechste Version je Paket in R/catalog2(.sig)

SPEC.toml enthaelt die Manifestfelder ohne Dateizeilen (Beispiel und Regeln in docs/PACKAGE_FORMAT.md). Welche Dateien
ausfuehrbar sind, nennt die Liste exec (nur Pfade); Hash und Groesse rechnet das Werkzeug aus. Schluessel und Signatur
kommen unveraendert aus scripts/build-package.py (OpenSSL, Ed25519); dessen Befehle fuer Version 1 bleiben gleich.

Format 2 erreicht die Shell ueber "pkg native" (Server noch nicht). Das Werkzeug fuehrt keine Paketinhalte aus, ruft keine
Shell auf und gibt Text aus Spec, Argumenten oder Repository nur gefiltert (druckbares ASCII, hoechstens 64 Zeichen) wieder.
"""

import argparse
import ctypes
import errno
import hashlib
import importlib.util
import os
import re
import secrets
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import tomllib
from pathlib import Path

_build_spec = importlib.util.spec_from_file_location("build_package", Path(__file__).with_name("build-package.py"))
build_package = importlib.util.module_from_spec(_build_spec)
_build_spec.loader.exec_module(build_package)

# Grenzen und Regeln wie ProjektZeh/Packages/PackageManifest2.cs und PackageCatalog2.cs; das Werkzeug darf strenger sein,
# nie weiter, sonst schriebe es Pakete, die der Kernel ablehnt.
RUNTIME = "linux-x64-v1"
MAX_MANIFEST = 1024 * 1024
MAX_FILES = 4096
MAX_LINKS = 4096
MAX_CONTENT = 1024 * 1024 * 1024
MAX_DEPENDS = 64
MAX_PROVIDES = 64
MAX_MEMORY_MIB = 4096
MAX_RELEASE = 9999
MAX_PATH = 200
MAX_LINK_HOPS = 40
MAX_CATALOG = 4 * 1024 * 1024
MAX_ENTRIES = 4096
MAX_GENERATION = 9_999_999_999          # hoechstens zehn Ziffern (PackageManifest2.TryParseSize)
MAX_SPEC = 1024 * 1024
CHUNK = 1024 * 1024
# PEM-Kopf eines privaten Schluessels (PKCS#8, ENCRYPTED, RSA, EC, OPENSSH, PGP ... BLOCK). Ein so markierter Inhalt wird nie
# signiert, auch nicht als Kopie der Schluesseldatei; ein Schluessel ohne PEM-Huelle (DER, roh) faellt nicht auf.
PRIVATE_KEY = re.compile(rb"-----BEGIN [A-Z0-9 ]{0,40}PRIVATE KEY(?: BLOCK)?-----")
PRIVATE_KEY_OVERLAP = 80

# Eigene Muster mit fullmatch: "$" in re.match liesse ein abschliessendes LF durch ("hello\n").
ID = re.compile(r"[a-z0-9][a-z0-9-]{0,47}")
_NUMBER = r"(?:0|[1-9][0-9]{0,8})"
VERSION = re.compile(rf"{_NUMBER}\.{_NUMBER}\.{_NUMBER}")
RELEASE_TEXT = r"[1-9][0-9]{0,3}"
VERSION_DIR = re.compile(rf"({_NUMBER}\.{_NUMBER}\.{_NUMBER})-r({RELEASE_TEXT})")
DEPENDENCY = re.compile(rf"([a-z0-9][a-z0-9-]{{0,47}})>=({_NUMBER}\.{_NUMBER}\.{_NUMBER})-r({RELEASE_TEXT})")
COMPONENT = re.compile(r"[A-Za-z0-9._+-]+")
LICENSE = re.compile(r"[A-Za-z0-9.+\-() ]{1,64}")
SONAME = re.compile(r"[A-Za-z0-9._+-]{1,200}")
SHA256 = re.compile(r"[0-9a-f]{64}")
SIZE = re.compile(r"0|[1-9][0-9]{0,9}")
CAPABILITIES = ("net", "storage", "files")
SPEC_KEYS = ("id", "version", "release", "runtime", "license", "name", "entry", "capability", "memory-mib", "min-kernel",
             "depends", "provides", "exec", "link")
REQUIRED_KEYS = ("id", "version", "release", "runtime", "license")


class PackageError(ValueError):
    """Fehler mit bereits gefilterter Meldung."""


def quote(text: object) -> str:
    """Wie PackageManifest2.Quote: nur druckbares ASCII (Rest '?'), hoechstens 64 Zeichen."""
    text = str(text)
    shown = "".join(c if " " <= c <= "~" else "?" for c in text[:64])
    return f'"{shown}"' + ("..." if len(text) > 64 else "")


def version_key(version: str, release: int) -> tuple[int, int, int, int]:
    major, minor, patch = (int(part) for part in version.split("."))
    return major, minor, patch, release


# --- Pfade und Links (gleiche Regeln wie PackageManifest2.IsValidPath, ResolveTarget und CheckLinkChains) ---------------

def valid_relative(path: str) -> bool:
    return len(path) > 0 and all(COMPONENT.fullmatch(part) and not part.endswith(".") for part in path.split("/"))


def valid_path(path: str) -> bool:
    return len(path) <= MAX_PATH and path.startswith(("usr/", "lib/", "etc/")) and valid_relative(path)


def resolve_target(link: str, target: str) -> str | None:
    """Ziel relativ zum Ordner des Links: fuehrende ../, dann normale Komponenten; Ergebnis muss ein gueltiger Pfad sein."""
    if not 0 < len(target) <= MAX_PATH:
        return None
    up = 0
    while target.startswith("../", up * 3):
        up += 1
    rest = target[up * 3:]
    if not valid_relative(rest):
        return None
    keep = link.rfind("/")
    for _ in range(up):
        if keep < 0:
            return None
        keep = link.rfind("/", 0, keep)
    resolved = rest if keep < 0 else link[:keep] + "/" + rest
    return resolved if valid_path(resolved) else None


def same_or_nested(path: str, link: str) -> bool:
    path, link = path.lower(), link.lower()
    return path == link or path.startswith(link + "/") or link.startswith(path + "/")


def check_paths(content: list[str], links: dict[str, str]) -> None:
    """Doppelte Pfade, Pfade als Ordner anderer und Linkketten, ohne Gross-/Kleinschreibung (FAT32)."""
    folded: dict[str, str | None] = {}
    for path in content:
        if path.lower() in folded:
            raise PackageError(f"Doppelter Pfad: {quote(path)}")
        folded[path.lower()] = None
    resolved: dict[str, str] = {}
    for path, target in links.items():
        if path.lower() in folded:
            raise PackageError(f"Doppelter Pfad: {quote(path)}")
        folded[path.lower()] = path
        hit = resolve_target(path, target)
        if hit is None:
            raise PackageError(f"Linkziel ist nicht kanonisch oder liegt ausserhalb von usr/, lib/, etc/: {quote(path)}")
        resolved[path] = hit
    for path in folded:
        for index, char in enumerate(path):
            if char == "/" and path[:index] in folded:
                raise PackageError(f"Pfad ist zugleich Ordner eines anderen: {quote(path[:index])}")
    for link, start in resolved.items():
        current = start
        for hops in range(MAX_LINK_HOPS + 1):
            if same_or_nested(current, link):
                raise PackageError(f"Link zeigt auf sich selbst, einen eigenen Ordner oder darunter: {quote(link)}")
            via, prefix = link_prefix(current, folded)
            if via is None:
                break
            if hops == MAX_LINK_HOPS:
                raise PackageError(f"Mehr als 40 Links in Folge oder Linkkreis: {quote(link)}")
            current = resolved[via] + current[prefix:]
            if len(current) > MAX_PATH:
                raise PackageError(f"Linkkette ergibt einen Pfad ueber 200 Zeichen: {quote(link)}")


def link_prefix(path: str, folded: dict[str, str | None]) -> tuple[str | None, int]:
    """Kuerzester Praefix (ganzer Pfad oder vor einem '/'), der ein Link ist, wie PathTable.FindLinkPrefix."""
    lowered = path.lower()
    for index in range(1, len(path) + 1):
        if index == len(path) or path[index] == "/":
            link = folded.get(lowered[:index])
            if link is not None:
                return link, index
    return None, -1


# --- Manifest -----------------------------------------------------------------------------------------------------------

class Manifest:
    """Felder eines Manifests des Formats 2; render() erzeugt die eine kanonische Schreibweise."""

    def __init__(self, fields: dict, files: list[tuple[str, int, str, bool]]):
        self.id: str = fields["id"]
        self.version: str = fields["version"]
        self.release: int = fields["release"]
        self.license: str = fields["license"]
        self.name: str | None = fields.get("name")
        self.entry: str | None = fields.get("entry")
        self.capabilities: list[str] = [c for c in CAPABILITIES if c in fields.get("capability", [])]
        self.memory_mib: int | None = fields.get("memory-mib")
        self.min_kernel: str | None = fields.get("min-kernel")
        self.depends: list[str] = sorted(fields.get("depends", []), key=lambda d: d.partition(">=")[0])
        self.provides: list[str] = sorted(fields.get("provides", []))
        self.files = sorted(files)
        self.links: dict[str, str] = dict(sorted(fields.get("link", {}).items()))
        self.validate()

    @property
    def full_version(self) -> str:
        return f"{self.version}-r{self.release}"

    @property
    def content_bytes(self) -> int:
        return sum(size for _, size, _, _ in self.files)

    def validate(self) -> None:
        if not ID.fullmatch(self.id):
            raise PackageError("Ungueltige Paketkennung.")
        if not VERSION.fullmatch(self.version):
            raise PackageError("Ungueltige Paketversion (drei Zahlen ohne fuehrende Null).")
        if not 1 <= self.release <= MAX_RELEASE:
            raise PackageError("Ungueltige Paketrevision (1 bis 9999).")
        if not LICENSE.fullmatch(self.license):
            raise PackageError("Ungueltiger Lizenzausdruck (1-64 Zeichen aus A-Z a-z 0-9 . + - ( ) und Leerzeichen).")
        if self.name is not None and not (1 <= len(self.name) <= 32 and self.name == self.name.strip(" ")
                                          and all(" " <= c <= "~" for c in self.name)):
            raise PackageError("Anzeigename: 1-32 druckbare ASCII-Zeichen ohne Leerzeichen am Rand.")
        if self.name is not None and self.entry is None:
            raise PackageError("name ist nur zusammen mit entry erlaubt.")
        if self.memory_mib is not None and (self.entry is None or not 1 <= self.memory_mib <= MAX_MEMORY_MIB):
            raise PackageError("memory-mib: 1 bis 4096 und nur zusammen mit entry.")
        if self.min_kernel is not None and not VERSION.fullmatch(self.min_kernel):
            raise PackageError("Ungueltige Mindest-Kernelversion.")
        if len(self.depends) > MAX_DEPENDS:
            raise PackageError("Mehr als 64 Abhaengigkeiten.")
        seen: set[str] = set()
        for dependency in self.depends:
            match = DEPENDENCY.fullmatch(dependency)
            if not match:
                raise PackageError(f"Ungueltige Abhaengigkeit: {quote(dependency)}")
            if match.group(1) == self.id:
                raise PackageError("Paket haengt von sich selbst ab.")
            if match.group(1) in seen:
                raise PackageError(f"Doppelte Abhaengigkeit: {quote(match.group(1))}")
            seen.add(match.group(1))
        if len(self.provides) > MAX_PROVIDES:
            raise PackageError("Mehr als 64 provides-Eintraege.")
        if len(set(self.provides)) != len(self.provides):
            raise PackageError("Doppelter provides-Eintrag.")
        for provided in self.provides:
            if not provided.startswith("so:") or not SONAME.fullmatch(provided[3:]):
                raise PackageError(f"Ungueltiger provides-Eintrag: {quote(provided)}")
        if not self.files:
            raise PackageError("Paket enthaelt keine Datei.")
        if len(self.files) > MAX_FILES:
            raise PackageError("Mehr als 4096 Dateien.")
        if len(self.links) > MAX_LINKS:
            raise PackageError("Mehr als 4096 Links.")
        for path, size, digest, _ in self.files:
            if not valid_path(path):
                raise PackageError(f"Ungueltiger Paketpfad: {quote(path)}")
            if not SHA256.fullmatch(digest) or not 0 <= size < 10 ** 10:
                raise PackageError(f"Ungueltige Dateiangabe: {quote(path)}")
        if self.content_bytes > MAX_CONTENT:
            raise PackageError("Paketinhalt groesser als 1 GiB.")
        for path in self.links:
            if not valid_path(path):
                raise PackageError(f"Ungueltiger Linkpfad: {quote(path)}")
        if self.entry is not None and not any(executable and path == self.entry for path, _, _, executable in self.files):
            raise PackageError("Einstiegspunkt ist keine gelistete exec-Datei.")
        check_paths([path for path, _, _, _ in self.files], self.links)
        if len(self.render()) > MAX_MANIFEST:
            raise PackageError("Manifest groesser als 1 MiB.")

    def render(self) -> bytes:
        lines = ["zehos-package 2", f"id={self.id}", f"version={self.version}", f"release={self.release}", f"runtime={RUNTIME}",
                 f"license={self.license}"]
        if self.name is not None:
            lines.append(f"name={self.name}")
        if self.entry is not None:
            lines.append(f"entry={self.entry}")
        lines += [f"capability={c}" for c in self.capabilities]
        if self.memory_mib is not None:
            lines.append(f"memory-mib={self.memory_mib}")
        if self.min_kernel is not None:
            lines.append(f"min-kernel={self.min_kernel}")
        lines += [f"depends={d}" for d in self.depends]
        lines += [f"provides={p}" for p in self.provides]
        lines += [f"{'exec' if executable else 'file'}={digest} {size} {path}" for path, size, digest, executable in self.files]
        lines += [f"link={path} {target}" for path, target in self.links.items()]
        lines.append("end")
        return ("\n".join(lines) + "\n").encode("utf-8")


def parse_manifest(data: bytes) -> Manifest:
    """Liest ein Manifest und verlangt die kanonische Schreibweise: render() muss genau die gelesenen Bytes ergeben."""
    if len(data) > MAX_MANIFEST:
        raise PackageError("Manifest groesser als 1 MiB.")
    try:
        text = data.decode("ascii")
    except UnicodeDecodeError:
        raise PackageError("Manifest enthaelt Nicht-ASCII-Zeichen.") from None
    lines = text.split("\n")
    if len(lines) < 3 or lines[0] != "zehos-package 2" or lines[-2:] != ["end", ""]:
        raise PackageError("Unbekanntes Manifestformat oder Ende fehlt.")
    fields: dict = {}
    files: list[tuple[str, int, str, bool]] = []
    for line in lines[1:-2]:
        key, separator, value = line.partition("=")
        if not separator:
            raise PackageError("Zeile ohne '='.")
        if key in ("exec", "file"):
            digest, _, rest = value.partition(" ")
            size, _, path = rest.partition(" ")
            if not SIZE.fullmatch(size):
                raise PackageError(f"Ungueltige Dateizeile: {quote(value)}")
            files.append((path, int(size), digest, key == "exec"))
        elif key == "link":
            path, _, target = value.partition(" ")
            fields.setdefault("link", {})[path] = target
        elif key in ("capability", "depends", "provides"):
            fields.setdefault(key, []).append(value)
        elif key in ("release", "memory-mib"):
            if not re.fullmatch(r"[1-9][0-9]{0,8}", value):
                raise PackageError(f"Ungueltige Zahl in {key}.")
            fields[key] = int(value)
        elif key in SPEC_KEYS and key not in ("exec", "link") and key not in fields:
            fields[key] = value
        else:
            raise PackageError(f"Unbekannte oder doppelte Zeile: {quote(key)}")
    if fields.get("runtime") != RUNTIME or any(key not in fields for key in REQUIRED_KEYS):
        raise PackageError("Pflichtfeld fehlt oder falsches Laufzeitprofil.")
    manifest = Manifest(fields, files)
    if manifest.render() != data:
        raise PackageError("Manifest ist nicht kanonisch (Reihenfolge, Sortierung oder Schreibweise).")
    return manifest


# --- Spec ---------------------------------------------------------------------------------------------------------------

def load_spec(path: Path) -> dict:
    data = read_regular(path, MAX_SPEC, "Spec")
    try:
        spec = tomllib.loads(data.decode("utf-8"))
    except UnicodeDecodeError:
        raise PackageError("Spec ist kein gueltiges UTF-8.") from None
    except tomllib.TOMLDecodeError as error:
        # Die Meldung von tomllib kann Eingabetext enthalten; nur die Position weitergeben.
        raise PackageError(f"Spec ist kein gueltiges TOML (Zeile {getattr(error, 'lineno', '?')}).") from None
    for key in spec:
        if key not in SPEC_KEYS:
            raise PackageError(f"Unbekanntes Feld in der Spec: {quote(key)}")
    for key in REQUIRED_KEYS:
        if key not in spec:
            raise PackageError(f"Pflichtfeld fehlt in der Spec: {key}")
    for key in ("id", "version", "runtime", "license", "name", "entry", "min-kernel"):
        if key in spec and not isinstance(spec[key], str):
            raise PackageError(f"{key} muss eine Zeichenkette sein.")
    for key in ("release", "memory-mib"):
        # bool ist in Python eine Unterklasse von int; true waere sonst release=1.
        if key in spec and (not isinstance(spec[key], int) or isinstance(spec[key], bool)):
            raise PackageError(f"{key} muss eine ganze Zahl sein.")
    for key in ("capability", "depends", "provides", "exec"):
        if key in spec and (not isinstance(spec[key], list) or not all(isinstance(item, str) for item in spec[key])):
            raise PackageError(f"{key} muss eine Liste von Zeichenketten sein.")
    if "link" in spec and (not isinstance(spec["link"], dict) or not all(isinstance(t, str) for t in spec["link"].values())):
        raise PackageError("link muss eine Tabelle Pfad = Ziel sein.")
    if spec["runtime"] != RUNTIME:
        raise PackageError(f"runtime muss {RUNTIME} sein.")
    for key in ("capability", "depends", "provides", "exec"):
        if len(set(spec.get(key, []))) != len(spec.get(key, [])):
            raise PackageError(f"Doppelter Eintrag in {key}.")
    unknown = [c for c in spec.get("capability", []) if c not in CAPABILITIES]
    if unknown:
        raise PackageError(f"Unbekannte Faehigkeit: {quote(unknown[0])}")
    return spec


# --- Dateien ------------------------------------------------------------------------------------------------------------

def open_regular(path: Path) -> int:
    """Oeffnet eine regulaere Datei ohne Symlink in der letzten Komponente; liefert den Deskriptor."""
    # O_NONBLOCK: Eine FIFO oder ein Geraet wuerde beim Oeffnen sonst ewig blockieren; abgelehnt wird es danach ueber fstat.
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0) | getattr(os, "O_BINARY", 0)
    try:
        if os.path.islink(path):
            raise PackageError(f"Quelle ist ein symbolischer Link: {quote(path)}")
        if not stat.S_ISREG(os.lstat(path).st_mode):
            raise PackageError(f"Quelle ist keine regulaere Datei: {quote(path)}")
        handle = os.open(path, flags)
    except OSError as error:
        raise PackageError(f"Quelle nicht lesbar ({error.strerror}): {quote(path)}") from None
    if not stat.S_ISREG(os.fstat(handle).st_mode):
        os.close(handle)
        raise PackageError(f"Quelle ist keine regulaere Datei: {quote(path)}")
    return handle


def read_regular(path: Path, limit: int, what: str) -> bytes:
    with os.fdopen(open_regular(path), "rb") as source:
        data = source.read(limit + 1)
    if len(data) > limit:
        raise PackageError(f"{what} ist zu gross.")
    return data


def copy_hashed(source: Path, destination: Path, budget: int) -> tuple[int, str]:
    """Kopiert und hasht dieselben Bytes in einem Durchgang; Groesse und Hash beschreiben genau die geschriebene Kopie."""
    digest = hashlib.sha256()
    size = 0
    tail = b""
    with os.fdopen(open_regular(source), "rb") as reader, open(destination, "xb") as writer:
        while chunk := reader.read(CHUNK):
            size += len(chunk)
            if size > budget:
                raise PackageError("Paketinhalt groesser als 1 GiB.")
            # Ganze Datei durchsuchen; der Ueberlapp findet auch einen Kopf, der ueber eine Blockgrenze reicht.
            if PRIVATE_KEY.search(tail + chunk):
                raise PackageError(f"Inhalt enthaelt einen privaten Schluessel (PEM): {quote(source)}")
            tail = (tail + chunk)[-PRIVATE_KEY_OVERLAP:]
            digest.update(chunk)
            writer.write(chunk)
    return size, digest.hexdigest()


def rename_no_replace(source: Path, target: Path) -> None:
    """Benennt um, ohne ein vorhandenes Ziel zu ersetzen (auch keinen leeren Ordner, den os.rename still ersetzen wuerde)."""
    renameat2 = None
    if sys.platform.startswith("linux"):
        try:
            renameat2 = ctypes.CDLL(None, use_errno=True).renameat2
        except (OSError, AttributeError):
            renameat2 = None
    if renameat2 is not None:
        at_fdcwd, rename_noreplace = -100, 1
        if renameat2(at_fdcwd, os.fsencode(source), at_fdcwd, os.fsencode(target), rename_noreplace) == 0:
            return
        code = ctypes.get_errno()
        if code == errno.EEXIST:
            raise PackageError("Version ist waehrend des Schreibens entstanden; nichts ueberschrieben.")
        if code not in (errno.ENOSYS, errno.EINVAL):
            raise OSError(code, os.strerror(code), str(target))
    # Ohne renameat2 (anderes System, Dateisystem ohne RENAME_NOREPLACE) bleibt ein kleines Fenster (Doku, Grenzen).
    if os.path.lexists(target):
        raise PackageError("Version ist waehrend des Schreibens entstanden; nichts ueberschrieben.")
    os.rename(source, target)


def hash_file(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    with os.fdopen(open_regular(path), "rb") as reader:
        while chunk := reader.read(CHUNK):
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


def parse_file_arguments(items: list[str]) -> list[tuple[Path, str]]:
    result = []
    for item in items:
        # Paketpfade enthalten kein '='; so darf die Quelle eines enthalten.
        source, separator, target = item.rpartition("=")
        if not separator or not source:
            raise PackageError(f"Dateiangabe muss QUELLE=PFAD sein: {quote(item)}")
        if not valid_path(target):
            raise PackageError(f"Ungueltiger Paketpfad (relativ unter usr/, lib/ oder etc/, ohne . und ..): {quote(target)}")
        result.append((Path(source), target))
    return result


def refuse_symlinks(base: Path, *parts: str) -> Path:
    """Baut base/parts und lehnt einen symbolischen Link auf jeder Stufe unter base ab (auch base selbst)."""
    path = base
    if os.path.islink(path):
        raise PackageError(f"Repository ist ein symbolischer Link: {quote(path)}")
    for part in parts:
        path = path / part
        if os.path.islink(path):
            raise PackageError(f"Symbolischer Link im Repository: {quote(path)}")
    return path


def same_file(left: Path, right: Path) -> bool:
    try:
        return os.path.samefile(left, right)
    except OSError:
        return False


def is_inside(path: Path, directory: Path) -> bool:
    try:
        path.resolve().relative_to(directory.resolve())
        return True
    except ValueError:
        return False


# --- Signatur -----------------------------------------------------------------------------------------------------------

class Signer:
    """Signiert mit build_package.sign und prueft jede Signatur mit dem daraus abgeleiteten oeffentlichen Schluessel."""

    def __init__(self, key: Path, scratch: Path):
        if os.path.islink(key) or not key.is_file():
            raise PackageError("Schluessel ist keine regulaere Datei.")
        self.key = key
        self.public = scratch / "public.pem"
        # Ausgabe abgefangen: OpenSSL soll nichts zum Schluessel ins Terminal schreiben.
        derived = subprocess.run(["openssl", "pkey", "-in", str(key), "-pubout", "-out", str(self.public)], capture_output=True)
        if derived.returncode != 0:
            raise PackageError("Schluessel nicht lesbar (OpenSSL).")

    def sign(self, content: Path, signature: Path) -> None:
        build_package.sign(self.key, content, signature)
        if not self.verify(content, signature):
            raise PackageError("Signatur passt nicht zu den geschriebenen Bytes.")

    def verify(self, content: Path, signature: Path) -> bool:
        if signature.stat().st_size != 64:
            return False
        checked = subprocess.run(["openssl", "pkeyutl", "-verify", "-pubin", "-inkey", str(self.public), "-rawin",
                                  "-in", str(content), "-sigfile", str(signature)], capture_output=True)
        return checked.returncode == 0


# --- Befehle ------------------------------------------------------------------------------------------------------------

def package(args: argparse.Namespace) -> None:
    spec = load_spec(args.spec)
    sources = parse_file_arguments(args.files)
    if len(sources) > MAX_FILES:
        raise PackageError("Mehr als 4096 Dateien.")
    executable = set(spec.get("exec", []))
    targets = {target for _, target in sources}
    if len(targets) != len(sources):
        raise PackageError("Paketpfad doppelt angegeben.")
    missing = sorted(executable - targets)
    if missing:
        raise PackageError(f"exec nennt keine angegebene Datei: {quote(missing[0])}")
    for source, _ in sources:
        if same_file(source, args.key):
            raise PackageError("Der Schluessel darf nicht Paketinhalt sein.")
    if is_inside(args.key, args.repo):
        raise PackageError("Der Schluessel liegt im Repository; er darf nie veroeffentlicht werden.")
    # Erst alle Felder ohne Inhalt pruefen, damit ein Fehler in der Spec nichts schreibt.
    placeholder = [(target, 0, "0" * 64, target in executable) for _, target in sources]
    Manifest(spec, placeholder)

    with tempfile.TemporaryDirectory() as scratch:
        signer = Signer(args.key, Path(scratch))
        manifest, data = write_version(args, spec, sources, executable, signer)
    print(f"{manifest.id} {manifest.full_version}: {hashlib.sha256(data).hexdigest()}")


def write_version(args: argparse.Namespace, spec: dict, sources: list[tuple[Path, str]], executable: set[str],
                  signer: "Signer") -> tuple["Manifest", bytes]:
    args.repo.mkdir(parents=True, exist_ok=True)
    package_dir = refuse_symlinks(args.repo, spec["id"])
    created = not os.path.lexists(package_dir)
    package_dir.mkdir(exist_ok=True)
    final = refuse_symlinks(package_dir, f"{spec['version']}-r{spec['release']}")
    if os.path.lexists(final):
        raise PackageError(f"{spec['id']} {spec['version']}-r{spec['release']} existiert bereits; veroeffentlichte Versionen sind unveraenderlich.")
    # Temporaer mit Punkt am Anfang: Der Katalog und PackageStore2 halten das nie fuer eine Version.
    staging = package_dir / f".neu-{secrets.token_hex(8)}"
    staging.mkdir()
    try:
        files = []
        budget = MAX_CONTENT
        for source, target in sorted(sources, key=lambda item: item[1]):
            destination = staging / "files" / target
            destination.parent.mkdir(parents=True, exist_ok=True)
            size, digest = copy_hashed(source, destination, budget)
            budget -= size
            files.append((target, size, digest, target in executable))
        manifest = Manifest(spec, files)
        data = manifest.render()
        # Dieselbe Lesefunktion wie der Katalog: Was hier nicht kanonisch zurueckkommt, wird nicht signiert.
        parse_manifest(data)
        (staging / "manifest").write_bytes(data)
        signer.sign(staging / "manifest", staging / "manifest.sig")
        if (staging / "manifest").read_bytes() != data:
            raise PackageError("Manifest wurde waehrend des Signierens veraendert.")
        rename_no_replace(staging, final)
    except BaseException:
        # Ein halber Versionsordner sperrte die Version (unveraenderlich); deshalb nur der fertige Ordner unter seinem Namen.
        shutil.rmtree(staging, ignore_errors=True)
        if created:
            try:
                package_dir.rmdir()
            except OSError:
                pass
        raise
    return manifest, data


def read_version(directory: Path, package_id: str, version: str, release: int, signer: "Signer", scratch: Path) -> "Manifest":
    """Prueft eine Version im Repository: Signatur vor dem Parsen, Kennung und Version, jede Datei mit Groesse und Hash."""
    label = f"{package_id} {version}-r{release}"
    manifest_path = refuse_symlinks(directory, "manifest")
    signature_path = refuse_symlinks(directory, "manifest.sig")
    # Signatur vor dem Parsen, und zwar ueber genau die Bytes, die danach geparst werden (eigene Kopie, kein Wettlauf).
    data = read_regular(manifest_path, MAX_MANIFEST, "Manifest")
    copy, signature = scratch / "manifest", scratch / "manifest.sig"
    copy.write_bytes(data)
    signature.write_bytes(read_regular(signature_path, 64, "Signatur"))
    if not signer.verify(copy, signature):
        raise PackageError(f"{label}: Signatur ungueltig.")
    try:
        manifest = parse_manifest(data)
    except PackageError as error:
        raise PackageError(f"{label}: {error}") from None
    if manifest.id != package_id or manifest.version != version or manifest.release != release:
        raise PackageError(f"{label}: Manifest nennt eine andere Kennung oder Version.")
    files_dir = refuse_symlinks(directory, "files")
    expected = {path: (size, digest) for path, size, digest, _ in manifest.files}
    found = set()
    for root, folders, names in os.walk(files_dir):
        for name in folders + names:
            if os.path.islink(Path(root) / name):
                raise PackageError(f"{label}: symbolischer Link unter files/.")
        for name in names:
            found.add((Path(root) / name).relative_to(files_dir).as_posix())
    if found != set(expected):
        raise PackageError(f"{label}: Dateien unter files/ passen nicht zum Manifest.")
    for path, (size, digest) in expected.items():
        if hash_file(files_dir / path) != (size, digest):
            raise PackageError(f"{label}: Datei weicht vom Manifest ab: {quote(path)}")
    return manifest


def catalog(args: argparse.Namespace) -> None:
    if not 1 <= args.generation <= MAX_GENERATION:
        raise PackageError("Generation muss zwischen 1 und 9999999999 liegen.")
    if is_inside(args.key, args.repo):
        raise PackageError("Der Schluessel liegt im Repository; er darf nie veroeffentlicht werden.")
    refuse_symlinks(args.repo)
    existing = refuse_symlinks(args.repo, "catalog2")
    if os.path.lexists(existing):
        head = read_regular(existing, MAX_CATALOG, "Vorhandener Katalog").split(b"\n", 2)
        known = re.fullmatch(rb"generation=([1-9][0-9]{0,9})", head[1]) if len(head) > 2 and head[0] == b"zehos-catalog 2" else None
        if known is None:
            raise PackageError("Vorhandener catalog2 ist unlesbar; nichts geschrieben.")
        # ZehOS nimmt keinen aelteren Katalog an; ein neuer Stand braucht immer eine hoehere Generation.
        if args.generation <= int(known.group(1)):
            raise PackageError(f"Generation muss groesser als die vorhandene ({int(known.group(1))}) sein.")
    with tempfile.TemporaryDirectory() as scratch:
        signer = Signer(args.key, Path(scratch))
        manifests: list[tuple[Manifest, str]] = []
        for package_dir in sorted(p for p in args.repo.iterdir() if ID.fullmatch(p.name)):
            # Ein verlinkter Paket- oder Versionsordner wuerde still uebergangen und eine Version nicht veroeffentlicht.
            if package_dir.is_symlink():
                raise PackageError(f"Symbolischer Link im Repository: {quote(package_dir.name)}")
            if not package_dir.is_dir():
                continue
            versions = []
            for version_dir in package_dir.iterdir():
                match = VERSION_DIR.fullmatch(version_dir.name)
                if match and version_dir.is_symlink():
                    raise PackageError(f"Symbolischer Link im Repository: {quote(package_dir.name + '/' + version_dir.name)}")
                if match and version_dir.is_dir():
                    versions.append((version_key(match.group(1), int(match.group(2))), match.group(1), int(match.group(2)), version_dir))
            if versions:
                _, version, release, newest = max(versions)
                manifest = read_version(newest, package_dir.name, version, release, signer, Path(scratch))
                manifests.append((manifest, hashlib.sha256(manifest.render()).hexdigest()))
        if len(manifests) > MAX_ENTRIES:
            raise PackageError("Mehr als 4096 Pakete.")
        check_dependencies([m for m, _ in manifests])
        lines = ["zehos-catalog 2", f"generation={args.generation}"]
        for manifest, digest in manifests:
            lines.append(f"package={manifest.id} {manifest.full_version} {digest} {manifest.content_bytes}")
            lines += [f"depends={d}" for d in manifest.depends]
            lines += [f"provides={p}" for p in manifest.provides]
        lines.append("end")
        data = ("\n".join(lines) + "\n").encode("utf-8")
        if len(data) > MAX_CATALOG:
            raise PackageError("Katalog groesser als 4 MiB.")
        # Neben dem Ziel schreiben und erst nach gepruefter Signatur umbenennen; ein Fehler laesst den alten Katalog stehen.
        new_catalog, new_signature = args.repo / ".catalog2.neu", args.repo / ".catalog2.sig.neu"
        try:
            for leftover in (new_catalog, new_signature):
                if os.path.lexists(leftover):
                    leftover.unlink()
            new_catalog.write_bytes(data)
            signer.sign(new_catalog, new_signature)
            if new_catalog.read_bytes() != data:
                raise PackageError("Katalog wurde waehrend des Signierens veraendert.")
            os.replace(new_catalog, args.repo / "catalog2")
            os.replace(new_signature, args.repo / "catalog2.sig")
        finally:
            for leftover in (new_catalog, new_signature):
                if os.path.lexists(leftover):
                    leftover.unlink()
    print(f"Katalog 2 Generation {args.generation}: {len(manifests)} Pakete")


def check_dependencies(manifests: list[Manifest]) -> None:
    """Jede Abhaengigkeit muss im Katalog mit mindestens der verlangten Version stehen, sonst scheitert jede Installation."""
    available = {m.id: version_key(m.version, m.release) for m in manifests}
    for manifest in manifests:
        for dependency in manifest.depends:
            match = DEPENDENCY.fullmatch(dependency)
            wanted = version_key(match.group(2), int(match.group(3)))
            if available.get(match.group(1), (-1,)) < wanted:
                raise PackageError(f"{manifest.id}: Abhaengigkeit fehlt im Katalog oder ist zu alt: {quote(dependency)}")


def _interrupt(number, frame) -> None:
    raise KeyboardInterrupt


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    build = commands.add_parser("package")
    for name in ("--key", "--repo", "--spec"):
        build.add_argument(name, type=Path, required=True)
    build.add_argument("files", nargs="+", help="QUELLE=PFAD im Paket (unter usr/, lib/ oder etc/)")
    index = commands.add_parser("catalog")
    index.add_argument("--key", type=Path, required=True)
    index.add_argument("--repo", type=Path, required=True)
    index.add_argument("--generation", type=int, required=True)
    args = parser.parse_args(argv)
    try:
        package(args) if args.command == "package" else catalog(args)
    except PackageError as error:
        print(f"Paketfehler: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Paketfehler: abgebrochen; nichts veroeffentlicht.", file=sys.stderr)
        return 130
    except ValueError:
        # build_package.sign meldet so eine nicht rohe Ed25519-Signatur; andere ValueError tragen womoeglich Eingabetext.
        print("Paketfehler: Signatur nicht erzeugt (kein Ed25519-Schluessel?).", file=sys.stderr)
        return 1
    except subprocess.CalledProcessError:
        # Die Ausnahme nennt die Befehlszeile; die Meldung bleibt allgemein.
        print("Paketfehler: OpenSSL ist gescheitert.", file=sys.stderr)
        return 1
    except OSError as error:
        print(f"Paketfehler: {error.strerror or 'Ein-/Ausgabefehler'} {quote(error.filename or '')}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    # SIGTERM und SIGHUP wie Strg+C behandeln, damit das Aufraeumen (except BaseException) auch dann laeuft.
    for _name in ("SIGTERM", "SIGHUP"):
        if hasattr(signal, _name):
            signal.signal(getattr(signal, _name), _interrupt)
    sys.exit(main())
