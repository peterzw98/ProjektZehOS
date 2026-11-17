#!/usr/bin/env python3
"""Hostseite der Gastabnahme fuer die Datenplatte ZEHOSDATA (Paket 2.3).

--iso-recovery: ISO mit -p:ZehOSSecurityRecovery=true (Textshell "Textshell ist bereit."; ein normaler Recovery-Build
lehnt disk init ohne Administrator ab). Je Groesse 32/64/128 GiB in einem NEUEN Arbeitsordner: sparse RAW-Image als
sata1 (sata0 ist eine leere 64-MiB-Platte), "disk init sata1", Ende, Neustart. Die leere ZEHOSDATA-Platte wird nie
automatisch eingebunden (sie traegt noch keine Kontendatei): der Boot meldet DataDiskNotMounted, eingebunden wird per
"disk use sata1 --yes"; danach zeigt das serielle Protokoll "Laufwerk sata1: <Bytes> Bytes, 1 Partition(en)".
Gegenprobe mit --legacy-image (Bestandsimage 64 MiB, Label ZEHOS): die Kopie liegt an sata0 und wird eingebunden; SHA-256
des Originals vor und nach dem Lauf muss gleich sein.

--iso-normal (normaler Build ohne SecurityRecovery) schaltet die Sicherheitsproben A-E ein: A) Platte mit dem
Recovery-ISO als sata1 initialisieren, B) als sata0 im normalen Build: Ersteinrichtung legt das Konto an, C) leere sata0 +
dieselbe Platte als sata1: automatisch eingebunden, Anmeldung geht, D) leere ZEHOSDATA allein: kein Mount, keine
Ersteinrichtung, E) zwei ZEHOSDATA-Platten: kein Mount, keine Ersteinrichtung; mit --legacy-image zusaetzlich: Bestand
auf sata0 gewinnt gegen eine ZEHOSDATA-Platte. Vorhandene Images werden nie beschrieben; der Arbeitsordner darf nicht
existieren.
"""
import argparse
import hashlib
import importlib.util
import os
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


def _set_sparse_windows(descriptor: int) -> None:
    """NTFS macht truncate() sonst zu einer voll belegten, genullten Datei (128 GiB); FSCTL_SET_SPARSE vorher setzen.
    shortcut: Fehler (etwa ein Dateisystem ohne Sparse-Dateien) werden ignoriert, die Datei ist dann nur nicht duenn."""
    import ctypes
    import msvcrt
    returned = ctypes.c_ulong(0)
    ctypes.windll.kernel32.DeviceIoControl(msvcrt.get_osfhandle(descriptor), 0x000900C4, None, 0, None, 0, ctypes.byref(returned), None)


def create_sparse(path: Path, size: int) -> None:
    if not MIN_SIZE <= size <= MAX_SIZE or size % 512:
        raise ValueError(f"Nicht vorgesehene Imagegroesse: {size}")
    with path.open("xb") as image:  # "x": nie ein vorhandenes Image ueberschreiben
        if os.name == "nt":
            _set_sparse_windows(image.fileno())
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


