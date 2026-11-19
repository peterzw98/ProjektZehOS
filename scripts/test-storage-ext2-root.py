#!/usr/bin/env python3
"""Hostseite der E4.0-RW-Qualifikation (ext2 als /), Spec docs/superpowers/specs/2026-11-17-storage-e4-0-rw-qualification.md.

Das ISO muss mit -p:ZehOSStorageRootProbe=true gebaut sein. Geraete je Lauf (AHCI-Port = sataN im Gast):
  sata0 fat.qcow2 64 MiB (FAT fuer /boot/efi, formatiert die Probe in Lauf 1), sata1 Root qcow2 32 oder 128 GiB,
  sata2 inodes.qcow2 64 MiB (nur Lauf 1 benutzt; eine Gruppe, 4096 Inodes), sata3 control.img 1 MiB RAW mit "ZEHOS-ROOTPROBE run=N" in Sektor 0.
Laeufe (--size 2): 1 frisch mit Last, 2 Neustart und Pruefung, 3 harter Abbruch (kill) nach 0,3/2/7/20 s auf je einer
Overlay-Kopie, 4 Gate ReadOnly und unveraendertes Image je Kopie, danach e2fsck -fy auf einer weiteren Kopie, 5 auf der
reparierten Kopie. Hochgruppenlauf (--size 32|128, Leitungsentscheid 19:02): 6 nur formatieren, Host belegt die unteren
Gruppen per debugfs fallocate in 1-GiB-Dateien unter /fill, 7 schreibt 2 GiB - 1 in die oberen Gruppen (8 mit --k-boundary
nur den letzten Abschnitt per Seek) und fuellt bis ENOSPC.
Nach jedem Lauf e2fsck -fn und dumpe2fs -h auf einer sparse RAW-Kopie. Exit 0 nur bei Go (Spec Abschnitt 5),
1 bei No-Go, 2 wenn Werkzeuge fehlen (offen, nie bestanden). Der Arbeitsordner muss neu sein.
"""
import argparse
import importlib.util
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)

MIB = 1024 * 1024
GIB = 1024 * MIB
RESULT = re.compile(r"^STORAGE_ROOT (?!DONE|COUNTERS)(\S+) (PASS|FAIL)\b", re.MULTILINE)
COUNTERS = re.compile(r"^STORAGE_ROOT COUNTERS (\S+) free_blocks=(\d+) free_inodes=(\d+) r_blocks=(\d+) state=(\d+)", re.MULTILINE)
ABORT_DELAYS = (0.3, 2.0, 7.0, 20.0)
# Frei bleiben die obersten Gruppen fuer k (2 GiB - 1) und etwas Luft fuer Verzeichnisse und l1/l2.
HIGH_GROUP_FREE = 2 * GIB + 256 * MIB
FILL_FILE_BLOCKS = GIB // 4096
# Spec Abschnitt 6, Punkt 4: waechst das qcow2 ueber 16 GiB oder faellt der freie Hostplatz unter 10 GiB, wirkt die
# Nullenerkennung nicht; Abbruch als Befund, kein Go.
QCOW2_LIMIT = 16 * GIB
HOST_FREE_MINIMUM = 10 * GIB
MACHINE_BASE = list(tools.MACHINE)


def new_work_dir(path: Path) -> Path:
    """Legt den Arbeitsordner an; ein vorhandener Pfad (auch Datei oder Link) ist ein Fehler."""
    if path.exists() or path.is_symlink():
        raise FileExistsError(f"Arbeitsordner existiert bereits: {path}")
    path.mkdir(parents=True)
    return path


def control_sector(run: int) -> bytes:
    if run not in range(1, 17):
        raise ValueError(f"Lauf ausserhalb 1..16: {run}")
    text = f"ZEHOS-ROOTPROBE run={run}\n".encode("ascii")
    return text + bytes(512 - len(text))


def write_control(path: Path, run: int) -> None:
    """1-MiB-Steuerplatte; wird je Lauf neu geschrieben, nur sie (nie ein Pruefimage)."""
    with path.open("wb") as image:
        image.write(control_sector(run))
        image.truncate(MIB)


def evaluate(serial: str) -> tuple[int, list[str], bool]:
    """Liefert (Anzahl PASS, Namen der FAIL-Schritte, DONE gesehen)."""
    results = [(match.group(1), match.group(2)) for match in RESULT.finditer(serial)]
    failed = [name for name, outcome in results if outcome == "FAIL"]
    return sum(1 for _, outcome in results if outcome == "PASS"), failed, "STORAGE_ROOT DONE" in serial


