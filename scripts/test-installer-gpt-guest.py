#!/usr/bin/env python3
"""Vorbereitung der GPT-Installer-Gastabnahme. G8 startet nur mit ausdruecklichem --run-g8.

Alle Images und der Arbeitsordner werden neu angelegt; bereits vorhandene Daten werden nie beschrieben.

Benoetigte Builds (sonst fehlen die seriellen GPT_TEST-Quittungen):
- --iso: Installer-ISO mit -p:ZehOSInstaller=true -p:ZehOSGptInstaller=true -p:ZehOSGptGuestProbe=true,
  fuer G8 zusaetzlich -p:ZehOSGptG8Probe=true.
- --payload/ProjektZeh.elf: der eingebettete Kernel ebenfalls mit -p:ZehOSGptGuestProbe=true, sonst fehlt
  "GPT_TEST IDENTITY" beim Start der installierten Platte.
- --iso-security (nur --run-cases): normaler Kernel mit -p:ZehOSSecurityRecovery=true -p:ZehOSGptGuestProbe=true
  fuer G5, die ZEHOSDATA-Platte von G7 und die Identitaetspruefung nach G9.

--run-cases installiert einmal 32 GiB und prueft jeden Fall auf einer eigenen Sparse-Kopie (cp --sparse=always):
G2 Rueckfalleintrag, G3/G4 Root offline geloescht/beschaedigt, G5 Security-Recovery-ISO auf G3/G4 mit Konto,
G7 installierte Platte + ZEHOSDATA, G9 Neuinstallation ueber Healthy mit Abbruch in CopyingFiles; dazu G6-Ziele
(roh geschriebenes ISO, ZEHOSDATA, eingebundenes Ziel, belegtes /data) und G8 Abbruch auf frischer Platte -> Unclear ->
Neuinstallation mit --erase.
--run-rdrand startet den Installer unter TCG mit qemu64 ohne RDRAND/RDSEED und erwartet die Verweigerung.

Exitcodes: 2 = alle ausgefuehrten Faelle bestanden, andere Faelle offen (VMware und Teile von G1/G2/G6 sind offen,
deshalb gibt es derzeit kein 0); 1 = Fehler. Nicht implementierte Faelle und fehlende Werkzeuge sind offen (2).
"""
import argparse
import errno
import hashlib
import importlib.util
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid
import zlib

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("storage_guest", ROOT / "scripts/test-storage-data-disk.py")
storage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage)
SECTOR = 512
GIB = 1024 ** 3
ESP_TYPE = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B"
ROOT_TYPE = "0FC63DAF-8483-4772-8E79-3D69D8477DE4"


def partitions(table: dict, image_size: int) -> tuple[dict, dict]:
    table = table["partitiontable"]
    parts = table["partitions"]
    if table.get("label") != "gpt" or table.get("sectorsize", SECTOR) != SECTOR or len(parts) != 2:
        raise ValueError("GPT mit genau zwei 512-Byte-Partitionen erwartet")
    esp, root = parts
    if esp["type"].upper() != ESP_TYPE or root["type"].upper() != ROOT_TYPE:
        raise ValueError("Unerwartete Partitionstypen")
    previous_end = 0
    for part in parts:
        start, size = part["start"], part["size"]
        if not isinstance(start, int) or not isinstance(size, int) or start <= previous_end or size <= 0 or (start + size) * SECTOR > image_size:
            raise ValueError("Partition ausserhalb des Images oder ueberlappend")
        previous_end = start + size - 1
    return esp, root


def extract_sparse(source: Path, offset: int, length: int, target: Path) -> None:
    """Kopiert nur belegte Bereiche; bei nicht unterstuetztem SEEK_DATA wird nicht stillschweigend Erfolg gemeldet."""
    if offset < 0 or length <= 0 or offset + length > source.stat().st_size:
        raise ValueError("Partitionsfenster ausserhalb des Images")
    with source.open("rb") as src, target.open("xb") as dst:
        dst.truncate(length)
        pos, end = offset, offset + length
        while pos < end:
            try:
                data = os.lseek(src.fileno(), pos, os.SEEK_DATA)
            except OSError as error:
                if error.errno == errno.ENXIO:
                    break
                raise
            if data >= end:
                break
            hole = min(os.lseek(src.fileno(), data, os.SEEK_HOLE), end)
            src.seek(data)
            dst.seek(data - offset)
            while data < hole:
                block = src.read(min(8 * 1024 * 1024, hole - data))
                if not block:
                    raise EOFError("Image waehrend des Kopierens verkuerzt")
                dst.write(block)
                data += len(block)
            pos = hole


