#!/usr/bin/env python3
"""Gesamtabnahme Storage Etappe 8 (Plan docs/superpowers/plans/2026-11-17-storage-linux.md), Runde 1, Linux/KVM.

Je Groesse (Standard 32, 64, 128 GiB) in einem NEUEN Arbeitsordner: GPT-Installation wie test-installer-gpt-guest.py,
dann Start von der Platte im Root-Profil (ext2 als /): Ersteinrichtung, zwei Terminals mit eigenem Arbeitsverzeichnis,
Dateimanager, in der Textshell cat der virtuellen Dateien (/proc, /sys, /dev/null) mit Bild, Neustart, zweite
Anmeldung, shutdown. Die Shell spiegelt ihre Ausgabe nicht seriell; deshalb hinterlaesst der Gast Dateien, die nach
dem Ende von QEMU offline geprueft werden (debugfs, e2fsck, fsck.fat, s_state). Jede Pruefung meldet einzeln
PASS/OFFEN/FEHLER, ein Fehlschlag verdeckt die uebrigen nicht.

Zusaetzlich (--inode-limit): eine Kopie der 32-GiB-Installation wird offline mit mehr als 17.519 leeren Dateien
gefuellt (Inode-Cache-Grenze je Sitzung, docs/COSMOS_SUPPORT.md); der Gast beruehrt sie per du. Erwartet: kein
Absturz, Shell und shutdown bleiben bedienbar, e2fsck sauber. Ersatzfall fuer "voller Datentraeger" (Runde 1).
Legacy-Regression (--legacy): Fall P1 aus test-power-guest.py (64-MiB-Datenplatte, Schreiben, shutdown, fsck.fat).

Benoetigte Builds:
- --iso: Installer-ISO mit -p:ZehOSInstaller=true -p:ZehOSGptInstaller=true -p:ZehOSGptGuestProbe=true
  -p:ZehOSRecovery=true; --payload/ProjektZeh.elf zusaetzlich mit -p:ZehOSRootProfile=true -p:ZehOSGptGuestProbe=true.
- --iso-normal / --iso-recovery (nur --legacy): normaler Build bzw. -p:ZehOSSecurityRecovery=true.
Werkzeuge wie test-installer-gpt-guest.py und test-power-guest.py, dazu debugfs.

Runde 1 OFFEN: VMware, voller Datentraeger, pkg install, update install, Dateimanager-Inhalt (nur Bild).
Grenze 0.6: cp aus /proc, /sys, /dev wird verweigert (cause=ForeignMount); virtuelle Dateien nur per cat (BILD-Zeilen).
Exitcodes: 2 = alle ausgefuehrten Pruefungen bestanden, Teile offen; 1 = mindestens ein Fehler.
"""
import argparse
import errno
import importlib.util
import os
import secrets
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def load(name: str, file: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


gpt = load("installer_gpt_guest", "test-installer-gpt-guest.py")
power = load("power_guest", "test-power-guest.py")
storage, tools = power.storage, power.tools
GIB = 1024 ** 3
USER_HOME = f"/home/{tools.TEST_USER}"
# Inode-Cache: bei 512 MiB scheitert das Dictionary bei 17519 Eintraegen; je Verzeichnis hoechstens 8189 Kinder.
INODE_FILES, INODE_DIRS = 18000, 3
VIRTUAL_FILES = (("version", "/proc/version"), ("meminfo", "/proc/meminfo"), ("mounts", "/proc/mounts"),
                 ("components", "/sys/kernel/components"), ("null", "/dev/null"))


class Results:
    """Sammelt PASS/OFFEN/FEHLER je Pruefung; ein Fehlschlag bricht nur seinen Fall ab, nicht die Auswertung."""

    def __init__(self):
        self.failed = False

    def report(self, state: str, text: str) -> None:
        self.failed |= state == "FEHLER"
        print(f"{state} {text}", flush=True)

    def check(self, name: str, action) -> None:
        try:
            detail = action()
            self.report("PASS", name + (f": {detail}" if detail else ""))
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
            self.report("FEHLER", f"{name}: {error}")


def launch(guest, name: str) -> None:
    """Startmenue per Alt+F1, Name tippen, Enter (wie Guest.launch in test-wasm-doom-desktop.py)."""
    guest.qmp.tap("alt", "f1")
    time.sleep(0.5)
    guest.qmp.text(name)
    time.sleep(0.5)
    guest.qmp.tap("ret")
    time.sleep(3)


def lines(guest, *values: str) -> None:
    # Unter Last koennen Zeichen verloren gehen; kurze Pausen, die Offline-Pruefung zeigt jeden Verlust.
    for value in values:
        guest.qmp.line(value)
        time.sleep(1)


def debugfs_cat(root: Path, path: str) -> bytes:
    result = subprocess.run(["debugfs", "-R", f"cat {path}", str(root)], capture_output=True, check=False)
    if result.returncode or b"File not found" in result.stderr:
        raise RuntimeError(f"{path} fehlt auf dem Root ({result.stderr.decode(errors='replace').strip()[-120:]})")
    return result.stdout


def expect_text(root: Path, path: str, expected: str):
    def check() -> str:
        content = debugfs_cat(root, path).decode(errors="replace")
        if content.strip() != expected:
            raise RuntimeError(f"{path} enthaelt {content[:80]!r}, erwartet {expected!r}")
        return expected
    return check


def first_boot(args, guest, run: Path, tokens: dict) -> None:
    guest.ready(args.timeout, extra=power.ROOT_READY)
    storage.login(guest.qmp, run, first=True)
    width, height = guest.qmp.shot(run / "01-angemeldet.png")
    launch(guest, "Terminal")
    lines(guest, f"mkdir {USER_HOME}/t1", f"cd {USER_HOME}/t1", f"write a.txt {tokens['t1a']}")
    launch(guest, "Terminal")
    lines(guest, f"mkdir {USER_HOME}/t2", f"cd {USER_HOME}/t2", f"write a.txt {tokens['t2a']}")
    guest.qmp.shot(run / "02-zwei-terminals.png")
    # Terminal 1 liegt bei 72/72, Terminal 2 um 40 px versetzt: die linke Titelleiste von Terminal 1 bleibt frei.
    guest.qmp.click(90, 82, width, height)
    time.sleep(1)
    lines(guest, f"write b.txt {tokens['t1b']}")
    guest.qmp.shot(run / "03-terminal1-wieder.png")
    launch(guest, "Dateien")
    guest.qmp.shot(run / "04-dateimanager.png")
    guest.to_textshell()
    # cp aus /proc, /sys, /dev ist fuer 0.6 bewusst nicht unterstuetzt (ForeignMount, Entscheid lead2): lesen per cat,
    # die Shell spiegelt nicht seriell, Beleg ist je ein Bild.
    for name, path in VIRTUAL_FILES:
        lines(guest, "clear", f"cat {path}")
        guest.qmp.shot(run / f"05-cat-{name}.png")
    lines(guest, "clear", f"write {USER_HOME}/boot1.txt {tokens['boot1']}")


def second_boot(args, guest, run: Path, tokens: dict) -> None:
    start = guest.mark()
    guest.power("reboot", expect_exit=False)
    guest.ready(args.timeout, start, extra=power.ROOT_READY)
    storage.login(guest.qmp, run, first=False)
    guest.to_textshell()
    lines(guest, f"write {USER_HOME}/boot2.txt {tokens['boot2']}")
    guest.qmp.shot(run / "06-nach-reboot.png")
    guest.power("shutdown", expect_exit=True)


def expect_clean_state(image: Path) -> str:
    state = power.read_s_state(image)
    if state != 1:
        raise RuntimeError(f"s_state = {state}, erwartet 1 (sauber ausgehaengt)")
    return "1"


def offline_checks(results: Results, image: Path, case: Path, label: str, tokens: dict) -> None:
    results.check(f"{label} s_state nach shutdown = 1", lambda: expect_clean_state(image))
    root = power.extract_sparse(image, power.find_partition(image, power.ROOT_TYPE), case / "root-part.img")
    esp = power.extract_sparse(image, power.find_partition(image, power.ESP_TYPE), case / "esp-part.img")
    results.check(f"{label} e2fsck -fn Root sauber", lambda: power.check_ext2(root))
    results.check(f"{label} fsck.fat ESP (2 tolerierte Formatierer-Meldungen)", lambda: gpt.check_fat(esp))
    results.check(f"{label} Terminal 1 schreibt in sein cwd", expect_text(root, f"{USER_HOME}/t1/a.txt", tokens["t1a"]))
    results.check(f"{label} Terminal 2 schreibt in sein cwd", expect_text(root, f"{USER_HOME}/t2/a.txt", tokens["t2a"]))
    results.check(f"{label} Terminal 1 behaelt cwd nach Terminal 2", expect_text(root, f"{USER_HOME}/t1/b.txt", tokens["t1b"]))
    results.check(f"{label} Datei vor Neustart", expect_text(root, f"{USER_HOME}/boot1.txt", tokens["boot1"]))
    results.check(f"{label} Datei nach Neustart", expect_text(root, f"{USER_HOME}/boot2.txt", tokens["boot2"]))
    for name, path in VIRTUAL_FILES:
        results.report("BILD", f"{label} cat {path}: Sichtpruefung {case / 'lauf' / f'05-cat-{name}.png'}")
    results.report("OFFEN", f"{label} Dateimanager: nur geoeffnet und fotografiert ({case / 'lauf' / '04-dateimanager.png'})")


def run_size(args, work: Path, size: int, results: Results) -> Path | None:
    label = f"{size // GIB} GiB"
    case = gpt.storage.new_work_dir(work / f"{size // GIB}g")
    try:
        image = gpt.install(args, case, size)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, TimeoutError) as error:
        results.report("FEHLER", f"{label} Installation: {error}")
        return None
    results.report("PASS", f"{label} Installation (GPT, Offline-Pruefung)")
    # Unveraenderte Kopie fuer den Inode-Fall, bevor der Gast Konten anlegt.
    pristine = case / "installiert.img"
    gpt.copy_sparse(image, pristine)
    tokens = {name: f"{name}-{secrets.token_hex(6)}" for name in ("t1a", "t2a", "t1b", "boot1", "boot2")}
    run = storage.new_work_dir(case / "lauf")
    guest = None
    try:
        guest = power.Guest(args, run, [image], args.iso, from_disk=True, reboot_exits=False)
        first_boot(args, guest, run, tokens)
        second_boot(args, guest, run, tokens)
        # Ob Terminals und Neustart wirkten, belegen erst die Offline-Pruefungen; hier nur der Ablauf ohne Haenger.
        results.report("PASS", f"{label} Gastablauf bis shutdown ohne Haenger (Boot, Ersteinrichtung, Eingaben, Neustart)")
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, TimeoutError) as error:
        results.report("FEHLER", f"{label} Gastablauf: {error} (Bilder/Protokolle: {run})")
        return pristine
    finally:
        if guest is not None:
            guest.kill()
    try:
        offline_checks(results, image, case, label, tokens)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        results.report("FEHLER", f"{label} Offline-Pruefung: {error}")
    return pristine


