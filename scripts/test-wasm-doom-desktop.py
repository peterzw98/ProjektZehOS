#!/usr/bin/env python3
"""Prueft Paketverwaltung und Paket-Apps im ZehOS-Desktop unter QEMU; bedient wird per QMP.

Das ISO muss mit -p:ZehOSWasm=true, -p:ZehOSPackageKeyPath (Testschluessel) und -p:ZehOSPackageTestCaPath (Test-CA)
gebaut sein. Das Skript baut daraus ein Testrepository: Doom aus --repo und die kleine App scripts/guest-testapp
(Version 1.0.0, im Upgrade-Repository 1.1.0), signiert mit --key. Die Paketlogik wird mit der kleinen App geprueft,
Doom nur im Szenario full. Ergebnisse erscheinen seriell als "PKG ...", "NET ...", "APP <id> ..." (erstes Bild,
Ende), "WASM Speicher frei=..." (nach dem Aufraeumen eines Laufs) und "Desktop ist bereit." / "Textshell aktiv.";
das Skript wartet auf diese Zeilen statt auf feste Zeiten.
Die Bedienung wird anhand der Bildschirmfotos ausgewertet.

Snapshot (nur TCG; WHPX blockiert savevm): Eine Vorbereitungs-VM bootet einmal, richtet das Konto ein, holt DHCP
und Katalog, installiert Doom ueber HTTPS und speichert den Zustand. Jedes Szenario startet von einer Kopie in
wenigen Sekunden. Der Snapshot liegt je ISO und Repository unter .tools/guest-snapshots und wird wiederverwendet.
Ohne Snapshot (--no-snapshot, --accel kvm/whpx, --source) richtet sich jede VM selbst ein. Das Datenlaufwerk ist
immer eine Wegwerfkopie (qcow2) im Arbeitsordner; vorhandene Datenimages werden nicht verwendet.

Szenarien (mehrere laufen gleichzeitig in eigenen VMs):
  full     Doom: neues Spiel, laufen/schiessen, Textshell (F4/Esc), Schliessen ueber X, Taskmanager,
           --starts Starts (Standard 3, fuer die Speicherpruefung 10) mit Beenden ueber das Doom-Menue; der freie
           Speicher darf dabei um hoechstens 8 MiB sinken.
  remove   Entfernen waehrend die App laeuft (abgelehnt), Entfernen, Startmenue ohne App, pkg install-local.
  reboot   Installation, Neustart mit Anmeldung, Start der App und von Doom (erneute Pruefung) nach dem Neustart.
  upgrade  Installation, Katalog mit hoeherer Generation, pkg upgrade auf 1.1.0, Start.
  corrupt  Server liefert eine veraenderte testapp.wasm; die Installation muss mit Pruefsummenfehler scheitern.
Gruppen: quick = remove upgrade corrupt (fuer jede Aenderung), all = alle Szenarien (vor dem PR).
"""

import argparse
import hashlib
import http.server
import json
import os
import re
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
# Nur fuer diese Wegwerf-VMs; die Datenlaufwerke sind Kopien im Arbeitsordner.
TEST_USER, TEST_PASSWORD = "tester", "testkennwort1"
# Deutsche Tastaturbelegung des Gasts (DEStandardLayout); QMP sendet physische US-Tastenpositionen.
SHIFTED = {":": "dot", "/": "7", "_": "slash"}
PLAIN = {" ": "spc", ".": "dot", "-": "slash", "y": "z", "z": "y"}
SCENARIOS = ["full", "remove", "reboot", "upgrade", "corrupt"]
GROUPS = {"quick": ["remove", "upgrade", "corrupt"], "all": SCENARIOS}
# Bei Aenderungen am Vorbereitungsablauf erhoehen, damit alte Snapshots nicht weiterverwendet werden.
SNAPSHOT_FORMAT = "1"
SNAPSHOTS = ROOT / ".tools" / "guest-snapshots"
KEPT_SNAPSHOTS = 2
# Erlaubter Rueckgang des freien Speichers ueber die Doom-Starts; gemessen schwankt er um weniger als 1 MiB.
MEMORY_TOLERANCE_KIB = 8 * 1024
TESTAPP_COLORS = {"1.0.0": "0x0000a0e0u", "1.1.0": "0x0030c040u"}


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

    def tap(self, *qcodes: str, hold: float = 0.03) -> None:
        for qcode in qcodes:
            self.key(qcode, True)
        time.sleep(hold)
        for qcode in reversed(qcodes):
            self.key(qcode, False)
        time.sleep(0.04)

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

    def move(self, x: int, y: int, width: int, height: int) -> None:
        position = [{"type": "abs", "data": {"axis": "x", "value": int(x * 32767 / (width - 1))}},
                    {"type": "abs", "data": {"axis": "y", "value": int(y * 32767 / (height - 1))}}]
        self.command("input-send-event", {"events": position})

    def click(self, x: int, y: int, width: int, height: int) -> None:
        self.move(x, y, width, height)
        time.sleep(0.2)
        for down in (True, False):
            self.command("input-send-event", {"events": [{"type": "btn", "data": {"down": down, "button": "left"}}]})
            time.sleep(0.1)

    def drag(self, x: int, y: int, to_x: int, to_y: int, width: int, height: int) -> None:
        """Zieht mit gedrueckter linker Taste, etwa an einer Fensterecke."""
        for (px, py), down in (((x, y), True), ((to_x, to_y), False)):
            self.command("input-send-event", {"events": [
                {"type": "abs", "data": {"axis": "x", "value": int(px * 32767 / (width - 1))}},
                {"type": "abs", "data": {"axis": "y", "value": int(py * 32767 / (height - 1))}}]})
            time.sleep(0.2)
            self.command("input-send-event", {"events": [{"type": "btn", "data": {"down": down, "button": "left"}}]})
            time.sleep(0.2)

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

    def __init__(self, directory: Path, tls: Path, corrupt: str | None = None):
        self.directory = directory
        repository = self

        class Handler(http.server.SimpleHTTPRequestHandler):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, directory=str(repository.directory), **kwargs)

            def log_message(self, format, *args):
                pass

            def copyfile(self, source, target):
                data = source.read()
                if corrupt and self.path.endswith("/" + corrupt):
                    data = data[:100] + bytes([data[100] ^ 1]) + data[101:]
                target.write(data)

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.source = f"https://10.0.2.2:{self.server.server_address[1]}/"
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(tls / "server.pem", tls / "server.key")
        self.server.socket = context.wrap_socket(self.server.socket, server_side=True)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()


