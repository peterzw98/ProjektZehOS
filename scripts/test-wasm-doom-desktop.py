#!/usr/bin/env python3
"""Installiert Doom per pkg aus einem lokalen HTTPS-Repository und bedient es im ZehOS-Desktop per QMP.

Das ISO muss mit -p:ZehOSWasm=true, -p:ZehOSPackageKeyPath (Testschluessel) und -p:ZehOSPackageTestCaPath (Test-CA)
gebaut sein. Das Datenlaufwerk ist ein leerer Ordner, den QEMU als FAT-Laufwerk mit verworfenen Schreibzugriffen
(vvfat, snapshot=on) einbindet; vorhandene Datenimages werden nicht verwendet. Paketergebnisse erscheinen seriell als
"PKG ..."; die Bedienung wird anhand der Bildschirmfotos ausgewertet.

Szenarien:
  full      Installation, neues Spiel, laufen/schiessen, Textshell (F4/Esc), Schliessen ueber X, Taskmanager,
            zehn Starts mit Beenden ueber das Doom-Menue, Taskmanager.
  packages  Installation, Entfernen waehrend Doom laeuft (abgelehnt), Entfernen, Neuinstallation, Neustart mit
            Anmeldung und Start nach dem Neustart, Aktualisierung auf --upgrade-repo.
  corrupt   Server liefert eine veraenderte WAD; die Installation muss mit Pruefsummenfehler scheitern.
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
# US-Tastaturbelegung des Gasts.
SHIFTED = {":": "semicolon", "_": "minus"}
PLAIN = {" ": "spc", ".": "dot", "/": "slash", "-": "minus"}


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
                self.tap("shift", character.lower())
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
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(tls / "server.pem", tls / "server.key")
        self.server.socket = context.wrap_socket(self.server.socket, server_side=True)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()


def start_qemu(iso: Path, disk: Path, work: Path, port: int) -> subprocess.Popen:
    qemu = Path(shutil.which("qemu-system-x86_64") or Path.home() / "AppData/Local/Cosmos/Tools/qemu/bin/qemu-system-x86_64.exe")
    share = qemu.parent.parent / "share" / "qemu"
    arguments = [str(qemu), "-L", str(share), "-accel", "tcg", "-machine", "q35", "-cpu", "max", "-m", "512M",
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
        failure = content.find("PKG ", start)
        if failure >= 0 and text.startswith("PKG ") and content.find("\n", failure) > 0:
            line = content[failure:content.find("\n", failure)]
            raise RuntimeError(f"Unerwartetes Paketergebnis: {line}")
        time.sleep(1)
    raise TimeoutError(f"Kein '{text}' im seriellen Protokoll.")


def serial_size(work: Path) -> int:
    log = work / "serial.log"
    return len(log.read_text(encoding="utf-8", errors="replace")) if log.exists() else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--repo", type=Path, required=True, help="mit scripts/build-package.py erzeugtes Repository")
    parser.add_argument("--upgrade-repo", type=Path, help="Repository mit neuerer Doom-Version (Szenario packages)")
    parser.add_argument("--tls", type=Path, required=True, help="Ordner mit server.pem und server.key der Test-CA")
    parser.add_argument("--scenario", choices=["full", "packages", "corrupt"], default="full")
    parser.add_argument("--port", type=int, default=4445)
    parser.add_argument("--repo-port", type=int, default=8443)
    args = parser.parse_args()

    work = ROOT / ".tools" / "wasm-desktop" / uuid.uuid4().hex
    (work / "disk").mkdir(parents=True)
    repository = Repository(args.repo.resolve(), args.tls.resolve(), args.repo_port, args.scenario == "corrupt")
    guest = start_qemu(args.iso.resolve(), work / "disk", work, args.port)
    try:
        wait_serial(work, "ist bereit", 180)
        qmp = Qmp(args.port)
        time.sleep(4)
        step = iter(range(1, 1000))

        def shot(name: str) -> tuple[int, int]:
            return qmp.shot(work / f"{next(step):02d}-{name}.png")

        width, height = shot("boot")
        for value in (TEST_USER, TEST_PASSWORD, TEST_PASSWORD):
            qmp.line(value)
            time.sleep(1)
        time.sleep(25)

        def shell(*lines: str) -> None:
            # Esc wechselt vom Desktop in die Textshell derselben Sitzung.
            qmp.tap("esc")
            time.sleep(1.5)
            for value in lines:
                qmp.line(value)
                time.sleep(1)

        def pkg(command: str, expected: str, timeout: float = 900) -> None:
            mark = serial_size(work)
            started = time.monotonic()
            qmp.line("pkg " + command)
            wait_serial(work, "PKG " + expected, timeout, mark)
            print(f"pkg {command}: {time.monotonic() - started:.0f} s", flush=True)

        def launch(name: str) -> None:
            # Das Suchfeld behaelt seinen Text; vor jeder Suche leeren.
            qmp.tap("alt", "f1")
            time.sleep(1)
            for _ in range(40):
                qmp.tap("backspace", hold=0.02)
            qmp.text(name)
            time.sleep(1)
            qmp.tap("ret")

        def quit_doom() -> None:
            qmp.tap("backspace")
            time.sleep(1)
            qmp.tap("up")
            time.sleep(0.5)
            qmp.tap("ret")
            time.sleep(1)
            qmp.tap("y")
            time.sleep(5)

        shell("net dhcp")
        time.sleep(10)
        qmp.line(f"pkg source https://10.0.2.2:{args.repo_port}/")
        time.sleep(1)
        pkg("update", "Katalog geladen", 120)
        if args.scenario == "corrupt":
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

        if args.scenario == "packages":
            launch("doom")
            time.sleep(30)
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
            qmp.line("reboot")
            time.sleep(5)
            mark = serial_size(work)
            wait_serial(work, "ist bereit", 240, mark)
            time.sleep(25)
            shot("after-reboot")
            for value in (TEST_USER, TEST_PASSWORD):
                qmp.line(value)
                time.sleep(1)
            time.sleep(25)
            launch("doom")
            time.sleep(30)
            shot("doom-after-reboot")
            quit_doom()
            if args.upgrade_repo is not None:
                repository.directory = args.upgrade_repo.resolve()
                shell("net dhcp")
                time.sleep(10)
                pkg("update", "Katalog geladen", 120)
                pkg("upgrade", "doom 1.1.0 aktualisiert.")
                qmp.line("pkg list")
                time.sleep(2)
                shot("upgraded")
                qmp.tap("f4")
                time.sleep(2)
                launch("doom")
                time.sleep(30)
                shot("doom-upgraded")
            return 0

        launch("doom")
        time.sleep(30)
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
        qmp.click(813, 100, width, height)
        time.sleep(3)
        shot("closed-x")
        launch("taskmanager")
        time.sleep(6)
        shot("taskmanager-before")
        for run in range(1, 11):
            launch("doom")
            time.sleep(30)
            shot(f"start-{run:02d}")
            quit_doom()
        shot("taskmanager-after")
        return 0
    finally:
        repository.server.shutdown()
        if guest.poll() is None:
            guest.kill()
            guest.wait()
        print(f"Bilder und Log: {work}")


if __name__ == "__main__":
    sys.exit(main())