def fill_inodes(root_part: Path, work: Path) -> None:
    """Legt INODE_FILES 1-Byte-Dateien in INODE_DIRS Verzeichnissen unter /home an (offline, debugfs -w)."""
    # Je ein Byte: du muss dann INODE_FILES Bytes zeigen; bei leeren Dateien saehe ein uebersprungener Ordner gleich aus.
    empty = work / "ein-byte"
    empty.write_bytes(b"x")
    commands = work / "debugfs.cmd"
    per_dir = -(-INODE_FILES // INODE_DIRS)
    counts = [min(per_dir, INODE_FILES - directory * per_dir) for directory in range(INODE_DIRS)]
    with commands.open("w") as stream:
        # Die frische Root hat noch kein /home (das legt erst die Ersteinrichtung an); mkdir auf ein vorhandenes /home
        # meldet debugfs nur und macht weiter.
        stream.write("mkdir /home\n")
        for directory, count in enumerate(counts):
            stream.write(f"mkdir /home/inode{directory}\n")
            for index in range(count):
                stream.write(f"write {empty} /home/inode{directory}/f{index}\n")
    subprocess.run(["debugfs", "-w", "-f", str(commands), str(root_part)], capture_output=True, check=True)
    # debugfs endet auch nach gescheiterten Befehlen mit 0; deshalb die angelegten Eintraege nachzaehlen.
    for directory, count in enumerate(counts):
        listing = subprocess.run(["debugfs", "-R", f"ls -p /home/inode{directory}", str(root_part)],
                                 capture_output=True, text=True, check=True).stdout
        found = sum(1 for line in listing.splitlines() if line.count("/") >= 6 and line.split("/")[5].startswith("f"))
        if found != count:
            raise RuntimeError(f"Inode-Fuellung: /home/inode{directory} hat {found} statt {count} Dateien")


def write_back(extract: Path, image: Path, offset: int) -> None:
    """Belegte Bereiche einer Partitionskopie an offset ins Image; Luecken bleiben Luecken (beide aus derselben
    Sparse-Extraktion, debugfs stanzt keine Luecken). pread/pwrite statt gepuffertem Lesen neben os.lseek."""
    end = extract.stat().st_size
    if offset < 0 or offset + end > image.stat().st_size:
        raise ValueError("Partitionskopie passt nicht ins Image")
    with extract.open("rb") as source, image.open("r+b") as target:
        position = 0
        while position < end:
            try:
                data = os.lseek(source.fileno(), position, os.SEEK_DATA)
            except OSError as error:
                if error.errno == errno.ENXIO:
                    break
                raise
            hole = os.lseek(source.fileno(), data, os.SEEK_HOLE)
            while data < hole:
                block = os.pread(source.fileno(), min(8 * 1024 * 1024, hole - data), data)
                if not block:
                    raise EOFError("Partitionskopie waehrend des Zurueckschreibens verkuerzt")
                os.pwrite(target.fileno(), block, offset + data)
                data += len(block)
            position = hole


def run_inode_limit(args, work: Path, installed: Path, results: Results) -> None:
    case = storage.new_work_dir(work / "inode-grenze")
    image = case / "target.img"
    try:
        gpt.copy_sparse(installed, image)
        part = power.find_partition(image, power.ROOT_TYPE)
        root = power.extract_sparse(image, part, case / "root-vorher.img")
        fill_inodes(root, case)
        write_back(root, image, part["start"] * power.SECTOR)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        results.report("FEHLER", f"Inode-Grenze Vorbereitung: {error}")
        return
    results.check("Inode-Grenze: Vorbereitung e2fsck sauber", lambda: power.check_ext2(root))
    run = storage.new_work_dir(case / "lauf")
    token = f"inode-{secrets.token_hex(6)}"
    guest = None
    try:
        guest = power.Guest(args, run, [image], args.iso, from_disk=True, reboot_exits=False)
        guest.ready(args.timeout, extra=power.ROOT_READY)
        storage.login(guest.qmp, run, first=True)
        guest.to_textshell()
        # Offline angelegte Dateien haben keinen ACL-Eintrag und bleiben gesperrt; du ueberspraenge sie (Lauf 386229d:
        # "0 Bytes /home" trotz 18000 Bytes). Das Testkonto ist Administrator und ordnet die Ordner zuerst zu.
        lines(guest, *(f"security adopt /home/inode{directory} {tools.TEST_USER}" for directory in range(INODE_DIRS)))
        start = guest.mark()
        lines(guest, "du /home")
        # shortcut: du meldet sein Ende nicht seriell; feste 120 s (gemessen deutlich kuerzer), bei Bedarf Bildvergleich.
        time.sleep(120)
        guest.qmp.shot(run / "du.png")
        results.report("BILD", f"Inode-Grenze: du muss {INODE_FILES} Bytes fuer /home zeigen (Sichtpruefung {run / 'du.png'})")
        serial = storage.serial_text(run)[start:]
        if "CPU EXCEPTION" in serial or "System halted" in serial:
            raise RuntimeError("Absturz waehrend du (CPU EXCEPTION/System halted im seriellen Protokoll)")
        lines(guest, f"write {USER_HOME}/nach-du.txt {token}")
        guest.power("shutdown", expect_exit=True)
        results.report("PASS", f"Inode-Grenze: du ueber {INODE_FILES} Dateien ohne Absturz, Shell und shutdown bedienbar")
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, TimeoutError) as error:
        results.report("FEHLER", f"Inode-Grenze Gastablauf: {error} (Bilder/Protokolle: {run})")
        return
    finally:
        if guest is not None:
            guest.kill()
    try:
        after = power.extract_sparse(image, part, case / "root-nachher.img")
    except (OSError, ValueError) as error:
        results.report("FEHLER", f"Inode-Grenze Offline-Pruefung: {error}")
        return
    results.check("Inode-Grenze: e2fsck -fn nach dem Lauf sauber", lambda: power.check_ext2(after))
    results.check("Inode-Grenze: Datei nach du geschrieben", expect_text(after, f"{USER_HOME}/nach-du.txt", token))


