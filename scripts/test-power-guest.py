#!/usr/bin/env python3
"""Gastabnahme der Energieaktionen P1-P4 (Etappe 7b, Spec 2026-11-17-storage-7b-updates-power.md §4) fuer Linux/KVM.

Die Faelle stehen als Daten in scripts/power_cases.json; P5-P6 sind dort als offen gefuehrt. Jeder Fall laeuft in
einem NEUEN Arbeitsordner auf einer eigenen Sparse-Kopie (cp --sparse=always); --root-disk und andere vorhandene
Images werden nie beschrieben. Erfolg hat keine eigene serielle Zeile: entscheidend sind die Offlinepruefungen nach
dem Ende von QEMU (Fehler der Energieaktion erscheinen als Shellfehler, etwa cause='PowerBusy').

Benoetigte Builds und Platten:
- --iso-normal: normaler Build ohne Profilflags (P1, Legacy).
- --iso-recovery: Build mit -p:ZehOSSecurityRecovery=true; richtet in P1 die frische 64-MiB-Datenplatte per disk init ein.
- --root-disk: installierte GPT-Platte (Root-Profil), etwa aus scripts/test-installer-gpt-guest.py --run-g1-basic
  (Branch task/storage-7a-s4-installer) mit einem Payload-Kernel aus dem 7b-Stand, gebaut mit -p:ZehOSRootProfile=true.
  Sie startet ueber OVMF von der Platte (--ovmf-code/--ovmf-vars); das angehaengte --iso-normal bleibt unbenutzt.
  P4a-P4f brauchen eine eigene Root-Platte, deren Payload zusaetzlich mit -p:ZehOSPowerFaultProbe=true gebaut ist
  (Shellbefehl powerfault, serielle Zeile POWER_FAULT <punkt>); dieser Build gehoert nie in ein Release.
Werkzeuge: sfdisk, fsck.fat, e2fsck, dumpe2fs, mtools (mtype), debugfs.

Exitcodes: 0 = alle ausgewaehlten Faelle bestanden (P5-P6 bleiben offen), 1 = mindestens ein Fall gescheitert.
"""
import argparse
import errno
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("storage_guest", ROOT / "scripts/test-storage-data-disk.py")
storage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage)
tools = storage.tools

SECTOR = 512
MIB = 1024 ** 2
ESP_TYPE = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B"
ROOT_TYPE = "0FC63DAF-8483-4772-8E79-3D69D8477DE4"
S_STATE_OFFSET = 1024 + 58  # ext2-Superblock ab Byte 1024, s_state (le16) an Versatz 58; 1 = sauber
ROOT_READY = "Root-Profil: / (ext2) und /boot/efi eingebunden."
CDROM_FIRST = "ide-cd.bootindex=0"
SCREEN = (1920, 1080)
# Das erste Paketfenster liegt bei 32,80 (800x560); sein Schliessen-Feld wie in test-wasm-doom-desktop.py.
FIRST_WINDOW_CLOSE = (813, 100)

# Artefakte des Cosmos-FAT-Formatierers (Entscheidung Koordinator 18.11., wie in test-installer-gpt-guest.py):
# das Label steht nur im Bootsektor, der FSInfo-Freizaehler bleibt "unbekannt".
FAT_TOLERATED = (re.compile(r"Label in boot sector is '.*', but there is no volume label in root directory\."),
                 re.compile(r"Auto-removing label from boot sector\."),
                 re.compile(r"Free cluster summary uninitialized \(should be \d+\)"))


def load_cases() -> list[dict]:
    return json.loads((ROOT / "scripts/power_cases.json").read_text(encoding="utf-8"))["cases"]


def copy_sparse(source: Path, target: Path) -> None:
    if target.exists():
        raise FileExistsError(f"Kopie existiert bereits: {target}")
    subprocess.run(["cp", "--sparse=always", str(source), str(target)], check=True)


def partitions(image: Path) -> list[dict]:
    table = json.loads(subprocess.run(["sfdisk", "-J", str(image)], capture_output=True, text=True, check=True).stdout)
    return table["partitiontable"]["partitions"]