def check_tool(name: str, *arguments: str) -> None:
    result = subprocess.run([name, *arguments], capture_output=True, text=True, check=False)
    if result.returncode:
        raise RuntimeError(f"{name} meldet Fehler {result.returncode}: {result.stdout} {result.stderr}")


def check_gpt(image: Path) -> None:
    result = subprocess.run(["sgdisk", "-v", str(image)], capture_output=True, text=True, check=False)
    if result.returncode or "No problems found" not in result.stdout or "warning" in (result.stdout + result.stderr).lower():
        raise RuntimeError("sgdisk meldet Fehler oder Warnungen: " + result.stdout + result.stderr)


def check_ext2(image: Path) -> None:
    result = subprocess.run(["e2fsck", "-fn", str(image)], capture_output=True, text=True, check=False)
    output = result.stdout + result.stderr
    # -n kann trotz nicht behobener Fehler Exit 0 liefern; die Ausgabe ist Teil der Pruefung.
    if result.returncode or re.search(r"fix\?\s*no|wrong|corrupt|error|warning|unexpected|not clean", output, re.IGNORECASE):
        raise RuntimeError("e2fsck meldet Fehler oder Warnungen: " + output)


def expected_markers(table: dict, root: dict) -> tuple[bytes, bytes]:
    disk_guid = str(uuid.UUID(table["partitiontable"]["id"]))
    root_guid = str(uuid.UUID(root["uuid"]))
    body = (f"zehos-install 1\ndisk={disk_guid}\nroot={root_guid}\n"
            f"root-first={root['start']}\nroot-sectors={root['size']}\n").encode()
    esp = body + f"crc32={zlib.crc32(body):08x}\n".encode()
    body = f"format=1\nstate=fresh\ndisk={disk_guid}\n".encode()
    return esp, body + f"crc32={zlib.crc32(body):08x}\n".encode()


def inspect_image(image: Path, work: Path, payload: Path, version: str, config: Path, marker_expected: bool) -> None:
    check_gpt(image)
    result = subprocess.run(["sfdisk", "-J", str(image)], capture_output=True, text=True, check=True)
    table = json.loads(result.stdout)
    esp, root = partitions(table, image.stat().st_size)
    esp_image, root_image = work / "esp.img", work / "root.img"
    extract_sparse(image, esp["start"] * SECTOR, esp["size"] * SECTOR, esp_image)
    extract_sparse(image, root["start"] * SECTOR, root["size"] * SECTOR, root_image)
    check_tool("fsck.fat", "-n", str(esp_image))
    check_ext2(root_image)
    expected = {"/EFI/BOOT/BOOTX64.EFI": (payload / "BOOTX64.EFI").read_bytes(),
                "/boot/ProjektZeh.elf": (payload / "ProjektZeh.elf").read_bytes(),
                "/boot/ProjektZeh.elf.bak": (payload / "ProjektZeh.elf").read_bytes(),
                "/boot/zehos-version.txt": (version + "\n").encode(),
                "/boot/limine/limine.conf": config.read_bytes()}
    for path, contents in expected.items():
        result = subprocess.run(["mcopy", "-i", str(esp_image), "::" + path, "-"], capture_output=True, check=True)
        if result.stdout != contents:
            raise ValueError(f"Bootdatei weicht ab: {path}")
    listing = subprocess.run(["mdir", "-a", "-b", "-i", str(esp_image), "::/boot"], capture_output=True, text=True, check=True)
    present = any(line.rstrip("/").rsplit("/", 1)[-1].lower() == ".zehos-install.id" for line in listing.stdout.splitlines())
    if present != marker_expected:
        raise ValueError("ESP-Marker vorhanden" if present else "ESP-Marker fehlt")
    if marker_expected:
        expected_marker, expected_root = expected_markers(table, root)
        actual = subprocess.run(["mcopy", "-i", str(esp_image), "::/boot/.zehos-install.id", "-"],
                                capture_output=True, check=True).stdout
        if actual != expected_marker:
            raise ValueError("ESP-Marker passt nicht zur GPT")
        actual_root = subprocess.run(["debugfs", "-R", "cat /etc/zehos/.zehos-layout", str(root_image)],
                                     capture_output=True, check=True).stdout
        if actual_root != expected_root:
            raise ValueError("Root-Marker fehlt oder passt nicht zur GPT")


def new_sparse(path: Path, size: int) -> None:
    if size not in (31 * GIB, 32 * GIB, 129 * GIB):
        raise ValueError("Nur die vorgesehenen G6-Groessen sind erlaubt")
    with path.open("xb") as image:
        if os.name == "nt":
            storage._set_sparse_windows(image.fileno())
            image.seek(size - 1)
            image.write(b"\0")
        else:
            image.truncate(size)


