#!/usr/bin/env python3
"""Hostseite der Gastabnahme fuer die Datenplatte ZEHOSDATA (Paket 2.3), je Groesse 32/64/128 GiB.

Das ISO muss mit -p:ZehOSSecurityRecovery=true gebaut sein (Textshell "Textshell ist bereit."; ein normaler
Recovery-Build lehnt disk init ohne Administrator ab). Je Groesse in einem NEUEN Arbeitsordner:
sparse RAW-Image als zweite Platte (sata1; sata0 ist eine leere 64-MiB-Platte), Boot, "disk init sata1", Ende,
Neustart; dann muss das serielle Protokoll "Datenlaufwerk unter /data eingebunden." (ohne Befehl, nur ueber das
Label, sata0 ist leer) und "Laufwerk sata1: <Bytes> Bytes, 1 Partition(en)" zeigen. "disk list" wird zusaetzlich
fotografiert. Gegenprobe mit --legacy-image (Bestandsimage 64 MiB, Label ZEHOS): die Kopie liegt an sata0,
der Rueckfall muss einbinden; SHA-256 des Originals vor und nach dem Lauf muss gleich sein.
Vorhandene Images werden nie beschrieben; der Arbeitsordner darf nicht existieren.
"""
import argparse
import hashlib
import importlib.util
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)

GIB = 1024 ** 3
MIB = 1024 ** 2
DEFAULT_SIZES = ("32G", "64G", "128G")
MIN_SIZE, MAX_SIZE = 64 * MIB, 128 * GIB
LEGACY_SIZE = 64 * MIB


def machine_without_appdisk() -> list[str]:
    """MACHINE von start_qemu ohne die feste App-Platte; die Platten setzt dieses Skript selbst."""
    result, items, index = [], tools.MACHINE, 0
    while index < len(items):
        if items[index:index + 2] == ["-device", "ide-hd,drive=appdisk,bus=ahci.0"]:
            index += 2
            continue
        result.append(items[index])
        index += 1
    return result


MACHINE_BASE = machine_without_appdisk()


def new_work_dir(path: Path) -> Path:
    """Ein vorhandener Pfad (auch Datei oder Link) ist ein Abbruch."""
    if path.exists() or path.is_symlink():
        raise FileExistsError(f"Arbeitsordner existiert bereits: {path}")
    path.mkdir(parents=True)
    return path


def parse_size(text: str) -> int:
    """'64M', '4G', '32G' in Bytes; nur 64 MiB bis 128 GiB (Bereich von disk init)."""
    match = re.fullmatch(r"(\d+)([MG])", text.strip().upper())
    if not match:
        raise argparse.ArgumentTypeError(f"Groesse wie 64M, 4G oder 32G erwartet: {text}")
    size = int(match.group(1)) * (MIB if match.group(2) == "M" else GIB)
    if not MIN_SIZE <= size <= MAX_SIZE:
        raise argparse.ArgumentTypeError(f"Groesse ausserhalb von 64M bis 128G: {text}")
    return size


def size_label(size: int) -> str:
    return f"{size // GIB}g" if size % GIB == 0 else f"{size // MIB}m"


def create_sparse(path: Path, size: int) -> None:
    if not MIN_SIZE <= size <= MAX_SIZE or size % 512:
        raise ValueError(f"Nicht vorgesehene Imagegroesse: {size}")
    with path.open("xb") as image:  # "x": nie ein vorhandenes Image ueberschreiben
        image.truncate(size)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(MIB), b""):
            digest.update(block)
    return digest.hexdigest()


def check_legacy(path: Path) -> Path:
    path = path.resolve()
    if not path.is_file() or path.stat().st_size != LEGACY_SIZE:
        raise ValueError(f"Bestandsimage muss eine vorhandene Datei von genau 64 MiB sein: {path}")
    return path


def device_line(name: str, size: int, partitions: int) -> str:
    return f"Laufwerk {name}: {size} Bytes, {partitions} Partition(en)"


def boot(args: argparse.Namespace, run: Path, disks: list[Path]) -> tuple[object, "tools.Qmp"]:
    """Startet QEMU mit den Platten an AHCI-Port 0, 1, ... und wartet auf die Recovery-Textshell."""
    machine = list(MACHINE_BASE)
    for port, disk in enumerate(disks):
        machine += ["-drive", f"file={disk},format=raw,if=none,id=d{port}", "-device", f"ide-hd,drive=d{port},bus=ahci.{port}"]
    tools.MACHINE = machine
    unused = run / "unused.qcow2"  # start_qemu haengt immer ein Laufwerk "appdisk" an; hier ohne Geraet
    tool, environment = tools.qemu_img()
    subprocess.run([*tool, "create", "-f", "qcow2", str(unused), "1M"], check=True, stdout=subprocess.DEVNULL, env=environment)
    port = tools.free_port()
    process = tools.start_qemu(args.iso, unused, run, port, args.accel, None)
    tools.wait_serial(run, "Textshell ist bereit.", args.timeout)
    qmp = tools.Qmp(port)
    # Nach "bereit" laeuft noch der Netzwerkstart; Eingaben davor gingen im Test verloren oder blieben liegen.
    try:
        tools.wait_serial(run, "NET DHCP IPv4", 60)
    except TimeoutError:
        print("Kein DHCP-Ergebnis in 60 s; Eingabe trotzdem versucht.", flush=True)
    time.sleep(3)
    qmp.tap("ret")  # die erste Taste nach dem Start kann verloren gehen
    qmp.shot(run / "00-bereit.png")
    return process, qmp


