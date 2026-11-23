#!/usr/bin/env python3
"""Prueft ClassiCube-Mehrspieler mit ZWEI ZehOS-Gaesten am selben Server (scripts/classic-test-server.py).

Das ISO muss mit dem oeffentlichen Gegenstueck von --key (ausschliesslich Testschluessel) gebaut sein.
--package bezeichnet die unsignierten Paketdateien des Ports (z. B. classicube/1.1.0/files aus dem Branch packages);
--manifest ist das echte Manifest dieses Pakets: Version, capability (net, storage) und min-kernel werden von dort
uebernommen, die Dateien muessen Hash und Groesse treffen (sonst Fehler). Nur die Signatur stammt vom Testschluessel;
Signatur- und Katalogweg des echten Pakets prueft dieser Test nicht. Das Paket wird in beiden Gaesten lokal installiert.
Netz: Der Testserver lauscht auf 127.0.0.1 des Hosts. Jeder Gast hat sein eigenes QEMU-Usermode-Netz (je ein NAT);
darin ist der Host fuer beide als 10.0.2.2 erreichbar. Das braucht weder Bruecke noch Root-Rechte noch Portweiterleitung;
ein gemeinsames Gastnetz (socket/Bruecke) waere nur noetig, wenn die Gaeste einander direkt erreichen muessten.
Ablauf: beide Gaeste starten parallel (net dhcp, Paket installieren), verbinden ueber "Direct connect" mit eigenem Namen;
der Server meldet beide Spieler und leitet Spawn-Pakete an den jeweils anderen weiter. Gast A setzt und entfernt einen
Block: Server-Ereignis und Weiterleitung an Gast B (Ereignis "relay": der Server hat gesendet; ob ClassiCube es
zeichnet, belegt nur das Bild und nur als Hinweis ohne Negativkontrolle). Gast B bewegt
sich: Position kommt am Server an und wird an A weitergeleitet. A schliesst das Fenster bei offener Verbindung (Server
und B bemerken es, B laeuft weiter), verbindet erneut; Serverausfall bei beiden Gaesten (Abbruch sichtbar, Textshell und
Desktop wechseln weiter, Fenster schliesst sauber); zuletzt Start gegen den nicht mehr erreichbaren Server.
Bilder und Protokolle bleiben unter .tools/classicube-mp-guest/ erhalten.
"""
import argparse
import hashlib
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
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

def read_manifest(path, package):
    """Liest das echte Manifest und prueft, dass die Dateien in package genau den dort eingetragenen entsprechen."""
    result = {"capability": []}
    listed = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        key, _, value = line.partition("=")
        if key == "capability":
            result["capability"].append(value)
        elif key == "file":
            digest, size, name = value.split(" ", 2)
            listed[name] = (digest, int(size))
        elif key in ("id", "version", "name", "entry", "min-kernel"):
            result[key] = value
    present = {p.relative_to(package).as_posix(): p for p in package.rglob("*") if p.is_file()}
    assert set(present) == set(listed), f"Dateien weichen vom Manifest ab: {sorted(set(present) ^ set(listed))}"
    for name, (digest, size) in listed.items():
        data = present[name].read_bytes()
        assert len(data) == size and hashlib.sha256(data).hexdigest() == digest, f"{name} weicht vom Manifest ab"
    return result


