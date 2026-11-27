#!/usr/bin/env python3
"""Ladybird V28: legt ein tar-Buendel (bin, libexec, lib, share, fonts ...) als Prozesswurzel auf eine neue FAT32-Datenplatte.

Nutzung:
  scripts/linux-root-disk.py <buendel.tar[.gz|.xz|.zst]> <platte.img|platte.qcow2> [--dest lb] [--strip 1]
                             [--add /ziel=quelle ...] [--free-mib 256] [--work <ordner>] [--skip-dangling] [--report <json>]

Im Gast ist das Buendel dann /data/<dest>; `cd /data/<dest>` und `run bin/<programm>` machen es zur Prozesswurzel
(docs/LINUX_ABI.md, "run aus der Shell"). Die Platte ist immer neu (MBR, eine FAT32-Partition); vorhandene Images werden
nie geoeffnet, ein vorhandenes Ziel ist ein Fehler.

Warum ein eigener FAT32-Schreiber statt vvfat oder mtools:
- vvfat (create_disk der Desktop-Proben) baut FAT16 bis 504 MiB, zu klein fuer ein Browserbuendel.
- mtools legt kleingeschriebene 8.3-Namen (libc.so, fonts.conf) ohne langen Eintrag mit NT-Kleinschreib-Flag an; Cosmos wertet
  das Flag nicht aus (COSMOS_SUPPORT.md, "NT-Kleinschreib-Flag"), readdir meldete LIBC.SO. Hier bekommt jeder Name einen langen
  Eintrag (LFN) und behaelt seine Schreibweise.
- reines Python laeuft auch auf Windows-Hosts; qemu-img braucht nur die Ausgabe als qcow2.

FAT kennt keine Symlinks: Jeder Link im Buendel wird aufgeloest und das Ziel kopiert (Datei) oder als Unterbaum kopiert (Ordner).
Absolute Linkziele beginnen an der Buendelwurzel, ".." endet dort (wie die Prozesswurzel im Kernel, LINUX_ABI.md). Ein Link,
dessen Ziel fehlt, ist ein Fehler (mit --skip-dangling: weggelassen und gemeldet); ein Ordnerlink auf einen eigenen Vorfahren
ebenso. Harte Links werden zu Kopien.

Geprueft wird vor dem Schreiben: Namensteil hoechstens 255 UTF-16-Einheiten (FAT-LFN) und 255 Bytes UTF-8 (Linux), Pfad unter
der Prozesswurzel unter 4096 Bytes, keine in FAT verbotenen Zeichen ("*:<>?\\| und Steuerzeichen), kein Punkt oder Leerzeichen am
Ende (FAT schneidet sie ab), keine zwei Namen im selben Ordner, die sich nur in der Schreibweise unterscheiden (FAT und die
Prozesswurzel vergleichen ohne Gross-/Kleinschreibung), kein Teil mit ".zehos" am Anfang (nie Prozesswurzel, LINUX_ABI.md).
Ausfuehrungsrechte gehen verloren; der Kernel meldet vorhandene Dateien mit 0755 (V26).
"""

import argparse
from dataclasses import dataclass, field
import importlib.util
import json
import math
import os
from pathlib import Path, PurePosixPath
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
SECTOR = 512
PARTITION_START = 2048  # 1 MiB, wie fdisk und vvfat ausrichten
RESERVED = 32
FAT_COUNT = 2
MIN_FAT32_CLUSTERS = 65525 + 16  # darunter erkennen Treiber die Platte als FAT16
FAT_FORBIDDEN = set('"*:<>?\\|')
SHORT_ALLOWED = set("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'()-@^_`{}~")
MAX_LINKS = 40
PATH_MAX = 4096


class BundleError(Exception):
    """Fehler im Buendel; die Liste nennt jede Stelle, damit ein Lauf alle Befunde zeigt."""

    def __init__(self, problems: list[str]):
        super().__init__("\n".join(problems))
        self.problems = problems


@dataclass
class Source:
    """Eine Datei im Staging-Ordner; mehrere Eintraege koennen dieselbe Quelle haben (aufgeloeste Links)."""
    path: Path
    size: int
    mtime: float


