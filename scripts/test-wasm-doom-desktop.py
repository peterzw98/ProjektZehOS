#!/usr/bin/env python3
"""Bedient Doom im ZehOS-Desktop per QMP und legt Bildschirmfotos jedes Schritts ab.

Das ISO muss mit -p:ZehOSWasm=true gebaut sein. Die App liegt in einem Ordner mit doom/doom.wasm und
doom/freedoom1.wad, den QEMU als FAT-Laufwerk mit verworfenen Schreibzugriffen (vvfat) einbindet; vorhandene
Datenimages werden nicht verwendet. Die Auswertung erfolgt anhand der Bilder, das Skript prueft keine Pixel.

Ablauf (--scenario full): Administrator anlegen, Doom starten, neues Spiel, laufen/drehen/schiessen, Pause in der
Textshell (F4), Esc/F4, Schliessen ueber X, Taskmanager, zehn Starts mit Beenden ueber das Doom-Menue, Taskmanager.
--scenario launch startet Doom nur einmal (fuer fehlende oder beschaedigte WAD).
"""

import argparse
import json
import shutil
import socket
import struct
import subprocess
import sys
import time
import uuid
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# Nur fuer diese Wegwerf-VM; vvfat verwirft alle Schreibzugriffe.
TEST_USER, TEST_PASSWORD = "tester", "testkennwort1"


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
            self.tap(character)

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


def start_qemu(iso: Path, disk: Path, work: Path, port: int) -> subprocess.Popen:
    qemu = Path(shutil.which("qemu-system-x86_64") or Path.home() / "AppData/Local/Cosmos/Tools/qemu/bin/qemu-system-x86_64.exe")
    share = qemu.parent.parent / "share" / "qemu"
    arguments = [str(qemu), "-L", str(share), "-accel", "tcg", "-machine", "q35", "-cpu", "max", "-m", "512M",
                 "-vga", "std", "-boot", "d", "-cdrom", str(iso), "-display", "none",
                 "-serial", f"file:{work / 'serial.log'}", "-monitor", "none",
                 "-qmp", f"tcp:127.0.0.1:{port},server=on,wait=off", "-action", "shutdown=poweroff,reboot=reset",
                 "-drive", f"file=fat:{disk},format=raw,if=none,id=appdisk,snapshot=on",
                 "-device", "ahci,id=ahci", "-device", "ide-hd,drive=appdisk,bus=ahci.0",
                 "-device", "qemu-xhci,id=tabletbus", "-device", "usb-tablet,bus=tabletbus.0"]
    return subprocess.Popen(arguments, stdout=subprocess.DEVNULL, stderr=(work / "stderr.log").open("w"))


def wait_for_shell(work: Path, timeout: float = 180) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        log = work / "serial.log"
        if log.exists() and "ist bereit" in log.read_text(encoding="utf-8", errors="replace"):
            return
        time.sleep(1)
    raise TimeoutError("ZehOS meldet keine bereite Shell.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--doom", type=Path, required=True, help="doom.wasm")
    parser.add_argument("--wad", type=Path, help="WAD-Datei; fehlt sie, wird keine bereitgestellt")
    parser.add_argument("--truncate-wad", type=int, help="nur die ersten N Bytes der WAD bereitstellen")
    parser.add_argument("--scenario", choices=["full", "launch"], default="full")
    parser.add_argument("--port", type=int, default=4445)
    args = parser.parse_args()

    work = ROOT / ".tools" / "wasm-desktop" / uuid.uuid4().hex
    app = work / "disk" / "doom"
    app.mkdir(parents=True)
    shutil.copyfile(args.doom, app / "doom.wasm")
    if args.wad is not None:
        data = args.wad.read_bytes()
        (app / "freedoom1.wad").write_bytes(data[:args.truncate_wad] if args.truncate_wad else data)
    guest = start_qemu(args.iso.resolve(), work / "disk", work, args.port)
    try:
        wait_for_shell(work)
        qmp = Qmp(args.port)
        time.sleep(4)
        step = iter(range(1, 1000))

        def shot(name: str) -> tuple[int, int]:
            return qmp.shot(work / f"{next(step):02d}-{name}.png")

        width, height = shot("boot")
        for line in (TEST_USER, TEST_PASSWORD, TEST_PASSWORD):
            qmp.text(line)
            qmp.tap("ret")
            time.sleep(1)
        time.sleep(25)

        def launch(name: str) -> None:
            # Das Suchfeld behaelt seinen Text; vor jeder Suche leeren.
            qmp.tap("alt", "f1")
            time.sleep(1)
            for _ in range(40):
                qmp.tap("backspace", hold=0.02)
            qmp.text(name)
            time.sleep(1)
            qmp.tap("ret")

        if args.scenario == "launch":
            launch("doom")
            time.sleep(25)
            shot("launch")
            return 0

        launch("doom")
        time.sleep(25)
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
            time.sleep(22)
            shot(f"start-{run:02d}")
            qmp.tap("backspace")
            time.sleep(1)
            qmp.tap("up")
            time.sleep(0.5)
            qmp.tap("ret")
            time.sleep(1)
            qmp.tap("y")
            time.sleep(5)
        shot("taskmanager-after")
        return 0
    finally:
        if guest.poll() is None:
            guest.kill()
            guest.wait()
        print(f"Bilder und Log: {work}")


if __name__ == "__main__":
    sys.exit(main())
