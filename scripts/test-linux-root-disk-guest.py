"""Ladybird V28: ein Programm aus einer Prozesswurzel auf der Datenplatte im Desktop-Terminal starten (KVM, Testbau -p:ZehOSLinuxRun=true).

Nutzung: scripts/test-linux-root-disk-guest.py <iso> <platte.qcow2> <arbeitsordner> --dest lb --run "bin/prog arg..."
             [--expect TEXT ...] [--status "exit 0"] [--timeout 300] [--smp 1] [--memory 2G]
             [--fetch lb/shot.png --out shot.png] [--accel kvm|tcg] [--wait-scale F]
Die Platte kommt von scripts/linux-root-disk.py (im Gast /data/<dest>). Das Skript meldet sich an, uebernimmt /data/<dest>,
dessen bin/ und das Programm (security adopt, wie test-linux-js.py), wechselt im Terminal nach /data/<dest> und startet --run.
Prueft: LINUXRUN BEGIN, das Ende mit --status und jeden --expect-Text in der Ausgabe. Die ganze Ausgabe steht danach in
<arbeitsordner>/program-out.txt, die seriellen Zeilen in serial.log. Die Platte wird als Kopie verwendet; das Original bleibt.
--fetch <pfad-auf-der-platte> (mit --out, Standard <arbeitsordner>/<dateiname>): nach dem Herunterfahren holt scripts/fetch-data-disk-file.py
die Datei aus der Platten-Kopie (mtools), z. B. den Screenshot von Hub --headless=screenshot --screenshot-path /data/lb/shot.png.
--accel tcg (ohne KVM, z. B. Gegenprobe auf anderer CPU): alle festen Wartezeiten (Desktop, Anmeldung, LINUXRUN BEGIN, Herunterfahren)
werden mit --wait-scale (Standard bei tcg 10, bei kvm 1) gestreckt; --timeout (Programmlaufzeit) bleibt wie angegeben.
Kernel-Halt (KERNEL PANIC, "System halting", [FAILFAST]) nach dem Programmstart: sofort Abbruch mit "HALT <zeile>", Gast wird
beendet, kein --fetch, Exit 3 (statt bis --timeout zu warten).
"""
import argparse
import importlib.util
import os
from pathlib import Path
import re
import shutil
import sys
import time

