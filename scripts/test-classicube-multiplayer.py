#!/usr/bin/env python3
"""Prueft ClassiCube-Mehrspieler gegen scripts/classic-test-server.py in einer frischen QEMU-Platte.

Das ISO muss mit dem oeffentlichen Gegenstueck von --key (ausschliesslich Testschluessel) gebaut sein.
--package bezeichnet die unsignierten Paketdateien des Ports; das Testpaket erhaelt capability=net.
Der Testserver lauscht auf 127.0.0.1; der Gast erreicht ihn ueber das QEMU-Usermode-Netz als 10.0.2.2.
Ablauf: net dhcp, Start aus dem Menue, Launcher "Direct connect", Name und Adresse tippen, Verbinden;
Bewegung (Positionen am Server), Block setzen/entfernen (am Server und bei einem zweiten Client), Bloecke des
zweiten Clients erscheinen im Bild, Schliessen des Fensters bei offener Verbindung, erneut verbinden, Serverausfall:
ClassiCube zeigt den Abbruch, Textshell und Desktop wechseln weiter, das Fenster schliesst sauber.
Bilder und Protokolle bleiben unter .tools/classicube-mp-guest/ erhalten.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import socket
import struct
import subprocess
import sys
import threading
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


tools = load("package_guest", "scripts/test-wasm-doom-desktop.py")
classic = load("classic_test_server", "scripts/classic-test-server.py")

PLAYER = "zehos"
OBSERVER = "beobachter"


class Observer:
    """Zweiter Client auf dem Host: sammelt, was der Server ihm schickt, und setzt selbst Bloecke."""

    def __init__(self, port):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.socket.sendall(struct.pack(">BB", 0x00, 7) + classic.text(OBSERVER) + classic.text("-") + b"\x00")
        self.packets = []
        self.lock = threading.Lock()
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        try:
            while True:
                opcode = classic.read_exact(self.socket, 1)[0]
                body = classic.read_exact(self.socket, classic.SERVER_SIZES[opcode] - 1)
                with self.lock:
                    self.packets.append((opcode, body))
        except (ConnectionError, OSError, KeyError):
            pass

    def seen(self, opcode, predicate=lambda body: True):
        with self.lock:
            return [body for code, body in self.packets if code == opcode and predicate(body)]

    def place(self, x, y, z, block):
        self.socket.sendall(struct.pack(">BhhhBB", 0x05, x, y, z, 1, block))

    def move(self, x, y, z):
        """Position in Blockeinheiten (Server rechnet in 1/32 Block)."""
        self.socket.sendall(struct.pack(">BbhhhBB", 0x08, -1, x * 32 + 16, y * 32, z * 32 + 16, 0, 0))

    def close(self):
        self.socket.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--key", type=Path, required=True)
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    args = parser.parse_args()
    work = ROOT / ".tools/classicube-mp-guest" / uuid.uuid4().hex[:12]
    work.mkdir(parents=True)
    files = [str(p.resolve()) + "=" + p.relative_to(args.package).as_posix()
             for p in sorted(args.package.rglob("*")) if p.is_file()]
    subprocess.run([sys.executable, str(ROOT / "scripts/build-package.py"), "package", "--key", str(args.key.resolve()),
                    "--repo", str(work / "seed/pkgs"), "--id", "classicube", "--version", "1.1.0", "--name", "ClassiCube",
                    "--entry", "classicube.wasm", "--min-kernel", "0.2.22", "--network", *files], check=True)
    events = (work / "server-events.jsonl").open("w", encoding="utf-8")
    server = classic.ClassicServer("127.0.0.1", 0, on_event=lambda e: (events.write(json.dumps(e) + "\n"), events.flush()))
    server.start()
    tools.create_disk(work / "seed", work / "data.qcow2")
    port = tools.free_port()
    vm = tools.start_qemu(args.iso.resolve(), work / "data.qcow2", work, port, args.accel, None)
    guest = tools.Guest(work, port, "", time.monotonic())
    step = 0
    launches = 0

    def capture(name):
        nonlocal step
        step += 1
        ppm = work / "screen.ppm"
        guest.qmp.command("screendump", {"filename": str(ppm)})
        header, size, maximum, pixels = ppm.read_bytes().split(b"\n", 3)
        assert header == b"P6" and maximum == b"255", "Unerwartetes QEMU-Bildformat"
        guest.size = tuple(map(int, size.split()))
        guest.qmp.shot(work / f"{step:02d}-{name}.png")
        return pixels

    # Paketfenster 800x560 bei 32,80, Inhalt 798x519 ab 33,120; jeder weitere Start versetzt das Fenster um 28.
    def origin():
        offset = 28 * (launches - 1)
        return 33 + offset, 120 + offset

    def click(x, y):
        left, top = origin()
        guest.qmp.click(left + x, top + y, *guest.size)
        time.sleep(.5)

    def view(pixels):
        width, _ = guest.size
        left, top = origin()
        left, top = left + 199, top + 130
        return b"".join(pixels[(row*width+left)*3:(row*width+left+400)*3] for row in range(top, top+260))

    def difference(first, second):
        return sum(first[i:i+3] != second[i:i+3] for i in range(0, len(first), 3*4))

    def changed(before, name, message, threshold=1500):
        deadline = time.monotonic() + 30
        while True:
            time.sleep(1)
            after = view(capture(name))
            if difference(before, after) > threshold:
                return after
            assert time.monotonic() < deadline, f"{message} (Schwelle {threshold} Stichproben)"

    def server_event(predicate, timeout, mark, message):
        try:
            return server.wait_for(predicate, timeout, mark)
        except TimeoutError:
            capture("fehlt-" + message.split()[0].lower())
            raise AssertionError(message) from None

    def button(name):
        for down in (True, False):
            guest.qmp.command("input-send-event", {"events":[{"type":"btn", "data":{"button":name,"down":down}}]})
            time.sleep(.15)

    def start():
        nonlocal launches
        mark = guest.mark()
        guest.launch("ClassiCube")
        launches += 1
        guest.wait("APP classicube erstes Bild", 180, mark)
        time.sleep(3)
        capture("launcher")

    def direct_connect():
        """Launcher (798x519): Direct connect, Name, Adresse (vorbelegt ":"), Connect; wartet auf den Login."""
        mark = server.mark()
        click(399, 319)
        capture("direct-connect")
        click(334, 139)
        guest.qmp.text(PLAYER)
        click(334, 184)
        guest.qmp.tap("end")
        for _ in range(8):
            guest.qmp.tap("backspace")
        guest.qmp.text(f"10.0.2.2:{server.port}")
        capture("eingaben")
        click(289, 279)
        server_event(lambda e: e["type"] == "joined" and e["player"] == PLAYER, 120, mark, "Login am Server fehlt")
        guest.log("am Server angemeldet")

    def world(name):
        deadline = time.monotonic() + 120
        while True:
            region = view(capture(name))
            if len({region[i:i+3] for i in range(0, len(region), 3*7)}) >= 24:
                return region
            assert time.monotonic() < deadline, f"{name}: keine Welt sichtbar"
            time.sleep(1)

    observer = None
    try:
        guest.wait("Desktop ist bereit", 180)
        guest.connect()
        capture("boot")
        guest.login(first=True)
        guest.shell()
        guest.dhcp()
        guest.pkg("install-local /data/pkgs/classicube/1.1.0", "classicube 1.1.0 installiert.")
        guest.desktop()
        memory_mark = guest.mark()

        # 1. Verbinden, Bewegung, Bloecke in beide Richtungen
        start()
        direct_connect()
        state = world("welt")
        observer = Observer(server.port)
        server_event(lambda e: e["type"] == "joined" and e["player"] == OBSERVER, 10, 0, "Beobachter nicht angemeldet")
        # Weg vom Spawnpunkt: Sonst steht der Beobachter beim erneuten Verbinden direkt vor der Kamera des Gasts, und
        # sein Kopf fuellt das Bild (welt-2 fand dann zu wenige Farben).
        observer.move(4, server.world.ground + 2, 4)
        # Hat ClassiCube die Maus schon gefangen, wirkt der Klick als Spielklick (Block entfernen); erst danach zaehlen.
        click(399, 259)
        time.sleep(2)
        state = view(capture("nach-fang"))
        mark = server.mark()
        guest.qmp.hold("w", 1.5)
        moved = server_event(lambda e: e["type"] == "position" and e["player"] == PLAYER and
                             (e["x"], e["z"]) != server.world.spawn()[::2],
                             30, mark, "Bewegung kommt nicht am Server an")
        state = changed(state, "bewegung-w", "W veraendert die Ansicht nicht")
        assert observer.seen(0x08), "Beobachter erhaelt keine Positionen"
        width, height = guest.size
        left, top = origin()
        for dy in range(1, 9):
            guest.qmp.move(left + 399, top + 259 + 25*dy, width, height)
            time.sleep(.1)
        state = changed(state, "blick-boden", "Blick nach unten veraendert die Ansicht nicht")
        mark = server.mark()
        button("right")
        placed = server_event(lambda e: e["type"] == "block" and e["player"] == PLAYER and e["mode"] == 1, 30, mark,
                              "Gesetzter Block kommt nicht am Server an")
        capture("block-gesetzt")
        button("left")
        removed = server_event(lambda e: e["type"] == "block" and e["player"] == PLAYER and e["mode"] == 0, 30, mark,
                               "Entfernter Block kommt nicht am Server an")
        time.sleep(1)
        target = lambda body, e: struct.unpack(">hhh", body[:6]) == (e["x"], e["y"], e["z"])
        assert observer.seen(0x06, lambda b: target(b, placed)), "Beobachter sieht den gesetzten Block nicht"
        assert observer.seen(0x06, lambda b: target(b, removed)), "Beobachter sieht den entfernten Block nicht"
        state = view(capture("bloecke-gast"))
        # Der Beobachter pflastert den Boden um den Gast mit Stein; der Blick nach unten muss das zeigen.
        latest = [e for e in server.events if e["type"] == "position" and e["player"] == PLAYER][-1]
        px, pz, ground = latest["x"] // 32, latest["z"] // 32, server.world.ground
        for x in range(px - 4, px + 5):
            for z in range(pz - 4, pz + 5):
                observer.place(x, ground, z, classic.STONE)
        changed(state, "bloecke-beobachter", "Bloecke des zweiten Clients erscheinen nicht im Bild")

        # 2. Abbruch durch den Spieler: Fenster bei offener Verbindung schliessen
        guest.qmp.tap("esc")
        time.sleep(2)
        mark, serial = server.mark(), guest.mark()
        guest.qmp.click(812 + 28*(launches-1), 100 + 28*(launches-1), *guest.size)
        server_event(lambda e: e["type"] == "disconnect" and e["player"] == PLAYER, 30, mark,
                     "Server bemerkt das Schliessen nicht")
        guest.wait_finished("classicube", serial)
        capture("geschlossen")

        # 3. Serverausfall bei bestehender Verbindung
        start()
        direct_connect()
        state = world("welt-2")
        observer.close()
        server.stop()
        guest.log("Server beendet")
        changed(state, "abbruch-anzeige", "ClassiCube zeigt den Verbindungsabbruch nicht")
        # Desktop bleibt bedienbar: Wechsel in die Textshell und zurueck (serielle Marken).
        guest.shell()
        guest.desktop()
        capture("desktop-nach-ausfall")
        serial = guest.mark()
        guest.qmp.click(812 + 28*(launches-1), 100 + 28*(launches-1), *guest.size)
        guest.wait_finished("classicube", serial)
        capture("geschlossen-2")
        free = guest.free_memory(memory_mark)
        print("CLASSICUBE_MULTIPLAYER PASS: Verbinden, Bewegung, Bloecke Gast->Server->Beobachter, Bloecke "
              "Beobachter->Gast, Schliessen bei offener Verbindung, Serverausfall ohne Einfrieren", flush=True)
        print("Bildschirmfotos visuell pruefen. Freier Speicher (KiB):", free, flush=True)
        return 0
    finally:
        if observer:
            observer.close()
        server.stop()
        events.close()
        if vm.poll() is None:
            vm.kill()
            vm.wait()
        print("Bilder und Protokoll:", work, flush=True)


if __name__ == "__main__":
    sys.exit(main())