def find_partition(image: Path, type_guid: str | None) -> dict:
    parts = [p for p in partitions(image) if type_guid is None or p["type"].upper() == type_guid]
    if len(parts) != 1:
        raise ValueError(f"Genau eine Partition {type_guid or '(beliebig)'} erwartet in {image}, gefunden {len(parts)}")
    return parts[0]


def extract_sparse(source: Path, part: dict, target: Path) -> Path:
    """Kopiert nur belegte Bereiche der Partition in eine eigene Datei (fsck-Werkzeuge kennen keinen Versatz)."""
    offset, length = part["start"] * SECTOR, part["size"] * SECTOR
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
            dst.seek(data - offset)
            while data < hole:
                # pread: kein gepuffertes Lesen neben os.lseek auf demselben Deskriptor.
                block = os.pread(src.fileno(), min(8 * MIB, hole - data), data)
                if not block:
                    raise EOFError("Image waehrend des Kopierens verkuerzt")
                dst.write(block)
                data += len(block)
            pos = hole
    return target


def check_fat(image: Path) -> None:
    result = subprocess.run(["fsck.fat", "-n", str(image)], capture_output=True, text=True, check=False)
    lines = [line.strip() for line in (result.stdout + result.stderr).splitlines()]
    findings = [line for line in lines if line and not line.startswith(("fsck.fat ", str(image) + ":"))
                and line != "Leaving filesystem unchanged."]
    unexpected = [line for line in findings if not any(pattern.fullmatch(line) for pattern in FAT_TOLERATED)]
    if result.returncode not in (0, 1) or unexpected:
        raise RuntimeError(f"fsck.fat Exit {result.returncode}: " + " | ".join(unexpected or lines))
    note = f" (tolerierte Formatierer-Artefakte: {' | '.join(findings)})" if findings else ""
    print(f"  fsck.fat -n Exit {result.returncode}{note}", flush=True)


def check_ext2(image: Path) -> None:
    result = subprocess.run(["e2fsck", "-fn", str(image)], capture_output=True, text=True, check=False)
    output = result.stdout + result.stderr
    # -n kann trotz nicht behobener Fehler Exit 0 liefern; die Ausgabe ist Teil der Pruefung.
    if result.returncode or re.search(r"fix\?\s*no|wrong|corrupt|error|warning|unexpected|not clean", output, re.IGNORECASE):
        raise RuntimeError("e2fsck -fn meldet Fehler: " + output)
    print("  e2fsck -fn Exit 0", flush=True)


def check_clean(image: Path) -> None:
    result = subprocess.run(["dumpe2fs", "-h", str(image)], capture_output=True, text=True, check=True)
    state = next((line.split(":", 1)[1].strip() for line in result.stdout.splitlines() if line.startswith("Filesystem state:")), None)
    if state != "clean":
        raise RuntimeError(f"dumpe2fs: Filesystem state {state!r} statt 'clean'")
    print("  dumpe2fs -h: clean", flush=True)


def read_s_state(image: Path) -> int:
    root = find_partition(image, ROOT_TYPE)
    with image.open("rb") as stream:
        magic = os.pread(stream.fileno(), 2, root["start"] * SECTOR + 1024 + 56)
        if magic != b"\x53\xef":
            raise ValueError("ext2-Magic des Root nicht gefunden")
        return int.from_bytes(os.pread(stream.fileno(), 2, root["start"] * SECTOR + S_STATE_OFFSET), "little")