def guest_counters(serial: str, phase: str = "m") -> dict[str, int] | None:
    found = None
    for match in COUNTERS.finditer(serial):
        if match.group(1) == phase:
            found = {"free_blocks": int(match.group(2)), "free_inodes": int(match.group(3)),
                     "r_blocks": int(match.group(4)), "state": int(match.group(5))}
    return found


def parse_dumpe2fs(text: str) -> dict[str, int | str]:
    """Die Felder aus dumpe2fs -h, die der Gast nach m meldet."""
    keys = {"Free blocks": "free_blocks", "Free inodes": "free_inodes", "Reserved block count": "r_blocks",
            "Filesystem state": "state"}
    values: dict[str, int | str] = {}
    for line in text.splitlines():
        name, _, value = line.partition(":")
        if name.strip() in keys:
            target = keys[name.strip()]
            value = value.strip()
            values[target] = value if target == "state" else int(value)
    return values


def counters_match(guest: dict[str, int] | None, host: dict[str, int | str]) -> list[str]:
    """Abweichungen Gast (nach m) gegen dumpe2fs -h; leer = gleich. state 1 entspricht "clean"."""
    if guest is None:
        return ["Gast meldete keine COUNTERS m"]
    problems = [f"{key}: Gast {guest[key]}, Image {host.get(key)}" for key in ("free_blocks", "free_inodes", "r_blocks")
                if host.get(key) != guest[key]]
    if guest["state"] != 1 or host.get("state") != "clean":
        problems.append(f"state: Gast {guest['state']}, Image {host.get('state')}")
    return problems


class Context:
    def __init__(self, args: argparse.Namespace, work: Path):
        self.args = args
        self.work = work
        self.tool, self.environment = tools.qemu_img()
        self.report: list[str] = []
        self.go = True

    def note(self, line: str, ok: bool = True) -> None:
        print(line, flush=True)
        self.report.append(line)
        if not ok:
            self.go = False

    def qemu_img(self, *arguments: str) -> None:
        subprocess.run([*self.tool, *arguments], check=True, stdout=subprocess.DEVNULL, env=self.environment)

    def raw_copy(self, image: Path, name: str) -> Path:
        """Sparse RAW-Kopie fuer e2fsck/dumpe2fs (Linux-Host; auf NTFS vorher Sparse-Flag setzen, Spec Abschnitt 4)."""
        raw = self.work / f"{name}.raw"
        started = time.monotonic()
        self.qemu_img("convert", "-O", "raw", str(image), str(raw))
        self.note(f"{name}: RAW-Kopie {time.monotonic() - started:.0f} s, belegt {allocated(raw) / GIB:.2f} GiB")
        return raw


def allocated(path: Path) -> int:
    stat = path.stat()
    return getattr(stat, "st_blocks", 0) * 512 or stat.st_size


def drives(root: Path, root_format: str, inodes: Path, control: Path) -> list[str]:
    return ["-drive", f"file={root},format={root_format},if=none,id=rootdisk,discard=unmap,detect-zeroes=unmap",
            "-device", "ide-hd,drive=rootdisk,bus=ahci.1",
            "-drive", f"file={inodes},format=qcow2,if=none,id=inodedisk,discard=unmap,detect-zeroes=unmap",
            "-device", "ide-hd,drive=inodedisk,bus=ahci.2",
            "-drive", f"file={control},format=raw,if=none,id=controldisk",
            "-device", "ide-hd,drive=controldisk,bus=ahci.3"]


def boot(ctx: Context, name: str, run: int, root: Path, root_format: str = "qcow2"):
    directory = new_work_dir(ctx.work / name)
    control = ctx.work / "control.img"
    write_control(control, run)
    base = list(MACHINE_BASE)
    base[base.index("-m") + 1] = ctx.args.memory
    tools.MACHINE = [*base, *drives(root, root_format, ctx.work / "inodes.qcow2", control)]
    process = tools.start_qemu(ctx.args.iso, ctx.work / "fat.qcow2", directory, tools.free_port(), ctx.args.accel, None)
    return process, directory


