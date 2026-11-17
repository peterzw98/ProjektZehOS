#!/usr/bin/env python3
"""Prueft das ClassiCube-Paket als Testpaket in einer frischen QEMU-Platte.

Das ISO muss mit dem oeffentlichen Gegenstueck von --key (ausschliesslich Testschluessel) gebaut sein.
--package bezeichnet die unsignierten Paketdateien des Ports (Wasm und Ressourcen).
Das Paket mit build.py --world-seed 1 bauen: Die Schritte sind fuer diese feste Welt abgestimmt (offene Wiese am
Spawnpunkt); mit Zufallswelt stand der Spieler etwa in einer Baumkrone oder Wasser trieb die Schwelle hoch.
Vorhandene VM-Images werden nicht gelesen. Bilder/Protokolle bleiben unter .tools/classicube-guest/ erhalten.
Ablauf: lokal installieren, Start aus dem Menue, Launcher "Singleplayer", Welt sichtbar, Bewegung (W), Blick per Maus,
Block entfernen (links) und setzen (rechts), "Save level" im Pausenmenue, Schliessen ueber das Fenster, drei Starts
mit freiem Speicher. Ohne --storage muss das Speichern scheitern, ohne das Spiel zu beenden. Mit --storage erhaelt das
Paket den App-Speicher /save (Etappe 13.4): die Welt wird mit einem zusaetzlich gesetzten Block gespeichert, im
zweiten Start ueber "Load level" gefunden und geladen; danach liest das Skript options.txt (Sichtweite per F
umgeschaltet) und die Welt aus der Testplatte. --disk-full fuellt die Platte vorab, Speichern muss dann scheitern.
"""
import argparse
import importlib.util
from pathlib import Path
import struct
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)


def disk_raw(work: Path) -> Path:
    """Rohkopie der Datenplatte (qcow2 -> raw) zum Lesen auf dem Host."""
    tool, environment = tools.qemu_img()
    raw = work / "data.raw"
    subprocess.run([*tool, "convert", "-O", "raw", str(work / "data.qcow2"), str(raw)], check=True, env=environment)
    return raw