def assert_blank(path: Path) -> None:
    # Bei sparse RAW ist jedes vom Gast geschriebene Byte als belegter Bereich erkennbar.
    with path.open("rb") as image:
        try:
            os.lseek(image.fileno(), 0, os.SEEK_DATA)
        except OSError as error:
            if error.errno == errno.ENXIO:
                return
            raise
    raise ValueError(f"Unerwarteter Schreibzugriff auf {path}")


def run_g6_basic(args, work: Path) -> None:
    disks = [work / "small.img", work / "large.img", work / "valid.img"]
    for path, size in zip(disks, (31 * GIB, 129 * GIB, 32 * GIB), strict=True):
        new_sparse(path, size)
    run = storage.new_work_dir(work / "guest-g6")
    process = None
    try:
        process, qmp = storage.boot(args, run, disks)
        cases = [("setup install sata0 --layout gpt --erase", "GPT-Layout nur fuer 32 bis 128 GiB"),
                 ("setup install sata1 --layout gpt --erase", "GPT-Layout nur fuer 32 bis 128 GiB"),
                 ("setup install sata2 --layout gpt", "--erase erforderlich")]
        for command, reason in cases:
            mark = len((run / "serial.log").read_text(encoding="utf-8", errors="replace"))
            qmp.line(command)
            storage.tools.wait_serial(run, "GPT_TEST RESULT ERROR " + reason, args.timeout, mark)
        storage.stop(process, qmp, run)
        process = None
        for disk in disks:
            assert_blank(disk)
        print("PASS G6 Teil: 31/129 GiB und fehlendes --erase abgewiesen, keine Schreibzugriffe", flush=True)
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)


HEALTHY_DESKTOP = ("GPT_TEST IDENTITY Healthy SETUP True", "Desktop ist bereit.")


def boot_installed(args, case: Path, disks: list[Path], expected: tuple[str, ...] = HEALTHY_DESKTOP,
                   create_account: bool = False) -> Path:
    """Startet von disks[0] (OVMF, -boot c), wartet auf alle erwarteten Zeilen und liefert den Laufordner."""
    run = storage.new_work_dir(case / "boot-installed")
    machine = list(storage.MACHINE_BASE)
    machine[machine.index("-boot") + 1] = "c"
    for port, disk in enumerate(disks):
        machine += ["-drive", f"file={disk.resolve()},format=raw,if=none,id=d{port}",
                    "-device", f"ide-hd,drive=d{port},bus=ahci.{port}"]
    storage.tools.MACHINE = machine
    unused = run / "unused.qcow2"
    tool, environment = storage.tools.qemu_img()
    subprocess.run([*tool, "create", "-f", "qcow2", str(unused), "1M"], check=True,
                   stdout=subprocess.DEVNULL, env=environment)
    port = storage.tools.free_port()
    process = storage.tools.start_qemu(args.iso, unused, run, port, args.accel, None)
    try:
        for text in expected:
            storage.tools.wait_serial(run, text, args.timeout)
        if create_account:
            # Ersteinrichtung wie ein Benutzer am Desktop, danach sauber herunterfahren (ESP aushaengen).
            qmp = storage.tools.Qmp(port)
            storage.login(qmp, run, first=True)
            storage.desktop_shutdown(process, qmp, run)
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=30)
    return run


def install(args, case: Path, size: int) -> Path:
    """Frische Zielplatte im eigenen Fallordner, GPT-Installation im Installer-ISO, danach Offline-Pruefung."""
    target = case / "target.img"
    storage.create_sparse(target, size)
    run = storage.new_work_dir(case / "guest")
    process = None
    try:
        process, qmp = storage.boot(args, run, [target])
        mark = len((run / "serial.log").read_text(encoding="utf-8", errors="replace"))
        qmp.line("setup install sata0 --layout gpt --erase")
        storage.tools.wait_serial(run, "GPT_TEST RESULT OK", args.timeout, mark)
        storage.stop(process, qmp, run)
        process = None
        inspect_image(target, case, args.payload, args.version, args.boot_config, marker_expected=True)
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)
    return target


def run_g1_basic(args, work: Path) -> None:
    for size in (32 * GIB, 128 * GIB):
        case = storage.new_work_dir(work / f"g1-{size // GIB}g")
        target = install(args, case, size)
        boot_installed(args, case, [target])
        print(f"PASS G1 Teil: {size // GIB} GiB installiert, offline geprueft und Healthy gestartet", flush=True)
    print("OFFEN G1: Kontoanlage (siehe --run-cases) und VMware", flush=True)