class Session:
    """Ein Gast mit eigener VM, Bildschirmfotos und Fensterbedienung."""

    def __init__(self, label, player, args, work, server):
        self.label, self.player, self.server, self.accel = label, player, server, args.accel
        self.work = work
        self.step = 0
        self.launches = 0
        self.vm = None
        self.guest = None
        self.iso, self.memory = args.iso.resolve(), args.memory

    def boot(self, seed):
        tools.create_disk(seed, self.work / "data.qcow2")
        port = tools.free_port()
        self.vm = tools.start_qemu(self.iso, self.work / "data.qcow2", self.work, port, self.accel, None, self.memory)
        self.guest = tools.Guest(self.work, port, "", time.monotonic())
        g = self.guest
        g.wait("Desktop ist bereit", 240)
        g.connect()
        self.capture("boot")
        g.login(first=True)
        g.shell()
        g.dhcp()
        g.pkg("install-local /data/pkgs/classicube/1.1.0", "classicube 1.1.0 installiert.")
        g.desktop()
        self.memory_mark = g.mark()

    def stop(self):
        if self.vm and self.vm.poll() is None:
            self.vm.kill()
            self.vm.wait()

    def capture(self, name):
        self.step += 1
        ppm = self.work / "screen.ppm"
        self.guest.qmp.command("screendump", {"filename": str(ppm)})
        header, size, maximum, pixels = ppm.read_bytes().split(b"\n", 3)
        assert header == b"P6" and maximum == b"255", "Unerwartetes QEMU-Bildformat"
        self.guest.size = tuple(map(int, size.split()))
        self.guest.qmp.shot(self.work / f"{self.step:02d}-{name}.png")
        return pixels

    # Paketfenster 800x560 bei 32,80, Inhalt 798x519 ab 33,120; jeder weitere Start versetzt das Fenster um 28.
    def origin(self):
        offset = 28 * (self.launches - 1)
        return 33 + offset, 120 + offset

    def click(self, x, y):
        left, top = self.origin()
        self.guest.qmp.click(left + x, top + y, *self.guest.size)
        time.sleep(.5)

    def close_window(self):
        offset = 28 * (self.launches - 1)
        self.guest.qmp.click(812 + offset, 100 + offset, *self.guest.size)

    def view(self, pixels):
        width, _ = self.guest.size
        left, top = self.origin()
        left, top = left + 199, top + 130
        return b"".join(pixels[(row*width+left)*3:(row*width+left+400)*3] for row in range(top, top+260))

    @staticmethod
    def difference(first, second):
        return sum(first[i:i+3] != second[i:i+3] for i in range(0, len(first), 3*4))

    def changed(self, before, name, message, threshold=1500):
        deadline = time.monotonic() + 30
        best = 0
        while True:
            time.sleep(1)
            after = self.view(self.capture(name))
            best = max(best, self.difference(before, after))
            if best > threshold:
                print(f"[{self.label}] {name}: Unterschied {best} Stichproben (Schwelle {threshold})", flush=True)
                return after
            assert time.monotonic() < deadline, \
                f"[{self.label}] {message} (hoechster Unterschied {best}, Schwelle {threshold} Stichproben)"

    def observe(self, before, name):
        """Bildunterschied nur zur Ansicht (Hinweis, keine Pruefung): ohne Negativkontrolle kein Blockbeleg."""
        time.sleep(3)
        after = self.view(self.capture(name))
        print(f"[{self.label}] {name}: Bildunterschied {self.difference(before, after)} Stichproben (nur Hinweis)",
              flush=True)
        return after

    def button(self, name):
        for down in (True, False):
            self.guest.qmp.command("input-send-event", {"events": [{"type": "btn", "data": {"button": name, "down": down}}]})
            time.sleep(.15)

    def start(self):
        mark = self.guest.mark()
        self.guest.launch("ClassiCube")
        self.launches += 1
        self.guest.wait("APP classicube erstes Bild", 180, mark)
        time.sleep(3)
        self.capture("launcher")

    def enter_address(self, address):
        """Launcher (798x519): Direct connect, Name, Adresse (vorbelegt ":"), Connect."""
        self.click(399, 319)
        self.capture("direct-connect")
        if self.launches == 1:
            self.click(334, 139)
            self.guest.qmp.text(self.player)
            self.click(334, 184)
            self.guest.qmp.tap("end")
            for _ in range(8):
                self.guest.qmp.tap("backspace")
            self.guest.qmp.text(address)
        # Spaetere Starts: Mit capability=storage merkt sich der Launcher Name und Adresse des ersten Starts (beim
        # Gast belegt: Felder stehen schon richtig; erneutes Tippen haengt den Text an, auch nach Leeren des Feldes).
        # Die Adresse bleibt in allen Starts eines Gasts dieselbe.
        self.capture("eingaben")

    def connect(self, address=None):
        mark = self.server.mark()
        self.enter_address(address or f"10.0.2.2:{self.server.port}")
        self.click(289, 279)
        return mark

    def world(self, name):
        deadline = time.monotonic() + 120
        while True:
            region = self.view(self.capture(name))
            if len({region[i:i+3] for i in range(0, len(region), 3*7)}) >= 24:
                return region
            assert time.monotonic() < deadline, f"[{self.label}] {name}: keine Welt sichtbar"
            time.sleep(1)

    def look_down(self, capture_mouse=True):
        """Maus fangen (ein weiterer Klick waere danach ein Spielklick, daher nur einmal) und zum Boden schauen."""
        if capture_mouse:
            self.click(399, 259)
            time.sleep(2)
        width, height = self.guest.size
        left, top = self.origin()
        for dy in range(1, 9):
            self.guest.qmp.move(left + 399, top + 259 + 25*dy, width, height)
            time.sleep(.1)
        time.sleep(1)
        return self.view(self.capture("blick-boden"))

    def desktop_alive(self, name):
        """Wechsel in die Textshell und zurueck (serielle Marken) belegt, dass der Kernel bedienbar bleibt."""
        self.guest.shell()
        self.guest.desktop()
        self.capture(name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--key", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True,
                        help="echtes Manifest des Pakets (classicube/1.1.0/manifest aus dem Branch packages): "
                             "Version, Berechtigungen und min-kernel werden von dort uebernommen, Dateien dagegen geprueft")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    parser.add_argument("--memory", default="512M", help="Gastspeicher je VM (QEMU -m)")
    args = parser.parse_args()
    work = ROOT / ".tools/classicube-mp-guest" / uuid.uuid4().hex[:12]
    work.mkdir(parents=True)
    manifest = read_manifest(args.manifest, args.package)
    # Abweichung vom echten Manifest ist ein Fehler; nur die Signatur kommt vom Testschluessel (das Test-ISO vertraut ihr).
    assert manifest["id"] == "classicube" and "net" in manifest["capability"], "Manifest ohne capability=net"
    files = [str(p.resolve()) + "=" + p.relative_to(args.package).as_posix()
             for p in sorted(args.package.rglob("*")) if p.is_file()]
    subprocess.run([sys.executable, str(ROOT / "scripts/build-package.py"), "package", "--key", str(args.key.resolve()),
                    "--repo", str(work / "seed/pkgs"), "--id", manifest["id"], "--version", manifest["version"],
                    "--name", manifest["name"], "--entry", manifest["entry"], "--min-kernel", manifest["min-kernel"],
                    *(["--network"] if "net" in manifest["capability"] else []),
                    *(["--storage"] if "storage" in manifest["capability"] else []), *files], check=True)
    events = (work / "server-events.jsonl").open("w", encoding="utf-8")

    def write_event(event):
        try:
            events.write(json.dumps(event) + "\n")
            events.flush()
        except ValueError:
            pass  # Datei beim Beenden schon zu; ein spaetes Ereignis eines Server-Fadens ist hier ohne Belang.

    server = classic.ClassicServer("127.0.0.1", 0, on_event=write_event)
    server.start()
    for label in "ab":
        (work / label).mkdir()
    a = Session("A", "zehos", args, work / "a", server)
    b = Session("B", "gast2", args, work / "b", server)
    sessions = (a, b)

    def server_event(predicate, timeout, mark, message):
        try:
            return server.wait_for(predicate, timeout, mark)
        except TimeoutError:
            for session in sessions:
                session.capture("fehlt-" + message.split()[0].lower())
            raise AssertionError(message) from None

    def joined(session, mark, timeout=120):
        server_event(lambda e: e["type"] == "joined" and e["player"] == session.player, timeout, mark,
                     f"Login von {session.player} am Server fehlt")
        session.guest.log(f"{session.label} am Server angemeldet")

    def relayed(to, opcode, mark, message, timeout=30):
        return server_event(lambda e: e["type"] == "relay" and e["to"] == to.player and e["opcode"] == opcode, timeout,
                            mark, message)

    try:
        with ThreadPoolExecutor(max_workers=2) as pool:
            futures = [pool.submit(session.boot, work / "seed") for session in sessions]
            for future in futures:
                future.result()

        # 1. Beide verbinden; jeder bekommt den Spawn des anderen
        mark = server.mark()
        a.start()
        a.connect()
        joined(a, mark)
        a.world("welt")
        # Weg vom Spawnpunkt: Sonst steht B beim Verbinden im Kopf von A und dessen Modell fuellt das Bild von B.
        a.look_down()
        mark_a = server.mark()
        a.guest.qmp.hold("w", 0.6)
        server_event(lambda e: e["type"] == "position" and e["player"] == a.player and
                     (e["x"], e["z"]) != server.world.spawn()[::2], 30, mark_a, "Bewegung von A kommt nicht am Server an")
        mark_b = server.mark()
        b.start()
        b.connect()
        joined(b, mark_b)
        state_b = b.world("welt")
        relayed(a, 0x07, mark_b, "Spawn von B kommt nicht bei A an")
        relayed(b, 0x07, mark, "Spawn von A kommt nicht bei B an")
        names = {e["player"] for e in server.events if e["type"] == "joined"}
        assert names == {a.player, b.player}, f"Server kennt die Spieler {names}"
        assert len(server.clients) == 2, "Server hat nicht zwei verbundene Clients"

        # 2. A setzt und entfernt einen Block; B erhaelt beides
        mark = server.mark()
        b_before = state_b
        a.button("right")
        placed = server_event(lambda e: e["type"] == "block" and e["player"] == a.player and e["mode"] == 1, 30, mark,
                              "Von A gesetzter Block kommt nicht am Server an")
        relayed(b, 0x06, mark, "Gesetzter Block wird nicht an B weitergegeben")
        b_placed = b.observe(b_before, "block-von-a")
        a.capture("a-block-gesetzt")
        mark = server.mark()
        a.button("left")
        removed = server_event(lambda e: e["type"] == "block" and e["player"] == a.player and e["mode"] == 0, 30, mark,
                               "Von A entfernter Block kommt nicht am Server an")
        relayed(b, 0x06, mark, "Entfernter Block wird nicht an B weitergegeben")
        b.observe(b_placed, "block-von-a-weg")
        assert (placed["x"], placed["y"], placed["z"]) == (removed["x"], removed["y"], removed["z"]), \
            "Setzen und Entfernen betreffen verschiedene Bloecke"

        # 3. Umgekehrt: B setzt einen Block; Server und Weiterleitung an A
        # B steht hinter A und sieht nach unten; sein Block liegt ausserhalb von As Blickfeld: Beleg nur am Server.
        b.look_down()
        a_before = a.view(a.capture("a-vor-block-von-b"))
        mark = server.mark()
        b.button("right")
        server_event(lambda e: e["type"] == "block" and e["player"] == b.player and e["mode"] == 1, 30, mark,
                     "Von B gesetzter Block kommt nicht am Server an")
        relayed(a, 0x06, mark, "Gesetzter Block von B wird nicht an A weitergegeben")
        b.capture("b-block-gesetzt")
        a.observe(a_before, "a-nach-block-von-b")

        # 4. B bewegt sich; die Position geht an A
        mark = server.mark()
        b.guest.qmp.hold("w", 1.5)
        server_event(lambda e: e["type"] == "position" and e["player"] == b.player and
                     (e["x"], e["z"]) != server.world.spawn()[::2], 30, mark, "Bewegung von B kommt nicht am Server an")
        relayed(a, 0x08, mark, "Position von B wird nicht an A weitergegeben")
        b.capture("b-bewegt")
        a.capture("a-sieht-b")

        # 5. A schliesst das Fenster bei offener Verbindung; B bemerkt es und laeuft weiter
        a.guest.qmp.tap("esc")
        time.sleep(2)
        mark, serial = server.mark(), a.guest.mark()
        a.close_window()
        server_event(lambda e: e["type"] == "disconnect" and e["player"] == a.player, 30, mark,
                     "Server bemerkt das Schliessen von A nicht")
        relayed(b, 0x0C, mark, "Abgang von A wird nicht an B weitergegeben")
        a.guest.wait_finished("classicube", serial)
        a.capture("a-geschlossen")
        b.capture("b-nach-abgang")

        # 6. A verbindet erneut
        mark = server.mark()
        a.start()
        a.connect()
        joined(a, mark)
        relayed(b, 0x07, mark, "Erneuter Spawn von A kommt nicht bei B an")
        state_a, state_b = a.world("welt-2"), b.view(b.capture("b-nach-rejoin"))

        # 7. Serverausfall bei beiden
        server.stop()
        for session in sessions:
            session.guest.log("Server beendet")
        a.changed(state_a, "abbruch-anzeige", "A zeigt den Verbindungsabbruch nicht")
        b.changed(state_b, "abbruch-anzeige", "B zeigt den Verbindungsabbruch nicht")
        for session in sessions:
            session.desktop_alive("desktop-nach-ausfall")
        for session in sessions:
            serial = session.guest.mark()
            session.close_window()
            session.guest.wait_finished("classicube", serial)
            session.capture("geschlossen")

        # 8. Start gegen den nicht mehr erreichbaren Server (Port geschlossen): Fehleranzeige, Desktop bedienbar
        for session in sessions:
            session.start()
            session.enter_address(f"10.0.2.2:{server.port}")
            before = session.view(session.capture("vor-connect"))
            session.click(289, 279)
            session.changed(before, "unerreichbar", "Launcher zeigt keine Fehlermeldung")
            time.sleep(3)
            assert not server.clients, "Server hat trotz Stopp einen Client angenommen"
            session.desktop_alive("desktop-unerreichbar")
            serial = session.guest.mark()
            session.close_window()
            session.guest.wait_finished("classicube", serial)
            session.capture("geschlossen-unerreichbar")
        free = {s.label: s.guest.free_memory(s.memory_mark) for s in sessions}
        print("CLASSICUBE_MULTIPLAYER PASS: zwei ZehOS-Gaeste am selben Server: Verbinden, Spawn gegenseitig, Bloecke "
              "A->B und B->A (am Server gesendet), Position B->A, Abgang, erneutes Verbinden, Serverausfall, Start gegen unerreichbaren "
              "Server ohne Einfrieren", flush=True)
        print("Bildschirmfotos visuell pruefen. Gastspeicher:", args.memory, "Freier Speicher (KiB):", free, flush=True)
        return 0
    finally:
        server.stop()
        events.close()
        for session in sessions:
            session.stop()
        print("Bilder und Protokoll:", work, flush=True)


if __name__ == "__main__":
    sys.exit(main())