def qemu_tool(name: str) -> Path:
    found = shutil.which(name)
    if found:
        return Path(found)
    # Die Cosmos CLI legt QEMU unter Linux ohne bin/ ab, unter Windows mit bin/ und .exe.
    for candidate in (Path.home() / ".cosmos/tools/qemu" / name, Path.home() / "AppData/Local/Cosmos/Tools/qemu/bin" / (name + ".exe")):
        if candidate.is_file():
            return candidate
    sys.exit(f"{name} nicht gefunden: weder im PATH noch unter ~/.cosmos/tools/qemu/ (Linux) "
             "oder ~/AppData/Local/Cosmos/Tools/qemu/bin/ (Windows)")


def qemu_img() -> tuple[list[str], dict[str, str] | None]:
    """Aufruf von qemu-img; der Linux-Wrapper der Cosmos CLI haengt -L an, das qemu-img ablehnt."""
    tool = qemu_tool("qemu-img")
    real = tool.with_name(tool.name + ".real")
    if not real.is_file():
        return [str(tool)], None
    environment = dict(os.environ)
    environment["LD_LIBRARY_PATH"] = os.pathsep.join(filter(None, [str(tool.parent / "lib"), environment.get("LD_LIBRARY_PATH")]))
    return [str(real)], environment


def free_port() -> int:
    # shortcut: der Port kann bis zum Start von QEMU neu vergeben werden; bei einem Portfehler erneut starten.
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def create_disk(directory: Path, image: Path) -> None:
    """Erzeugt aus einem Ordner ein beschreibbares FAT-Datenlaufwerk (qcow2) ueber den vvfat-Treiber von QEMU."""
    tool, environment = qemu_img()
    subprocess.run([*tool, "convert", "-f", "vvfat", "-O", "qcow2", f"fat:{directory}", str(image)],
                   check=True, stdout=subprocess.DEVNULL, env=environment)


# Maschine und Geraete muessen beim Laden eines Snapshots gleich sein; sie gehen deshalb in dessen Schluessel ein.
MACHINE = ["-machine", "q35", "-m", "512M", "-vga", "std", "-boot", "d", "-display", "none", "-monitor", "none",
           "-action", "shutdown=poweroff,reboot=reset",
           "-device", "ahci,id=ahci", "-device", "ide-hd,drive=appdisk,bus=ahci.0",
           "-netdev", "user,id=net0", "-device", "e1000e,netdev=net0",
           "-device", "qemu-xhci,id=tabletbus", "-device", "usb-tablet,bus=tabletbus.0"]


def start_qemu(iso: Path, disk: Path, work: Path, port: int, accel: str, loadvm: str | None) -> subprocess.Popen:
    qemu = qemu_tool("qemu-system-x86_64")
    # Windows: bin/../share/qemu; Cosmos unter Linux: share/qemu neben dem Wrapper.
    share = next((d for d in (qemu.parent.parent / "share" / "qemu", qemu.parent / "share" / "qemu") if d.is_dir()),
                 qemu.parent.parent / "share" / "qemu")
    # Kennwortanlage und TLS brauchen RDSEED/RDRAND, die das Standardmodell nicht bietet. Unter WHPX bricht der
    # Gast mit -cpu max oder host jedoch sofort ab (Unexpected VP exit code 4); dort bleibt das Standardmodell.
    cpu = {"tcg": ["-cpu", "max"], "kvm": ["-cpu", "qemu64,+rdrand,+rdseed"]}.get(accel, [])
    arguments = [str(qemu), "-L", str(share), "-accel", accel, *cpu, *MACHINE, "-cdrom", str(iso),
                 "-serial", f"file:{work / 'serial.log'}", "-qmp", f"tcp:127.0.0.1:{port},server=on,wait=off",
                 "-drive", f"file={disk},format=qcow2,if=none,id=appdisk",
                 *(["-loadvm", loadvm] if loadvm else [])]
    with (work / "stderr.log").open("w") as stderr:
        return subprocess.Popen(arguments, stdout=subprocess.DEVNULL, stderr=stderr)