def boot(args: argparse.Namespace, run: Path, disks: list[Path], desktop: bool = False) -> tuple[object, "tools.Qmp"]:
    """Startet QEMU mit den Platten an AHCI-Port 0, 1, ... und wartet auf die Textshell (Recovery-ISO) bzw. den Desktop (normales ISO)."""
    machine = list(MACHINE_BASE)
    for port, disk in enumerate(disks):
        machine += ["-drive", f"file={disk},format=raw,if=none,id=d{port}", "-device", f"ide-hd,drive=d{port},bus=ahci.{port}"]
    tools.MACHINE = machine
    unused = run / "unused.qcow2"  # start_qemu haengt immer ein Laufwerk "appdisk" an; hier ohne Geraet
    tool, environment = tools.qemu_img()
    subprocess.run([*tool, "create", "-f", "qcow2", str(unused), "1M"], check=True, stdout=subprocess.DEVNULL, env=environment)
    port = tools.free_port()
    process = tools.start_qemu(args.iso_normal if desktop else args.iso_recovery, unused, run, port, args.accel, None)
    tools.wait_serial(run, "Desktop ist bereit." if desktop else "Textshell ist bereit.", args.timeout)
    qmp = tools.Qmp(port)
    # Nach "bereit" laeuft noch der Netzwerkstart; Eingaben davor gingen im Test verloren oder blieben liegen.
    try:
        tools.wait_serial(run, "NET DHCP IPv4", 60)
    except TimeoutError:
        print("Kein DHCP-Ergebnis in 60 s; Eingabe trotzdem versucht.", flush=True)
    time.sleep(3)
    if not desktop:
        qmp.tap("ret")  # die erste Taste nach dem Start kann verloren gehen; am Anmeldebild waere sie schaedlich
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
    # Das getippte shutdown ist der echte Abschaltpfad (Datentraeger aushaengen); QEMU beendet sich oft noch vor
    # dem Loslassen der Taste, dann bricht die QMP-Verbindung ab. Das gilt als Beginn des Herunterfahrens.
    try:
        qmp.line("shutdown")
    except (ConnectionError, OSError):
        pass
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
    # Eine leere ZEHOSDATA-Platte (ohne Kontendatei) wird nie automatisch eingebunden; eingebunden wird per disk use.
    # Die Ersteinrichtung gibt es nur beim Boot, daher entfaellt hier jede Kontoanlage.
    tools.wait_serial(run, "DataDiskNotMounted", args.timeout)
    tools.wait_serial(run, device_line("sata1", size, 1), 5)
    if "Datenlaufwerk unter /data eingebunden." in (run / "serial.log").read_text(encoding="utf-8", errors="replace"):
        raise RuntimeError("Leere ZEHOSDATA-Platte wurde ohne disk use eingebunden")
    mark = send_command(qmp, run, "disk use sata1 --yes", "Befehl: disk use sata1 --yes")
    tools.wait_serial(run, "STORAGE use ok sata1", args.init_timeout, mark)
    send_command(qmp, run, "disk list", "Befehl: disk list", tries=3)
    time.sleep(2)
    qmp.shot(run / "disk-list.png")
    stop(process, qmp, run)
    print(f"{label}: init, Neustart, disk use und Groesse ok (Bild: {run / 'disk-list.png'})", flush=True)


def quit_guest(process, qmp) -> None:
    """Beendet QEMU ohne Herunterfahren (Wegwerfkopien); bis 30 s warten, sonst kill."""
    qmp.quit()
    try:
        process.wait(timeout=30)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()


def login(qmp, run: Path, first: bool) -> None:
    """Anmeldung am Desktop; first = Ersteinrichtung (Name, Kennwort, Wiederholung), sonst Name und Kennwort."""
    mark = len((run / "serial.log").read_text(encoding="utf-8", errors="replace"))
    time.sleep(1)
    for value in (tools.TEST_USER, tools.TEST_PASSWORD, tools.TEST_PASSWORD) if first else (tools.TEST_USER, tools.TEST_PASSWORD):
        qmp.line(value)
        time.sleep(0.5)
    # Die Ersteinrichtung (PBKDF2) dauert unter TCG unterschiedlich lange.
    tools.wait_serial(run, "Benutzer angemeldet.", 300, mark)


def desktop_shutdown(process, qmp, run: Path) -> None:
    """Vom Desktop in die Textshell (Esc) und sauber herunterfahren, damit /data ausgehaengt wird."""
    mark = len((run / "serial.log").read_text(encoding="utf-8", errors="replace"))
    qmp.tap("esc")
    try:
        tools.wait_serial(run, "Textshell aktiv.", 10, mark)
    except TimeoutError:
        qmp.tap("esc")
        tools.wait_serial(run, "Textshell aktiv.", 30, mark)
    qmp.tap("ret")
    stop(process, qmp, run)