def watch_space(ctx: Context, image: Path, process: subprocess.Popen, stop: threading.Event) -> None:
    while not stop.wait(5):
        size = allocated(image)
        free = shutil.disk_usage(ctx.work).free
        if size > QCOW2_LIMIT or free < HOST_FREE_MINIMUM:
            ctx.note(f"Abbruch: qcow2 belegt {size / GIB:.1f} GiB, Host frei {free / GIB:.1f} GiB (Nullenerkennung unwirksam?)", False)
            process.kill()
            return


def prefill_commands(free_blocks: int, keep_bytes: int = HIGH_GROUP_FREE) -> list[str]:
    """debugfs-Befehle: /fill mit Dateien zu je 1 GiB (letzte kleiner), zusammen alle freien Bloecke ausser keep_bytes."""
    remaining = free_blocks - keep_bytes // 4096
    if remaining <= 0:
        raise ValueError(f"zu wenig freie Bloecke fuer die Vorbelegung: {free_blocks}")
    commands = ["mkdir fill", "cd fill"]
    index = 0
    while remaining > 0:
        blocks = min(FILL_FILE_BLOCKS, remaining)
        name = f"f{index:04d}"
        commands += ["write /dev/null " + name, f"fallocate {name} 0 {blocks - 1}", f"sif {name} size {blocks * 4096}"]
        remaining -= blocks
        index += 1
    return commands


def free_blocks_of(dumpe2fs: str, image: Path) -> int:
    result = subprocess.run([dumpe2fs, "-h", str(image)], capture_output=True, text=True, check=True)
    value = parse_dumpe2fs(result.stdout).get("free_blocks")
    if not isinstance(value, int):
        raise ValueError("dumpe2fs lieferte keine Free blocks")
    return value


def high_group_run(ctx: "Context", boundary: bool) -> None:
    """32/128 GiB: Format im Gast, Vorbelegung am Host, dann k und ENOSPC im Gast; RAW-Image, damit debugfs direkt arbeitet."""
    root = ctx.work / "root.raw"
    with root.open("xb") as image:
        image.truncate(ctx.args.size * GIB)
    if run_to_done(ctx, "lauf6", 6, root, "raw") is None or not ctx.go:
        return
    started = time.monotonic()
    script = ctx.work / "prefill.debugfs"
    script.write_text("\n".join(prefill_commands(free_blocks_of(ctx.args.dumpe2fs, root))) + "\n", encoding="ascii")
    done = subprocess.run([ctx.args.debugfs, "-w", "-f", str(script), str(root)], capture_output=True, text=True)
    (ctx.work / "prefill.txt").write_text(done.stdout + done.stderr, encoding="utf-8")
    ctx.note(f"Vorbelegung debugfs Exit {done.returncode}, {time.monotonic() - started:.0f} s, frei danach "
             f"{free_blocks_of(ctx.args.dumpe2fs, root) * 4096 / GIB:.2f} GiB", done.returncode == 0)
    if not ctx.go or fsck(ctx, root, "nach-vorbelegung") != 0:
        return
    clean_run(ctx, "lauf8" if boundary else "lauf7", 8 if boundary else 7, root, "raw")


def run_to_done(ctx: Context, name: str, run: int, root: Path, root_format: str = "qcow2") -> str | None:
    """Ein Lauf bis STORAGE_ROOT DONE und Gastende; liefert das serielle Protokoll oder None bei Haenger."""
    process, directory = boot(ctx, name, run, root, root_format)
    stop = threading.Event()
    watcher = threading.Thread(target=watch_space, args=(ctx, root, process, stop), daemon=True)
    watcher.start()
    started = time.monotonic()
    try:
        tools.wait_serial(directory, "STORAGE_ROOT DONE", ctx.args.timeout)
        process.wait(timeout=120)
    except (TimeoutError, subprocess.TimeoutExpired) as error:
        process.kill()
        process.wait()
        ctx.note(f"{name}: kein DONE oder kein Gastende ({error or 'Zeitlimit'})", False)
        return None
    finally:
        stop.set()
    serial = (directory / "serial.log").read_text(encoding="utf-8", errors="replace")
    passed, failed, done = evaluate(serial)
    ok = done and not failed and passed > 0 and process.returncode == 0
    ctx.note(f"{name}: PASS={passed} FAIL={','.join(failed) or 0} DONE={done} qemu-exit={process.returncode} "
             f"{time.monotonic() - started:.0f} s, qcow2 belegt {allocated(root) / GIB:.2f} GiB", ok)
    return serial


