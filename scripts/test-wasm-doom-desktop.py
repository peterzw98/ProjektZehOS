#!/usr/bin/env python3
"""Installiert Doom per pkg aus einem lokalen HTTPS-Repository und bedient es im ZehOS-Desktop per QMP.

Das ISO muss mit -p:ZehOSWasm=true, -p:ZehOSPackageKeyPath (Testschluessel) und -p:ZehOSPackageTestCaPath (Test-CA)
gebaut sein. Das Datenlaufwerk ist ein leerer Ordner, den QEMU als FAT-Laufwerk mit verworfenen Schreibzugriffen
(vvfat, snapshot=on) einbindet; vorhandene Datenimages werden nicht verwendet. Ergebnisse erscheinen seriell als
"PKG ...", "NET ..." und "APP <id> ..." (erstes Bild, Ende); das Skript wartet auf diese Zeilen statt auf feste
Zeiten. Die Bedienung wird anhand der Bildschirmfotos ausgewertet.

Szenarien:
  full      Installation, neues Spiel, laufen/schiessen, Textshell (F4/Esc), Schliessen ueber X, Taskmanager,
            --starts Starts (Standard 3, fuer die Speicherpruefung 10) mit Beenden ueber das Doom-Menue, Taskmanager.
  packages  Installation, Entfernen waehrend Doom laeuft (abgelehnt), Entfernen, Neuinstallation, Neustart mit
            Anmeldung und Start nach dem Neustart, Aktualisierung auf --upgrade-repo.
  corrupt   Server liefert eine veraenderte WAD; die Installation muss mit Pruefsummenfehler scheitern.

Mehrere Szenarien (--scenario full packages corrupt) laufen gleichzeitig in eigenen VMs; Szenario i verwendet
--port + i und --repo-port + i.
"""

import argparse
import http.server
import json
import shutil
import socket
import ssl
import struct
import subprocess
import sys
import threading
import time
import uuid
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# Nur fuer diese Wegwerf-VM; vvfat verwirft alle Schreibzugriffe.
TEST_USER, TEST_PASSWORD = "tester", "testkennwort1"
# Deutsche Tastaturbelegung des Gasts (DEStandardLayout); QMP sendet physische US-Tastenpositionen.
SHIFTED = {":": "dot", "/": "7", "_": "slash"}
PLAIN = {" ": "spc", ".": "dot", "-": "slash", "y": "z", "z": "y"}


class Qmp:
    def __init__(self, port: int):
        deadline = time.monotonic() + 60
        while True:
            try:
                self.stream = socket.create_connection(("127.0.0.1", port), timeout=60).makefile("rwb")
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise
                time.sleep(0.5)
        self.stream.readline()
        self.command("qmp_capabilities")

    def command(self, name: str, arguments: dict | None = None) -> dict:
        message = {"execute": name, **({"arguments": arguments} if arguments else {})}
        self.stream.write((json.dumps(message) + "\n").encode())
        self.stream.flush()
        while True:
            reply = json.loads(self.stream.readline())
            if "return" in reply or "error" in reply:
                if "error" in reply:
                    raise RuntimeError(f"QMP {name}: {reply['error']}")
                return reply

    def key(self, qcode: str, down: bool) -> None:
        self.command("input-send-event", {"events": [{"type": "key", "data": {"down": down, "key": {"type": "qcode", "data": qcode}}}]})

    def tap(self, *qcodes: str, hold: float = 0.08) -> None:
        for qcode in qcodes:
            self.key(qcode, True)
        time.sleep(hold)
        for qcode in reversed(qcodes):
            self.key(qcode, False)
        time.sleep(0.15)

    def hold(self, qcode: str, seconds: float) -> None:
        self.key(qcode, True)
        time.sleep(seconds)
        self.key(qcode, False)

    def text(self, value: str) -> None:
        for character in value:
            if character in SHIFTED:
                self.tap("shift", SHIFTED[character])
            elif character.isupper():
                self.tap("shift", PLAIN.get(character.lower(), character.lower()))
            else:
                self.tap(PLAIN.get(character, character))

    def line(self, value: str) -> None:
        self.text(value)
        self.tap("ret")

    def click(self, x: int, y: int, width: int, height: int) -> None:
        position = [{"type": "abs", "data": {"axis": "x", "value": int(x * 32767 / (width - 1))}},
                    {"type": "abs", "data": {"axis": "y", "value": int(y * 32767 / (height - 1))}}]
        self.command("input-send-event", {"events": position})
        time.sleep(0.2)
        for down in (True, False):
            self.command("input-send-event", {"events": [{"type": "btn", "data": {"down": down, "button": "left"}}]})
            time.sleep(0.1)

    def shot(self, path: Path) -> tuple[int, int]:
        ppm = path.with_suffix(".ppm")
        self.command("screendump", {"filename": str(ppm)})
        time.sleep(0.5)
        header, size, _, pixels = ppm.read_bytes().split(b"\n", 3)
        width, height = map(int, size.split())
        raw = b"".join(b"\0" + pixels[row * width * 3:(row + 1) * width * 3] for row in range(height))

        def chunk(kind: bytes, body: bytes) -> bytes:
            return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

        path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                         + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))
        ppm.unlink()
        return width, height