def serial_text(run: Path) -> str:
    return (run / "serial.log").read_text(encoding="utf-8", errors="replace")


def expect_no_mount(run: Path, cause: str) -> None:
    """Kein Mount und keine Ersteinrichtung: Fehlerzeile mit Ursache und 'Keine Ersteinrichtung', kein Mount-Marker."""
    tools.wait_serial(run, cause, 60)
    tools.wait_serial(run, "Keine Ersteinrichtung", 5)
    time.sleep(3)
    if "Datenlaufwerk unter /data eingebunden." in serial_text(run):
        raise RuntimeError(f"{cause}: es darf nichts eingebunden werden")


def run_probes(args: argparse.Namespace, base: Path, legacy: Path | None) -> None:
    """Sicherheitsproben A-E im normalen Build (Spec Etappe 2, 3.2): Platten ohne Konten werden nie automatisch zur Datenplatte."""
    work = new_work_dir(base / "proben")
    blank = work / "blank.img"
    create_sparse(blank, 64 * MIB)
    # A) mit dem Recovery-ISO als sata1 initialisieren (leere ZEHOSDATA-Platte, 64 MiB: Vollscan, Sekunden).
    leer = work / "leer.img"
    create_sparse(leer, LEGACY_SIZE)
    run = new_work_dir(work / "a-init")
    process, qmp = boot(args, run, [blank, leer])
    mark = send_command(qmp, run, "disk init sata1", "Befehl: disk init sata1")
    wait_init(qmp, run, mark, args.init_timeout)
    stop(process, qmp, run)
    print("A) Platte mit dem Recovery-ISO initialisiert", flush=True)
    # D) leere ZEHOSDATA allein (vor B, solange sie keine Konten hat).
    leer_d = work / "leer-d.img"
    shutil.copyfile(leer, leer_d)
    run = new_work_dir(work / "d-leer")
    process, qmp = boot(args, run, [blank, leer_d], desktop=True)
    expect_no_mount(run, "DataDiskNotMounted")
    qmp.shot(run / "d-leer.png")  # Bild: keine Ersteinrichtung am Anmeldebild
    quit_guest(process, qmp)
    print("D) leere ZEHOSDATA allein: kein Mount, keine Ersteinrichtung", flush=True)
    # B) als sata0: Bestand, Ersteinrichtung beim Boot legt das Konto an.
    konten = work / "konten.img"
    shutil.copyfile(leer, konten)
    run = new_work_dir(work / "b-erst")
    process, qmp = boot(args, run, [konten], desktop=True)
    tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", args.timeout)
    login(qmp, run, first=True)
    desktop_shutdown(process, qmp, run)
    print("B) normaler Build, Platte als sata0: Ersteinrichtung legt das Konto an", flush=True)
    # C) leere sata0 + dieselbe Platte (jetzt mit Konten) als sata1: automatisch eingebunden, Anmeldung geht.
    konten_c = work / "konten-c.img"
    shutil.copyfile(konten, konten_c)
    run = new_work_dir(work / "c-auto")
    process, qmp = boot(args, run, [blank, konten_c], desktop=True)
    tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", args.timeout)
    login(qmp, run, first=False)
    desktop_shutdown(process, qmp, run)
    print("C) leere sata0 + Platte mit Konten als sata1: automatisch eingebunden, Anmeldung ok", flush=True)
    # E) zwei ZEHOSDATA-Platten.
    zweite = work / "konten-e2.img"
    erste = work / "konten-e1.img"
    shutil.copyfile(konten, erste)
    shutil.copyfile(konten, zweite)
    run = new_work_dir(work / "e-zwei")
    process, qmp = boot(args, run, [blank, erste, zweite], desktop=True)
    expect_no_mount(run, "AmbiguousDataDisk")
    qmp.shot(run / "e-zwei.png")
    quit_guest(process, qmp)
    print("E) zwei ZEHOSDATA-Platten: kein Mount, keine Ersteinrichtung", flush=True)
    if legacy is None:
        print("Bestand-gewinnt-Probe uebersprungen (kein --legacy-image).", flush=True)
        return
    before = sha256(legacy)
    existing = work / "bestand.img"
    shutil.copyfile(legacy, existing)
    labelled = work / "konten-f.img"
    shutil.copyfile(konten, labelled)
    run = new_work_dir(work / "f-bestand")
    process, qmp = boot(args, run, [existing, labelled], desktop=True)
    tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", args.timeout)
    tools.wait_serial(run, "Hinweis: Datenplatte ZEHOSDATA auf sata1 vorhanden", 5)
    quit_guest(process, qmp)
    if sha256(legacy) != before:
        raise RuntimeError("Das Original des Bestandsimages hat sich veraendert")
    print("F) Bestand auf sata0 gewinnt gegen ZEHOSDATA auf sata1, Hinweis im Protokoll", flush=True)


