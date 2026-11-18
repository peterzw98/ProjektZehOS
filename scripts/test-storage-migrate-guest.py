#!/usr/bin/env python3
"""Gastabnahme der Migration Legacy -> Root (Etappe 4, Paket 4.5, Spec storage-etappe4-design §6: G4-G6).

Alle Images und Arbeitsordner werden neu angelegt; vorhandene Daten werden nie beschrieben.

Benoetigte Builds:
- --iso-installer: Installer-ISO wie fuer test-installer-gpt-guest.py (-p:ZehOSInstaller=true -p:ZehOSGptInstaller=true
  -p:ZehOSGptGuestProbe=true -p:ZehOSRecovery=true); der eingebettete Kernel (--payload) zusaetzlich mit
  -p:ZehOSRootProfile=true, sonst startet das migrierte Ziel immer im Legacy-Profil.
- --iso-security: normaler Kernel mit -p:ZehOSSecurityRecovery=true (disk init der Quelle).
- --iso-migrate: normaler Desktop-Kernel mit -p:ZehOSRootMigration=true (Ersteinrichtung, system migrate, Altplatte).

Ablauf je Fall in einem eigenen Ordner:
- Quelle: 64-MiB-Platte per disk init (ZEHOSDATA), Ersteinrichtung des Testkontos, danach offline Testdateien per mtools.
- Ziel: frische GPT-Installation per Installer-ISO (32/64/128 GiB), offline geprueft wie G1.
- Migration: Desktop anmelden, Textshell, "system migrate sata1 --yes", Ende per serieller MIGRATE-Zeile.
- Offline: Quelle unveraendert (Digest der belegten Bereiche), Root e2fsck -fn, Marker state=ready, Datei-Hashes.

G4: 32 GiB, danach Start der installierten Platte (Profil Root, Anmeldung mit altem Kennwort) und Start der Altplatte allein.
G5: Abbruch (Gast hart beenden) waehrend der Migration -> Profil Blocked, e2fsck -fn, Wiederholung erfolgreich, e2fsck -fn.
G6: 64 und 128 GiB: Migration, Start der installierten Platte, e2fsck -fn.

Offen und so gemeldet: ACL-Vergleich, NetSurf-Start nach der Migration, G5 mit vollem Ziel (kein Testlayout mit kleiner
Root-Partition vorhanden), VMware.

Exitcodes: 2 = alle ausgefuehrten Faelle bestanden, andere Teile offen (deshalb derzeit kein 0); 1 = Fehler.
"""
import argparse
import errno
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("installer_guest", ROOT / "scripts/test-installer-gpt-guest.py")
gpt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gpt)
storage = gpt.storage
tools = storage.tools
GIB, MIB, SECTOR = gpt.GIB, storage.MIB, gpt.SECTOR
SOURCE_SIZE = 64 * MIB
TARGET_SIZES = {"g4": 32 * GIB, "g5": 32 * GIB, "g6-64g": 64 * GIB, "g6-128g": 128 * GIB}
MIGRATE_DONE = "MIGRATE Migration abgeschlossen: "
# OVMF startet eine angehaengte Platte mit ESP vor dem ISO (RED cd97673); ISO-Boots stellen das CD-Laufwerk vor.
CDROM_FIRST = ["-global", "ide-cd.bootindex=0"]
# Fehler meldet der Auftrag als Statustext "cause='...'" (g5-5: ForeignEntry blieb sonst bis zum Timeout unbemerkt).
MIGRATE_FAILED = ("MIGRATE Migrationsauftrag fehlgeschlagen", "MIGRATE Fehler: ", "MIGRATE cause=")