def copy_sparse(source: Path, target: Path) -> None:
    if target.exists():
        raise ValueError(f"Kopie existiert bereits: {target}")
    subprocess.run(["cp", "--sparse=always", str(source), str(target)], check=True)


def layout(image: Path) -> tuple[dict, dict, dict]:
    table = json.loads(subprocess.run(["sfdisk", "-J", str(image)], capture_output=True, text=True, check=True).stdout)
    return (table, *partitions(table, image.stat().st_size))


def esp_tool(image: Path, esp: dict, tool: str, *arguments: str) -> bytes:
    """mtools direkt auf der ESP im Image (image@@Byteoffset), ohne Extrakt und Zurueckschreiben."""
    environment = dict(os.environ, MTOOLS_SKIP_CHECK="1")
    result = subprocess.run([tool, "-i", f"{image}@@{esp['start'] * SECTOR}", *arguments],
                            capture_output=True, check=True, env=environment)
    return result.stdout


def security_files(image: Path) -> dict[str, bytes]:
    """Kontendatei und Recovery-Sicherungen im ESP-Wurzelverzeichnis (= /data) mit Inhalt."""
    _, esp, _ = layout(image)
    listing = esp_tool(image, esp, "mdir", "-a", "-b", "::/").decode(errors="replace")
    names = [line.rstrip("/").rsplit("/", 1)[-1] for line in listing.splitlines()]
    return {name: esp_tool(image, esp, "mcopy", "::/" + name, "-")
            for name in names if name.lower().startswith(".zehos-security")}


def serial_identity(run: Path, timeout: float) -> str:
    """Rest der Zeile "GPT_TEST IDENTITY <Zustand> SETUP <bool>" ab dem Zustand."""
    end = storage.tools.wait_serial(run, "GPT_TEST IDENTITY ", timeout)
    text = storage.serial_text(run)
    newline = text.find("\n", end)
    return text[end:newline if newline >= 0 else len(text)].strip()


def damage_root(image: Path, delete: bool) -> None:
    """G3: Partition 2 per sgdisk loeschen; G4: ext2-Magic (Superblock-Offset 56) offline nullen."""
    _, _, root = layout(image)
    if delete:
        subprocess.run(["sgdisk", "-d", "2", str(image)], capture_output=True, check=True)
        return
    offset = root["start"] * SECTOR + 1024 + 56
    with image.open("r+b") as stream:
        stream.seek(offset)
        if stream.read(2) != b"\x53\xef":
            raise ValueError("ext2-Magic des Root nicht gefunden")
        stream.seek(offset)
        stream.write(b"\0\0")


def security_args(args):
    """storage.boot startet args.iso_recovery; fuer G5/G7/G9 ist das das Security-Recovery-ISO."""
    copy = argparse.Namespace(**vars(args))
    copy.iso_recovery = args.iso_security
    return copy


def run_g2(args, work: Path, base: Path) -> None:
    case = storage.new_work_dir(work / "g2")
    image = case / "target.img"
    copy_sparse(base, image)
    _, esp, _ = layout(image)
    config = esp_tool(image, esp, "mcopy", "::/boot/limine/limine.conf", "-")
    if not config.startswith(b"timeout: 3\n") or b"default_entry" in config:
        raise ValueError("limine.conf hat nicht den erwarteten Anfang")
    # Eintrag 3 ist "/ZehOS (vorherige Version)"; ohne Hauptkernel kann nur er starten (keine Menuetasten noetig).
    changed = case / "limine.conf"
    changed.write_bytes(b"timeout: 3\ndefault_entry: 3\n" + config[len(b"timeout: 3\n"):])
    esp_tool(image, esp, "mcopy", "-o", str(changed), "::/boot/limine/limine.conf")
    esp_tool(image, esp, "mdel", "::/boot/ProjektZeh.elf")
    boot_installed(args, case, [image])
    print("PASS G2 Teil: Rueckfalleintrag .bak startet ohne Hauptkernel bis zum Desktop, Identitaet Healthy", flush=True)
    print("OFFEN G2: update-Regression (7b) und Auswahl per Menue", flush=True)


def run_g3_g4(args, work: Path, base: Path) -> None:
    for name, delete, state, cause, parts in (("g3", True, "RootMissing", "RootMissing", 1),
                                               ("g4", False, "RootDamaged", "RootGate", 2)):
        case = storage.new_work_dir(work / name)
        image = case / "target.img"
        copy_sparse(base, image)
        damage_root(image, delete)
        boot_installed(args, case, [image], (f"GPT_TEST IDENTITY {state} SETUP False",
                                             f"STORAGE Fehler: cause='{cause}'",
                                             storage.device_line("sata0", 32 * GIB, parts),
                                             "Datenlaufwerk unter /data eingebunden.", "Desktop ist bereit."))
        print(f"PASS {name.upper()}: Boot normal, ESP als /data, {state}, keine Ersteinrichtung", flush=True)