class Guest:
    """Ein QEMU-Lauf auf einer Kopie; Root-Platten starten ueber OVMF von der Platte, sonst vom ISO."""

    def __init__(self, args, run: Path, disks: list[Path], iso: Path, from_disk: bool, reboot_exits: bool):
        machine = list(storage.MACHINE_BASE)
        if reboot_exits:
            # Gleichwertig zu -no-reboot; ein zusaetzliches -no-reboot stuende neben reboot=reset im Grundaufruf.
            machine[machine.index("shutdown=poweroff,reboot=reset")] = "shutdown=poweroff,reboot=shutdown"
        if from_disk:
            if CDROM_FIRST in machine:
                index = machine.index(CDROM_FIRST)
                del machine[index - 1:index + 1]
            machine[machine.index("-boot") + 1] = "c"
            vars_copy = run / "ovmf-vars.fd"
            copy_sparse(args.ovmf_vars, vars_copy)
            machine += ["-drive", f"if=pflash,format=raw,readonly=on,file={args.ovmf_code.resolve()}",
                        "-drive", f"if=pflash,format=raw,file={vars_copy}"]
        for port, disk in enumerate(disks):
            # OVMF beachtet -boot c nicht; ohne bootindex startet es das angehaengte ISO statt der Platte.
            machine += ["-drive", f"file={disk.resolve()},format=raw,if=none,id=d{port}",
                        "-device", f"ide-hd,drive=d{port},bus=ahci.{port}" + (",bootindex=0" if from_disk and port == 0 else "")]
        tools.MACHINE = machine
        unused = run / "unused.qcow2"  # start_qemu haengt immer ein Laufwerk "appdisk" an; hier ohne Geraet
        tool, environment = tools.qemu_img()
        subprocess.run([*tool, "create", "-f", "qcow2", str(unused), "1M"], check=True, stdout=subprocess.DEVNULL, env=environment)
        self.run, self.port = run, tools.free_port()
        self.process = tools.start_qemu(iso, unused, run, self.port, args.accel, None)
        self.qmp = None

    def mark(self) -> int:
        log = self.run / "serial.log"
        return len(log.read_text(encoding="utf-8", errors="replace")) if log.exists() else 0

    def ready(self, timeout: float, start: int = 0, extra: str | None = None) -> None:
        if extra:
            tools.wait_serial(self.run, extra, timeout, start)
        tools.wait_serial(self.run, "Desktop ist bereit.", timeout, start)
        if self.qmp is None:
            self.qmp = tools.Qmp(self.port)
        time.sleep(3)

    def to_textshell(self) -> None:
        start = self.mark()
        self.qmp.tap("esc")
        try:
            tools.wait_serial(self.run, "Textshell aktiv.", 10, start)
        except TimeoutError:
            self.qmp.tap("esc")
            tools.wait_serial(self.run, "Textshell aktiv.", 30, start)
        self.qmp.tap("ret")
        time.sleep(1)

    def power(self, action: str, expect_exit: bool, timeout: float = 60) -> None:
        try:
            self.qmp.line(action)
        except (ConnectionError, OSError):
            pass  # QEMU endet oft vor dem Loslassen der Taste
        if not expect_exit:
            return
        try:
            self.process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            self.qmp.shot(self.run / f"{action}-haenger.png")
            raise RuntimeError(f"{action}: QEMU lief nach {timeout:.0f} s weiter (Bild {self.run / f'{action}-haenger.png'})")

    def kill(self) -> None:
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait(timeout=30)


def write_file(guest: Guest, path: str, case_id: str) -> str:
    text = f"{case_id}-power-ok"
    guest.qmp.line(f"write {path} {text}")
    time.sleep(2)
    guest.qmp.shot(guest.run / f"write-{Path(path).stem}.png")
    return text


def prepare_data_disk(args, case: Path) -> Path:
    """Frische 64-MiB-Platte, disk init im Security-Recovery-Build (der normale Build lehnt es ohne Administrator ab)."""
    blank, data = case / "blank.img", case / "data.img"
    storage.create_sparse(blank, 64 * MIB)
    storage.create_sparse(data, 64 * MIB)
    run = storage.new_work_dir(case / "init")
    process, qmp = storage.boot(argparse.Namespace(iso_recovery=args.iso_recovery, accel=args.accel, timeout=args.timeout),
                                run, [blank, data])
    try:
        mark = storage.send_command(qmp, run, "disk init sata1", "Befehl: disk init sata1")
        storage.wait_init(qmp, run, mark, args.timeout)
        storage.stop(process, qmp, run)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=30)
    blank.unlink()
    return data