R = Path(__file__).resolve().parent.parent
ANSI = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]")
# Marken der Cosmos-Haltpfade (Panic.cs, ExceptionHelper.cs). Nach einer davon kommt kein LINUXRUN END mehr
# (lb-base3 mit 512M: FAILFAST OutOfMemoryException, danach nur noch Timer-Ticks), also nicht bis zum Zeitlimit warten.
HALT = re.compile(r"^.*(?:KERNEL PANIC|System halting|\[FAILFAST\]).*$", re.M)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("iso", type=Path)
    parser.add_argument("disk", type=Path)
    parser.add_argument("work", type=Path)
    parser.add_argument("--dest", default="lb")
    parser.add_argument("--run", required=True)
    parser.add_argument("--expect", action="append", default=[])
    parser.add_argument("--status", default="exit 0")
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--smp", default="1")
    parser.add_argument("--memory")
    parser.add_argument("--fetch", help="Datei auf der Datenplatte (relativ zur Wurzel, z. B. lb/shot.png), nach dem Lauf holen")
    parser.add_argument("--out", type=Path, help="Ziel fuer --fetch (Standard: <arbeitsordner>/<dateiname>)")
    parser.add_argument("--accel", choices=["kvm", "tcg"], default="kvm")
    parser.add_argument("--wait-scale", type=float, help="Faktor fuer feste Wartezeiten (Standard: tcg 10, kvm 1)")
    args = parser.parse_args()
    scale = args.wait_scale if args.wait_scale else (10.0 if args.accel == "tcg" else 1.0)
    spec = importlib.util.spec_from_file_location("t", R / "scripts/test-wasm-doom-desktop.py")
    t = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(t)
    if scale != 1.0:
        # Unter TCG laeuft der Gast etwa zehnmal langsamer; die Wartezeiten der Hilfsfunktionen (Anmeldung, Shell) mitstrecken.
        base_wait = t.wait_serial
        t.wait_serial = lambda work, text, timeout, start=0: base_wait(work, text, timeout * scale, start)
    args.work.mkdir()
    os.environ["ZEHOS_QEMU_SMP"] = args.smp
    disk = args.work / "disk.qcow2"
    shutil.copyfile(args.disk, disk)
    program = args.run.split()[0]
    port = t.free_port()
    process = t.start_qemu(args.iso, disk, args.work, port, args.accel, None, args.memory)
    serial = args.work / "serial.log"
    results = []

    def check(name, condition):
        results.append(condition)
        print(("PASS " if condition else "FAIL ") + name, flush=True)

    def serial_text(mark):
        return serial.read_text(encoding="utf-8", errors="replace")[mark:]

    halted = None

    def wait_serial(pattern, timeout, mark):
        nonlocal halted
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            text = serial_text(mark)
            if found := re.search(pattern, text):
                return found
            if halt := HALT.search(text):
                halted = halt.group(0).strip()
                return None
            time.sleep(0.5)
        return None

    def output(mark):
        data = b""
        for line in serial_text(mark).splitlines():
            if "LINUXRUN OUT " in line:
                try:
                    data += bytes.fromhex(line.split("LINUXRUN OUT ", 1)[1].split()[0])
                except ValueError:
                    pass
        return ANSI.sub("", data.decode("utf-8", errors="replace"))

    try:
        guest = t.Guest(args.work, port, "http://127.0.0.1:1", time.monotonic())
        # Kein "* scale": Guest.wait geht durch das oben gepatchte t.wait_serial, das schon streckt.
        guest.wait("Desktop ist bereit", 240)
        guest.connect()
        time.sleep(1)
        guest.login(first=True)
        root = f"/data/{args.dest}"
        guest.shell(f"security adopt {root} tester", f"security adopt {root}/bin tester",
                    f"security adopt {root}/{program} tester")
        time.sleep(1)
        guest.desktop()
        guest.launch("Terminal")
        time.sleep(3)
        guest.qmp.line(f"cd {root}")
        time.sleep(1)
        mark = guest.mark()
        guest.qmp.line(f"run {args.run}")
        check("start", wait_serial(r"LINUXRUN BEGIN", 60 * scale, mark) is not None)
        end = wait_serial(r"LINUXRUN END ([^\r\n]*)", args.timeout, mark) if halted is None else None
        if halted is not None:
            # Der Kernel steht: kein Herunterfahren, keine Platte holen (Kopie evtl. unvollstaendig geschrieben).
            print(f"HALT {halted}", flush=True)
            (args.work / "program-out.txt").write_text(output(mark), encoding="utf-8")
            check("kein Kernel-Halt", False)
            return 3
        status = end.group(1).strip() if end else "kein Ende"
        print(f"ENDE {status}", flush=True)
        check(f"status {args.status}", status.startswith(args.status))
        text = output(mark)
        (args.work / "program-out.txt").write_text(text, encoding="utf-8")
        for expected in args.expect:
            check(f"ausgabe {expected[:60]}", expected in text)
        guest.shot("ende")
        try:
            guest.qmp.line("shutdown")
        except Exception:
            pass
        process.wait(timeout=60 * scale)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
    if args.fetch:
        # Der Gast ist beendet, die Platten-Kopie ist vollstaendig geschrieben.
        spec = importlib.util.spec_from_file_location("fetch_disk", R / "scripts/fetch-data-disk-file.py")
        fetcher = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(fetcher)
        out = args.out or args.work / Path(args.fetch).name
        results.append(False)
        results[-1] = fetcher.fetch(disk, args.fetch, out) == 0
        print(("PASS " if results[-1] else "FAIL ") + f"holen {args.fetch}", flush=True)
    print(f"ERGEBNIS {sum(results)}/{len(results)}")
    return 0 if results and all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