# So lange darf eine abweichende Ergebniszeile stehen, bevor sie als Fehlschlag gilt; sie kann zerteilt sein.
MISMATCH_GRACE = 10
# Hoechstens so viele Unterbrechungen werden innerhalb einer Marke uebersprungen.
MAX_INTRUSIONS = 3


def find_marker(content: str, text: str, start: int) -> int:
    """Sucht text ab start und liefert die Position danach oder -1.

    Cosmos schreibt die serielle Ausgabe nicht zeilenatomar: ein Threadwechsel kann eine Marke mit Debugzeilen
    anderer Threads zerteilen, etwa "PKG Kata" + "D\\n[LowLevelMonitor] ...\\n" + "log geladen". Nach den ersten
    drei zusammenhaengenden Zeichen darf deshalb an einer Abweichung der Rest der fremden Zeile samt folgender
    "[...]"-Zeilen uebersprungen werden.
    """
    candidate = content.find(text[:3], start)
    while candidate >= 0:
        position, matched, skipped = candidate, 0, 0
        while matched < len(text):
            if position < len(content) and content[position] == text[matched]:
                position += 1
                matched += 1
                continue
            newline = content.find("\n", position)
            if matched < 3 or skipped == MAX_INTRUSIONS or newline < 0:
                break
            position, skipped = newline + 1, skipped + 1
            while content.startswith("[", position) and (newline := content.find("\n", position)) >= 0:
                position = newline + 1
        if matched == len(text):
            return position
        candidate = content.find(text[:3], candidate + 1)
    return -1


def wait_serial(work: Path, text: str, timeout: float, start: int = 0) -> int:
    """Wartet auf text ab Position start im seriellen Protokoll und liefert die Position danach."""
    prefix = None
    if text.startswith(("PKG ", "NET ", "APP ")):
        prefix = text[:text.index(" ", 4) + 1] if text.startswith("APP ") else text[:4]
    mismatch_since = None
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        log = work / "serial.log"
        content = log.read_text(encoding="utf-8", errors="replace") if log.exists() else ""
        found = find_marker(content, text, start)
        if found >= 0:
            return found
        # Eine vollstaendige Ergebniszeile derselben Art (PKG, NET, APP <id>) ohne den Text ist ein Fehlschlag.
        failure = content.find(prefix, start) if prefix else -1
        end = content.find("\n", failure) if failure >= 0 else -1
        if end > 0:
            mismatch_since = mismatch_since or time.monotonic()
            if time.monotonic() - mismatch_since > MISMATCH_GRACE:
                raise RuntimeError(f"Unerwartetes Ergebnis: {content[failure:end]}")
        time.sleep(0.5)
    raise TimeoutError(f"Kein '{text}' im seriellen Protokoll.")