def run_p1(args, case: Path, spec: dict) -> None:
    data = prepare_data_disk(args, case)
    run = storage.new_work_dir(case / "lauf")
    guest = Guest(args, run, [data], args.iso_normal, from_disk=False, reboot_exits=False)
    try:
        guest.ready(args.timeout)
        storage.login(guest.qmp, run, first=True)
        tools.wait_serial(run, "Datenlaufwerk unter /data eingebunden.", 5)
        guest.to_textshell()
        text = write_file(guest, spec["write"], spec["id"])
        guest.power(spec["action"], expect_exit=True)
    finally:
        guest.kill()
    fat = extract_sparse(data, find_partition(data, None), case / "data-part.img")
    check_fat(fat)
    content = subprocess.run(["mtype", "-i", str(fat), "::/" + Path(spec["write"]).name], capture_output=True, text=True,
                             check=False, env=dict(os.environ, MTOOLS_SKIP_CHECK="1"))
    if text not in content.stdout:
        raise RuntimeError(f"Geschriebene Datei fehlt oder falsch auf der Datenplatte: {content.stdout!r} {content.stderr!r}")
    print(f"  {spec['write']} auf der Platte: {text}", flush=True)


def root_checks(image: Path, case: Path, spec: dict, written: str) -> Path:
    root = extract_sparse(image, find_partition(image, ROOT_TYPE), case / "root-part.img")
    esp = extract_sparse(image, find_partition(image, ESP_TYPE), case / "esp-part.img")
    if "root-clean" in spec["checks"]:
        check_clean(root)
    if "e2fsck" in spec["checks"]:
        check_ext2(root)
    if "fat-esp" in spec["checks"]:
        check_fat(esp)
    expect_root_file(root, spec["write"], written)
    return root


def expect_root_file(root: Path, path: str, text: str | None) -> None:
    """text=None: die Datei darf auf dem Root nicht stehen (Schreibversuch waehrend der Root-Schreibsperre)."""
    content = subprocess.run(["debugfs", "-R", f"cat {path}", str(root)], capture_output=True, text=True, check=False)
    if text is None:
        if content.stdout.strip():
            raise RuntimeError(f"{path} steht auf dem Root, obwohl Root schreibgesperrt war: {content.stdout[:200]!r}")
        print(f"  {path} fehlt auf dem Root (Schreibsperre griff)", flush=True)
        return
    if text not in content.stdout:
        raise RuntimeError(f"{path} fehlt oder falsch auf dem Root: {content.stdout[:200]!r} {content.stderr[-200:]!r}")
    print(f"  {path} auf dem Root: {text}", flush=True)


def run_root(args, case: Path, spec: dict) -> None:
    image = case / "target.img"
    copy_sparse(args.root_disk, image)
    run = storage.new_work_dir(case / "lauf")
    reboot_exits = spec.get("rebootExits", False)
    guest = Guest(args, run, [image], args.iso_normal, from_disk=True, reboot_exits=reboot_exits)
    try:
        guest.ready(args.timeout, extra=ROOT_READY)
        storage.login(guest.qmp, run, first=args.root_first_setup)
        guest.to_textshell()
        written = write_file(guest, spec["write"], spec["id"])
        if "s_state" in spec["checks"]:
            # Gegenprobe: waehrend Root beschreibbar eingehaengt ist, muss am Rohimage 0 stehen, sonst bewiese die 1 nach
            # dem Neustart nichts. Nur zwei Bytes lesen (kein dumpe2fs am laufenden Gast, Spec §4).
            running = read_s_state(image)
            if running != 0:
                raise RuntimeError(f"s_state waehrend des Betriebs = {running}, erwartet 0 (Root eingehaengt)")
            print("  s_state waehrend des Betriebs = 0", flush=True)
        if spec["action"] == "shutdown" or reboot_exits:
            guest.power(spec["action"], expect_exit=True)
        else:
            # P3b: echter Neustart; derselbe QEMU-Prozess startet erneut im Root-Profil.
            start = guest.mark()
            guest.power("reboot", expect_exit=False)
            guest.ready(args.timeout, start, extra=ROOT_READY)
            storage.login(guest.qmp, run, first=False)
            guest.to_textshell()
            guest.power(spec["afterReboot"], expect_exit=True)
    finally:
        guest.kill()
    if "s_state" in spec["checks"]:
        state = read_s_state(image)
        if state != 1:
            raise RuntimeError(f"s_state am Rohimage = {state}, erwartet 1 (sauber)")
        print("  s_state am Rohimage = 1", flush=True)
    root_checks(image, case, spec, written)