def run_g5(args, work: Path, account_base: Path) -> None:
    for name, delete, state, parts in (("g5-g3", True, "RootMissing", 1), ("g5-g4", False, "RootDamaged", 2)):
        case = storage.new_work_dir(work / name)
        image = case / "target.img"
        copy_sparse(account_base, image)
        damage_root(image, delete)
        before = security_files(image)
        if ".zehos-security.db" not in before:
            raise ValueError("Kontendatei der Kontobasis fehlt")
        run = storage.new_work_dir(case / "guest")
        process = None
        try:
            process, qmp = storage.boot(security_args(args), run, [image])
            identity = serial_identity(run, 5)
            if identity != f"{state} SETUP False":
                raise RuntimeError(f"{name}: Identitaet {identity}, erwartet {state} SETUP False")
            storage.tools.wait_serial(run, storage.device_line("sata0", 32 * GIB, parts), 5)
            storage.tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", 5)
            qmp.line("disk list")
            time.sleep(2)
            qmp.shot(run / "disk-list.png")
            qmp.line(f"security recover {storage.tools.TEST_USER}")
            time.sleep(2)
            qmp.line(storage.tools.TEST_PASSWORD)
            # Das Kennwort wird vor der Sperrpruefung gehasht (PBKDF2); danach muss die Sperre greifen.
            time.sleep(30)
            qmp.shot(run / "security-recover.png")
            storage.stop(process, qmp, run)
            process = None
        finally:
            if process is not None and process.poll() is None:
                process.kill()
                process.wait(timeout=30)
        if security_files(image) != before:
            raise RuntimeError(f"{name}: Kontendateien nach security recover veraendert")
        print(f"PASS {name.upper()}: Recovery-ISO bootet bei {state}, ESP lesbar, security recover ohne Wirkung, "
              f"Konten unveraendert (Bilder: {run})", flush=True)


def run_g7(args, work: Path, base: Path) -> None:
    case = storage.new_work_dir(work / "g7")
    image, data = case / "target.img", case / "data.img"
    copy_sparse(base, image)
    storage.create_sparse(data, 64 * 1024 ** 2)
    run = storage.new_work_dir(case / "init")
    process = None
    try:
        process, qmp = storage.boot(security_args(args), run, [image, data])
        mark = storage.send_command(qmp, run, "disk init sata1", "Befehl: disk init sata1")
        storage.wait_init(qmp, run, mark, args.timeout)
        storage.stop(process, qmp, run)
        process = None
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)
    checksum = storage.sha256(data)
    boot_installed(args, case, [image, data], (
        "GPT_TEST IDENTITY Healthy SETUP True",
        "STORAGE Hinweis: Datenplatte ZEHOSDATA auf sata1 vorhanden; das installierte System auf sata0 hat Vorrang",
        "Datenlaufwerk unter /data eingebunden.", "Desktop ist bereit."))
    if storage.sha256(data) != checksum:
        raise RuntimeError("G7: ZEHOSDATA-Platte wurde beim Start des installierten Systems veraendert")
    print("PASS G7: installierte Platte gewinnt gegen ZEHOSDATA, Hinweis im Protokoll, Datenplatte unveraendert", flush=True)


def kill_in_copy(args, case: Path, image: Path) -> None:
    """GPT-Installation auf sata0 starten und den Gast bei Beginn von CopyingFiles hart beenden."""
    run = storage.new_work_dir(case / "install")
    process = None
    try:
        process, qmp = storage.boot(args, run, [image])
        mark = len(storage.serial_text(run))
        qmp.line("setup install sata0 --layout gpt --erase")
        deadline = time.monotonic() + args.timeout
        while storage.tools.find_marker(storage.serial_text(run), "GPT_TEST OUTPUT Installation: CopyingFiles", mark) < 0:
            # Eine Verweigerung vor dem Kopieren waere kein Abbruchtest; ihr Text erklaert den Grund.
            text = storage.serial_text(run)
            if (error := text.find("GPT_TEST RESULT ", mark)) >= 0:
                raise RuntimeError(f"{case.name}: Installation endete vor CopyingFiles: " + text[error:text.find("\n", error)])
            if time.monotonic() > deadline:
                raise TimeoutError(f"{case.name}: CopyingFiles nicht erreicht")
            time.sleep(0.2)
        process.kill()
        process.wait(timeout=30)
        process = None
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)