class Guest:
    """Bedienschritte einer laufenden VM; jeder Schritt wartet auf die passende serielle Marke."""

    def __init__(self, work: Path, port: int, source: str, started: float):
        self.work, self.port, self.source, self.started = work, port, source, started
        self.qmp: Qmp | None = None
        self.step = iter(range(1, 1000))
        self.size = (1920, 1080)
        # Das Suchfeld des Startmenues behaelt seinen Text; so viele Zeichen muessen vor der naechsten Suche weg.
        self.search = 0

    def log(self, text: str) -> None:
        print(f"{text}: {time.monotonic() - self.started:.0f} s", flush=True)

    def mark(self) -> int:
        log = self.work / "serial.log"
        return len(log.read_text(encoding="utf-8", errors="replace")) if log.exists() else 0

    def wait(self, text: str, timeout: float, mark: int = 0) -> int:
        return wait_serial(self.work, text, timeout, mark)

    def shot(self, name: str) -> Path:
        path = self.work / f"{next(self.step):02d}-{name}.png"
        self.size = self.qmp.shot(path)
        return path

    def connect(self) -> None:
        self.qmp = Qmp(self.port)

    def login(self, first: bool) -> None:
        mark = self.mark()
        for value in (TEST_USER, TEST_PASSWORD, TEST_PASSWORD) if first else (TEST_USER, TEST_PASSWORD):
            self.qmp.line(value)
            time.sleep(0.5)
        # Die Ersteinrichtung (PBKDF2) dauert unter TCG unterschiedlich lange.
        self.wait("Benutzer angemeldet.", 300, mark)
        self.log("angemeldet")
        time.sleep(1)

    def shell(self, *lines: str) -> None:
        """Wechselt vom Desktop in die Textshell; ein offenes Startmenue schliesst das erste Esc."""
        mark = self.mark()
        self.qmp.tap("esc")
        try:
            self.wait("Textshell aktiv.", 5, mark)
        except TimeoutError:
            self.qmp.tap("esc")
            self.wait("Textshell aktiv.", 30, mark)
        # Leerzeile zuerst: unter Last kann die erste Taste nach dem Wechsel verloren gehen.
        self.qmp.tap("ret")
        for value in lines:
            self.qmp.line(value)
            time.sleep(0.5)

    def desktop(self) -> None:
        mark = self.mark()
        self.qmp.tap("f4")
        self.wait("Desktop ist bereit.", 30, mark)
        time.sleep(0.5)

    def pkg(self, command: str, expected: str, timeout: float = 900) -> None:
        mark = self.mark()
        started = time.monotonic()
        self.qmp.line("pkg " + command)
        self.wait("PKG " + expected, timeout, mark)
        print(f"pkg {command}: {time.monotonic() - started:.0f} s", flush=True)

    def dhcp(self) -> None:
        # Getippte Zeilen koennen unter Last verloren gehen; ohne NET-Ergebnis erneut anstossen.
        for attempt in range(3):
            mark = self.mark()
            self.qmp.line("net dhcp")
            try:
                self.wait("NET DHCP IPv4", 90, mark)
                return
            except (RuntimeError, TimeoutError):
                self.shot("dhcp-failed")
                if attempt == 2:
                    raise

    def update_catalog(self) -> None:
        for attempt in range(3):
            try:
                self.pkg("update", "Katalog geladen", 120)
                return
            except RuntimeError as error:
                self.shot("update-failed")
                if not ("IPv4" in str(error) or "Paketquelle" in str(error)) or attempt == 2:
                    raise
                # Eine verlorene Eingabe von pkg source oder net dhcp nachholen.
                self.qmp.line(f"pkg source {self.source}")
                time.sleep(0.5)
                self.dhcp()

    def use_source(self) -> None:
        """Setzt die Paketquelle dieses Laufs; ein Snapshot kennt nur den Server der Vorbereitung."""
        self.shell(f"pkg source {self.source}")
        self.update_catalog()

    def setup(self, with_doom: bool) -> None:
        """Erster Start bis zum Desktop mit Konto, Netz, Katalog und auf Wunsch installiertem Doom."""
        self.wait("Desktop ist bereit", 180)
        self.log("Desktop bereit")
        self.connect()
        time.sleep(1)
        self.shot("boot")
        self.login(first=True)
        self.shell(f"pkg source {self.source}")
        self.dhcp()
        self.update_catalog()
        if with_doom:
            self.pkg("install doom", "doom 1.0.0 installiert.")
        self.desktop()

    def launch(self, name: str) -> None:
        self.qmp.tap("alt", "f1")
        time.sleep(0.5)
        for _ in range(self.search):
            self.qmp.tap("backspace", hold=0.02)
        self.qmp.text(name)
        self.search = len(name)
        time.sleep(0.5)
        self.qmp.tap("ret")

    def start_app(self, name: str) -> None:
        mark = self.mark()
        self.launch(name)
        try:
            self.wait(f"APP {name} erstes Bild", 180, mark)
        except TimeoutError:
            self.shot(f"{name}-timeout")
            raise
        # Kurz weiterlaufen lassen, damit das Bildschirmfoto mehr als das erste Bild zeigt.
        time.sleep(2)

    def quit_app(self, name: str, *keys: tuple[str, float]) -> None:
        """Beendet die App ueber ihre eigene Bedienung (Tasten mit Pause) und wartet auf ihre Ende-Marke."""
        mark = self.mark()
        for key, pause in keys:
            self.qmp.tap(key)
            time.sleep(pause)
        self.wait_finished(name, mark)

    def wait_finished(self, name: str, mark: int) -> None:
        """Wartet auf das Ende der App und das anschliessende Aufraeumen des Wasm-Workers (Speicherzeile)."""
        end = self.wait(f"APP {name} beendet", 60, mark)
        self.wait("WASM Speicher", 60, end)

    def free_memory(self, mark: int) -> list[int]:
        """Freier Speicher in KiB nach jedem Wasm-Lauf seit mark; zerteilte Zeilen fehlen in der Liste."""
        log = (self.work / "serial.log").read_text(encoding="utf-8", errors="replace")
        return [int(value) for value in re.findall(r"WASM Speicher frei=(\d+) KiB", log[mark:])]

    def frame_times(self, mark: int) -> list[dict[str, int]]:
        """Doom-Bildzeiten je 60-s-Fenster seit mark (ms, Pausen, heap in KiB); zerteilte Zeilen fehlen in der Liste."""
        log = (self.work / "serial.log").read_text(encoding="utf-8", errors="replace")
        # Ohne "APP doom": Cosmos-Debugausgabe ueberschreibt gelegentlich den Zeilenanfang; nur Doom wird gemessen.
        pattern = (r"Bildzeit n=(?P<n>\d+) min=(?P<min>\d+) median=(?P<median>\d+) p95=(?P<p95>\d+) "
                   r"max=(?P<max>\d+) ms pausen=(?P<pauses>\d+) heap=(?P<heap>\d+) KiB")
        return [{key: int(value) for key, value in match.groupdict().items()} for match in re.finditer(pattern, log[mark:])]

    def quit_testapp(self) -> None:
        self.quit_app("testapp", ("q", 0))

    def quit_doom(self) -> None:
        self.quit_app("doom", ("backspace", 1), ("up", 0.5), ("ret", 1), ("y", 0))


