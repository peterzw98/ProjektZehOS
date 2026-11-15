#!/usr/bin/env python3
"""Prueft den vollstaendigen lokalen NetSurf-Browser als Testpaket in einer frischen QEMU-Platte.

Das ISO muss mit dem oeffentlichen Gegenstueck von --key (ausschliesslich Testschluessel) gebaut sein.
--package bezeichnet die unsignierten Dateien aus experiments/NetSurf.Wasi/bin/package oder dem CI-Artefakt.
Vorhandene VM-Images werden nicht gelesen. Bilder/Protokolle bleiben unter .tools/netsurf-guest/ erhalten.
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
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    args = parser.parse_args()
    work = ROOT / ".tools/netsurf-guest" / uuid.uuid4().hex[:12]
    work.mkdir(parents=True)
    files = [str(p.resolve()) + "=" + p.relative_to(args.package).as_posix()
             for p in sorted(args.package.rglob("*")) if p.is_file()]
    subprocess.run([sys.executable, str(ROOT / "scripts/build-package.py"), "package", "--key", str(args.key.resolve()),
                    "--repo", str(work / "seed/pkgs"), "--id", "netsurf", "--version", "1.0.0", "--name", "NetSurf",
                    "--entry", "netsurf.wasm", *files], check=True)
    tools.create_disk(work / "seed", work / "data.qcow2")
    port = tools.free_port()
    vm = tools.start_qemu(args.iso.resolve(), work / "data.qcow2", work, port, args.accel, None)
    guest = tools.Guest(work, port, "", time.monotonic())
    step = 0

    def capture(name):
        nonlocal step
        step += 1
        guest.qmp.shot(work / f"{step:02d}-{name}.png")
        ppm = work / "screen.ppm"
        guest.qmp.command("screendump", {"filename": str(ppm)})
        header, size, maximum, pixels = ppm.read_bytes().split(b"\n", 3)
        assert header == b"P6" and maximum == b"255", "Unerwartetes QEMU-Bildformat"
        guest.size = tuple(map(int, size.split()))
        return pixels

    def page(name, color):
        deadline = time.monotonic() + 30
        while True:
            pixels = capture(name)
            if pixels.count(bytes.fromhex(color)) >= 4000:
                return pixels
            if time.monotonic() > deadline:
                raise AssertionError(f"{name}: erwartete Seitenfarbe #{color} fehlt")
            time.sleep(.5)

    def click(x, y):
        guest.qmp.click(x, y, *guest.size)

    def button(name, down):
        guest.qmp.command("input-send-event", {"events":[{"type":"btn", "data":{"button":name,"down":down}}]})
        time.sleep(.15)

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
        guest.pkg("install-local /data/pkgs/netsurf/1.0.0", "netsurf 1.0.0 installiert.")
        guest.desktop()
        memory_mark = guest.mark()
        guest.start_app("netsurf")
        page("start", "d8ecff")
        # Erstes Paketfenster 800x560 bei 32,80: Inhalt798x519, Frame632x392 zentriert bei 116,183.
        click(215,392)
        page("linkziel", "d8ffd8")
        click(126,196)
        page("zurueck", "d8ecff")
        # Down auf Link, Fokusverlust, physisches Up: kein abgeschlossener Klick.
        point(215,392)
        button("left",True)
        guest.shell()
        button("left",False)
        guest.desktop()
        page("reset-ohne-klick", "d8ecff")
        click(226,414)
        before = page("lang-oben", "fff4d0")
        for _ in range(8):
            button("wheel-down",True)
            button("wheel-down",False)
        time.sleep(1)
        after = page("lang-gescrollt", "fff4d0")
        assert before != after, "Rad veraendert den langen Seiteninhalt nicht"
        guest.qmp.tap("pgdn")
        time.sleep(.5)
        assert capture("bild-ab") != after, "Bild ab veraendert den Seiteninhalt nicht"
        click(126,196)
        page("zurueck-vor-formular", "d8ecff")
        click(205,435)
        page("formular-leer", "f0e0ff")
        click(380,289)
        guest.qmp.text("Gr")
        guest.qmp.tap("bracket_left") # Deutsche U-Taste mit Umlaut.
        guest.qmp.tap("minus") # Deutsches scharfes S.
        guest.qmp.text("e ")
        guest.qmp.tap("alt_r","q")
        time.sleep(.5)
        capture("formular-gruesse-altgr")
        click(126,196)
        page("zurueck-vor-fehler", "d8ecff")
        click(210,456)
        time.sleep(2)
        capture("nicht-gefunden")
        click(126,196)
        page("nach-fehler-bedienbar", "d8ecff")
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
        print("NETSURF_GUEST PASS: lokal installieren, HTML/CSS, Links/Zurueck, Reset ohne Klick, Rad/Bild ab, Fehlernavigation, 3 Starts/Schliessen",flush=True)
        print("Textfeld/Fehlerseite anhand der Bildschirmfotos visuell pruefen. Freier Speicher (KiB):",free,flush=True)
        return 0
    finally:
        if vm.poll() is None:
            vm.kill()
            vm.wait()
        print("Bilder und Protokoll:",work,flush=True)


if __name__ == "__main__":
    sys.exit(main())