class Repository:
    """HTTPS-Server fuer das Testrepository; das bediente Verzeichnis laesst sich waehrend des Laufs wechseln."""

    def __init__(self, directory: Path, tls: Path, port: int, corrupt: bool):
        self.directory = directory
        repository = self

        class Handler(http.server.SimpleHTTPRequestHandler):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, directory=str(repository.directory), **kwargs)

            def log_message(self, format, *args):
                pass

            def copyfile(self, source, target):
                data = source.read()
                if corrupt and self.path.endswith("/freedoom1.wad"):
                    data = data[:100] + bytes([data[100] ^ 1]) + data[101:]
                target.write(data)

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
        self.port = self.server.server_address[1]
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(tls / "server.pem", tls / "server.key")
        self.server.socket = context.wrap_socket(self.server.socket, server_side=True)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()


def start_qemu(iso: Path, disk: Path, work: Path, port: int, accel: str) -> subprocess.Popen:
    qemu = Path(shutil.which("qemu-system-x86_64") or Path.home() / "AppData/Local/Cosmos/Tools/qemu/bin/qemu-system-x86_64.exe")
    share = qemu.parent.parent / "share" / "qemu"
    # Unter WHPX bricht der Gast mit -cpu max oder host sofort ab (Unexpected VP exit code 4); das Standardmodell laeuft.
    cpu = ["-cpu", "max"] if accel == "tcg" else []
    arguments = [str(qemu), "-L", str(share), "-accel", accel, "-machine", "q35", *cpu, "-m", "512M",
                 "-vga", "std", "-boot", "d", "-cdrom", str(iso), "-display", "none",
                 "-serial", f"file:{work / 'serial.log'}", "-monitor", "none",
                 "-qmp", f"tcp:127.0.0.1:{port},server=on,wait=off", "-action", "shutdown=poweroff,reboot=reset",
                 "-drive", f"file=fat:{disk},format=raw,if=none,id=appdisk,snapshot=on",
                 "-device", "ahci,id=ahci", "-device", "ide-hd,drive=appdisk,bus=ahci.0",
                 "-netdev", "user,id=net0", "-device", "e1000e,netdev=net0",
                 "-device", "qemu-xhci,id=tabletbus", "-device", "usb-tablet,bus=tabletbus.0"]
    return subprocess.Popen(arguments, stdout=subprocess.DEVNULL, stderr=(work / "stderr.log").open("w"))