def view_region(png: Path) -> bytes:
    """3D-Ansicht des Doom-Bilds ohne Statusleiste, deren Gesicht sich auch ohne Eingabe bewegt.

    Liest nur die PNGs aus Qmp.shot: ein IDAT, RGB, Filter 0 je Zeile. Der Ausschnitt gilt fuer das erste
    Doom-Fenster bei 1920x1080 (Bild 640x400 ab 112/180).
    """
    data = png.read_bytes()
    width = struct.unpack(">I", data[16:20])[0]
    start = data.index(b"IDAT") + 4
    length = struct.unpack(">I", data[start - 8:start - 4])[0]
    raw = zlib.decompress(data[start:start + length])
    stride = 1 + width * 3
    return b"".join(raw[row * stride + 1 + 120 * 3:row * stride + 1 + 744 * 3] for row in range(184, 500))


def scenario_full(guest: Guest, args: argparse.Namespace, fixtures: Path | None, repository: Repository | None) -> None:
    qmp = guest.qmp
    guest.start_app("doom")
    guest.shot("doom-demo")
    qmp.tap("backspace")
    time.sleep(1.5)
    for _ in range(3):
        qmp.tap("ret")
        time.sleep(1.5)
    time.sleep(3)
    guest.shot("e1m1-start")
    qmp.hold("up", 1.5)
    qmp.hold("right", 0.6)
    qmp.tap("ctrl")
    time.sleep(0.3)
    qmp.tap("ctrl")
    time.sleep(1)
    guest.shot("moved-fired")
    # Maus (#79): der erste Klick ins Doom-Bild faengt den Zeiger ein, ohne zu feuern; danach dreht eine
    # Bewegung nach rechts die Ansicht. Das USB-Tablet liefert absolute Positionen, der Kernel daraus Deltas.
    centre = (416, 380)
    qmp.click(*centre, *guest.size)
    time.sleep(1)
    before = guest.shot("mouse-captured")
    for step in range(1, 11):
        qmp.move(centre[0] + step * 20, centre[1], *guest.size)
        time.sleep(0.05)
    time.sleep(1)
    after = guest.shot("mouse-turned")
    if view_region(before) == view_region(after):
        raise RuntimeError("Doom-Ansicht hat sich nach der Mausbewegung nicht gedreht.")
    # Doom muss in der Textshell pausieren; die 10 s sind Teil der Pruefung, keine Wartezeit.
    qmp.tap("f4")
    time.sleep(10)
    guest.desktop()
    guest.shot("after-10s-textshell")
    guest.shell()
    guest.shot("textshell-esc")
    guest.desktop()
    qmp.tap("ctrl")
    time.sleep(1.5)
    guest.shot("resumed-fired")
    # Fenstergroessen (#78): maximiert und im Paketminimum 640x440 passt der Kernel das Bild seitentreu ein.
    # Das Doom-Fenster liegt bei 32,80 (800x560); Maximieren-Feld bei 776,100, maximiert bei 1864,20.
    qmp.click(776, 100, *guest.size)
    time.sleep(1.5)
    guest.shot("maximiert")
    qmp.click(1864, 20, *guest.size)
    qmp.drag(829, 637, 669, 517, *guest.size)
    time.sleep(1.5)
    guest.shot("paketminimum")
    qmp.drag(669, 517, 829, 637, *guest.size)
    time.sleep(1)
    # Doom ist das erste Fenster; sein Schliessen-Feld liegt bei 1920x1080 an fester Stelle.
    memory_mark = guest.mark()
    qmp.click(813, 100, *guest.size)
    # Belegt das Beenden und verhindert, dass die Ende-Marke in den naechsten Start faellt.
    guest.wait_finished("doom", memory_mark)
    guest.shot("closed-x")
    guest.launch("taskmanager")
    time.sleep(3)
    guest.shot("taskmanager-before")
    for run in range(1, args.starts + 1):
        guest.start_app("doom")
        guest.shot(f"start-{run:02d}")
        guest.quit_doom()
    if args.long_seconds:
        frame_mark = guest.mark()
        guest.start_app("doom")
        qmp.tap("backspace")
        time.sleep(1.5)
        for _ in range(3):
            qmp.tap("ret")
            time.sleep(1.5)
        started = time.monotonic()
        cycle = 0
        while time.monotonic() - started < args.long_seconds:
            qmp.hold("up", 1.0)
            qmp.hold("right", 0.3)
            qmp.tap("ctrl")
            # Die Benutzungstaste startet nach dem Tod erneut und oeffnet im Spiel Tueren.
            qmp.tap("spc")
            time.sleep(3.6)
            cycle += 1
            if cycle % 10 == 0:
                guest.shot(f"long-{cycle:03d}")
                print(f"Langlauf: {time.monotonic() - started:.1f} s", flush=True)
        print(f"Langlauf gesamt: {time.monotonic() - started:.1f} s", flush=True)
        guest.quit_doom()
        # Der Kernel meldet die Doom-Bildzeit je 60 s Spielzeit; ein angebrochenes letztes Fenster fehlt.
        windows = guest.frame_times(frame_mark)
        for window in windows:
            print("Doom-Bildzeit (ms): " + " ".join(f"{key}={value}" for key, value in window.items()), flush=True)
        if args.long_seconds >= 120 and not windows:
            raise RuntimeError("Keine Doom-Bildzeit im seriellen Protokoll.")
        # Menue und Levelstart verlaengern die Spielzeit vor dem Langlauf; mindestens long_seconds // 60 Fenster sind
        # daher zu erwarten. Weniger heisst: Zeilen zerteilt oder Spielzeit pausiert.
        if len(windows) < args.long_seconds // 60:
            print(f"Warnung: nur {len(windows)} Bildzeit-Fenster, Zeilen zerteilt oder Spielzeit pausiert.", flush=True)
        if windows:
            # Pausen sind nicht gemessene Abstaende (auch Desktop-Stillstaende ueber 250 ms); verlorene Zeilen meldet
            # nur die Fensterwarnung oben.
            print(f"Doom-Bildzeit gesamt: n={sum(w['n'] for w in windows)} min={min(w['min'] for w in windows)} "
                  f"median={min(w['median'] for w in windows)}-{max(w['median'] for w in windows)} "
                  f"p95<={max(w['p95'] for w in windows)} max={max(w['max'] for w in windows)} ms, "
                  f"pausen={sum(w['pauses'] for w in windows)}", flush=True)
    guest.shot("taskmanager-after")
    # Speicher nach dem Schliessen ueber X gegen den nach dem letzten Start; gemessen jeweils nach dem Aufraeumen.
    # Zerteilte Speicherzeilen fehlen in der Liste; dann vergleicht die Pruefung den ersten und letzten lesbaren Wert.
    free = guest.free_memory(memory_mark)
    print("frei nach jedem Doom-Lauf (MiB): " + " ".join(f"{value / 1024:.1f}" for value in free), flush=True)
    if len(free) < 2:
        raise RuntimeError("Zu wenige Speicherzeilen im seriellen Protokoll.")
    if free[0] - free[-1] > MEMORY_TOLERANCE_KIB:
        raise RuntimeError(f"Freier Speicher sinkt ueber die Starts um {(free[0] - free[-1]) / 1024:.1f} MiB.")