def run_fault(args, case: Path, spec: dict) -> None:
    """P4 (Fehlerprobe-Build -p:ZehOSPowerFaultProbe=true): powerfault schaltet Punkte scharf, das erste shutdown muss
    abbrechen und die Shell bedienbar lassen; ein zweites shutdown ohne Probe faehrt sauber herunter (Spec 7b §2)."""
    image = case / "target.img"
    copy_sparse(args.root_disk, image)
    run = storage.new_work_dir(case / "lauf")
    guest = Guest(args, run, [image], args.iso_normal, from_disk=True, reboot_exits=False)
    points = spec["fault"].split()
    try:
        guest.ready(args.timeout, extra=ROOT_READY)
        storage.login(guest.qmp, run, first=args.root_first_setup)
        guest.to_textshell()
        before = write_file(guest, spec["write"], spec["id"] + "-vor")
        guest.qmp.line("powerfault " + spec["fault"])
        time.sleep(2)
        start = guest.mark()
        guest.power(spec["action"], expect_exit=False)
        for point in points:
            # Fires() protokolliert jeden Punkt genau einmal; fehlt die Zeile, hat die Probe nicht gegriffen.
            tools.wait_serial(run, f"POWER_FAULT {point}", 60, start)
        try:
            guest.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            pass
        else:
            raise RuntimeError(f"Gast endete trotz Fehlerpunkt {spec['fault']} (Exit {guest.process.returncode})")
        guest.qmp.shot(run / "nach-fehler.png")
        print(f"  POWER_FAULT {', '.join(points)} gemeldet, Gast laeuft weiter", flush=True)
        # Shellausgaben erscheinen nicht seriell; die Fehlermeldung (etwa "Updates gesperrt") bleibt Sichtpruefung.
        print(f"BILD {spec['id']} Meldung der abgebrochenen Energieaktion: Sichtpruefung {run / 'nach-fehler.png'}", flush=True)
        if "s_state-running" in spec["checks"]:
            # Nach der Ruecknahme ist Root wieder beschreibbar eingehaengt: auf dem Rohimage muss 0 stehen (P2-1).
            state = read_s_state(image)
            if state != 0:
                raise RuntimeError(f"s_state nach der Ruecknahme = {state}, erwartet 0")
            print("  s_state nach der Ruecknahme = 0", flush=True)
        # Bedienbarkeit: die Shell nimmt den naechsten Befehl an; unter Schreibsperre darf er nichts auf Root schreiben.
        after = write_file(guest, spec["writeAfterFault"], spec["id"] + "-nach")
        if spec["rootLockedAfterFault"]:
            # Die fehlende Datei allein belegt die Sperre nicht (Eingabe koennte fehlen); die Shell zeigt cause='StorageBusy'.
            print(f"BILD {spec['id']} Schreibversuch unter Root-Sperre: Sichtpruefung "
                  f"{run / ('write-' + Path(spec['writeAfterFault']).stem + '.png')}", flush=True)
        guest.power(spec["action"], expect_exit=True)
    finally:
        guest.kill()
    root = root_checks(image, case, spec, before)
    expect_root_file(root, spec["writeAfterFault"], None if spec["rootLockedAfterFault"] else after)
    state = read_s_state(image)
    if state != 1:
        raise RuntimeError(f"s_state am Rohimage = {state}, erwartet 1 (zweites shutdown sauber)")
    print("  s_state am Rohimage = 1", flush=True)