def run_legacy(args: argparse.Namespace, base: Path, original: Path) -> None:
    before = sha256(original)
    work = new_work_dir(base / "legacy")
    copy = work / "legacy.img"
    shutil.copyfile(original, copy)
    run = new_work_dir(work / "lauf")
    process, qmp = boot(args, run, [copy])
    tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", args.timeout)
    tools.wait_serial(run, device_line("sata0", LEGACY_SIZE, 1), 5)
    # Kein sauberes Herunterfahren: das Bestandsimage hat Konten, die Shell zeigt die Anmeldung und ein getipptes
    # shutdown landete im Benutzernamen. Geprueft werden nur Mount und das unveraenderte Original; die Kopie ist Wegwerf.
    quit_guest(process, qmp)
    if sha256(original) != before:
        raise RuntimeError("Das Original des Bestandsimages hat sich veraendert")
    print("Bestandsimage: Rueckfall sata0 mountet, Original unveraendert", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso-recovery", "--iso", dest="iso_recovery", type=Path, required=True, help="ISO, gebaut mit -p:ZehOSSecurityRecovery=true")
    parser.add_argument("--iso-normal", type=Path, help="normales ISO (ohne SecurityRecovery) fuer die Sicherheitsproben A-E")
    parser.add_argument("--work", type=Path, required=True, help="neuer Arbeitsordner (darf nicht existieren)")
    parser.add_argument("--size", type=parse_size, action="append", help="Groesse wie 64M, 4G, 5G, 32G (64M..128G, mehrfach moeglich; Standard 32G 64G 128G)")
    parser.add_argument("--legacy-image", type=Path, help="Bestandsimage (64 MiB, Label ZEHOS) fuer die Gegenprobe")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    parser.add_argument("--timeout", type=float, default=600, help="Sekunden bis zur Bootmarke")
    parser.add_argument("--init-timeout", type=float, default=120, help="Sekunden fuer disk init; bei Timeout wird die letzte STORAGE-Zeile ausgegeben")
    args = parser.parse_args()
    for iso in (args.iso_recovery, args.iso_normal):
        if iso is not None and not iso.is_file():
            sys.exit(f"ISO nicht gefunden: {iso}")
    legacy = check_legacy(args.legacy_image) if args.legacy_image else None
    args.iso_recovery = args.iso_recovery.resolve()
    args.iso_normal = args.iso_normal.resolve() if args.iso_normal else None
    base = new_work_dir(args.work.resolve())
    sizes = args.size or [parse_size(text) for text in DEFAULT_SIZES]
    for size in sizes:
        run_size(args, base, size)
    if args.iso_normal:
        run_probes(args, base, legacy)
    else:
        print("Sicherheitsproben A-F uebersprungen (kein --iso-normal).", flush=True)
    if legacy:
        run_legacy(args, base, legacy)
    else:
        print("Gegenprobe uebersprungen (kein --legacy-image).", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