# Testdateien der Quelle (Pfad unter /data -> Ziel auf Root, Spec E4.4 §2) und ihr Inhalt; ein Unterordner, eine
# Datei im Home des Testkontos und eine Binaerdatei ueber mehrere Bloecke. Gemischte Schreibweise erzwingt lange
# Namen: kurze kleingeschriebene Namen legt mtools als 8.3 mit NT-Kleinschreib-Flag an, das Cosmos-FAT nicht
# auswertet (Gast sah NOTIZ.TXT statt notiz.txt; G4-Lauf 2, 18.11.).
SEED = {
    "Projekt/Notiz.txt": ("/srv/legacy-data/Projekt/Notiz.txt", "Migrationstest G4\n".encode()),
    "Projekt/Unterordner/Daten.bin": ("/srv/legacy-data/Projekt/Unterordner/Daten.bin", bytes(range(256)) * 64),
    f"home/{tools.TEST_USER}/Brief.txt": (f"/home/{tools.TEST_USER}/Brief.txt", "Hallo Root\n".encode()),
}
# G5 braucht eine Migration, die laenger als einen Augenblick laeuft: 5 kleine Dateien waren in Lauf g5-1 binnen
# Sekundenbruchteilen fertig. 300 x 64 KiB (rund 19 MiB) geben dem harten Abbruch ein Zeitfenster.
BULK_DIR, BULK_COUNT, BULK_SIZE = "Projekt/Masse", 300, 64 * 1024
BULK_TARGET = "/srv/legacy-data/" + BULK_DIR


def bulk_name(index: int) -> str:
    return f"Datei-{index:03d}.bin"