def seed_root(image: Path, case: Path, repo: Path, target: str) -> None:
    """Legt das signierte Paketverzeichnis offline auf die Root-Partition der Kopie (debugfs, dann zurueck an ihren Platz)."""
    part = find_partition(image, ROOT_TYPE)
    root = extract_sparse(image, part, case / "root-seed.img")
    commands = [f"mkdir {target}"]
    for path in sorted(repo.rglob("*")):
        inside = f"{target}/{path.relative_to(repo).as_posix()}"
        commands.append(f"mkdir {inside}" if path.is_dir() else f"write {path.resolve()} {inside}")
    script = case / "seed.debugfs"
    script.write_text("\n".join(commands) + "\n", encoding="utf-8")
    result = subprocess.run(["debugfs", "-w", "-f", str(script), str(root)], capture_output=True, text=True, check=False)
    if result.returncode != 0 or "rror" in result.stderr:
        raise RuntimeError(f"debugfs konnte das Paket nicht ablegen: {result.stderr[-400:]!r}")
    subprocess.run(["dd", f"if={root}", f"of={image}", "bs=1M", f"seek={part['start'] * SECTOR}", "oflag=seek_bytes",
                    "conv=notrunc,sparse", "status=none"], check=True)
    root.unlink()


def run_package(args, case: Path, spec: dict) -> None:
    """P6: eine laufende Paket-App laesst shutdown mit PowerBusy abbrechen (Spec 7b §2.4); nach dem Schliessen sauber."""
    package = spec["package"]
    image = case / "target.img"
    copy_sparse(args.root_disk, image)
    seed_root(image, case, args.package_repo, package["seedDir"])
    run = storage.new_work_dir(case / "lauf")
    guest = Guest(args, run, [image], args.iso_normal, from_disk=True, reboot_exits=False)
    app = package["id"]
    try:
        guest.ready(args.timeout, extra=ROOT_READY)
        storage.login(guest.qmp, run, first=args.root_first_setup)
        guest.to_textshell()
        written = write_file(guest, spec["write"], spec["id"])
        mark = guest.mark()
        guest.qmp.line(f"pkg install-local {package['seedDir']}/{app}/{package['version']}")
        tools.wait_serial(run, f"PKG {app} {package['version']} installiert.", 600, mark)
        to_desktop(guest)
        mark = guest.mark()
        launch(guest, app)
        tools.wait_serial(run, f"APP {app} erstes Bild", 180, mark)
        time.sleep(3)
        guest.qmp.shot(run / "app-laeuft.png")
        guest.to_textshell()
        guest.power(spec["action"], expect_exit=False)
        try:
            guest.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            pass
        else:
            raise RuntimeError(f"Gast endete trotz laufender Paket-App {app}")
        guest.qmp.shot(run / "nach-shutdown.png")
        print(f"  shutdown bei laufender App {app}: Gast laeuft weiter", flush=True)
        # Shellausgaben erscheinen nicht seriell; cause='PowerBusy' bleibt Sichtpruefung.
        print(f"BILD {spec['id']} Meldung bei laufender App: Sichtpruefung {run / 'nach-shutdown.png'}", flush=True)
        to_desktop(guest)
        mark = guest.mark()
        guest.qmp.click(*FIRST_WINDOW_CLOSE, *SCREEN)
        tools.wait_serial(run, f"APP {app} beendet", 60, mark)
        print(f"  {app} ueber das Schliessen-Feld beendet", flush=True)
        guest.to_textshell()
        guest.power(spec["action"], expect_exit=True)
    finally:
        guest.kill()
    root_checks(image, case, spec, written)
    state = read_s_state(image)
    if state != 1:
        raise RuntimeError(f"s_state am Rohimage = {state}, erwartet 1")
    print("  s_state am Rohimage = 1", flush=True)


def to_desktop(guest: Guest) -> None:
    start = guest.mark()
    guest.qmp.tap("f4")
    tools.wait_serial(guest.run, "Desktop ist bereit.", 30, start)
    time.sleep(1)


def launch(guest: Guest, name: str) -> None:
    """Startmenue (Alt+F1), Suche nach dem Namen, Enter; wie test-wasm-doom-desktop.py."""
    guest.qmp.tap("alt", "f1")
    time.sleep(0.5)
    guest.qmp.text(name)
    time.sleep(0.5)
    guest.qmp.tap("ret")


# Testbuilds haengen eine Kennzeichnung an, etwa " [UPDATE-TESTSCHLUESSEL]".
BOOT_VERSION = re.compile(r"Boot: ZehOS (\S+)(?: \[[^\]]*\])? startet\.")