def scenario_remove(guest: Guest, args: argparse.Namespace, fixtures: Path, repository: Repository) -> None:
    guest.use_source()
    guest.pkg("install testapp", "testapp 1.0.0 installiert.")
    guest.desktop()
    guest.start_app("testapp")
    guest.shot("testapp-running")
    guest.shell("pkg remove testapp")
    guest.shot("remove-while-running")
    guest.desktop()
    guest.quit_testapp()
    guest.shell("pkg remove testapp", "pkg list")
    guest.shot("removed")
    guest.desktop()
    guest.launch("testapp")
    time.sleep(1)
    guest.shot("menu-after-remove")
    guest.shell()
    # Neuinstallation ohne Netz aus dem Paketordner, den das Skript auf das Datenlaufwerk gelegt hat.
    guest.pkg("install-local /data/pkgs/testapp/1.0.0", "testapp 1.0.0 installiert.")
    guest.desktop()
    guest.start_app("testapp")
    guest.shot("reinstalled-local")
    guest.quit_testapp()


def scenario_reboot(guest: Guest, args: argparse.Namespace, fixtures: Path, repository: Repository) -> None:
    guest.use_source()
    guest.pkg("install testapp", "testapp 1.0.0 installiert.")
    mark = guest.mark()
    guest.qmp.line("reboot")
    guest.wait("Desktop ist bereit", 240, mark)
    time.sleep(1)
    guest.shot("after-reboot")
    guest.login(first=False)
    guest.start_app("testapp")
    guest.shot("testapp-after-reboot")
    guest.quit_testapp()
    # Nach dem Neustart prueft der Kernel alle 28 MB von Doom erneut, bevor es startet.
    guest.start_app("doom")
    guest.shot("doom-after-reboot")
    guest.quit_doom()


def scenario_upgrade(guest: Guest, args: argparse.Namespace, fixtures: Path, repository: Repository) -> None:
    guest.use_source()
    guest.pkg("install testapp", "testapp 1.0.0 installiert.")
    repository.directory = fixtures / "repo-upgrade"
    guest.update_catalog()
    guest.pkg("upgrade", "testapp 1.1.0 aktualisiert.")
    guest.qmp.line("pkg list")
    time.sleep(1)
    guest.shot("upgraded")
    guest.desktop()
    guest.start_app("testapp")
    guest.shot("testapp-upgraded")
    guest.quit_testapp()


def scenario_corrupt(guest: Guest, args: argparse.Namespace, fixtures: Path, repository: Repository) -> None:
    guest.use_source()
    guest.pkg("install testapp", "Pruefsumme falsch: testapp.wasm")
    guest.qmp.line("pkg list")
    time.sleep(1)
    guest.shot("corrupt-rejected")


RUN = {"full": scenario_full, "remove": scenario_remove, "reboot": scenario_reboot, "upgrade": scenario_upgrade,
       "corrupt": scenario_corrupt}


def catalog_generation(repo: Path) -> int:
    found = re.search(r"^generation=(\d+)$", (repo / "catalog").read_text(encoding="utf-8"), re.MULTILINE)
    if not found:
        raise ValueError(f"{repo / 'catalog'} enthaelt keine Generation.")
    return int(found.group(1))