def wait_serial(work: Path, text: str, timeout: float, start: int = 0) -> int:
    """Wartet auf text ab Position start im seriellen Protokoll und liefert die Position danach."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        log = work / "serial.log"
        content = log.read_text(encoding="utf-8", errors="replace") if log.exists() else ""
        found = content.find(text, start)
        if found >= 0:
            return found + len(text)
        # Die erste vollstaendige Ergebniszeile derselben Art (PKG, NET, APP <id>) ohne den Text ist ein Fehlschlag.
        if text.startswith(("PKG ", "NET ", "APP ")):
            prefix = text[:text.index(" ", 4) + 1] if text.startswith("APP ") else text[:4]
            failure = content.find(prefix, start)
            end = content.find("\n", failure) if failure >= 0 else -1
            if end > 0:
                raise RuntimeError(f"Unerwartetes Ergebnis: {content[failure:end]}")
        time.sleep(1)
    raise TimeoutError(f"Kein '{text}' im seriellen Protokoll.")


def free_port() -> int:
    # shortcut: der Port kann bis zum Start von QEMU neu vergeben werden; bei Bedarf --port angeben.
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def run_parallel(args: argparse.Namespace) -> int:
    """Startet jedes Szenario als eigenen Prozess mit eigener VM und eigenen Ports."""
    children = []
    for index, scenario in enumerate(args.scenario):
        command = [sys.executable, __file__, "--iso", str(args.iso), "--scenario", scenario, "--accel", args.accel,
                   "--starts", str(args.starts), "--port", str(args.port and args.port + index),
                   "--repo-port", str(args.repo_port and args.repo_port + index)]
        for name in ("repo", "upgrade_repo", "tls", "source"):
            if getattr(args, name) is not None:
                command += ["--" + name.replace("_", "-"), str(getattr(args, name))]
        print(f"{scenario}: gestartet", flush=True)
        children.append((scenario, time.monotonic(), subprocess.Popen(command, stdout=subprocess.PIPE,
                                                                       stderr=subprocess.STDOUT, text=True)))
    # Ausgaben gleichzeitig abholen, damit die Laufzeit je Szenario stimmt und volle Pipes nicht blockieren.
    finished = {}

    def collect(scenario: str, started: float, child: subprocess.Popen) -> None:
        output, _ = child.communicate()
        finished[scenario] = (output, time.monotonic() - started)

    readers = [threading.Thread(target=collect, args=entry) for entry in children]
    for reader in readers:
        reader.start()
    for reader in readers:
        reader.join()
    for scenario, _, child in children:
        output, seconds = finished[scenario]
        print(f"--- {scenario}: Exitcode {child.returncode} nach {seconds:.0f} s\n{output}", flush=True)
    return 1 if any(child.returncode != 0 for _, _, child in children) else 0


def serial_size(work: Path) -> int:
    log = work / "serial.log"
    return len(log.read_text(encoding="utf-8", errors="replace")) if log.exists() else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--repo", type=Path, help="mit scripts/build-package.py erzeugtes Repository")
    parser.add_argument("--upgrade-repo", type=Path, help="Repository mit neuerer Doom-Version (Szenario packages)")
    parser.add_argument("--tls", type=Path, help="Ordner mit server.pem und server.key der Test-CA")
    parser.add_argument("--source", help="vorhandenes Repository (https://.../) statt des lokalen Testservers")
    parser.add_argument("--scenario", nargs="+", choices=["full", "packages", "corrupt"], default=["full"])
    parser.add_argument("--port", type=int, default=0, help="QMP-Port; 0 waehlt einen freien")
    parser.add_argument("--repo-port", type=int, default=0, help="Port des Testservers; 0 waehlt einen freien")
    parser.add_argument("--accel", default="tcg", help="QEMU-Beschleuniger: tcg oder whpx (Windows)")
    parser.add_argument("--starts", type=int, default=3, help="Doom-Starts am Ende von full (Speicherpruefung: 10)")
    args = parser.parse_args()
    if args.source is None and (args.repo is None or args.tls is None):
        parser.error("ohne --source sind --repo und --tls noetig")
    if len(args.scenario) > 1:
        return run_parallel(args)
    scenario = args.scenario[0]

    work = ROOT / ".tools" / "wasm-desktop" / uuid.uuid4().hex
    (work / "disk").mkdir(parents=True)
    repository = None if args.source else Repository(args.repo.resolve(), args.tls.resolve(), args.repo_port, scenario == "corrupt")
    source = args.source or f"https://10.0.2.2:{repository.port}/"
    port = args.port or free_port()
    started = time.monotonic()
    guest = start_qemu(args.iso.resolve(), work / "disk", work, port, args.accel)
    try:
        wait_serial(work, "Desktop ist bereit", 180)
        print(f"Desktop bereit: {time.monotonic() - started:.0f} s", flush=True)
        qmp = Qmp(port)
        time.sleep(2)
        step = iter(range(1, 1000))

        def shot(name: str) -> tuple[int, int]:
            return qmp.shot(work / f"{next(step):02d}-{name}.png")

        width, height = shot("boot")
        mark = serial_size(work)
        for value in (TEST_USER, TEST_PASSWORD, TEST_PASSWORD):
            qmp.line(value)
            time.sleep(1)
        # Die Ersteinrichtung (PBKDF2) dauert unter TCG unterschiedlich lange; erst danach tippen.
        wait_serial(work, "Benutzer angemeldet.", 300, mark)
        print(f"angemeldet: {time.monotonic() - started:.0f} s", flush=True)
        time.sleep(5)

        def shell(*lines: str) -> None:
            # Esc wechselt vom Desktop in die Textshell derselben Sitzung.
            qmp.tap("esc")
            time.sleep(4)
            # Leerzeile zuerst: der Wechsel in die Textshell kann unter Last laenger dauern.
            qmp.tap("ret")
            time.sleep(1)
            for value in lines:
                qmp.line(value)
                time.sleep(1)

        def pkg(command: str, expected: str, timeout: float = 900) -> None:
            mark = serial_size(work)
            started = time.monotonic()
            qmp.line("pkg " + command)
            wait_serial(work, "PKG " + expected, timeout, mark)
            print(f"pkg {command}: {time.monotonic() - started:.0f} s", flush=True)

        def dhcp() -> None:
            # Getippte Zeilen koennen unter Last verloren gehen; ohne NET-Ergebnis erneut anstossen.
            for attempt in range(3):
                mark = serial_size(work)
                qmp.line("net dhcp")
                try:
                    wait_serial(work, "NET DHCP IPv4", 90, mark)
                    return
                except (RuntimeError, TimeoutError):
                    shot("dhcp-failed")
                    if attempt == 2:
                        raise

        def update_catalog() -> None:
            for attempt in range(3):
                try:
                    pkg("update", "Katalog geladen", 120)
                    return
                except RuntimeError as error:
                    shot("update-failed")
                    if not ("IPv4" in str(error) or "Paketquelle" in str(error)) or attempt == 2:
                        raise
                    # Eine verlorene Eingabe von pkg source oder net dhcp nachholen.
                    qmp.line(f"pkg source {source}")
                    time.sleep(1)
                    dhcp()
                except TimeoutError:
                    shot("update-timeout")
                    raise

        def launch(name: str) -> None:
            # Das Suchfeld behaelt seinen Text; vor jeder Suche leeren.
            qmp.tap("alt", "f1")
            time.sleep(1)
            for _ in range(40):
                qmp.tap("backspace", hold=0.02)
            qmp.text(name)
            time.sleep(1)
            qmp.tap("ret")

        def start_doom() -> None:
            mark = serial_size(work)
            launch("doom")
            try:
                wait_serial(work, "APP doom erstes Bild", 180, mark)
            except TimeoutError:
                shot("doom-timeout")
                raise
            # Kurz weiterlaufen lassen, damit das Bildschirmfoto mehr als das erste Bild zeigt.
            time.sleep(3)

        def quit_doom() -> None:
            mark = serial_size(work)
            qmp.tap("backspace")
            time.sleep(1)
            qmp.tap("up")
            time.sleep(0.5)
            qmp.tap("ret")
            time.sleep(1)
            qmp.tap("y")
            wait_serial(work, "APP doom beendet", 60, mark)
            time.sleep(1)

        shell(f"pkg source {source}")
        dhcp()
        update_catalog()
        if scenario == "corrupt":
            pkg("install doom", "Pruefsumme falsch: freedoom1.wad")
            qmp.line("pkg list")
            time.sleep(2)
            shot("corrupt-rejected")
            return 0
        pkg("install doom", "doom 1.0.0 installiert.")
        qmp.line("pkg list")
        time.sleep(2)
        shot("installed")
        qmp.tap("f4")
        time.sleep(3)

        if scenario == "packages":
            start_doom()
            shot("doom-running")
            shell("pkg remove doom")
            shot("remove-while-running")
            qmp.tap("f4")
            time.sleep(3)
            quit_doom()
            shell("pkg remove doom", "pkg list")
            shot("removed")
            qmp.tap("f4")
            time.sleep(2)
            launch("doom")
            time.sleep(3)
            shot("menu-after-remove")
            qmp.tap("esc")
            time.sleep(1)
            shell()
            pkg("install doom", "doom 1.0.0 installiert.")
            mark = serial_size(work)
            qmp.line("reboot")
            wait_serial(work, "Desktop ist bereit", 240, mark)
            time.sleep(2)
            shot("after-reboot")
            mark = serial_size(work)
            for value in (TEST_USER, TEST_PASSWORD):
                qmp.line(value)
                time.sleep(1)
            wait_serial(work, "Benutzer angemeldet.", 300, mark)
            time.sleep(5)
            start_doom()
            shot("doom-after-reboot")
            quit_doom()
            if args.upgrade_repo is not None:
                repository.directory = args.upgrade_repo.resolve()
                shell()
                dhcp()
                update_catalog()
                pkg("upgrade", "doom 1.1.0 aktualisiert.")
                qmp.line("pkg list")
                time.sleep(2)
                shot("upgraded")
                qmp.tap("f4")
                time.sleep(2)
                start_doom()
                shot("doom-upgraded")
            return 0

        start_doom()
        shot("doom-demo")
        qmp.tap("backspace")
        time.sleep(1.5)
        for _ in range(3):
            qmp.tap("ret")
            time.sleep(1.5)
        time.sleep(3)
        shot("e1m1-start")
        qmp.hold("up", 1.5)
        qmp.hold("right", 0.6)
        qmp.tap("ctrl")
        time.sleep(0.3)
        qmp.tap("ctrl")
        time.sleep(1)
        shot("moved-fired")
        qmp.tap("f4")
        time.sleep(10)
        qmp.tap("f4")
        time.sleep(3)
        shot("after-10s-textshell")
        qmp.tap("esc")
        time.sleep(3)
        shot("textshell-esc")
        qmp.tap("f4")
        time.sleep(3)
        qmp.tap("ctrl")
        time.sleep(1.5)
        shot("resumed-fired")
        # Doom ist das erste Fenster; sein Schliessen-Feld liegt bei 1920x1080 an fester Stelle.
        mark = serial_size(work)
        qmp.click(813, 100, width, height)
        # Belegt das Beenden und verhindert, dass die Ende-Marke in den naechsten Start faellt.
        wait_serial(work, "APP doom beendet", 60, mark)
        time.sleep(1)
        shot("closed-x")
        launch("taskmanager")
        time.sleep(6)
        shot("taskmanager-before")
        for run in range(1, args.starts + 1):
            start_doom()
            shot(f"start-{run:02d}")
            quit_doom()
        shot("taskmanager-after")
        return 0
    finally:
        if repository is not None:
            repository.server.shutdown()
        if guest.poll() is None:
            guest.kill()
            guest.wait()
        print(f"Bilder und Log: {work}")


if __name__ == "__main__":
    sys.exit(main())