def fsck(ctx: Context, raw: Path, name: str, repair: bool = False, must_pass: bool = True) -> int:
    started = time.monotonic()
    result = subprocess.run([ctx.args.e2fsck, "-fy" if repair else "-fn", str(raw)], capture_output=True, text=True)
    (ctx.work / f"{name}.e2fsck.txt").write_text(result.stdout + result.stderr, encoding="utf-8")
    ok = result.returncode == 0 or not must_pass
    ctx.note(f"{name}: e2fsck {'-fy' if repair else '-fn'} Exit {result.returncode}, {time.monotonic() - started:.0f} s", ok)
    return result.returncode


def compare_counters(ctx: Context, serial: str, raw: Path, name: str) -> None:
    result = subprocess.run([ctx.args.dumpe2fs, "-h", str(raw)], capture_output=True, text=True)
    (ctx.work / f"{name}.dumpe2fs.txt").write_text(result.stdout + result.stderr, encoding="utf-8")
    problems = counters_match(guest_counters(serial), parse_dumpe2fs(result.stdout))
    ctx.note(f"{name}: Zaehler Gast/dumpe2fs {'gleich, clean' if not problems else '; '.join(problems)}", not problems)


def clean_run(ctx: Context, name: str, run: int, root: Path, root_format: str = "qcow2") -> bool:
    serial = run_to_done(ctx, name, run, root, root_format)
    if serial is None or not ctx.go:
        return False
    raw = root if root_format == "raw" else ctx.raw_copy(root, name)
    fsck(ctx, raw, name)
    compare_counters(ctx, serial, raw, name)
    if raw != root:
        raw.unlink()
    return ctx.go