def build_fixtures(args: argparse.Namespace, target: Path) -> Path:
    """Baut Testapp, Repository (Doom + testapp 1.0.0), Upgrade-Repository (+ 1.1.0) und den Ordner fuers Datenlaufwerk."""
    tool = [sys.executable, str(ROOT / "scripts" / "build-package.py")]
    clang = args.wasi_sdk / "bin" / ("clang.exe" if sys.platform == "win32" else "clang")
    repo, upgrade = target / "repo", target / "repo-upgrade"
    repo.mkdir(parents=True)
    for package in (p for p in args.repo.iterdir() if p.is_dir()):
        shutil.copytree(package, repo / package.name)
    # Gleiche Generation wie das Doom-Repository: der Gast nimmt sie an, auch wenn er dessen Katalog schon kennt.
    generation = catalog_generation(args.repo)

    def add_testapp(destination: Path, version: str) -> None:
        wasm = target / f"testapp-{version}.wasm"
        subprocess.run([str(clang), "--target=wasm32-wasip1", f"--sysroot={args.wasi_sdk / 'share' / 'wasi-sysroot'}",
                        "-O2", "-Wall", "-Wextra", "-Werror", f"-DTESTAPP_COLOR={TESTAPP_COLORS[version]}", "-o", str(wasm),
                        str(ROOT / "scripts" / "guest-testapp" / "testapp.c")], check=True)
        subprocess.run([*tool, "package", "--key", str(args.key), "--repo", str(destination), "--id", "testapp",
                        "--version", version, "--name", "Testapp", "--entry", "testapp.wasm", f"{wasm}=testapp.wasm"],
                       check=True, stdout=subprocess.DEVNULL)

    add_testapp(repo, "1.0.0")
    subprocess.run([*tool, "catalog", "--key", str(args.key), "--repo", str(repo), "--generation", str(generation)],
                   check=True, stdout=subprocess.DEVNULL)
    shutil.copytree(repo, upgrade)
    add_testapp(upgrade, "1.1.0")
    subprocess.run([*tool, "catalog", "--key", str(args.key), "--repo", str(upgrade), "--generation", str(generation + 1)],
                   check=True, stdout=subprocess.DEVNULL)
    shutil.copytree(repo / "testapp" / "1.0.0", target / "disk" / "pkgs" / "testapp" / "1.0.0")
    return target


def snapshot_key(args: argparse.Namespace, fixtures: Path) -> str:
    """Alles, wovon der gespeicherte Gastzustand abhaengt: Ablauf, QEMU, Geraete, ISO, Katalog und Datenlaufwerk."""
    digest = hashlib.sha256(SNAPSHOT_FORMAT.encode())
    version = subprocess.run([str(qemu_tool("qemu-system-x86_64")), "--version"], check=True, capture_output=True, text=True)
    digest.update((version.stdout + " ".join(MACHINE)).encode())
    with args.iso.open("rb") as iso:
        for block in iter(lambda: iso.read(1 << 20), b""):
            digest.update(block)
    for path in sorted((fixtures / "disk").rglob("*")) + [fixtures / "repo" / "catalog"]:
        digest.update(str(path.relative_to(fixtures)).encode())
        if path.is_file():
            digest.update(path.read_bytes())
    return digest.hexdigest()[:16]


def prepare_snapshot(args: argparse.Namespace, fixtures: Path, run_root: Path) -> Path:
    """Liefert den Snapshot fuer ISO und Repository; erzeugt ihn bei Bedarf mit einer Vorbereitungs-VM."""
    base = SNAPSHOTS / snapshot_key(args, fixtures) / "base.qcow2"
    if base.exists():
        # Zeitstempel erneuern: das Aufraeumen behaelt die zuletzt benutzten Snapshots.
        os.utime(base.parent)
        print(f"Snapshot wiederverwendet: {base}", flush=True)
        return base
    work = run_root / "prepare"
    work.mkdir(parents=True)
    image = work / "base.qcow2"
    create_disk(fixtures / "disk", image)
    repository = Repository(fixtures / "repo", args.tls)
    port = free_port()
    guest = Guest(work, port, repository.source, time.monotonic())
    vm = start_qemu(args.iso, image, work, port, "tcg", None)
    try:
        guest.setup(with_doom=True)
        guest.shot("prepared")
        # Angehalten speichern und QEMU geordnet beenden, damit das qcow2-Image vollstaendig geschrieben ist.
        guest.qmp.command("stop")
        reply = guest.qmp.command("human-monitor-command", {"command-line": "savevm base"})
        if reply["return"]:
            raise RuntimeError(f"savevm: {reply['return'].strip()}")
        guest.qmp.command("quit")
        vm.wait(60)
        guest.log("Snapshot gespeichert")
        # shortcut: zwei gleichzeitige Aufrufe bauen denselben Snapshot doppelt; der zweite ersetzt den ersten.
        base.parent.mkdir(parents=True, exist_ok=True)
        os.replace(image, base)
    finally:
        repository.server.shutdown()
        if vm.poll() is None:
            vm.kill()
            vm.wait()
        # Ein gescheiterter Versuch hinterlaesst sonst einige 100 MB; Protokoll und Bilder bleiben.
        image.unlink(missing_ok=True)
        print(f"Vorbereitung: {work}", flush=True)
    # Aeltere Snapshots belegen je einige 100 MB; nur die neuesten behalten.
    for old in sorted(SNAPSHOTS.iterdir(), key=lambda p: p.stat().st_mtime, reverse=True)[KEPT_SNAPSHOTS:]:
        shutil.rmtree(old, ignore_errors=True)
    return base