def run_legacy(args, work: Path, results: Results) -> None:
    spec = next(case for case in power.load_cases() if case["id"] == "P1")
    case = storage.new_work_dir(work / "legacy-64m")
    try:
        power.run_p1(args, case, spec)
        results.report("PASS", "Legacy-Regression 64 MiB (P1: Datenplatte, Schreiben, shutdown, fsck.fat, Inhalt)")
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, TimeoutError) as error:
        results.report("FEHLER", f"Legacy-Regression 64 MiB: {error}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--work", required=True, type=Path)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--boot-config", type=Path, required=True)
    parser.add_argument("--ovmf-code", type=Path, required=True)
    parser.add_argument("--ovmf-vars", type=Path, required=True)
    parser.add_argument("--sizes", default="32,64,128", help="GiB, kommagetrennt")
    parser.add_argument("--inode-limit", action="store_true", help="Inode-Cache-Grenzfall auf der 32-GiB-Installation")
    parser.add_argument("--legacy", action="store_true", help="Legacy-Regression P1 (braucht --iso-normal, --iso-recovery)")
    parser.add_argument("--iso-normal", type=Path)
    parser.add_argument("--iso-recovery", type=Path)
    parser.add_argument("--accel", choices=["kvm", "tcg"], default="kvm")
    parser.add_argument("--timeout", type=float, default=600)
    args = parser.parse_args()
    sizes = [int(text) * GIB for text in args.sizes.split(",") if text]
    if any(size not in (32 * GIB, 64 * GIB, 128 * GIB) for size in sizes):
        parser.error("--sizes erlaubt nur 32, 64 und 128")
    if args.inode_limit and 32 * GIB not in sizes:
        parser.error("--inode-limit braucht die 32-GiB-Installation (--sizes mit 32)")
    if args.legacy and not (args.iso_normal and args.iso_recovery):
        parser.error("--legacy braucht --iso-normal und --iso-recovery")
    missing = [name for name in ("sgdisk", "sfdisk", "fsck.fat", "e2fsck", "debugfs", "mcopy", "mdir", "cp")
               if not shutil.which(name)]
    if missing:
        parser.error("Werkzeuge fehlen: " + ", ".join(missing))
    work = gpt.storage.new_work_dir(args.work)
    # Installationen starten vom ISO (CD vor der Platte), Root-Laeufe von der Platte (power.Guest setzt OVMF selbst).
    # gpt.storage und power.storage sind getrennte Modulobjekte (je eigenes spec_from_file_location); OVMF hier landet
    # nur in gpt.storage.MACHINE_BASE, sonst haette ein Root-Lauf vier pflash-Laufwerke und P1 liefe unter OVMF.
    vars_copy = work / "ovmf-vars.fd"
    shutil.copyfile(args.ovmf_vars, vars_copy)
    gpt.storage.MACHINE_BASE += ["-drive", f"if=pflash,format=raw,readonly=on,file={args.ovmf_code.resolve()}",
                                 "-drive", f"if=pflash,format=raw,file={vars_copy.resolve()}", *gpt.CDROM_FIRST]
    gpt_args = argparse.Namespace(**vars(args))
    gpt_args.iso_recovery = args.iso
    results = Results()
    installed_32 = None
    for size in sizes:
        installed = run_size(gpt_args, work, size, results)
        if size == 32 * GIB:
            installed_32 = installed
    if args.inode_limit:
        if installed_32 is None:
            results.report("FEHLER", "Inode-Grenze: keine 32-GiB-Installation")
        else:
            run_inode_limit(args, work, installed_32, results)
    else:
        results.report("OFFEN", "Inode-Grenze nicht ausgewaehlt (--inode-limit)")
    if args.legacy:
        run_legacy(args, work, results)
    else:
        results.report("OFFEN", "Legacy-Regression nicht ausgewaehlt (--legacy)")
    for text in ("VMware", "voller Datentraeger (Dauer; Ersatz Inode-Grenze)", "pkg install (Runde 2)",
                 "update install mit Testschluessel (Runde 2)"):
        results.report("OFFEN", text)
    return 1 if results.failed else 2


if __name__ == "__main__":
    sys.exit(main())