def send_command(qmp, run: Path, command: str, ack: str, tries: int = 2, ack_timeout: float = 60) -> int:
    """Tippt command und wartet auf die Eingangsquittung des Kernels (serielle Zeile); danach bleibt die Ergebniszeit offen.

    Ohne Quittung wird einmal erneut getippt; ein Bild pro Versuch zeigt, was die Shell wirklich anzeigt.
    shortcut: Bildschirmtext wird nur als PNG festgehalten, nicht gelesen (kein OCR); bei Bedarf Textausgabe der Shell seriell spiegeln.
    """
    for attempt in range(1, tries + 1):
        log = run / "serial.log"
        mark = len(log.read_text(encoding="utf-8", errors="replace")) if log.exists() else 0
        qmp.line(command)
        try:
            return tools.wait_serial(run, ack, ack_timeout, mark)
        except TimeoutError:
            qmp.shot(run / f"eingabe-versuch-{attempt}.png")
            print(f"Keine Quittung '{ack}' nach Versuch {attempt}; Bild: {run / f'eingabe-versuch-{attempt}.png'}", flush=True)
    raise TimeoutError(f"Eingabe '{command}' kam im Gast nicht an (Quittung '{ack}' fehlt)")


def stop(process, qmp, run: Path) -> None:
    qmp.line("shutdown")
    try:
        process.wait(timeout=60)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
        raise RuntimeError(f"Gast beendete sich nicht: {run}")


def wait_init(qmp, run: Path, mark: int, timeout: float) -> None:
    """Wartet auf das Ende von disk init; ein Fehlertext des Kernels beendet sofort, bei Timeout bleiben Bild und letzte STORAGE-Zeile."""
    deadline, shown, last = time.monotonic() + timeout, mark, "(keine)"
    while time.monotonic() < deadline:
        text = (run / "serial.log").read_text(encoding="utf-8", errors="replace")
        for line in text[shown:].splitlines():
            if "STORAGE " in line:
                last = line.strip()
                print(last, flush=True)
        shown = len(text)
        if "Laufwerksaenderung abgeschlossen." in text[mark:]:
            return
        failure = text.find("Befehl: Fehler:", mark)
        if failure >= 0:
            raise RuntimeError(text[failure:text.find("\n", failure)].strip())
        time.sleep(1)
    qmp.shot(run / "init-timeout.png")
    raise TimeoutError(f"disk init nach {timeout:.0f} s ohne Ergebnis; letzte STORAGE-Zeile: {last}; Bild {run / 'init-timeout.png'}")


def run_size(args: argparse.Namespace, base: Path, size: int) -> None:
    label = size_label(size).upper()
    work = new_work_dir(base / size_label(size))
    blank, data = work / "blank.img", work / "data.img"
    create_sparse(blank, 64 * MIB)
    create_sparse(data, size)
    run = new_work_dir(work / "lauf1")
    process, qmp = boot(args, run, [blank, data])
    # Ueber 4 GiB prueft der Kernel nur stichprobenartig und verlangt --yes-large.
    command = "disk init sata1" + (" --yes-large" if size > 4 * GIB else "")
    mark = send_command(qmp, run, command, "Befehl: " + command)
    started = time.monotonic()
    wait_init(qmp, run, mark, args.init_timeout)
    print(f"{label}: disk init dauerte {time.monotonic() - started:.0f} s", flush=True)
    stop(process, qmp, run)
    run = new_work_dir(work / "lauf2")
    process, qmp = boot(args, run, [blank, data])
    tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", args.timeout)
    tools.wait_serial(run, device_line("sata1", size, 1), 5)
    send_command(qmp, run, "disk list", "Befehl: disk list")
    time.sleep(2)
    qmp.shot(run / "disk-list.png")
    stop(process, qmp, run)
    print(f"{label}: init, Neustart, Einbindung ueber Label und Groesse ok (Bild: {run / 'disk-list.png'})", flush=True)


def run_legacy(args: argparse.Namespace, base: Path, original: Path) -> None:
    before = sha256(original)
    work = new_work_dir(base / "legacy")
    copy = work / "legacy.img"
    shutil.copyfile(original, copy)
    run = new_work_dir(work / "lauf")
    process, qmp = boot(args, run, [copy])
    tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", args.timeout)
    tools.wait_serial(run, device_line("sata0", LEGACY_SIZE, 1), 5)
    stop(process, qmp, run)
    if sha256(original) != before:
        raise RuntimeError("Das Original des Bestandsimages hat sich veraendert")
    print("Bestandsimage: Rueckfall sata0 mountet, Original unveraendert", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True, help="ISO, gebaut mit -p:ZehOSSecurityRecovery=true")
    parser.add_argument("--work", type=Path, required=True, help="neuer Arbeitsordner (darf nicht existieren)")
    parser.add_argument("--size", type=parse_size, action="append", help="Groesse wie 64M, 4G, 5G, 32G (64M..128G, mehrfach moeglich; Standard 32G 64G 128G)")
    parser.add_argument("--legacy-image", type=Path, help="Bestandsimage (64 MiB, Label ZEHOS) fuer die Gegenprobe")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    parser.add_argument("--timeout", type=float, default=600, help="Sekunden bis zur Bootmarke")
    parser.add_argument("--init-timeout", type=float, default=120, help="Sekunden fuer disk init; bei Timeout wird die letzte STORAGE-Zeile ausgegeben")
    args = parser.parse_args()
    if not args.iso.is_file():
        sys.exit(f"ISO nicht gefunden: {args.iso}")
    legacy = check_legacy(args.legacy_image) if args.legacy_image else None
    args.iso = args.iso.resolve()
    base = new_work_dir(args.work.resolve())
    for size in args.size or [parse_size(text) for text in DEFAULT_SIZES]:
        run_size(args, base, size)
    if legacy:
        run_legacy(args, base, legacy)
    else:
        print("Gegenprobe uebersprungen (kein --legacy-image).", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