def identity_with_security_iso(args, case: Path, image: Path) -> str:
    """Identitaet der Platte aus Sicht eines normalen Kernels (Security-Recovery-ISO), ohne von ihr zu starten."""
    run = storage.new_work_dir(case / "check")
    process = None
    try:
        process, qmp = storage.boot(security_args(args), run, [image])
        identity = serial_identity(run, 5)
        storage.quit_guest(process, qmp)
        process = None
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)
    return identity


def run_g8_copy(args, work: Path) -> None:
    case = storage.new_work_dir(work / "g8-copy")
    image = case / "target.img"
    storage.create_sparse(image, 32 * GIB)
    kill_in_copy(args, case, image)
    identity = identity_with_security_iso(args, case, image)
    if identity != "Unclear SETUP False":
        raise RuntimeError(f"G8: Identitaet nach Abbruch {identity}, erwartet Unclear SETUP False")
    run = storage.new_work_dir(case / "reinstall")
    process = None
    try:
        process, qmp = storage.boot(args, run, [image])
        mark = len(storage.serial_text(run))
        qmp.line("setup install sata0 --layout gpt --erase")
        storage.tools.wait_serial(run, "GPT_TEST RESULT OK", args.timeout, mark)
        storage.stop(process, qmp, run)
        process = None
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)
    inspect_image(image, storage.new_work_dir(case / "inspect"), args.payload, args.version, args.boot_config, marker_expected=True)
    print("PASS G8 Teil: Abbruch in CopyingFiles auf frischer Platte ergibt Unclear, Neuinstallation mit --erase "
          "gelingt und besteht die Offline-Pruefung", flush=True)


def run_g9(args, work: Path, base: Path) -> None:
    case = storage.new_work_dir(work / "g9")
    image = case / "target.img"
    copy_sparse(base, image)
    kill_in_copy(args, case, image)
    identity = identity_with_security_iso(args, case, image)
    if identity.startswith("Healthy") or not identity.endswith("SETUP False"):
        raise RuntimeError(f"G9: Identitaet nach Abbruch {identity}, erwartet nicht Healthy und SETUP False")
    print(f"PASS G9: Neuinstallation ueber Healthy in CopyingFiles abgebrochen, danach {identity}", flush=True)