@dataclass
class Dir:
    mtime: float = 0.0
    children: dict = field(default_factory=dict)  # Name -> Dir | Source


# ---------------------------------------------------------------- Buendel lesen

def _open_tar(bundle: Path):
    """tarfile kennt zstd erst ab Python 3.14; dann liest zstd -dc als Strom."""
    if bundle.suffix in (".zst", ".tzst"):
        process = subprocess.Popen(["zstd", "-dc", str(bundle)], stdout=subprocess.PIPE)
        return tarfile.open(fileobj=process.stdout, mode="r|"), process
    return tarfile.open(bundle, mode="r:*"), None


def _member_path(name: str, strip: int) -> PurePosixPath | None:
    parts = [p for p in PurePosixPath(name).parts if p not in ("", ".", "/")]
    if any(p == ".." for p in parts):
        raise BundleError([f"Eintrag mit '..': {name}"])
    parts = parts[strip:]
    return PurePosixPath(*parts) if parts else None


def read_bundle(bundle: Path, staging: Path, strip: int, warnings: list[str]):
    """Packt Dateien nach staging aus und gibt (Dateien, Ordner, Links, harte Links) mit Pfaden relativ zur Wurzel zurueck."""
    files: dict[PurePosixPath, Source] = {}
    dirs: dict[PurePosixPath, float] = {}
    links: dict[PurePosixPath, str] = {}
    hardlinks: dict[PurePosixPath, PurePosixPath] = {}
    archive, process = _open_tar(bundle)
    counter = 0
    try:
        for member in archive:
            path = _member_path(member.name, strip)
            if path is None:
                continue
            if member.isdir():
                dirs[path] = member.mtime
            elif member.issym():
                links[path] = member.linkname
            elif member.islnk():
                target = _member_path(member.linkname, strip)
                if target is None:
                    raise BundleError([f"harter Link ohne Ziel: {member.name}"])
                hardlinks[path] = target
            elif member.isfile():
                counter += 1
                # Flache Ablage: Namen aus dem Buendel erreichen das Dateisystem des Hosts nie.
                target = staging / f"{counter:08d}"
                with archive.extractfile(member) as data, target.open("wb") as out:
                    shutil.copyfileobj(data, out, 1 << 20)
                files[path] = Source(target, member.size, member.mtime)
            else:
                warnings.append(f"uebersprungen (Geraet/FIFO): {member.name}")
    finally:
        archive.close()
        if process is not None:
            process.stdout.close()
            if process.wait() != 0:
                raise BundleError([f"zstd -dc endete mit {process.returncode}"])
    for path, target in hardlinks.items():
        if target not in files:
            raise BundleError([f"harter Link {path} zeigt auf fehlende Datei {target}"])
        files[path] = files[target]
    return files, dirs, links