def abort_run(ctx: Context, root: Path, delay: float) -> None:
    tag = f"lauf3-{delay:g}s"
    overlay = ctx.work / f"{tag}.qcow2"
    ctx.qemu_img("create", "-f", "qcow2", "-b", str(root), "-F", "qcow2", str(overlay))
    process, directory = boot(ctx, tag, 3, overlay)
    try:
        tools.wait_serial(directory, "STORAGE_ROOT WRITING", ctx.args.timeout)
        time.sleep(delay)
    except TimeoutError as error:
        ctx.note(f"{tag}: kein WRITING ({error})", False)
    finally:
        process.kill()
        process.wait()
    serial = (directory / "serial.log").read_text(encoding="utf-8", errors="replace")
    if "STORAGE_ROOT DONE" in serial or "FLUSHED-STATE" not in serial:
        ctx.note(f"{tag}: Probe endete oder setzte s_state nicht vor dem Abbruch", False)
        return
    raw = ctx.raw_copy(overlay, tag)
    # Exit != 0 ist nach hartem Abbruch erlaubt; die Ausgabe wird als Kategorie festgehalten.
    fsck(ctx, raw, tag, must_pass=False)
    raw.unlink()

    # Lauf 4: Gate ReadOnly, nichts geschrieben. Inhaltsvergleich gegen eine eingefrorene Kopie statt Dateihash,
    # weil qcow2-Metadaten sich auch ohne Gastschreiben aendern koennen.
    frozen = ctx.work / f"{tag}-frozen.qcow2"
    ctx.qemu_img("convert", "-O", "qcow2", str(overlay), str(frozen))
    run_to_done(ctx, f"lauf4-{delay:g}s", 4, overlay)
    same = subprocess.run([*ctx.tool, "compare", str(frozen), str(overlay)], capture_output=True, text=True, env=ctx.environment)
    ctx.note(f"lauf4-{delay:g}s: Image nach Lauf 4 {'unveraendert' if same.returncode == 0 else 'VERAENDERT'}", same.returncode == 0)

    repaired = ctx.raw_copy(frozen, f"{tag}-repariert")
    fsck(ctx, repaired, f"{tag}-repariert-fy", repair=True, must_pass=False)
    fsck(ctx, repaired, f"{tag}-repariert-fn")
    if ctx.go:
        clean_run(ctx, f"lauf5-{delay:g}s", 5, repaired, "raw")
    repaired.unlink()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True, help="neuer Arbeitsordner (darf nicht existieren)")
    parser.add_argument("--size", type=int, choices=[2, 32, 128], required=True,
                        help="Root-Platte in GiB: 2 = Laeufe 1-5, 32/128 = Hochgruppenlauf 6-8")
    parser.add_argument("--k-boundary", action="store_true", help="Hochgruppenlauf: k nur als Grenzpruefung per Seek (Lauf 8)")
    parser.add_argument("--limit-only", action="store_true", help="Diagnose: nur Lauf 9 (Format + e-limit mit 8191 Eintraegen) auf 2 GiB; kein Go")
    parser.add_argument("--diagnose-run", type=int, choices=[9, 10, 11, 12, 13, 14, 15, 16],
                        help="Diagnose: nur dieser Lauf auf 2 GiB (10 = Loeschen ueber Blockgrenzen, 11 = Verzeichnisgroesse, 12 = GC-Fragmentierung, 13 = Nebenlaeufigkeit, 14 = nur mit Sperre, 15 = mit Sperre auf FAT, 16 = 20.000 Eintraege in einem Verzeichnis); kein Go")
    parser.add_argument("--memory", default="512M", help="Gastspeicher (QEMU -m), Standard 512M wie das Produktprofil")
    parser.add_argument("--debugfs", default="debugfs")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="kvm")
    parser.add_argument("--timeout", type=float, default=3600, help="Sekunden je Lauf bis STORAGE_ROOT DONE")
    parser.add_argument("--e2fsck", default="e2fsck")
    parser.add_argument("--dumpe2fs", default="dumpe2fs")
    parser.add_argument("--last-run", type=int, choices=[1, 2, 5], default=5, help="nach diesem Lauf enden (Entwicklung; kein Go)")
    args = parser.parse_args()
    if not args.iso.is_file():
        sys.exit(f"ISO nicht gefunden: {args.iso}")
    missing = [name for name in (args.e2fsck, args.dumpe2fs, args.debugfs) if not shutil.which(name)]
    if missing:
        print(f"offen: {', '.join(missing)} fehlt; nichts gestartet, nicht bestanden", file=sys.stderr)
        return 2
    args.e2fsck, args.dumpe2fs, args.debugfs = shutil.which(args.e2fsck), shutil.which(args.dumpe2fs), shutil.which(args.debugfs)
    args.iso = args.iso.resolve()
    ctx = Context(args, new_work_dir(args.work.resolve()))

    ctx.qemu_img("create", "-f", "qcow2", str(ctx.work / "fat.qcow2"), "64M")
    inodes = ctx.work / "inodes.qcow2"
    ctx.qemu_img("create", "-f", "qcow2", str(inodes), "64M")
    if args.diagnose_run:
        args.limit_only = True
    if args.limit_only:
        if args.size != 2:
            sys.exit("--limit-only nur mit --size 2")
        root = ctx.work / "root.qcow2"
        ctx.qemu_img("create", "-f", "qcow2", str(root), "2G")
        run = args.diagnose_run or 9
        clean_run(ctx, f"lauf{run}", run, root)
        ctx.note(f"ERGEBNIS Diagnose e-limit: {'bestanden' if ctx.go else 'FAIL'} (kein Go-Kriterium)")
        return 0 if ctx.go else 1
    if args.size != 2:
        high_group_run(ctx, args.k_boundary)
        verdict = "Go (Hochgruppenlauf)" if ctx.go else "No-Go"
        ctx.note(f"ERGEBNIS {args.size} GiB: {verdict}; kein Stromausfall-Beleg (Spec Abschnitt 1)")
        (ctx.work / "bericht.txt").write_text("\n".join(ctx.report) + "\n", encoding="utf-8")
        return 0 if ctx.go else 1
    root = ctx.work / "root.qcow2"
    ctx.qemu_img("create", "-f", "qcow2", str(root), f"{args.size}G")

    if clean_run(ctx, "lauf1", 1, root):
        raw = ctx.raw_copy(inodes, "inodes")
        fsck(ctx, raw, "i2-zusatzplatte")
        raw.unlink()
    if ctx.go and args.last_run >= 2:
        clean_run(ctx, "lauf2", 2, root)
    if ctx.go and args.last_run >= 5:
        for delay in ABORT_DELAYS:
            abort_run(ctx, root, delay)
            if not ctx.go:
                break

    complete = args.last_run == 5
    verdict = "Go" if ctx.go and complete else ("unvollstaendig (--last-run)" if ctx.go else "No-Go")
    ctx.note(f"ERGEBNIS {args.size} GiB: {verdict}; kein Stromausfall-Beleg (Spec Abschnitt 1)")
    (ctx.work / "bericht.txt").write_text("\n".join(ctx.report) + "\n", encoding="utf-8")
    return 0 if ctx.go and complete else 1


if __name__ == "__main__":
    sys.exit(main())
