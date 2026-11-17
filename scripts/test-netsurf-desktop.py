#!/usr/bin/env python3
"""Prueft den vollstaendigen lokalen NetSurf-Browser als Testpaket in einer frischen QEMU-Platte.

Das ISO muss mit dem oeffentlichen Gegenstueck von --key (ausschliesslich Testschluessel) gebaut sein.
--package bezeichnet die unsignierten Dateien aus experiments/NetSurf.Wasi/bin/package oder dem CI-Artefakt.
Vorhandene VM-Images werden nicht gelesen. Bilder/Protokolle bleiben unter .tools/netsurf-guest/ erhalten.
Das Paket erhaelt die Netzwerkberechtigung; HTTP-Seiten liefert ein lokaler Testserver, den der Gast als 10.0.2.2 erreicht.
"""
import argparse
import importlib.util
from pathlib import Path
import subprocess
import struct
import sys
import threading
import time
import uuid
import zlib

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)
spec = importlib.util.spec_from_file_location("netsurf_host", ROOT / "experiments/NetSurf.Wasi/host/run_tests.py")
pages = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pages)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--key", type=Path, required=True)
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    args = parser.parse_args()
    work = ROOT / ".tools/netsurf-guest" / uuid.uuid4().hex[:12]
    work.mkdir(parents=True)
    files = [str(p.resolve()) + "=" + p.relative_to(args.package).as_posix()
             for p in sorted(args.package.rglob("*")) if p.is_file()]
    probe = work / "reset-probe.html"
    probe.write_text('<!doctype html><style>body{margin:0;background:#e0ffff}a{display:block;width:100%;height:1500px;background:#e0ffff}</style><a href="ziel.html">Reset darf diesen Link nicht anklicken.</a>', encoding="utf-8")
    files.append(str(probe) + "=pages/reset-probe.html")
    subprocess.run([sys.executable, str(ROOT / "scripts/build-package.py"), "package", "--key", str(args.key.resolve()),
                    "--repo", str(work / "seed/pkgs"), "--id", "netsurf", "--version", "1.0.0", "--name", "NetSurf",
                    "--entry", "netsurf.wasm", "--network", *files], check=True)
    server = pages.http.server.ThreadingHTTPServer(("127.0.0.1", 0), pages.TestServer)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    base = f"http://10.0.2.2:{server.server_address[1]}"
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
        width, height = guest.size
        raw = b"".join(b"\0" + pixels[row * width * 3:(row + 1) * width * 3] for row in range(height))

        def chunk(kind, data):
            return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

        # Das Belegbild muss genau die Pixel enthalten, die der Test prueft.
        (work / f"{step:02d}-{name}.png").write_bytes(
            b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))
        return pixels

    def page(name, color, points=()):
        deadline = time.monotonic() + 30
        while True:
            pixels = capture(name)
            width, height = guest.size
            # Die alte Seite ist schon vor dem Resize sichtbar; erst ein vollstaendiges Bild zaehlt.
            edges_ready = all(0 <= x < width and 0 <= y < height
                              and len(pixels[(y * width + x) * 3:(y * width + x) * 3 + 3]) == 3
                              and pixels[(y * width + x) * 3:(y * width + x) * 3 + 3] != bytes(3)
                              for x, y in points)
            if pixels.count(bytes.fromhex(color)) >= 4000 and edges_ready:
                return pixels
            if time.monotonic() > deadline:
                raise AssertionError(f"{name}: Seitenfarbe #{color} oder vollstaendiges Bild an {points} fehlt")
            time.sleep(.5)

    def click(x, y):
        guest.qmp.click(x, y, *guest.size)

    def button(name, down):
        guest.qmp.command("input-send-event", {"events":[{"type":"btn", "data":{"button":name,"down":down}}]})
        time.sleep(.15)

    # Erstes Paketfenster 800x560 bei 32,80; sein Inhalt (798x519 ab 33,120) ist genau das NetSurf-Bild.
    # Koordinaten im Bild wie in host/run_tests.py, hier in Bildschirmkoordinaten umgerechnet.
    def frame(x, y):
        return 33 + x, 120 + y

    def point(x, y):
        width, height = guest.size
        guest.qmp.command("input-send-event", {"events":[
            {"type":"abs", "data":{"axis":"x", "value":int(x*32767/(width-1))}},
            {"type":"abs", "data":{"axis":"y", "value":int(y*32767/(height-1))}}]})
        time.sleep(.15)

    try:
        guest.wait("Desktop ist bereit", 180)
        guest.connect()
        capture("boot")
        guest.login(first=True)
        guest.shell()
        guest.dhcp()
        guest.pkg("install-local /data/pkgs/netsurf/1.0.0", "netsurf 1.0.0 installiert.")
        guest.desktop()
        memory_mark = guest.mark()
        guest.start_app("netsurf")
        page("start", "d8ecff")
        click(*frame(99,209))
        page("linkziel", "d8ffd8")
        click(*frame(10,13))
        page("zurueck", "d8ecff")
        # Down auf Link, Fokusverlust, physisches Up: kein abgeschlossener Klick.
        point(*frame(99,209))
        button("left",True)
        guest.shell()
        button("left",False)
        guest.desktop()
        page("reset-ohne-klick", "d8ecff")
        # Ein Link bei Dokumentposition 0,0 macht einen synthetischen Fehlklick nach Mehrtasten-Drag sichtbar.
        click(*frame(284,13))
        guest.qmp.tap("home")
        for _ in range(32):
            guest.qmp.tap("delete",hold=.005)
        guest.qmp.text("file:///pages/reset-probe.html")
        guest.qmp.tap("ret")
        page("mehrtasten-probe", "e0ffff")
        point(*frame(104,97))
        button("left",True)
        point(*frame(184,157))
        button("right",True)
        guest.shell()
        button("right",False)
        button("left",False)
        guest.desktop()
        page("mehrtasten-reset-ohne-klick", "e0ffff")
        click(*frame(10,13))
        page("nach-mehrtasten-reset", "d8ecff")
        click(*frame(110,231))
        before = page("lang-oben", "fff4d0")

        def body(pixels):
            width, _ = guest.size
            left, top = frame(8, 57)
            return b"".join(pixels[(row*width+left)*3:(row*width+left+596)*3] for row in range(top, top+308))

        # Der Rollbalken hat einen eigenen Capture-Pfad; Fokusverlust muss auch diesen abbrechen.
        point(*frame(788,57))
        button("left",True)
        point(*frame(788,97))
        time.sleep(.5)
        dragged = capture("rollbalken-gehalten")
        assert body(dragged) != body(before), "Test hat keinen Rollbalken-Drag begonnen"
        guest.shell()
        button("left",False)
        guest.desktop()
        point(*frame(788,97))
        time.sleep(.5)
        reset = capture("rollbalken-reset")
        point(*frame(788,217))
        time.sleep(.5)
        assert body(capture("rollbalken-nach-reset")) == body(reset), "Rollbalken zieht nach Fokusreset weiter"
        click(*frame(10,13))
        page("nach-rollbalken-zurueck", "d8ecff")
        click(*frame(110,231))
        before = page("lang-vor-rad", "fff4d0")
        for _ in range(8):
            button("wheel-down",True)
            button("wheel-down",False)
        time.sleep(1)
        after = page("lang-gescrollt", "fff4d0")
        assert before != after, "Rad veraendert den langen Seiteninhalt nicht"
        guest.qmp.tap("pgdn")
        time.sleep(.5)
        assert capture("bild-ab") != after, "Bild ab veraendert den Seiteninhalt nicht"
        click(*frame(10,13))
        page("zurueck-vor-formular", "d8ecff")
        click(*frame(89,252))
        page("formular-leer", "f0e0ff")
        click(*frame(264,106))
        guest.qmp.text("Gr")
        guest.qmp.tap("bracket_left") # Deutsche U-Taste mit Umlaut.
        guest.qmp.tap("minus") # Deutsches scharfes S.
        guest.qmp.text("e ")
        guest.qmp.tap("alt_r","q")
        time.sleep(.5)
        capture("formular-gruesse-altgr")
        click(*frame(10,13))
        page("zurueck-vor-fehler", "d8ecff")
        click(*frame(94,273))
        time.sleep(2)
        capture("nicht-gefunden")
        click(*frame(10,13))
        page("nach-fehler-bedienbar", "d8ecff")

        def address(url):
            click(*frame(284,13))
            guest.qmp.tap("home")
            for _ in range(48):
                guest.qmp.tap("delete",hold=.005)
            guest.qmp.text(url)
            guest.qmp.tap("ret")

        hexcolor = lambda colour: "".join(f"{c:02x}" for c in colour)
        address(f"{base}/seite.html")
        page("http-seite", hexcolor(pages.HTTP_PAGE))
        address(f"{base}/umleitung")
        page("http-umleitung", hexcolor(pages.HTTP_PAGE))
        address(f"{base}/chunked.html")
        page("http-chunked", hexcolor(pages.HTTP_CHUNKED))
        address(f"{base}/fehlt.html")
        page("http-404", hexcolor(pages.HTTP_MISSING))
        address("https://10.0.2.2/")
        time.sleep(2)
        capture("https-fehlerseite")
        address("file:///pages/index.html")
        page("nach-http-lokal", "d8ecff")

        # Paketminimum 640x440 (Inhalt 638x399): NetSurf folgt der Fenstergroesse (poll_event Typ 10) und zeichnet
        # 1:1 ab 33,120; Adressleiste und Zurueck muessen sichtbar und treffbar bleiben (#78).
        def small(x, y):
            return 33 + x, 120 + y

        # In Schritten ziehen: jede Zwischengroesse erreicht NetSurf als neues Bild (wachsender Bildpuffer im Kernel).
        point(829,637)
        button("left",True)
        for step in range(1, 9):
            point(829-20*step,637-15*step)
        button("left",False)
        page("paketminimum", "d8ecff")
        click(*small(284,13))
        guest.qmp.tap("home")
        for _ in range(32):
            guest.qmp.tap("delete",hold=.005)
        guest.qmp.text("file:///pages/formular.html")
        guest.qmp.tap("ret")
        page("paketminimum-formular", "f0e0ff")
        click(*small(10,13))
        page("paketminimum-zurueck", "d8ecff")
        # Zurueck auf 800x560: die folgenden Schliessen-Klicks erwarten die Ausgangsgroesse.
        point(669,517)
        button("left",True)
        point(829,637)
        button("left",False)
        page("wieder-standardgroesse", "d8ecff")

        # Maximiert (1920x1024 Arbeitsflaeche, Inhalt 1918x983 ab 1,40): NetSurf fuellt den Inhalt 1:1 ohne Rand.
        def large(x, y):
            return 1 + x, 40 + y

        click(776,100)
        page("maximiert", "d8ecff", ((8, 900), (1910, 500), (1910, 900)))
        click(*large(284,13))
        guest.qmp.tap("home")
        for _ in range(32):
            guest.qmp.tap("delete",hold=.005)
        guest.qmp.text("file:///pages/formular.html")
        guest.qmp.tap("ret")
        page("maximiert-formular", "f0e0ff")
        click(*large(10,13))
        page("maximiert-zurueck", "d8ecff")
        click(1864,20)
        page("wiederhergestellt", "d8ecff")
        for run in range(3):
            if run:
                guest.start_app("netsurf")
                page(f"neustart-{run}", "d8ecff")
            mark = guest.mark()
            click(812+28*run,100+28*run)
            guest.wait_finished("netsurf",mark)
        free = guest.free_memory(memory_mark)
        assert len(free) == 3, f"Drei aufgeraeumte Laeufe erwartet: {free}"
        assert free[0]-free[-1] <= 8*1024, f"Speicherverlust ueber Starts: {free} KiB"
        print("NETSURF_GUEST PASS: lokal installieren, HTML/CSS, Links/Zurueck, Reset ohne Klick, Rad/Bild ab, Fehlernavigation, HTTP (Seite, Weiterleitung, chunked, 404), Paketminimum 640x440 und maximiert mit Bild in Fenstergroesse (Adressleiste, Zurueck), 3 Starts/Schliessen",flush=True)
        print("Textfeld/Fehlerseiten (auch HTTPS) anhand der Bildschirmfotos visuell pruefen. Freier Speicher (KiB):",free,flush=True)
        return 0
    finally:
        server.shutdown()
        if vm.poll() is None:
            vm.kill()
            vm.wait()
        print("Bilder und Protokoll:",work,flush=True)


if __name__ == "__main__":
    sys.exit(main())