def fat16(raw: Path):
    """Minimaler Leser fuer die vvfat-Testplatte (MBR, eine FAT16-Partition): freie Bytes, Clustergroesse und eine
    Funktion, die eine Datei per Pfad (lange Namen, Grossschreibung egal) liest oder None liefert."""
    data = raw.read_bytes()
    start = struct.unpack_from("<I", data, 446 + 8)[0] * 512
    sector, per_cluster, reserved, fats, roots, total, _, fat_sectors = struct.unpack_from("<HBHBHHBH", data, start + 11)
    total = total or struct.unpack_from("<I", data, start + 32)[0]
    cluster = sector * per_cluster
    fat = start + reserved * sector
    root = fat + fats * fat_sectors * sector
    first = root + roots * 32
    clusters = (total * sector - (first - start)) // cluster
    table = struct.unpack_from(f"<{clusters + 2}H", data, fat)

    def chain(number):
        while 2 <= number < 0xFFF8:
            yield data[first + (number - 2) * cluster:first + (number - 1) * cluster]
            number = table[number]

    def entries(raw_dir):
        long_name = []
        for offset in range(0, len(raw_dir), 32):
            entry = raw_dir[offset:offset + 32]
            if entry[0] == 0:
                return
            if entry[0] == 0xE5:
                long_name = []
            elif entry[11] == 0x0F:
                part = (entry[1:11] + entry[14:26] + entry[28:32]).decode("utf-16-le").split("\0")[0]
                long_name = [part] + ([] if entry[0] & 0x40 else long_name)
            else:
                base, extension = (entry[0:8].decode("ascii", "replace").rstrip(),
                                   entry[8:11].decode("ascii", "replace").rstrip())
                short = base + ("." + extension if extension else "")
                yield ("".join(long_name) or short).lower(), entry[11], \
                    struct.unpack_from("<H", entry, 26)[0], struct.unpack_from("<I", entry, 28)[0]
                long_name = []

    def read(path):
        current = data[root:first]
        for index, name in enumerate(path.lower().split("/")):
            found = next((e for e in entries(current) if e[0] == name), None)
            if found is None:
                return None
            current = b"".join(chain(found[2]))
            if index == len(path.split("/")) - 1:
                return current if found[1] & 0x10 else current[:found[3]]
        return current

    def names(path):
        """Namen in einem Ordner (fuer den Kontoordner mit unbekannter ID)."""
        directory = read(path)
        return [] if directory is None else [e[0] for e in entries(directory) if e[0] not in (".", "..")]

    read.names = names
    return table[2:].count(0) * cluster, cluster, read


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--key", type=Path, required=True)
    parser.add_argument("--entry", default="classicube.wasm", help="Wasm-Datei im Paketverzeichnis")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    # Paket aus build.py --raster-toggle: F8 wechselt zwischen Kernel- und Gast-Rasterisierung derselben Szene.
    parser.add_argument("--raster-compare", action="store_true")
    parser.add_argument("--storage", action="store_true", help="Paket mit capability=storage: Welt speichern und laden")
    parser.add_argument("--disk-full", action="store_true", help="mit --storage: fast volle Datenplatte, Speichern scheitert")
    args = parser.parse_args()
    if args.disk_full and not args.storage:
        parser.error("--disk-full braucht --storage")
    work = ROOT / ".tools/classicube-guest" / uuid.uuid4().hex[:12]
    work.mkdir(parents=True)
    files = [str(p.resolve()) + "=" + p.relative_to(args.package).as_posix()
             for p in sorted(args.package.rglob("*")) if p.is_file()]
    subprocess.run([sys.executable, str(ROOT / "scripts/build-package.py"), "package", "--key", str(args.key.resolve()),
                    "--repo", str(work / "seed/pkgs"), "--id", "classicube", "--version", "1.0.0", "--name", "ClassiCube",
                    "--entry", args.entry, "--min-kernel", "0.2.22", *(["--storage"] if args.storage else []), *files],
                   check=True)
    tools.create_disk(work / "seed", work / "data.qcow2")
    if args.disk_full:
        # Die Quote laesst sich nicht vorbelegen: Der Kontoordner traegt eine bei der Ersteinrichtung gewuerfelte ID.
        # Stattdessen fuellt eine Datei die Platte so weit, dass Installation, Anmeldung und Ordner noch passen
        # (Paket plus rund 15 Cluster, gemessen 13), die gespeicherte Welt (rund 75 KB, 10 Cluster) aber nicht mehr.
        free, cluster, _ = fat16(disk_raw(work))
        package = sum(-(-p.stat().st_size // cluster) for p in (work / "seed/pkgs/classicube/1.0.0").rglob("*") if p.is_file())
        with (work / "seed/fuellung.bin").open("wb") as filler:
            filler.truncate(free - (package + 20) * cluster)
        tools.create_disk(work / "seed", work / "data.qcow2")
    port = tools.free_port()
    vm = tools.start_qemu(args.iso.resolve(), work / "data.qcow2", work, port, args.accel, None)
    guest = tools.Guest(work, port, "", time.monotonic())
    step = 0

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

    # Erstes Paketfenster 800x560 bei 32,80, Inhalt 798x519 ab 33,120 (wie NetSurf/Doom).
    # Verglichen wird ein Ausschnitt um das Fadenkreuz, damit ziehende Wolken am Rand nicht zaehlen.
    def view(pixels):
        width, _ = guest.size
        left, top = 33 + 199, 120 + 130
        return b"".join(pixels[(row*width+left)*3:(row*width+left+400)*3] for row in range(top, top+260))

    def world(name):
        deadline = time.monotonic() + 120
        while True:
            region = view(capture(name))
            # Himmel und Gelaende ergeben viele Farben; Lade- oder Leerbild nur wenige.
            colours = {region[i:i+3] for i in range(0, len(region), 3*7)}
            if len(colours) >= 32:
                return region
            if time.monotonic() > deadline:
                raise AssertionError(f"{name}: keine Welt sichtbar ({len(colours)} Farben im Ausschnitt)")
            time.sleep(1)

    def difference(first, second):
        return sum(first[i:i+3] != second[i:i+3] for i in range(0, len(first), 3*4))

    # Wasser, Lava und nachladende Chunks aendern das Bild auch ohne Eingabe. Eine Eingabe gilt erst, wenn sie
    # deutlich mehr Pixel aendert als ein gleich langes Kontrollfenster ohne Eingabe (siehe idle_threshold).
    threshold = 0

    def idle_threshold(before):
        # Unter TCG laedt die Welt noch Sekunden nach; erst wenn ein Kontrollfenster ruhig ist, taugt es als Schwelle.
        nonlocal threshold
        for attempt in range(10):
            time.sleep(3)
            idle = view(capture("kontrolle-ohne-eingabe"))
            change = difference(before, idle)
            if change < 2000:
                break
            before = idle
        threshold = max(2 * change, 500)
        return idle

    def changed(before, name, message):
        # Unter TCG dauert ein Bild bis zu einigen Sekunden (unter Wasser mehr); auf das naechste Bild warten.
        deadline = time.monotonic() + 30
        while True:
            time.sleep(1)
            after = view(capture(name))
            if difference(before, after) > threshold:
                return after
            assert time.monotonic() < deadline, f"{message} (Schwelle {threshold} Pixel)"

    def start():
        # Menuesuche nach dem Menueeintrag; die serielle Marke nennt die Paket-ID.
        mark = guest.mark()
        guest.launch("ClassiCube")
        guest.wait("APP classicube erstes Bild", 180, mark)
        # Erst zeigt der Launcher; "Singleplayer" liegt 110 Pixel unter der Mitte des Inhalts (798x519). Jeder Start
        # versetzt das Fenster um 28 Pixel.
        time.sleep(3)
        launcher = view(capture("launcher"))
        run = launches.pop(0)
        offset = 28 * run
        if run == 0:
            # Enter ohne gewaehltes Widget startet Singleplayer (onEnterWidget); vorher zeigte es auf das unter
            # ZehOS ausgeblendete "Sign in" ohne OnClick und beendete die App mit einem Trap.
            guest.qmp.tap("ret")
        else:
            guest.qmp.click(33+399+offset, 120+259+110+offset, *guest.size)
        # Die verrauschte Launcher-Flaeche hat selbst viele Farben; erst wenn sie verschwunden ist, zaehlt world().
        deadline = time.monotonic() + 60
        while difference(launcher, view(capture("launcher-verlassen"))) < len(launcher) // 24:
            assert time.monotonic() < deadline, "Singleplayer im Launcher startet kein Spiel"
            time.sleep(1)

    launches = [0, 1, 2]

    def button(name):
        for down in (True, False):
            guest.qmp.command("input-send-event", {"events":[{"type":"btn", "data":{"button":name,"down":down}}]})
            time.sleep(.15)

    # Pausenmenue (Menus.c, Ankerpunkt Inhaltsmitte 399,259): "Load level..." bei +160,0, "Save level..." bei +160,+50;
    # im Speicherdialog "Save" bei 0,+20, in der Liste von "Load level" der erste Eintrag bei 0,-100.
    def save_level(run, expect_saved):
        left, top = 33 + 399 + 28*run, 120 + 259 + 28*run
        pause = view(capture("pausenmenue-speichern"))
        guest.qmp.click(left + 160, top + 50, *guest.size)
        time.sleep(2)
        dialog = view(capture("speichern-dialog"))
        assert difference(pause, dialog) > len(pause) // 120, "Save level... oeffnet keinen Dialog"
        # Das Namensfeld ist gewaehlt und enthaelt den Weltnamen.
        guest.qmp.tap("end")
        for _ in range(24):
            guest.qmp.tap("backspace", hold=0.02)
        guest.qmp.text("zehtest")
        guest.qmp.click(left, top + 20, *guest.size)
        # Nach Erfolg kehrt ClassiCube ins Pausenmenue zurueck, nach einem Fehler bleibt der Dialog (Fehler im Chat).
        # Komprimieren und Schreiben dauern unter wasm3 einige Sekunden.
        deadline = time.monotonic() + 30
        while True:
            time.sleep(2)
            after = view(capture("gespeichert" if expect_saved else "speichern-abgelehnt"))
            saved = difference(pause, after) < difference(dialog, after)
            if saved or time.monotonic() > deadline:
                break
        assert saved == expect_saved, "Welt gespeichert" if saved else "Speichern scheitert"

    def load_level(run, fresh):
        # Die Liste bleibt nach dem Laden offen; "Done" (unten, 0,+213) fuehrt ins Pausenmenue, "Back to game" liegt
        # an derselben Stelle. Die geladene Welt traegt den vor dem Speichern gesetzten Block und den gespeicherten
        # Spawnpunkt; sie muss sich deshalb deutlich von der frisch erzeugten Welt unterscheiden.
        left, top = 33 + 399 + 28*run, 120 + 259 + 28*run
        guest.qmp.click(left + 160, top, *guest.size)
        time.sleep(2)
        capture("load-level-liste")
        guest.qmp.click(left, top - 100, *guest.size)
        time.sleep(10)
        capture("welt-geladen-liste")
        guest.qmp.click(left, top + 213, *guest.size)
        time.sleep(2)
        guest.qmp.click(left, top + 213, *guest.size)
        loaded = world("welt-geladen")
        time.sleep(5)
        loaded = view(capture("welt-geladen-ruhig"))
        change = difference(fresh, loaded)
        print(f"Geladene Welt weicht von der frisch erzeugten um {change} Stichproben ab (Schwelle {threshold})",
              flush=True)
        assert change > threshold, "Load level laedt die gespeicherte Welt nicht"

    try:
        guest.wait("Desktop ist bereit", 180)
        guest.connect()
        capture("boot")
        guest.login(first=True)
        guest.shell()
        guest.pkg("install-local /data/pkgs/classicube/1.0.0", "classicube 1.0.0 installiert.")
        guest.desktop()
        memory_mark = guest.mark()
        start()
        state = world("welt")
        # Measure_ZehOS.c meldet alle 5 s Bildrate und Anteile im Chat; zwei Berichtsfenster abwarten.
        time.sleep(12)
        capture("messung")
        if args.raster_compare:
            # Ein Lauf, zwei Rasterwege: Bild und Messzeile mit Kernel (Standard), dann mit SoftGPU im Gast.
            kernel = view(capture("raster-kernel"))
            guest.qmp.tap("f8")
            time.sleep(12)
            software = view(capture("raster-softgpu"))
            samples = len(kernel) // (3 * 4)
            print(f"RASTER_COMPARE abweichend {difference(kernel, software)} von {samples} Stichproben "
                  "(Wasser, Wolken und Nachladen aendern sich auch ohne Wechsel)", flush=True)
            guest.qmp.tap("f8")
            time.sleep(3)
        # Klick in den Inhalt startet den Zeigerfang (relative Maus, poll_event Typ 9). Hat ClassiCube die Maus beim
        # Start schon gefangen, wirkt er als Spielklick; der Ausgangszustand wird deshalb erst danach aufgenommen.
        guest.qmp.click(33+399, 120+259, *guest.size)
        time.sleep(2)
        state = idle_threshold(view(capture("nach-fang")))
        guest.qmp.hold("w", 1.5)
        state = changed(state, "bewegung-w", "W veraendert die Ansicht nicht")
        # hypothese: der Kernel bildet aus absoluten Tablet-Bewegungen die relativen Deltas; sonst auf "rel" umstellen.
        width, height = guest.size
        for dx in range(1, 9):
            guest.qmp.move(33+399+20*dx, 120+259, width, height)
            time.sleep(.1)
        state = changed(state, "blick-maus", "Mausbewegung dreht den Blick nicht")
        # Nach unten blicken, damit Block-Klicks sicher den Boden treffen. Mit 25 Pixeln je Schritt lag das Ziel an der
        # Reichweitengrenze (rund 5 Bloecke); je nach Laufweite von W fiel der Rechtsklick dann ins Leere.
        for dy in range(1, 9):
            guest.qmp.move(33+399+160, 120+259+55*dy, width, height)
            time.sleep(.1)
        state = changed(state, "blick-boden", "Blick nach unten veraendert die Ansicht nicht")
        # Erst setzen, dann denselben Block entfernen: Ein entfernter Block direkt vor dem Spieler laesst sich nicht
        # wieder setzen (ClassiCube verbietet Bloecke im Spielerkoerper).
        button("right")
        state = changed(state, "block-gesetzt", "Rechtsklick setzt keinen Block")
        button("left")
        state = changed(state, "block-entfernt", "Linksklick entfernt keinen Block")
        if args.storage:
            # Die gespeicherte Welt soll sich von der frisch erzeugten unterscheiden.
            button("right")
            changed(state, "block-fuer-speicherstand", "Rechtsklick setzt keinen Block")
            # F schaltet die Sichtweite um (Option viewdist); ClassiCube schreibt options.txt beim Beenden.
            guest.qmp.tap("f")
            time.sleep(1)
        # Esc gibt den Zeiger frei und oeffnet das Pausenmenue; danach ist das Schliessen-Symbol erreichbar.
        guest.qmp.tap("esc")
        time.sleep(1)
        save_level(0, args.storage and not args.disk_full)
        for run in range(3):
            if run:
                start()
                world(f"neustart-{run}")
                # Nachladende Chunks abwarten; die Ansicht dient als Vergleich fuer die geladene Welt.
                time.sleep(5)
                fresh = view(capture(f"neustart-{run}-ruhig"))
            # Ist das Fenster beim Start aktiv, faengt ClassiCube die Maus sofort; Esc gibt sie frei und oeffnet
            # das Pausenmenue.
            if run:
                guest.qmp.tap("esc")
                time.sleep(3)
            if run == 1 and args.storage and not args.disk_full:
                load_level(run, fresh)
                guest.qmp.tap("esc")
                time.sleep(3)
            mark = guest.mark()
            if run < 2:
                guest.qmp.click(812+28*run, 100+28*run, *guest.size)
            else:
                # Letzter Lauf ueber "Quit game" (unten rechts im Pausenmenue, 120 breit, 5 Pixel Rand): Das Spiel
                # muss enden statt neu zu starten.
                capture("pausenmenue")
                guest.qmp.click(33+798-5-60+28*run, 120+519-5-20+28*run, *guest.size)
            time.sleep(2)
            capture(f"schliessen-{run}")
            guest.wait_finished("classicube", mark)
        free = guest.free_memory(memory_mark)
        assert len(free) == 3, f"Drei aufgeraeumte Laeufe erwartet: {free}"
        assert free[0]-free[-1] <= 8*1024, f"Speicherverlust ueber Starts: {free} KiB"
        saving = "Speichern ohne /save scheitert ohne Abbruch"
        if args.disk_full:
            saving = "Speichern auf voller Platte scheitert ohne Abbruch"
        elif args.storage:
            # Platte nach dem Lauf auf dem Host lesen; der Kontoordner hat eine zufaellige ID.
            vm.kill()
            vm.wait()
            _, _, read = fat16(disk_raw(work))
            accounts = read.names(".zehos-apps/classicube")
            assert len(accounts) == 1, f"Ein Kontoordner erwartet: {accounts}"
            options = read(f".zehos-apps/classicube/{accounts[0]}/options.txt")
            assert options and b"viewdist=" in options, f"options.txt ohne viewdist: {options!r}"
            world_file = read(f".zehos-apps/classicube/{accounts[0]}/maps/zehtest.cw")
            assert world_file and world_file[:2] == b"\x1f\x8b", "maps/zehtest.cw fehlt oder ist kein gzip"
            viewdist = next(line for line in options.splitlines() if line.startswith(b"viewdist=")).decode()
            print(f"Platte: options.txt mit {viewdist!r}, "
                  f"maps/zehtest.cw {len(world_file)} Bytes", flush=True)
            saving = "Welt speichern, nach Neustart ueber Load level laden, options.txt auf der Platte"
        print("CLASSICUBE_GUEST PASS: lokal installieren, Start aus Menue, Welt, W-Bewegung, Mausblick, "
              f"Block entfernen/setzen, {saving}, 2x Schliessen ueber X, 1x Quit game", flush=True)
        print("Bildschirmfotos visuell pruefen. Freier Speicher (KiB):", free, flush=True)
        return 0
    finally:
        if vm.poll() is None:
            vm.kill()
            vm.wait()
        print("Bilder und Protokoll:", work, flush=True)


if __name__ == "__main__":
    sys.exit(main())