def build_tree(files, dirs, links, adds: dict[PurePosixPath, Path], skip_dangling: bool,
               warnings: list[str]) -> Dir:
    """Loest alle Links auf und baut den Baum, der auf die Platte kommt."""
    for path, source in adds.items():
        stat = source.stat()
        files[path] = Source(source, stat.st_size, stat.st_mtime)
        links.pop(path, None)
    kinds: dict[PurePosixPath, str] = {PurePosixPath(): "dir"}
    for path in [*files, *dirs, *links]:
        for parent in path.parents:
            kinds.setdefault(parent, "dir")
    for path in dirs:
        kinds[path] = "dir"
    for path in links:
        kinds[path] = "link"
    for path in files:
        kinds[path] = "file"
    children: dict[PurePosixPath, list[str]] = {}
    for path in kinds:
        if path != PurePosixPath():
            children.setdefault(path.parent, []).append(path.name)

    def resolve(path: PurePosixPath, budget: list[int]) -> PurePosixPath | None:
        """Echter Pfad ohne Links, oder None (fehlt). Absolute Ziele und '..' bleiben in der Wurzel."""
        current: list[str] = []
        pending = list(path.parts)
        while pending:
            part = pending.pop(0)
            if part in ("", "."):
                continue
            if part == "..":
                if current:
                    current.pop()
                continue
            candidate = PurePosixPath(*current, part)
            kind = kinds.get(candidate)
            if kind is None:
                return None
            if kind == "link":
                budget[0] -= 1
                if budget[0] < 0:
                    raise BundleError([f"mehr als {MAX_LINKS} Links bei {path}"])
                target = links[candidate]
                if target.startswith("/"):
                    current = []
                pending = [p for p in target.split("/")] + pending
                continue
            if kind == "file" and pending and any(p not in ("", ".") for p in pending):
                return None  # Datei mitten im Pfad: ENOTDIR, fuer das Buendel wie fehlend
            current.append(part)
        return PurePosixPath(*current)

    problems: list[str] = []
    now = time.time()

    def materialize(real: PurePosixPath, shown: PurePosixPath, ancestors: tuple) -> Dir:
        if real in ancestors:
            problems.append(f"Ordnerlink-Kreis: {shown} -> {real or '/'}")
            return Dir()
        # Ordner ohne eigenen tar-Eintrag bekommen die Zeit des Laufs statt 1980.
        node = Dir(dirs.get(real, now))
        for name in sorted(children.get(real, [])):
            child = real / name
            child_shown = shown / name
            kind = kinds[child]
            if kind == "link":
                target = resolve(child, [MAX_LINKS])
                if target is None:
                    message = f"Link ohne Ziel: /{child_shown} -> {links[child]}"
                    (warnings if skip_dangling else problems).append(message)
                    continue
                child, kind = target, kinds[target]
            if kind == "file":
                # Eigenes Objekt je Eintrag: Clusters werden je Objekt vergeben, geteilte Clusters waeren Kreuzverweise.
                source = files[child]
                node.children[name] = Source(source.path, source.size, source.mtime)
            else:
                node.children[name] = materialize(child, child_shown, ancestors + (real,))
        return node

    try:
        tree = materialize(PurePosixPath(), PurePosixPath(), ())
    except RecursionError:
        raise BundleError(problems + ["Baum zu tief"])
    if problems:
        raise BundleError(problems)
    return tree


# ---------------------------------------------------------------- Pruefen

def check_names(tree: Dir) -> tuple[list[str], dict]:
    """Befunde und Kennzahlen (laengster Name, laengster Pfad, Anzahl)."""
    problems: list[str] = []
    stats = {"files": 0, "dirs": 0, "bytes": 0, "longest_name": "", "longest_path": ""}

    def walk(node: Dir, prefix: str) -> None:
        seen: dict[str, str] = {}
        for name, child in node.children.items():
            path = f"{prefix}/{name}"
            units = len(name.encode("utf-16-le")) // 2
            if units > 255:
                problems.append(f"Name mit {units} UTF-16-Einheiten (FAT hoechstens 255): {path}")
            if len(name.encode()) > 255:
                problems.append(f"Name mit {len(name.encode())} Bytes (Linux hoechstens 255): {path}")
            if len(path.encode()) >= PATH_MAX:
                problems.append(f"Pfad mit {len(path.encode())} Bytes (hoechstens {PATH_MAX - 1}): {path[:80]}...")
            bad = sorted({c for c in name if c in FAT_FORBIDDEN or ord(c) < 0x20})
            if bad:
                problems.append(f"in FAT verbotene Zeichen {''.join(bad)!r}: {path}")
            if name.endswith((".", " ")):
                problems.append(f"Punkt oder Leerzeichen am Ende (FAT schneidet es ab): {path}")
            if name.lower().startswith(".zehos"):
                problems.append(f"Teil mit .zehos am Anfang (nie Prozesswurzel): {path}")
            key = name.upper()
            if key in seen:
                problems.append(f"nur in der Schreibweise verschieden: {prefix}/{seen[key]} und {path}")
            seen[key] = name
            if len(name) > len(stats["longest_name"]):
                stats["longest_name"] = name
            if len(path.encode()) > len(stats["longest_path"].encode()):
                stats["longest_path"] = path
            if isinstance(child, Dir):
                stats["dirs"] += 1
                walk(child, path)
            else:
                stats["files"] += 1
                stats["bytes"] += child.size

    walk(tree, "")
    return problems, stats


# ---------------------------------------------------------------- FAT32 schreiben