def bulk_contents(index: int) -> bytes:
    return (f"{index:03d}".encode() * (BULK_SIZE // 3 + 1))[:BULK_SIZE]


def source_partition(image: Path) -> dict:
    """Einzige MBR-Partition der Quelle (disk init legt genau eine an)."""
    table = json.loads(subprocess.run(["sfdisk", "-J", str(image)], capture_output=True, text=True, check=True).stdout)
    parts = table["partitiontable"]["partitions"]
    if table["partitiontable"].get("label") != "dos" or len(parts) != 1:
        raise ValueError("Quelle: genau eine MBR-Partition erwartet")
    return parts[0]


def mtool(image: Path, part: dict, tool: str, *arguments: str, check: bool = True) -> subprocess.CompletedProcess:
    environment = dict(os.environ, MTOOLS_SKIP_CHECK="1")
    return subprocess.run([tool, "-i", f"{image}@@{part['start'] * SECTOR}", *arguments],
                          capture_output=True, check=check, env=environment)


def seed_source(image: Path, case: Path, bulk: bool) -> None:
    """Legt die Testdateien offline auf die FAT-Partition; vorhandene Ordner (etwa das Home) bleiben."""
    part = source_partition(image)
    folder = case / "seed"
    folder.mkdir()
    if bulk:
        local = folder / BULK_DIR.rsplit("/", 1)[-1]
        local.mkdir()
        for index in range(BULK_COUNT):
            (local / bulk_name(index)).write_bytes(bulk_contents(index))
        mtool(image, part, "mmd", "::/" + BULK_DIR.split("/")[0], check=False)
        mtool(image, part, "mcopy", "-s", str(local), "::/" + BULK_DIR.split("/")[0])
    for relative, (_, contents) in SEED.items():
        parent = ""
        for name in relative.split("/")[:-1]:
            parent += "/" + name
            # mmd scheitert bei vorhandenem Ordner; das ist hier kein Fehler (Home legt die Ersteinrichtung an).
            mtool(image, part, "mmd", "::" + parent, check=False)
        local = folder / relative.replace("/", "_")
        local.write_bytes(contents)
        mtool(image, part, "mcopy", str(local), "::/" + relative)
    for relative, (_, contents) in SEED.items():
        if mtool(image, part, "mcopy", "::/" + relative, "-").stdout != contents:
            raise RuntimeError(f"Quelle: {relative} nach dem Kopieren abweichend")


def prepare_source(args, case: Path, bulk: bool) -> Path:
    """64-MiB-ZEHOSDATA mit Testkonto und Testdateien."""
    source = case / "source.img"
    storage.create_sparse(source, SOURCE_SIZE)
    blank = case / "blank.img"
    storage.create_sparse(blank, SOURCE_SIZE)
    run = storage.new_work_dir(case / "source-init")
    process, qmp = storage.boot(args.security, run, [blank, source])
    try:
        mark = storage.send_command(qmp, run, "disk init sata1", "Befehl: disk init sata1")
        storage.wait_init(qmp, run, mark, args.timeout)
        storage.stop(process, qmp, run)
        process = None
    finally:
        kill(process)
    run = storage.new_work_dir(case / "source-account")
    process, qmp = storage.boot(args.migrate, run, [source], desktop=True)
    try:
        tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", args.timeout)
        storage.login(qmp, run, first=True)
        storage.desktop_shutdown(process, qmp, run)
        process = None
    finally:
        kill(process)
    seed_source(source, case, bulk)
    return source


def kill(process) -> None:
    if process is not None and process.poll() is None:
        process.kill()
        process.wait(timeout=30)


def start_migration(args, run: Path, source: Path, target: Path):
    """Altplatte als sata0 (Legacy-Profil), Ziel als sata1; Anmeldung, Textshell, Befehl mit Eingangsquittung."""
    process, qmp = storage.boot(args.migrate, run, [source, target], desktop=True)
    try:
        # Nur der installierte Kernel (ZehOSRootProfile) meldet "Profil:"; dann haette OVMF die Zielplatte gestartet.
        # Er bindet /data nie ein, deshalb auf beide Marken warten statt den vollen Timeout abzusitzen (Review RED P3-1).
        deadline = time.monotonic() + args.timeout
        while not any(marker in (serial := storage.serial_text(run))
                      for marker in ("Datenlaufwerk unter /data eingebunden.", "Profil: ")):
            if time.monotonic() > deadline:
                raise TimeoutError(f"Migrations-ISO bindet /data nicht ein: {run}")
            time.sleep(1)
        if "Profil: " in serial:
            raise RuntimeError(f"Gast startete von der Zielplatte statt vom Migrations-ISO: {run}")
        storage.login(qmp, run, first=False)
        mark = len(storage.serial_text(run))
        qmp.tap("esc")
        tools.wait_serial(run, "Textshell aktiv.", 30, mark)
        qmp.tap("ret")
        # Kein erneutes Tippen wie bei send_command: eine zweite Migration waere ein anderer Testfall. Nur disk-Befehle
        # quittieren mit "Befehl:"; hier ist die erste MIGRATE-Zeile (Zielpruefung oder Fehler) die Quittung.
        mark = len(storage.serial_text(run))
        qmp.line("system migrate sata1 --yes")
        try:
            tools.wait_serial(run, "MIGRATE ", 60, mark)
        except TimeoutError:
            qmp.shot(run / "eingabe.png")
            raise TimeoutError(f"system migrate kam im Gast nicht an; Bild {run / 'eingabe.png'}") from None
    except BaseException:
        kill(process)
        raise
    return process, qmp, mark


def wait_migration(run: Path, mark: int, timeout: float) -> str:
    """Wartet auf das Ergebnis; Fehlzeilen beenden sofort mit ihrem Text, Fortschritt wird mitgeschrieben."""
    deadline, shown = time.monotonic() + timeout, mark
    while time.monotonic() < deadline:
        text = storage.serial_text(run)
        for line in text[shown:].splitlines():
            if line.startswith("MIGRATE "):
                print(line.strip(), flush=True)
        shown = len(text)
        if (done := text.find(MIGRATE_DONE, mark)) >= 0:
            return text[done:text.find("\n", done)].strip()
        for failed in MIGRATE_FAILED:
            if (error := text.find(failed, mark)) >= 0:
                raise RuntimeError(text[error:text.find("\n", error)].strip())
        time.sleep(1)
    raise TimeoutError(f"Migration nach {timeout:.0f} s ohne Ergebnis: {run}")


def migrate(args, case: Path, name: str, source: Path, target: Path) -> str:
    run = storage.new_work_dir(case / name)
    process, qmp, mark = start_migration(args, run, source, target)
    try:
        result = wait_migration(run, mark, args.timeout)
        storage.stop(process, qmp, run)
        process = None
    finally:
        kill(process)
    return result


def root_image(target: Path, case: Path, name: str) -> Path:
    """Root-Partition des Ziels als eigene Sparse-Datei (pread-Extraktion aus test-installer-gpt-guest.py)."""
    _, _, root = gpt.layout(target)
    image = case / f"{name}-root.img"
    gpt.extract_sparse(target, root["start"] * SECTOR, root["size"] * SECTOR, image)
    return image


def write_back(extract: Path, target: Path, offset: int) -> None:
    """Belegte Bereiche einer reparierten Partitionskopie zurueck ins Image; Luecken bleiben Luecken (beide stammen
    aus derselben Sparse-Extraktion). pread/pwrite statt gepufferter Zugriffe neben os.lseek (RED 5cbf8bd)."""
    with extract.open("rb") as src, target.open("r+b") as dst:
        end, position = extract.stat().st_size, 0
        if offset + end > target.stat().st_size:
            raise ValueError("Partitionskopie passt nicht ins Image")
        while position < end:
            try:
                data = os.lseek(src.fileno(), position, os.SEEK_DATA)
            except OSError as error:
                if error.errno == errno.ENXIO:
                    break
                raise
            hole = os.lseek(src.fileno(), data, os.SEEK_HOLE)
            while data < hole:
                block = os.pread(src.fileno(), min(8 * MIB, hole - data), data)
                if not block:
                    raise EOFError("Partitionskopie waehrend des Zurueckschreibens verkuerzt")
                os.pwrite(dst.fileno(), block, offset + data)
                data += len(block)
            position = hole


def repair_root(target: Path, case: Path) -> None:
    """Wie G7: offline e2fsck -fy auf der Root-Partition, danach muss -fn sauber sein (Entscheidung lead2 18.11.)."""
    _, _, root = gpt.layout(target)
    image = root_image(target, case, "repair")
    result = subprocess.run(["e2fsck", "-fy", str(image)], capture_output=True, text=True, check=False)
    # Exit 1 = korrigiert; 0 = nichts zu tun; alles ab 4 waere ein nicht behebbarer Zustand.
    if result.returncode not in (0, 1):
        raise RuntimeError(f"G5: e2fsck -fy Exit {result.returncode}: {result.stdout + result.stderr}")
    gpt.check_ext2(image)
    write_back(image, target, root["start"] * SECTOR)


def debugfs_cat(image: Path, path: str) -> bytes:
    return subprocess.run(["debugfs", "-R", f"cat {path}", str(image)], capture_output=True, check=True).stdout


def check_bulk(image: Path, name: str) -> None:
    """Anzahl und Groessen der Massendateien, Inhalt an drei Stichproben."""
    listing = subprocess.run(["debugfs", "-R", f"ls -l {BULK_TARGET}", str(image)], capture_output=True, text=True,
                             check=True).stdout
    sizes = {fields[-1]: int(fields[5]) for line in listing.splitlines()
             if len(fields := line.split()) >= 7 and fields[-1].startswith("Datei-")}
    expected = {bulk_name(index): BULK_SIZE for index in range(BULK_COUNT)}
    if sizes != expected:
        raise RuntimeError(f"{name}: Massendateien unvollstaendig: {len(sizes)} von {BULK_COUNT} mit erwarteter Groesse")
    for index in (0, BULK_COUNT // 2, BULK_COUNT - 1):
        if debugfs_cat(image, f"{BULK_TARGET}/{bulk_name(index)}") != bulk_contents(index):
            raise RuntimeError(f"{name}: {bulk_name(index)} weicht ab")


def check_migrated(target: Path, case: Path, name: str, bulk: bool = False) -> None:
    image = root_image(target, case, name)
    gpt.check_ext2(image)
    marker = debugfs_cat(image, "/etc/zehos/.zehos-layout")
    if b"\nstate=ready\n" not in marker:
        raise RuntimeError(f"{name}: Root-Marker nicht ready: {marker!r}")
    for relative, (path, contents) in SEED.items():
        if debugfs_cat(image, path) != contents:
            raise RuntimeError(f"{name}: {relative} fehlt oder weicht ab auf dem Ziel ({path})")
    if bulk:
        check_bulk(image, name)


def start_disk(args, run: Path, target: Path) -> tuple[object, int]:
    """Startet von der Zielplatte; liefert Prozess und QMP-Port."""
    # ISO-Vorrang (CDROM_FIRST) hier zuruecknehmen; OVMF beachtet -boot c nicht, die Platte bekommt bootindex=0 (RED 5792491).
    machine = list(storage.MACHINE_BASE)
    index = machine.index(CDROM_FIRST[1])
    del machine[index - 1:index + 1]
    machine += ["-drive", f"file={target.resolve()},format=raw,if=none,id=d0",
                "-device", "ide-hd,drive=d0,bus=ahci.0,bootindex=0"]
    tools.MACHINE = machine
    unused = run / "unused.qcow2"
    tool, environment = tools.qemu_img()
    subprocess.run([*tool, "create", "-f", "qcow2", str(unused), "1M"], check=True, stdout=subprocess.DEVNULL, env=environment)
    port = tools.free_port()
    process = tools.start_qemu(args.iso_installer, unused, run, port, args.accel, None)
    storage.GUESTS.append(process)
    return process, port


def boot_root(args, case: Path, target: Path) -> None:
    """Installierte Platte starten: Profil Root, Anmeldung mit dem Kennwort von der Altplatte."""
    run = storage.new_work_dir(case / "boot-root")
    process, port = start_disk(args, run, target)
    try:
        for text in ("Profil: Root", "Root-Profil: / (ext2) und /boot/efi eingebunden."):
            tools.wait_serial(run, text, args.timeout)
        # Im Root-Profil prueft die Probe /data, das es dort nicht gibt ("NoData"); entscheidend ist SETUP False:
        # die migrierten Konten werden erkannt, es gibt keine Ersteinrichtung.
        identity = gpt.serial_identity(run, 60)
        if not identity.endswith("SETUP False"):
            raise RuntimeError(f"Ziel bietet nach der Migration eine Ersteinrichtung an: {identity}")
        tools.wait_serial(run, "Desktop ist bereit.", args.timeout)
        qmp = tools.Qmp(port)
        storage.login(qmp, run, first=False)
        storage.desktop_shutdown(process, qmp, run)
        process = None
    finally:
        kill(process)


def boot_legacy(args, case: Path, source: Path) -> None:
    """Altplatte allein: bindet weiter ein und nimmt das alte Kennwort an."""
    run = storage.new_work_dir(case / "boot-legacy")
    process, qmp = storage.boot(args.migrate, run, [source], desktop=True)
    try:
        tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", args.timeout)
        storage.login(qmp, run, first=False)
        storage.quit_guest(process, qmp)
        process = None
    finally:
        kill(process)


def new_case(args, work: Path, name: str) -> tuple[Path, Path, Path, str]:
    case = storage.new_work_dir(work / name)
    source = prepare_source(args, case, bulk=name == "g5")
    target = gpt.install(args.installer, storage.new_work_dir(case / "install"), TARGET_SIZES[name])
    return case, source, target, gpt.data_digest(source)


def run_g4(args, work: Path) -> None:
    case, source, target, digest = new_case(args, work, "g4")
    print(migrate(args, case, "migrate", source, target), flush=True)
    if gpt.data_digest(source) != digest:
        raise RuntimeError("G4: Altplatte durch die Migration veraendert")
    check_migrated(target, case, "g4")
    boot_root(args, case, target)
    boot_legacy(args, case, source)
    print("PASS G4: 64 MiB -> 32 GiB migriert, Quelle unveraendert, e2fsck sauber, Marker ready, Dateien gleich, "
          "Ziel startet im Profil Root mit altem Kennwort, Altplatte startet weiter", flush=True)
    print("OFFEN G4: ACL-Vergleich, NetSurf-Start nach der Migration, VMware", flush=True)


def target_written(qmp) -> int:
    """Vom Gast auf das Ziel (sata1, Laufwerk d1) geschriebene Bytes laut QEMU."""
    for entry in qmp.command("query-blockstats")["return"]:
        if entry.get("device") == "d1":
            return entry["stats"]["wr_bytes"]
    raise RuntimeError("query-blockstats nennt das Ziellaufwerk d1 nicht")


def abort_while_copying(args, run: Path, process, qmp, mark: int) -> None:
    """Bis Schritt 5 liest die Migration das Ziel nur; erst danach schreibt sie Marker, Journal und rund 19 MiB Dateien.
    Hart beendet wird, sobald --abort-bytes auf dem Ziel angekommen sind (g5-2: feste 0,5 s trafen noch das Inventar)."""
    tools.wait_serial(run, "MIGRATE Hinweis: /data ist waehrend der Migration nur lesbar", args.timeout, mark)
    start = target_written(qmp)
    deadline = time.monotonic() + args.timeout
    while target_written(qmp) - start < args.abort_bytes:
        if MIGRATE_DONE in storage.serial_text(run)[mark:]:
            raise RuntimeError("G5: Migration war vor dem Abbruch schon fertig; --abort-bytes verkleinern")
        if time.monotonic() > deadline:
            raise TimeoutError("G5: Migration schrieb nicht auf das Ziel")
        time.sleep(0.05)
    print(f"G5: Abbruch nach {target_written(qmp) - start} geschriebenen Bytes auf dem Ziel", flush=True)
    process.kill()
    process.wait(timeout=30)


def run_g5(args, work: Path) -> None:
    case, source, target, digest = new_case(args, work, "g5")
    run = storage.new_work_dir(case / "abort")
    process, qmp, mark = start_migration(args, run, source, target)
    try:
        abort_while_copying(args, run, process, qmp, mark)
        process = None
    finally:
        kill(process)
    aborted = root_image(target, case, "abort")
    # Ein harter Abbruch auf ext2 ohne Journal kann Belegungen verlieren (g5-3: Inode-Bitmap -25, Freizaehler); die Spec
    # nennt kein Soll fuer diesen Zeitpunkt. Ergebnis festhalten, das Soll gilt nach der Wiederholung (Entscheidung lead2).
    result = subprocess.run(["e2fsck", "-fn", str(aborted)], capture_output=True, text=True, check=False)
    findings = [line.strip() for line in (result.stdout + result.stderr).splitlines()
                if line.strip() and not line.startswith(("e2fsck ", "Pass ")) and "files (" not in line]
    print(f"BEFUND G5 nach Abbruch: e2fsck -fn Exit {result.returncode}: " + (" | ".join(findings) or "sauber"), flush=True)
    # Ein Abbruch vor dem ersten Schreiben oder nach dem Abschluss waere kein Abbruchtest.
    marker = debugfs_cat(aborted, "/etc/zehos/.zehos-layout")
    if b"\nstate=migrating\n" not in marker:
        raise RuntimeError(f"G5: Root-Marker nach dem Abbruch nicht migrating: {marker!r}; --abort-bytes anpassen")
    run = storage.new_work_dir(case / "boot-blocked")
    process, _ = start_disk(args, run, target)
    try:
        tools.wait_serial(run, "Profil: Blocked", args.timeout)
    finally:
        kill(process)
    repair_root(target, case)
    print(migrate(args, case, "retry", source, target), flush=True)
    if gpt.data_digest(source) != digest:
        raise RuntimeError("G5: Altplatte durch Abbruch oder Wiederholung veraendert")
    check_migrated(target, case, "retry", bulk=True)
    print("PASS G5 Teil: Abbruch waehrend der Kopie -> Marker migrating, Profil Blocked, offline e2fsck -fy, "
          "Wiederholung erfolgreich, e2fsck -fn sauber, Dateien gleich, Quelle unveraendert", flush=True)
    print("OFFEN G5: volles Ziel (kein Testlayout mit kleiner Root-Partition)", flush=True)


def run_g6(args, work: Path) -> None:
    for name in ("g6-64g", "g6-128g"):
        case, source, target, digest = new_case(args, work, name)
        print(migrate(args, case, "migrate", source, target), flush=True)
        if gpt.data_digest(source) != digest:
            raise RuntimeError(f"{name}: Altplatte durch die Migration veraendert")
        check_migrated(target, case, name)
        boot_root(args, case, target)
        print(f"PASS {name.upper()}: Migration, Start im Profil Root, e2fsck sauber", flush=True)


def installer_args(args) -> argparse.Namespace:
    """Namespace wie test-installer-gpt-guest.py ihn fuer install() erwartet (storage.boot nutzt iso_recovery)."""
    return argparse.Namespace(iso=args.iso_installer, iso_recovery=args.iso_installer, payload=args.payload,
                              version=args.version, boot_config=args.boot_config, accel=args.accel, timeout=args.timeout)


def boot_args(args, iso: Path) -> argparse.Namespace:
    """storage.boot startet iso_normal (desktop=True) bzw. iso_recovery; beides zeigt hier auf dasselbe ISO."""
    return argparse.Namespace(iso_normal=iso, iso_recovery=iso, accel=args.accel, timeout=args.timeout)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--run", choices=["g4", "g5", "g6"], required=True, help="ein Fall je Arbeitsordner")
    parser.add_argument("--work", required=True, type=Path)
    parser.add_argument("--iso-installer", type=Path, required=True)
    parser.add_argument("--iso-security", type=Path, required=True)
    parser.add_argument("--iso-migrate", type=Path, required=True)
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--boot-config", type=Path, required=True)
    parser.add_argument("--ovmf-code", type=Path, required=True)
    parser.add_argument("--ovmf-vars", type=Path, required=True)
    parser.add_argument("--accel", choices=["tcg", "kvm"], default="tcg")
    parser.add_argument("--timeout", type=float, default=1200)
    parser.add_argument("--abort-bytes", type=int, default=2 * MIB,
                        help="G5: auf das Ziel geschriebene Bytes bis zum harten Ende (Datenmenge rund 19 MiB)")
    args = parser.parse_args()
    if not hasattr(os, "SEEK_DATA") or not hasattr(os, "SEEK_HOLE"):
        print("OFFEN: os.SEEK_DATA/SEEK_HOLE fehlen auf dieser Plattform (Linux erforderlich)", flush=True)
        return 2
    if not 0 < args.abort_bytes < BULK_COUNT * BULK_SIZE:
        parser.error("--abort-bytes muss groesser 0 und kleiner als die Massendaten sein")
    required = ("sgdisk", "sfdisk", "fsck.fat", "e2fsck", "debugfs", "mcopy", "mdir", "mmd")
    missing = [name for name in required if not shutil.which(name)]
    if missing:
        print("OFFEN: Werkzeuge fehlen: " + ", ".join(missing), flush=True)
        return 2
    files = [args.iso_installer, args.iso_security, args.iso_migrate, args.boot_config, args.ovmf_code, args.ovmf_vars,
             args.payload / "ProjektZeh.elf", args.payload / "BOOTX64.EFI"]
    for file in files:
        if not file.is_file():
            parser.error(f"Datei fehlt: {file}")
    work = storage.new_work_dir(args.work)
    vars_copy = work / "ovmf-vars.fd"
    with args.ovmf_vars.open("rb") as src, vars_copy.open("xb") as dst:
        shutil.copyfileobj(src, dst)
    storage.MACHINE_BASE += ["-drive", f"if=pflash,format=raw,readonly=on,file={args.ovmf_code.resolve()}",
                             "-drive", f"if=pflash,format=raw,file={vars_copy.resolve()}", *CDROM_FIRST]
    args.installer = installer_args(args)
    args.security = boot_args(args, args.iso_security)
    args.migrate = boot_args(args, args.iso_migrate)
    try:
        {"g4": run_g4, "g5": run_g5, "g6": run_g6}[args.run](args, work)
    finally:
        for process in storage.GUESTS:
            kill(process)
    print("OFFEN: VMware fuer alle Faelle", flush=True)
    return 2


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, RuntimeError, TimeoutError, subprocess.SubprocessError) as error:
        print(f"FEHLER: {error}", file=sys.stderr)
        sys.exit(1)
