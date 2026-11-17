#!/usr/bin/env python3
"""Prueft das ClassiCube-Paket als Testpaket in einer frischen QEMU-Platte.

Das ISO muss mit dem oeffentlichen Gegenstueck von --key (ausschliesslich Testschluessel) gebaut sein.
--package bezeichnet die unsignierten Paketdateien des Ports (Wasm und Ressourcen).
Vorhandene VM-Images werden nicht gelesen. Bilder/Protokolle bleiben unter .tools/classicube-guest/ erhalten.
Ablauf: lokal installieren, Start aus dem Menue, Welt sichtbar, Bewegung (W), Blick per Maus,
Block entfernen (links) und setzen (rechts), Schliessen ueber das Fenster, drei Starts mit freiem Speicher.
"""
import argparse
import importlib.util
from pathlib import Path
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--key", type=Path, required=True)
    parser.add_argument("--entry", default="classicube.wasm", help="Wasm-Datei im Paketverzeichnis")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    # Paket aus build.py --raster-toggle: F8 wechselt zwischen Kernel- und Gast-Rasterisierung derselben Szene.
    parser.add_argument("--raster-compare", action="store_true")
    args = parser.parse_args()
    work = ROOT / ".tools/classicube-guest" / uuid.uuid4().hex[:12]
    work.mkdir(parents=True)
    files = [str(p.resolve()) + "=" + p.relative_to(args.package).as_posix()
             for p in sorted(args.package.rglob("*")) if p.is_file()]
    subprocess.run([sys.executable, str(ROOT / "scripts/build-package.py"), "package", "--key", str(args.key.resolve()),
                    "--repo", str(work / "seed/pkgs"), "--id", "classicube", "--version", "1.0.0", "--name", "ClassiCube",
                    "--entry", args.entry, "--min-kernel", "0.2.22", *files], check=True)
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

    def button(name):
        for down in (True, False):
            guest.qmp.command("input-send-event", {"events":[{"type":"btn", "data":{"button":name,"down":down}}]})
            time.sleep(.15)

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
        # Nach unten blicken, damit Block-Klicks sicher den Boden treffen.
        for dy in range(1, 9):
            guest.qmp.move(33+399+160, 120+259+25*dy, width, height)
            time.sleep(.1)
        state = changed(state, "blick-boden", "Blick nach unten veraendert die Ansicht nicht")
        # Erst setzen, dann denselben Block entfernen: Ein entfernter Block direkt vor dem Spieler laesst sich nicht
        # wieder setzen (ClassiCube verbietet Bloecke im Spielerkoerper), je nach Zufallswelt.
        button("right")
        state = changed(state, "block-gesetzt", "Rechtsklick setzt keinen Block")
        button("left")
        changed(state, "block-entfernt", "Linksklick entfernt keinen Block")
        # Esc gibt den Zeiger frei; danach ist das Schliessen-Symbol des Fensters erreichbar.
        guest.qmp.tap("esc")
        time.sleep(1)
        for run in range(3):
            if run:
                start()
                world(f"neustart-{run}")
            # Ist das Fenster beim Start aktiv, faengt ClassiCube die Maus sofort; Esc gibt sie frei und oeffnet
            # das Pausenmenue.
            if run:
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
        print("CLASSICUBE_GUEST PASS: lokal installieren, Start aus Menue, Welt, W-Bewegung, Mausblick, "
              "Block entfernen/setzen, 2x Schliessen ueber X, 1x Quit game", flush=True)
        print("Bildschirmfotos visuell pruefen. Freier Speicher (KiB):", free, flush=True)
        return 0
    finally:
        if vm.poll() is None:
            vm.kill()
            vm.wait()
        print("Bilder und Protokoll:", work, flush=True)


if __name__ == "__main__":
    sys.exit(main())