def booted_version(guest: Guest, start: int) -> str:
    log = (guest.run / "serial.log").read_text(encoding="utf-8", errors="replace")[start:]
    match = BOOT_VERSION.search(log)
    if not match:
        raise RuntimeError("Keine Zeile 'Boot: ZehOS <version> startet.' im seriellen Protokoll")
    return match.group(1)


def esp_sha256(esp: Path, name: str) -> str:
    data = subprocess.run(["mtype", "-i", str(esp), "::" + name], capture_output=True, check=True,
                          env=dict(os.environ, MTOOLS_SKIP_CHECK="1")).stdout
    return hashlib.sha256(data).hexdigest()


def manifest_value(directory: Path, key: str) -> str:
    for line in (directory / "update-manifest.txt").read_text(encoding="ascii").splitlines():
        if line.startswith(key + "="):
            return line[len(key) + 1:]
    raise RuntimeError(f"update-manifest.txt ohne {key}=")


def run_update(args, case: Path, spec: dict) -> None:
    """P5: update install aus einem Ordner auf Root, Neustart startet B; offline ESP pruefen; Rueckfalleintrag startet A."""
    update = spec["update"]
    version_b, kernel_b = manifest_value(args.update_dir, "version"), manifest_value(args.update_dir, "kernel")
    hash_a, hash_b = (hashlib.sha256(path.read_bytes()).hexdigest() for path in (args.kernel_a, args.update_dir / kernel_b))
    image = case / "target.img"
    copy_sparse(args.root_disk, image)
    seed_root(image, case, args.update_dir, update["seedDir"])
    run = storage.new_work_dir(case / "lauf")
    guest = Guest(args, run, [image], args.iso_normal, from_disk=True, reboot_exits=False)
    try:
        guest.ready(args.timeout, extra=ROOT_READY)
        version_a = booted_version(guest, 0)
        print(f"  gestartet: {version_a}", flush=True)
        storage.login(guest.qmp, run, first=args.root_first_setup)
        guest.to_textshell()
        written = write_file(guest, spec["write"], spec["id"])
        guest.qmp.line(f"update install {update['seedDir']}/{kernel_b}")
        time.sleep(30)  # Ersetzen des Kernels auf /boot/efi; Ergebnis nur auf dem Bildschirm
        guest.qmp.shot(run / "update-install.png")
        print(f"BILD {spec['id']} update install: Sichtpruefung {run / 'update-install.png'}", flush=True)
        start = guest.mark()
        guest.power("reboot", expect_exit=False)
        guest.ready(args.timeout, start, extra=ROOT_READY)
        after = booted_version(guest, start)
        if not after.startswith(version_b):
            raise RuntimeError(f"Nach dem Neustart laeuft {after}, erwartet {version_b}")
        print(f"  nach Neustart: {after}", flush=True)
        storage.login(guest.qmp, run, first=False)
        guest.to_textshell()
        guest.power("shutdown", expect_exit=True)
    finally:
        guest.kill()
    root_checks(image, case, spec, written)
    esp = extract_sparse(image, find_partition(image, ESP_TYPE), case / "esp-update.img")
    for name, expected, label in (("/boot/ProjektZeh.elf", hash_b, "B"), ("/boot/ProjektZeh.elf.bak", hash_a, "A")):
        actual = esp_sha256(esp, name)
        if actual != expected:
            raise RuntimeError(f"{name} auf der ESP ist nicht Kernel {label} ({actual[:12]} statt {expected[:12]})")
        print(f"  ESP {name} = Kernel {label}", flush=True)

    # Rueckfall: im Limine-Menue den Eintrag der vorherigen Version waehlen. Jeder Tastendruck haelt den Zeitablauf an;
    # Tasten vor dem Menue (OVMF) gehen verloren, deshalb ab dem Start mehrfach. Limine liest die Pfeiltasten ueber
    # UEFI ConIn nur mit gehaltener Taste (send-key mit hold-time); ein kurzes input-send-event kommt nicht an.
    run = storage.new_work_dir(case / "rueckfall")
    guest = Guest(args, run, [image], args.iso_normal, from_disk=True, reboot_exits=False)
    try:
        qmp = tools.Qmp(guest.port)
        for _ in range(16):
            qmp.command("send-key", {"keys": [{"type": "qcode", "data": "down"}], "hold-time": 150})
            time.sleep(0.2)
        qmp.shot(run / "limine-auswahl.png")
        print(f"BILD {spec['id']} Limine-Auswahl vor Enter: Sichtpruefung {run / 'limine-auswahl.png'}", flush=True)
        qmp.command("send-key", {"keys": [{"type": "qcode", "data": "ret"}], "hold-time": 150})
        guest.qmp = qmp
        guest.ready(args.timeout, extra=ROOT_READY)
        fallback = booted_version(guest, 0)
        if fallback != version_a:
            raise RuntimeError(f"Rueckfalleintrag startet {fallback}, erwartet {version_a}")
        print(f"  Rueckfalleintrag startet {fallback} (Kernel A)", flush=True)
        storage.login(guest.qmp, run, first=False)
        guest.to_textshell()
        guest.power("shutdown", expect_exit=True)
    finally:
        guest.kill()
    check_clean(extract_sparse(image, find_partition(image, ROOT_TYPE), case / "root-rueckfall.img"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--work", required=True, type=Path, help="neuer Arbeitsordner")
    parser.add_argument("--cases", default="P1,P2,P3a,P3b")
    parser.add_argument("--iso-normal", type=Path, required=True)
    parser.add_argument("--iso-recovery", type=Path, help="Security-Recovery-Build fuer disk init (P1)")
    parser.add_argument("--root-disk", type=Path, help="installierte Root-Platte (P2/P3), wird nur kopiert")
    parser.add_argument("--root-first-setup", action="store_true",
                        help="Root-Platte ohne Konto: Ersteinrichtung statt Anmeldung beim ersten Start")
    parser.add_argument("--package-repo", type=Path,
                        help="P6: signiertes Paketverzeichnis (<id>/<version>, build-package.py package), passend zu "
                             "-p:ZehOSPackageKeyPath des Payloads")
    parser.add_argument("--update-dir", type=Path,
                        help="P5: Ordner mit update-manifest.txt, update-manifest.sig und Kernel B (Update-Testschluessel)")
    parser.add_argument("--kernel-a", type=Path, help="P5: ELF des installierten Kernels A (Vergleich mit .bak)")
    parser.add_argument("--ovmf-code", type=Path)
    parser.add_argument("--ovmf-vars", type=Path)
    parser.add_argument("--accel", choices=["tcg", "kvm"], default="kvm")
    parser.add_argument("--timeout", type=float, default=300)
    args = parser.parse_args()
    wanted = [name.strip() for name in args.cases.split(",") if name.strip()]
    cases = {case["id"]: case for case in load_cases()}
    unknown = [name for name in wanted if name not in cases]
    if unknown:
        parser.error(f"Unbekannte Faelle: {unknown}")
    if any(cases[name]["disk"] == "root" for name in wanted) and not all((args.root_disk, args.ovmf_code, args.ovmf_vars)):
        parser.error("Root-Faelle brauchen --root-disk, --ovmf-code und --ovmf-vars")
    if "P5" in wanted and not (args.update_dir and args.kernel_a):
        parser.error("P5 braucht --update-dir und --kernel-a")
    if "P6" in wanted and not args.package_repo:
        parser.error("P6 braucht --package-repo")
    if "P1" in wanted and not args.iso_recovery:
        parser.error("P1 braucht --iso-recovery")
    work = storage.new_work_dir(args.work)
    failed = []
    for name in wanted:
        spec = cases[name]
        print(f"== {name}: {spec['title']}", flush=True)
        case = storage.new_work_dir(work / name)
        try:
            (run_p1 if spec["disk"] == "data" else run_fault if "fault" in spec
             else run_package if "package" in spec else run_update if "update" in spec else run_root)(args, case, spec)
            print(f"PASS {name}", flush=True)
        except Exception as error:  # Jeder Fall meldet seine Ursache; die naechsten laufen weiter.
            failed.append(name)
            print(f"FAIL {name}: {error}", flush=True)
    for name in json.loads((ROOT / "scripts/power_cases.json").read_text(encoding="utf-8"))["open"]:
        print(f"OFFEN {name}", flush=True)
    print("OFFEN: VMware, TCG", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
