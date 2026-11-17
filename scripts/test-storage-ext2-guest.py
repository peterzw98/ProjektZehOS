#!/usr/bin/env python3
"""Hostseite der ext2-Gastqualifikation: zwei QEMU-Laeufe auf demselben RAW-Image, danach e2fsck -fn.

Das ISO muss die Storage-Probe enthalten, die seriell "STORAGE_PROBE <Name> PASS|FAIL ..." und am Ende
"STORAGE_PROBE DONE" schreibt und sich danach selbst beendet. Das Image (genau 72 MiB, sparse) kommt als zweite Platte
(AHCI-Port 1) in einen NEUEN Arbeitsordner; ein vorhandener Pfad ist ein Abbruch, nichts wird ueberschrieben.
Exit 0 nur, wenn beide Laeufe ohne FAIL und mit DONE enden und e2fsck -fn 0 liefert.
"""
import argparse
import importlib.util
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)

# Genau 72 MiB: die leere App-Platte hat 64 MiB, so ist die Testplatte im Gast eindeutig.
IMAGE_SIZE = 72 * 1024 * 1024
RESULT = re.compile(r"^STORAGE_PROBE (?!DONE)(\S+) (PASS|FAIL)\b", re.MULTILINE)


def new_work_dir(path: Path) -> Path:
    """Legt den Arbeitsordner an; ein vorhandener Pfad (auch Datei oder Link) ist ein Fehler."""
    if path.exists() or path.is_symlink():
        raise FileExistsError(f"Arbeitsordner existiert bereits: {path}")
    path.mkdir(parents=True)
    return path


def create_image(path: Path, size: int = IMAGE_SIZE) -> None:
    if size != IMAGE_SIZE:
        raise ValueError(f"Image muss genau {IMAGE_SIZE} Byte (72 MiB) gross sein: {size}")
    with path.open("xb") as image:  # "x": nie ein vorhandenes Image ueberschreiben
        image.truncate(size)


def evaluate(serial: str) -> tuple[int, int, bool]:
    """Liefert (Anzahl PASS, Anzahl FAIL, DONE gesehen)."""
    results = [match.group(2) for match in RESULT.finditer(serial)]
    return results.count("PASS"), results.count("FAIL"), "STORAGE_PROBE DONE" in serial


def run_once(args: argparse.Namespace, work: Path, image: Path, appdisk: Path, name: str) -> bool:
    run = new_work_dir(work / name)
    # start_qemu liest MACHINE als Modulvariable; die Probe-Platte haengt als zweiter AHCI-Port dran.
    tools.MACHINE = [*MACHINE_BASE, "-drive", f"file={image},format=raw,if=none,id=probedisk",
                     "-device", "ide-hd,drive=probedisk,bus=ahci.1"]
    process = tools.start_qemu(args.iso, appdisk, run, tools.free_port(), args.accel, None)
    try:
        tools.wait_serial(run, "STORAGE_PROBE DONE", args.timeout)
        # Die Probe beendet den Gast selbst; ein erzwungenes Ende laesst offen, ob das Image sauber ist.
        process.wait(timeout=60)
    except (TimeoutError, subprocess.TimeoutExpired) as error:
        print(f"{name}: {error or 'Gast beendete sich nicht'}", file=sys.stderr)
        process.kill()
        process.wait()
        return False
    passed, failed, done = evaluate((run / "serial.log").read_text(encoding="utf-8", errors="replace"))
    print(f"{name}: PASS={passed} FAIL={failed} DONE={done} qemu-exit={process.returncode}")
    return done and failed == 0 and passed > 0 and process.returncode == 0


MACHINE_BASE = list(tools.MACHINE)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True, help="neuer Arbeitsordner (darf nicht existieren)")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    parser.add_argument("--timeout", type=float, default=600, help="Sekunden je Lauf bis STORAGE_PROBE DONE")
    parser.add_argument("--e2fsck", default="e2fsck")
    args = parser.parse_args()
    if not args.iso.is_file():
        sys.exit(f"ISO nicht gefunden: {args.iso}")
    e2fsck = shutil.which(args.e2fsck)
    if not e2fsck:
        sys.exit(f"{args.e2fsck} nicht gefunden")
    args.iso = args.iso.resolve()
    work = new_work_dir(args.work.resolve())
    image = work / "ext2.img"
    create_image(image)
    appdisk = work / "app.qcow2"
    tool, environment = tools.qemu_img()
    subprocess.run([*tool, "create", "-f", "qcow2", str(appdisk), "64M"], check=True, stdout=subprocess.DEVNULL, env=environment)
    # Kurzschluss: nach einem Fehlschlag kein zweiter Lauf und kein e2fsck auf einem unklaren Image.
    ok = run_once(args, work, image, appdisk, "lauf1") and run_once(args, work, image, appdisk, "lauf2")
    return 0 if ok and fsck(e2fsck, image) else 1


def fsck(e2fsck: str, image: Path) -> bool:
    result = subprocess.run([e2fsck, "-fn", str(image)], capture_output=True, text=True)
    print(result.stdout + result.stderr)
    print(f"e2fsck -fn Exit {result.returncode}")
    return result.returncode == 0


if __name__ == "__main__":
    sys.exit(main())