def fat_disk(path: Path, label: str) -> None:
    """32 GiB sparse mit einer MBR-0x0C-Partition und FAT32-Label wie disk init bzw. ein Bestand."""
    new_sparse(path, 32 * GIB)
    subprocess.run(["sfdisk", "-q", str(path)], input="label: dos\n2048,,c\n", text=True, check=True, capture_output=True)
    sectors = 32 * GIB // SECTOR - 2048
    subprocess.run(["mkfs.fat", "-F", "32", "-n", label, "--offset", "2048", str(path), str(sectors // 2)],
                   check=True, capture_output=True)


def data_digest(path: Path) -> str:
    """SHA-256 ueber Lage und Inhalt der belegten Bereiche; ein Schreibzugriff des Gastes aendert ihn."""
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        position, end = 0, path.stat().st_size
        while position < end:
            try:
                data = os.lseek(stream.fileno(), position, os.SEEK_DATA)
            except OSError as error:
                if error.errno == errno.ENXIO:
                    break
                raise
            hole = os.lseek(stream.fileno(), data, os.SEEK_HOLE)
            stream.seek(data)
            digest.update(data.to_bytes(8, "little"))
            for block in iter(lambda: stream.read(min(8 * 1024 * 1024, hole - stream.tell())), b""):
                digest.update(block)
            position = hole
    return digest.hexdigest()


def run_g6_targets(args, work: Path) -> None:
    case = storage.new_work_dir(work / "g6-targets")
    medium, data, existing, blank = (case / name for name in ("medium.img", "data.img", "existing.img", "blank.img"))
    new_sparse(medium, 32 * GIB)
    # Wie ein roh auf USB geschriebenes ISO: Kopf des Installer-ISO samt CD001 bei Byte 32769.
    with args.iso.open("rb") as source, medium.open("r+b") as target:
        target.write(source.read(64 * 1024))
    fat_disk(data, "ZEHOSDATA")
    fat_disk(existing, "ZEHOS")
    new_sparse(blank, 32 * GIB)
    unchanged = {path: data_digest(path) for path in (medium, data)}
    run = storage.new_work_dir(case / "guest")
    process = None
    try:
        process, qmp = storage.boot(args, run, [medium, data, existing, blank])
        for disk, reason in (("sata0", "Das Installationsmedium ist kein Installationsziel."),
                             ("sata1", "Datenplatte/installiertes System, nicht als Ziel erlaubt.")):
            mark = len(storage.serial_text(run))
            qmp.line(f"setup install {disk} --layout gpt --erase")
            storage.tools.wait_serial(run, "GPT_TEST RESULT ERROR " + reason, args.timeout, mark)
        # Belegt zugleich den Installationsmountpunkt /data (Legacy-Profil); das Ziel selbst wird zuerst erkannt.
        mark = storage.send_command(qmp, run, "disk mount sata2 --yes", "Befehl: disk mount sata2 --yes")
        storage.tools.wait_serial(run, "Laufwerksaenderung abgeschlossen.", 60, mark)
        for disk, reason in (("sata2", "Das Ziellaufwerk ist eingebunden."), ("sata3", "Installations-Mountpunkt belegt.")):
            mark = len(storage.serial_text(run))
            qmp.line(f"setup install {disk} --layout gpt --erase")
            storage.tools.wait_serial(run, "GPT_TEST RESULT ERROR " + reason, args.timeout, mark)
        storage.quit_guest(process, qmp)
        process = None
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)
    assert_blank(blank)
    for path, digest in unchanged.items():
        if data_digest(path) != digest:
            raise RuntimeError(f"G6: {path.name} wurde beschrieben")
    print("PASS G6 Teil: Medium, ZEHOSDATA, eingebundenes Ziel und belegter Installationsmountpunkt abgewiesen, "
          "keine Schreibzugriffe", flush=True)


def run_cases(args, work: Path) -> None:
    case = storage.new_work_dir(work / "base")
    base = install(args, case, 32 * GIB)
    boot_installed(args, case, [base])
    print("PASS Basis: 32 GiB installiert, offline geprueft und Healthy gestartet", flush=True)
    # Die Kontobasis traegt ein Konto (G1-Kontoanlage, fuer G5 "Konten unveraendert"); G3/G4 bleiben ohne Konto,
    # damit "keine Ersteinrichtung" dort wirklich an der Root-Pruefung haengt.
    case = storage.new_work_dir(work / "account")
    account_base = case / "target.img"
    copy_sparse(base, account_base)
    boot_installed(args, case, [account_base], create_account=True)
    if ".zehos-security.db" not in security_files(account_base):
        raise RuntimeError("Kontoanlage: keine Kontendatei auf der ESP")
    print("PASS G1 Teil: Ersteinrichtung auf der installierten Platte legt das Konto auf der ESP an", flush=True)
    run_g2(args, work, base)
    run_g3_g4(args, work, base)
    run_g5(args, work, account_base)
    run_g7(args, work, base)
    run_g9(args, work, base)
    run_g6_targets(args, work)
    run_g8_copy(args, work)
    print("OFFEN: VMware fuer alle Faelle", flush=True)


def run_rdrand(args, work: Path) -> None:
    target = work / "target.img"
    new_sparse(target, 32 * GIB)
    # Spaeteres -cpu gewinnt gegen das "-cpu max" von start_qemu; ohne virtio-rng bleibt keine Zufallsquelle.
    storage.MACHINE_BASE += ["-cpu", "qemu64,-rdrand,-rdseed"]
    run = storage.new_work_dir(work / "guest")
    process = None
    try:
        process, qmp = storage.boot(args, run, [target])
        mark = len(storage.serial_text(run))
        qmp.line("setup install sata0 --layout gpt --erase")
        storage.tools.wait_serial(run, "GPT_TEST RESULT ERROR Keine brauchbare Hardware-Zufallsquelle", args.timeout, mark)
        storage.quit_guest(process, qmp)
        process = None
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)
    assert_blank(target)
    print("PASS TCG ohne RDRAND: Installer verweigert mit Zufallsquellen-Fehler, keine Schreibzugriffe", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-g8", action="store_true", help="G8 mit vorbereitetem Recovery-Setup-ISO ausfuehren")
    parser.add_argument("--run-g6-basic", action="store_true", help="G6-Groessengrenzen und --erase pruefen")
    parser.add_argument("--run-g1-basic", action="store_true", help="G1-Installation und Offline-Pruefung vorbereiten")
    parser.add_argument("--run-cases", action="store_true", help="G2-G5, G7 und G9 auf Kopien einer 32-GiB-Installation")
    parser.add_argument("--run-rdrand", action="store_true", help="Installer unter TCG ohne RDRAND/RDSEED muss verweigern")
    parser.add_argument("--work", required=True, type=Path)
    parser.add_argument("--iso", type=Path)
    parser.add_argument("--iso-security", type=Path)
    parser.add_argument("--payload", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--boot-config", type=Path)
    parser.add_argument("--ovmf-code", type=Path)
    parser.add_argument("--ovmf-vars", type=Path)
    parser.add_argument("--accel", choices=["tcg", "kvm"], default="tcg")
    parser.add_argument("--timeout", type=float, default=1200)
    args = parser.parse_args()
    # Die Schreibfreiheits- und Sparse-Pruefungen brauchen SEEK_DATA/SEEK_HOLE (unter Windows nicht vorhanden).
    if not hasattr(os, "SEEK_DATA") or not hasattr(os, "SEEK_HOLE"):
        print("OFFEN: os.SEEK_DATA/SEEK_HOLE fehlen auf dieser Plattform (Linux erforderlich)", flush=True)
        return 2
    modes = (args.run_g8, args.run_g6_basic, args.run_g1_basic, args.run_cases, args.run_rdrand)
    if not any(modes):
        print("OFFEN: kein Lauf angegeben (--run-g1-basic, --run-g6-basic, --run-g8, --run-cases, --run-rdrand)", flush=True)
        return 2
    if sum(modes) > 1:
        parser.error("Jeder Lauf braucht einen eigenen Arbeitsordner")
    if args.run_rdrand and args.accel != "tcg":
        parser.error("--run-rdrand prueft gerade TCG ohne RDRAND (--accel tcg)")
    installs = args.run_g8 or args.run_g1_basic or args.run_cases
    required = ("sgdisk", "sfdisk", "fsck.fat", "e2fsck", "mcopy", "mdir") if installs else ()
    if args.run_g1_basic or args.run_cases:
        required += ("debugfs",)
    if args.run_cases:
        required += ("mdel", "cp", "mkfs.fat")
    missing = [name for name in required if not shutil.which(name)]
    if missing:
        print("OFFEN: Werkzeuge fehlen: " + ", ".join(missing), flush=True)
        return 2
    if not all((args.iso, args.ovmf_code, args.ovmf_vars)):
        parser.error("Gastlauf benoetigt --iso, --ovmf-code und --ovmf-vars")
    if installs and not all((args.payload, args.version, args.boot_config)):
        parser.error("Installationslaeufe benoetigen --payload, --version und --boot-config")
    if args.run_cases and not args.iso_security:
        parser.error("--run-cases benoetigt --iso-security")
    files = [args.iso, args.ovmf_code, args.ovmf_vars]
    if installs:
        files += [args.boot_config, args.payload / "ProjektZeh.elf", args.payload / "BOOTX64.EFI"]
    if args.run_cases:
        files.append(args.iso_security)
    for file in files:
        if not file.is_file():
            parser.error(f"Datei fehlt: {file}")
    work = storage.new_work_dir(args.work)
    vars_copy = work / "ovmf-vars.fd"
    with args.ovmf_vars.open("rb") as src, vars_copy.open("xb") as dst:
        shutil.copyfileobj(src, dst)
    storage.MACHINE_BASE += ["-drive", f"if=pflash,format=raw,readonly=on,file={args.ovmf_code.resolve()}",
                             "-drive", f"if=pflash,format=raw,file={vars_copy.resolve()}"]
    args.iso_recovery = args.iso
    if args.run_g6_basic:
        run_g6_basic(args, work)
        return 2
    if args.run_g1_basic:
        run_g1_basic(args, work)
        return 2
    if args.run_cases:
        run_cases(args, work)
        return 2
    if args.run_rdrand:
        run_rdrand(args, work)
        return 2
    target = work / "target.img"
    storage.create_sparse(target, 32 * GIB)
    run = storage.new_work_dir(work / "guest")
    process = None
    try:
        process, qmp = storage.boot(args, run, [target])
        qmp.line("setup install sata0 --layout gpt --erase")
        storage.tools.wait_serial(run, "GPT_G8_BOOT_FLUSHED", args.timeout)
        process.kill()
        process.wait(timeout=30)
        process = None
        inspect_image(target, work, args.payload, args.version, args.boot_config, marker_expected=False)
        # SIGKILL nach der Quittung belegt Bestand gegen Gast-Absturz (Gast-/QEMU-Cache), nicht gegen Stromausfall.
        print("PASS G8: Bootdateien nach Gast-Flush im Image, Marker fehlt, Offline-Pruefungen bestanden", flush=True)
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=30)
    return 2  # G1-G7/G9 sind weiterhin offen.


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(f"FEHLER: {error}", file=sys.stderr)
        sys.exit(1)
