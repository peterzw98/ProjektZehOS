#!/usr/bin/env python3
"""Prueft (#478), dass Tasten waehrend des Ladens einer langsamen Seite nicht verloren gehen.

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
        guest.login(first=True)
        guest.shell()
        guest.dhcp()
        guest.pkg("install-local /data/pkgs/netsurf/1.0.0", "netsurf 1.0.0 installiert.")
        guest.desktop()
        guest.start_app("netsurf")
        page("start", "d8ecff")
        for run in range(3):
            # Langsame Seite laden (8 Stylesheets zu je 0,5 s) und waehrend des Ladens eine Adresse tippen, dazwischen
            # Mausbewegungen: Jede verlorene Taste macht die Adresse ungueltig, das Formular erscheint nicht.
            click(*frame(284,13))
            guest.qmp.tap("home")
            for _ in range(48):
                guest.qmp.tap("delete",hold=.005)
            guest.qmp.text(f"{base}/viele")
            guest.qmp.tap("ret")
            click(*frame(284,13))
            # Home beim Laden getrennt belegen: "ZZ", Home, "Q" muss "QZZ" ergeben (Belegbild home-beim-laden).
            guest.qmp.text("zz")
            guest.qmp.tap("home")
            guest.qmp.text("q")
            time.sleep(.3)
            capture(f"home-beim-laden-{run}")
            guest.qmp.tap("end")
            for _ in range(90):
                guest.qmp.tap("backspace",hold=.005)
            for index, character in enumerate("file:///pages/formular.html"):
                # Ungebremster Mausstrom (ohne Pausen) zwischen den Tasten fuellt die 128er-Warteschlange.
                width, height = guest.size
                for jitter in range(25):
                    guest.qmp.command("input-send-event", {"events":[
                        {"type":"abs", "data":{"axis":"x", "value":int((100 + index * 7 + jitter * 5) * 32767 / (width - 1))}},
                        {"type":"abs", "data":{"axis":"y", "value":int((200 + jitter * 3) * 32767 / (height - 1))}}]})
                guest.qmp.text(character)
            guest.qmp.tap("ret")
            page(f"tasten-beim-laden-{run}", "f0e0ff")
            click(*frame(10,13))
            page(f"zurueck-{run}", "e0e0d0")
        print("NETSURF_TYPING_LOAD PASS: 3 Laeufe, Adresse waehrend des Ladens vollstaendig angekommen",flush=True)
        return 0
    finally:
        server.shutdown()
        if vm.poll() is None:
            vm.kill()
            vm.wait()
        print("Bilder und Protokoll:",work,flush=True)


if __name__ == "__main__":
    sys.exit(main())