def run_scenario(args: argparse.Namespace, scenario: str, fixtures: Path | None, base: Path | None, run_root: Path) -> int:
    work = run_root / scenario
    work.mkdir(parents=True)
    repository = None
    if args.source is None:
        repository = Repository(fixtures / "repo", args.tls, "testapp.wasm" if scenario == "corrupt" else None)
    source = args.source or repository.source
    disk = work / "data.qcow2"
    if base is not None:
        shutil.copyfile(base, disk)
    else:
        empty = work / "empty"
        empty.mkdir()
        create_disk(fixtures / "disk" if fixtures else empty, disk)
    port = free_port()
    guest = Guest(work, port, source, time.monotonic())
    vm = start_qemu(args.iso, disk, work, port, args.accel, "base" if base else None)
    try:
        if base is not None:
            guest.connect()
            time.sleep(1)
            guest.shot("resumed")
            guest.log("aus Snapshot bereit")
        else:
            guest.setup(with_doom=scenario in ("full", "reboot"))
        RUN[scenario](guest, args, fixtures, repository)
        guest.log("fertig")
        return 0
    finally:
        if repository is not None:
            repository.server.shutdown()
        if vm.poll() is None:
            vm.kill()
            vm.wait()
        # Die Laufwerkskopie ist gross und nach dem Lauf wertlos; Protokoll und Bilder bleiben.
        disk.unlink(missing_ok=True)
        print(f"Bilder und Log: {work}", flush=True)


def run_parallel(args: argparse.Namespace, scenarios: list[str], fixtures: Path | None, base: Path | None,
                 run_root: Path) -> int:
    """Startet jedes Szenario als eigenen Prozess mit eigener VM, eigenem Testserver und eigenen Ports."""
    children = []
    for scenario in scenarios:
        command = [sys.executable, __file__, "--iso", str(args.iso), "--scenario", scenario, "--accel", args.accel,
                   "--starts", str(args.starts), "--run-root", str(run_root)]
        if scenario == "full" and args.long_seconds:
            command += ["--long-seconds", str(args.long_seconds)]
        for name in ("tls", "source", "fixtures", "base"):
            value = {"fixtures": fixtures, "base": base}.get(name, getattr(args, name, None))
            if value is not None:
                command += ["--" + name, str(value)]
        if base is None:
            command.append("--no-snapshot")
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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--repo", type=Path, help="Repository mit Doom, signiert mit dem Testschluessel")
    parser.add_argument("--key", type=Path, help="privater Testschluessel (private.pem) fuer die Testapp")
    parser.add_argument("--tls", type=Path, help="Ordner mit server.pem und server.key der Test-CA")
    parser.add_argument("--wasi-sdk", type=Path, help="wasi-sdk 25 (Standard: WASI_SDK_PATH oder .tools)",
                        default=Path(os.environ.get("WASI_SDK_PATH", ROOT / ".tools" / "wasi-sdk-25.0-x86_64-windows")))
    parser.add_argument("--source", help="vorhandenes Repository (https://.../) fuer Szenario full, ohne Testserver")
    parser.add_argument("--scenario", nargs="+", choices=SCENARIOS + list(GROUPS), default=["quick"])
    parser.add_argument("--accel", default="tcg", help="QEMU-Beschleuniger: tcg, kvm oder whpx (kvm/whpx ohne Snapshot)")
    parser.add_argument("--starts", type=int, default=3, help="Doom-Starts am Ende von full (Speicherpruefung: 10)")
    parser.add_argument("--long-seconds", type=int, default=0, help="automatisierte Doom-Eingaben nach den Neustarts (Sekunden)")
    parser.add_argument("--no-snapshot", action="store_true", help="jede VM richtet sich selbst ein")
    # Intern fuer die Kindprozesse paralleler Laeufe.
    for name in ("--fixtures", "--base", "--run-root"):
        parser.add_argument(name, type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.long_seconds < 0:
        parser.error("--long-seconds darf nicht negativ sein")
    scenarios = list(dict.fromkeys(name for value in args.scenario for name in GROUPS.get(value, [value])))
    if args.long_seconds and "full" not in scenarios:
        parser.error("--long-seconds benoetigt das Szenario full")
    if args.source is not None and scenarios != ["full"]:
        parser.error("--source prueft nur das Szenario full")
    if args.source is None and args.fixtures is None and (args.repo is None or args.key is None or args.tls is None):
        parser.error("ohne --source sind --repo, --key und --tls noetig")
    args.iso = args.iso.resolve()

    run_root = args.run_root or ROOT / ".tools" / "wasm-desktop" / uuid.uuid4().hex
    fixtures = args.fixtures
    if fixtures is None and args.source is None:
        fixtures = build_fixtures(args, run_root / "fixtures")
    base = args.base
    if base is None and fixtures is not None and args.accel == "tcg" and not args.no_snapshot:
        base = prepare_snapshot(args, fixtures, run_root)
    if len(scenarios) > 1:
        return run_parallel(args, scenarios, fixtures, base, run_root)
    return run_scenario(args, scenarios[0], fixtures, base, run_root)


if __name__ == "__main__":
    sys.exit(main())