def _fat_datetime(mtime: float) -> tuple[int, int]:
    t = time.gmtime(max(315532800, min(mtime, 4354819199)))  # 1980 bis 2107
    date = ((t.tm_year - 1980) << 9) | (t.tm_mon << 5) | t.tm_mday
    clock = (t.tm_hour << 11) | (t.tm_min << 5) | (t.tm_sec // 2)
    return date, clock


def _short_name(name: str, used: set[bytes]) -> bytes:
    """8.3-Name mit Endung ~N; jeder Name bekommt ohnehin einen langen Eintrag, der Kurzname muss nur eindeutig sein."""
    upper = name.upper().lstrip(".")
    base, _, ext = upper.rpartition(".") if "." in upper else (upper, "", "")
    clean = lambda text: "".join(c if c in SHORT_ALLOWED else "_" for c in text if c not in " .")
    base, ext = clean(base) or "_", clean(ext)[:3]
    for number in range(1, 1000000):
        tail = f"~{number}"
        candidate = (base[:8 - len(tail)] + tail).ljust(8).encode("ascii") + ext.ljust(3).encode("ascii")
        if candidate not in used:
            used.add(candidate)
            return candidate
    raise BundleError([f"keine freie 8.3-Endung fuer {name}"])


def _checksum(short: bytes) -> int:
    total = 0
    for byte in short:
        total = (((total & 1) << 7) + (total >> 1) + byte) & 0xFF
    return total


def _lfn_entries(name: str, checksum: int) -> list[bytes]:
    units = list(struct.unpack(f"<{len(name.encode('utf-16-le')) // 2}H", name.encode("utf-16-le")))
    count = math.ceil(len(units) / 13)
    if len(units) % 13:
        units.append(0)
    units += [0xFFFF] * (count * 13 - len(units))
    entries = []
    for index in range(count, 0, -1):
        chunk = units[(index - 1) * 13:index * 13]
        order = index | (0x40 if index == count else 0)
        entries.append(struct.pack("<B5HBBB6HH2H", order, *chunk[:5], 0x0F, 0, checksum, *chunk[5:11], 0, *chunk[11:13]))
    return entries


def _short_entry(short: bytes, attr: int, cluster: int, size: int, mtime: float) -> bytes:
    date, clock = _fat_datetime(mtime)
    return struct.pack("<11sBBBHHHHHHHI", short, attr, 0, 0, clock, date, date, cluster >> 16, clock, date,
                       cluster & 0xFFFF, size)


def _entry_count(node: Dir, is_root: bool) -> int:
    count = 0 if is_root else 2
    for name in node.children:
        count += math.ceil(len(name.encode("utf-16-le")) // 2 / 13) + 1
    return count


def write_fat32(tree: Dir, image: Path, cluster_size: int = 4096, free_bytes: int = 256 << 20,
                label: str = "NO NAME") -> dict:
    """Schreibt MBR und eine FAT32-Partition (Typ 0x0C ab Sektor 2048) mit tree als Inhalt. Clusters liegen zusammenhaengend.
    Eine Bezeichnung im Bootsektor verlangt einen Bezeichnungseintrag in der Wurzel (fsck.fat); ohne ihn bleibt "NO NAME"."""
    per_cluster = lambda size: math.ceil(size / cluster_size)
    plan: list[tuple] = []  # (Knoten, erster Cluster, Clusteranzahl)
    next_cluster = [2]

    def allocate(node, is_root: bool):
        if isinstance(node, Dir):
            clusters = max(1, per_cluster(_entry_count(node, is_root) * 32))
        else:
            clusters = per_cluster(node.size)
        first = next_cluster[0] if clusters else 0
        next_cluster[0] += clusters
        plan.append((node, first, clusters))
        if isinstance(node, Dir):
            for child in node.children.values():
                allocate(child, False)
        return first

    allocate(tree, True)
    used = next_cluster[0] - 2
    clusters = max(MIN_FAT32_CLUSTERS, used + per_cluster(free_bytes))
    fat_sectors = math.ceil((clusters + 2) * 4 / SECTOR)
    spc = cluster_size // SECTOR
    total_sectors = RESERVED + FAT_COUNT * fat_sectors + clusters * spc
    if total_sectors >= 1 << 32 or clusters > 0x0FFFFFF5:
        raise BundleError(["Platte zu gross fuer FAT32"])
    first_cluster = {id(node): first for node, first, _ in plan}
    data_start = (PARTITION_START + RESERVED + FAT_COUNT * fat_sectors) * SECTOR
    cluster_offset = lambda number: data_start + (number - 2) * cluster_size

    with image.open("xb") as out:
        out.truncate((PARTITION_START + total_sectors) * SECTOR)
        mbr = bytearray(SECTOR)
        mbr[446:462] = struct.pack("<B3sB3sII", 0, b"\xfe\xff\xff", 0x0C, b"\xfe\xff\xff", PARTITION_START, total_sectors)
        mbr[510:512] = b"\x55\xaa"
        out.seek(0)
        out.write(mbr)

        boot = bytearray(SECTOR)
        boot[0:3] = b"\xeb\x58\x90"
        boot[3:11] = b"MSWIN4.1"
        struct.pack_into("<HBHBHHBHHHII", boot, 11, SECTOR, spc, RESERVED, FAT_COUNT, 0, 0, 0xF8, 0, 63, 255,
                         PARTITION_START, total_sectors)
        struct.pack_into("<IHHIHH", boot, 36, fat_sectors, 0, 0, 2, 1, 6)
        struct.pack_into("<BBBI11s8s", boot, 64, 0x80, 0, 0x29, 0x5A454853, label.upper().ljust(11)[:11].encode("ascii"),
                         b"FAT32   ")
        boot[510:512] = b"\x55\xaa"
        info = bytearray(SECTOR)
        struct.pack_into("<I", info, 0, 0x41615252)
        struct.pack_into("<III", info, 484, 0x61417272, clusters - used, next_cluster[0])
        struct.pack_into("<I", info, 508, 0xAA550000)
        third = bytearray(SECTOR)
        third[510:512] = b"\x55\xaa"
        for base in (0, 6):  # Hauptsektoren und Sicherung ab Sektor 6
            out.seek((PARTITION_START + base) * SECTOR)
            out.write(boot + info + third)

        fat = bytearray((clusters + 2) * 4)
        struct.pack_into("<II", fat, 0, 0x0FFFFFF8, 0x0FFFFFFF)
        for _, first, count in plan:
            for number in range(first, first + count):
                struct.pack_into("<I", fat, number * 4, number + 1 if number + 1 < first + count else 0x0FFFFFFF)
        for copy in range(FAT_COUNT):
            out.seek((PARTITION_START + RESERVED + copy * fat_sectors) * SECTOR)
            out.write(fat)

        def write_dir(node: Dir, first: int, parent: int, is_root: bool):
            entries = []
            if not is_root:
                entries.append(_short_entry(b".          ", 0x10, first, 0, node.mtime))
                entries.append(_short_entry(b"..         ", 0x10, parent, 0, node.mtime))
            shorts: set[bytes] = set()
            for name, child in node.children.items():
                short = _short_name(name, shorts)
                entries += _lfn_entries(name, _checksum(short))
                if isinstance(child, Dir):
                    entries.append(_short_entry(short, 0x10, first_cluster[id(child)], 0, child.mtime))
                else:
                    entries.append(_short_entry(short, 0x20, first_cluster[id(child)] if child.size else 0,
                                                child.size, child.mtime))
            out.seek(cluster_offset(first))
            out.write(b"".join(entries))
            for child in node.children.values():
                if isinstance(child, Dir):
                    # ".." auf die Wurzel ist Cluster 0 (FAT-Spezifikation), nicht der Wurzelcluster 2.
                    write_dir(child, first_cluster[id(child)], 0 if is_root else first, False)

        write_dir(tree, 2, 0, True)
        for node, first, count in plan:
            if isinstance(node, Source) and count:
                out.seek(cluster_offset(first))
                with node.path.open("rb") as data:
                    copied = 0
                    while chunk := data.read(1 << 20):
                        out.write(chunk)
                        copied += len(chunk)
                if copied != node.size:
                    raise BundleError([f"Groesse geaendert: {node.path} ({copied} statt {node.size})"])
    return {"clusters": clusters, "used_clusters": used, "cluster_size": cluster_size,
            "partition_offset": PARTITION_START * SECTOR, "image_bytes": (PARTITION_START + total_sectors) * SECTOR}


def to_qcow2(raw: Path, target: Path) -> None:
    spec = importlib.util.spec_from_file_location("doom_desktop", ROOT / "scripts/test-wasm-doom-desktop.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    tool, environment = module.qemu_img()
    subprocess.run([*tool, "convert", "-f", "raw", "-O", "qcow2", str(raw), str(target)], check=True,
                   stdout=subprocess.DEVNULL, env=environment)


def build(bundle: Path, image: Path, dest: str = "lb", strip: int = 0, adds: list[str] | None = None,
          free_mib: int = 256, work: Path | None = None, skip_dangling: bool = False, cluster_size: int = 4096) -> dict:
    if image.exists():
        raise BundleError([f"Ziel existiert schon (vorhandene Images werden nie ueberschrieben): {image}"])
    dest_parts = [p for p in dest.strip("/").split("/") if p]
    if not dest_parts or any(p in (".", "..") for p in dest_parts):
        raise BundleError([f"--dest braucht einen Ordnernamen: {dest}"])
    add_map: dict[PurePosixPath, Path] = {}
    for item in adds or []:
        target, _, source = item.partition("=")
        path = _member_path(target, 0) if target.startswith("/") else None
        if path is None or not source or not Path(source).is_file():
            raise BundleError([f"--add braucht /ziel=vorhandene-datei: {item}"])
        add_map[path] = Path(source)
    warnings: list[str] = []
    with tempfile.TemporaryDirectory(prefix="lrd-", dir=work) as temp:
        staging = Path(temp)
        files, dirs, links = read_bundle(bundle, staging, strip, warnings)
        tree = build_tree(files, dirs, links, add_map, skip_dangling, warnings)
        problems, stats = check_names(tree)
        # Die Pfadgrenze gilt in der Prozesswurzel; zusaetzlich der Name des Zielordners auf der Platte.
        problems += check_names(Dir(children={p: Dir() for p in dest_parts}))[0]
        if problems:
            raise BundleError(problems)
        disk = Dir(time.time())
        node = disk
        for part in dest_parts[:-1]:
            node.children[part] = Dir(time.time())
            node = node.children[part]
        tree.mtime = tree.mtime or time.time()
        node.children[dest_parts[-1]] = tree
        raw = image if image.suffix != ".qcow2" else staging / "disk.img"
        layout = write_fat32(tree=disk, image=raw, cluster_size=cluster_size, free_bytes=free_mib << 20)
        if raw != image:
            to_qcow2(raw, image)
    return {**stats, **layout, "links": len(links), "warnings": warnings, "dest": "/".join(dest_parts)}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("bundle", type=Path)
    parser.add_argument("image", type=Path)
    parser.add_argument("--dest", default="lb", help="Ordner auf der Platte (im Gast /data/<dest>)")
    parser.add_argument("--strip", type=int, default=0, help="fuehrende Pfadteile im tar weglassen")
    parser.add_argument("--add", action="append", default=[], help="/ziel=quelle, zusaetzliche Datei in der Wurzel")
    parser.add_argument("--free-mib", type=int, default=256, help="freier Platz auf der Platte")
    parser.add_argument("--work", type=Path, help="Ordner fuer das Auspacken (Standard: Temp)")
    parser.add_argument("--skip-dangling", action="store_true", help="Links ohne Ziel weglassen statt abbrechen")
    parser.add_argument("--report", type=Path, help="Kennzahlen als JSON")
    args = parser.parse_args(argv)
    try:
        result = build(args.bundle, args.image, args.dest, args.strip, args.add, args.free_mib, args.work,
                       args.skip_dangling)
    except BundleError as error:
        for problem in error.problems:
            print(f"FEHLER {problem}", file=sys.stderr)
        return 1
    for warning in result["warnings"]:
        print(f"WARNUNG {warning}", file=sys.stderr)
    print(f"LINUX_ROOT_DISK ok dest=/{result['dest']} files={result['files']} dirs={result['dirs']} "
          f"bytes={result['bytes']} links={result['links']} longest_name={len(result['longest_name'])} "
          f"longest_path={len(result['longest_path'].encode())} image={result['image_bytes']}")
    if args.report:
        args.report.write_text(json.dumps(result, indent=1, ensure_ascii=False), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
