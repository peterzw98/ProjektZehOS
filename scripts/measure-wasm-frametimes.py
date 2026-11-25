#!/usr/bin/env python3
"""Misst die Bildzeit einer Paket-App und ihre Anteile (frametimes on) in einer frischen QEMU-Platte.

Standard ist das veroeffentlichte Doom 1.1.0 aus dem Branch packages; das ISO muss dessen Herausgeberschluessel
vertrauen (normaler Build). Doom laeuft ohne Eingabe im Titel-/Demoablauf, damit die Messung reproduzierbar bleibt.
Mit --package/--key wird stattdessen ein lokal mit einem Testschluessel signiertes Paket verwendet (ISO mit
-p:ZehOSPackageKeyPath=<Test-public.b64>), optional mit --aot-file. --aot on|off sendet vor dem Start
"wasmaot on|off" und bricht ab, wenn die App nicht unter der verlangten Laufzeit startet (AOT ist Standard). --start-keys tippt nach dem ersten Bild Tasten (ClassiCube: ret
startet Singleplayer aus dem Launcher). --background busy startet vorher eine rechnende Hintergrundaufgabe
(tasks start busy; Beleg nur als Foto, die Shell meldet das nicht seriell). Jeder der --runs Laeufe bekommt eine
frische Platte.
Ausgabe: die seriellen Zeilen "Bildzeit ..." und "Anteile ..." je 60-s-Fenster und eine Zusammenfassung aus dem
letzten Fenster jedes Laufs (das erste enthaelt Start, Launcher und Laden). Bilder/Protokolle bleiben unter
.tools/frametimes/. Vorhandene VM-Images werden nicht verwendet. Linux-Aufrufe fuer AOT-Plan Etappe F stehen in
docs/WASM_FEASIBILITY.md (Abschnitt "Messung wasm3 gegen AOT").
"""
import argparse
import importlib.util
from pathlib import Path
import re
import statistics
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)


def parse_args(argv=None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="kvm")
    parser.add_argument("--seconds", type=int, default=130, help="Laufzeit der App; je 60 s entsteht ein Messfenster")
    parser.add_argument("--ref", default="origin/packages", help="Git-Stand, aus dem <id>/<version> gelesen wird")
    parser.add_argument("--package", type=Path, help="Verzeichnis mit den unsignierten Paketdateien statt --ref")
    parser.add_argument("--key", type=Path, help="privater Testschluessel zum Signieren von --package")
    parser.add_argument("--id", default="doom")
    parser.add_argument("--version", default="1.1.0")
    parser.add_argument("--name", default="Doom")
    parser.add_argument("--entry", default="doom.wasm")
    parser.add_argument("--min-kernel", help="an build-package.py package durchgereicht")
    parser.add_argument("--network", action="store_true", help="an build-package.py package durchgereicht")
    parser.add_argument("--aot-file", type=Path, help="an build-package.py package durchgereicht (Beleg .json daneben)")
    parser.add_argument("--aot", choices=["on", "off"], default="off")
    parser.add_argument("--start-keys", nargs="*", default=[],
                        help="QMP-Tasten nach dem ersten Bild, etwa ret; shift-f drueckt beide zusammen, "
                             "wait:<s> wartet dazwischen (Welt laden)")
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--move", action="store_true",
                        help="waehrend der Messung abwechselnd w/a/s/d halten (bewegte Kamera, ClassiCube)")
    parser.add_argument("--background", choices=["busy"],
                        help="vor dem App-Start eine rechnende Hintergrundaufgabe starten (tasks start busy, E5-I)")
    parser.add_argument("-m", "--memory",
                        help="Gastspeicher fuer QEMU -m (Standard: 1G fuer classicube, sonst 512M wie die Gasttests)")
    args = parser.parse_args(argv)
    # ClassiCube braucht mindestens 768 MiB (#237); mit 512M lagen die Bildraten am 18.11.2026 rund 20 % zu niedrig.
    if args.memory is None:
        args.memory = "1G" if args.id == "classicube" else "512M"
    if args.package and not args.key:
        parser.error("--package braucht --key")
    if (args.key or args.aot_file or args.network or args.min_kernel) and not args.package:
        parser.error("--key, --aot-file, --network und --min-kernel gelten nur mit --package")
    if args.runs < 1:
        parser.error("--runs muss mindestens 1 sein")
    return args


# Bewegung fuer --move: vor und zurueck, dazwischen seitlich; die Welt bleibt dadurch im Bild. Auf ebenem Boden bleibt
# der Spieler etwa am Ort, an Kanten und Bloecken driftet er; Laeufe sind vergleichbar, aber nicht exakt gleich.
MOVE_STEPS = (("w", 2.5), ("a", 1.0), ("s", 2.5), ("d", 1.0))


def last_average_fps(lines: list[str]) -> float | None:
    """Durchschnittliche Bilder je Sekunde im letzten 60-s-Fenster: Bildzahl n der letzten Zeile "Bildzeit ..." / 60.

    Der Kernel schliesst das Fenster erst mit dem ersten Bild nach 60 s; n/60 ist dadurch um hoechstens ein Bild zu hoch
    (bei 30 fps unter 0,1 %).
    """
    found = [int(m.group(1)) for line in lines if (m := re.search(r"^Bildzeit n=(\d+)", line))]
    return round(found[-1] / 60, 1) if found else None


def last_median(lines: list[str]) -> int | None:
    """Median aus der letzten Zeile "Bildzeit ..." eines Laufs."""
    found = [int(m.group(1)) for line in lines if (m := re.search(r"^Bildzeit .* median=(\d+)", line))]
    return found[-1] if found else None


def seed(args: argparse.Namespace, work: Path) -> Path:
    packages = work / "seed/pkgs"
    packages.mkdir(parents=True)
    if args.package:
        files = [str(p.resolve()) + "=" + p.relative_to(args.package).as_posix()
                 for p in sorted(args.package.rglob("*")) if p.is_file()]
        extra = (["--network"] if args.network else []) + (["--min-kernel", args.min_kernel] if args.min_kernel else [])
        extra += ["--aot-file", str(args.aot_file.resolve())] if args.aot_file else []
        subprocess.run([sys.executable, str(ROOT / "scripts/build-package.py"), "package", "--key", str(args.key.resolve()),
                        "--repo", str(packages), "--id", args.id, "--version", args.version, "--name", args.name,
                        "--entry", args.entry, *extra, *files], check=True)
    else:
        # Nur lesend aus Git: das signierte Paket unveraendert auf die frische Testplatte legen.
        archive = subprocess.run(["git", "-C", str(ROOT), "archive", args.ref, f"{args.id}/{args.version}"],
                                 check=True, capture_output=True).stdout
        subprocess.run(["tar", "-x", "-C", str(packages)], input=archive, check=True)
    return work / "seed"


def shell_setup(args: argparse.Namespace) -> list[str]:
    """Shellbefehle vor dem App-Start: Messung an, Laufzeit ausdruecklich waehlen (Kernelstandard ist AOT), wahlweise rechnende Hintergrundlast."""
    return ["frametimes on", f"wasmaot {args.aot}",
            *([f"tasks start {args.background}"] if args.background else [])]


def check_engine(aot: str, engines: list[str]) -> None:
    """Bricht ab, wenn die App nicht durchgehend unter der verlangten Laufzeit lief; sonst misst ein Rueckfall still die andere."""
    expected = "aot" if aot == "on" else "wasm3"
    if not engines or any(engine.split()[:1] != [expected] for engine in engines):
        raise RuntimeError(f"--aot {aot}, aber Laufzeit={engines or 'keine Zeile WASM Start'}")


def run(args: argparse.Namespace, seed_dir: Path, work: Path) -> list[str]:
    work.mkdir(exist_ok=True)
    tools.create_disk(seed_dir, work / "data.qcow2")
    port = tools.free_port()
    vm = tools.start_qemu(args.iso.resolve(), work / "data.qcow2", work, port, args.accel, None, args.memory)
    guest = tools.Guest(work, port, "", time.monotonic())
    try:
        guest.wait("Desktop ist bereit", 180)
        guest.connect()
        guest.login(first=True)
        guest.shell(*shell_setup(args))
        # Die Shell meldet "Aufgabe <n> gestartet." nur auf dem Bildschirm, nicht seriell: das Foto ist der Beleg.
        if args.background:
            guest.shot("hintergrund")
        guest.pkg(f"install-local /data/pkgs/{args.id}/{args.version}", f"{args.id} {args.version} installiert.")
        guest.desktop()
        mark = guest.mark()
        guest.start_app(args.id)
        log = (work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]
        engine = re.search(rf"WASM Start {re.escape(args.id)} Laufzeit=([^\r\n]*)", log)
        print(f"Laufzeit: {engine.group(1) if engine else 'keine Zeile WASM Start'}", flush=True)
        check_engine(args.aot, re.findall(rf"WASM Start {re.escape(args.id)} Laufzeit=([^\r\n]*)", log))
        if args.start_keys:
            # Der Launcher von ClassiCube nimmt Eingaben erst nach einigen Sekunden an (siehe test-classicube-desktop.py).
            time.sleep(3)
            for key in args.start_keys:
                if key.startswith("wait:"):
                    time.sleep(float(key[len("wait:"):]))
                    continue
                guest.qmp.tap(*key.split("-"))
                time.sleep(0.5)
        if args.move:
            end = time.monotonic() + args.seconds
            step = 0
            while time.monotonic() < end:
                key, seconds = MOVE_STEPS[step % len(MOVE_STEPS)]
                guest.qmp.hold(key, min(seconds, max(0.0, end - time.monotonic())))
                step += 1
        else:
            time.sleep(args.seconds)
        guest.shot("ende")
        log = (work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]
        lines = re.findall(r"(Bildzeit n=[^\r\n]*|Anteile \(ms[^\r\n]*)", log)
        for line in lines:
            print(line, flush=True)
        print(f"Protokoll: {work / 'serial.log'}", flush=True)
        return lines
    finally:
        try:
            guest.qmp.quit()
        except Exception:
            vm.kill()
        vm.wait(30)


def main() -> int:
    args = parse_args()
    work = ROOT / ".tools/frametimes" / uuid.uuid4().hex[:12]
    seed_dir = seed(args, work)
    results = []
    for number in range(1, args.runs + 1):
        print(f"Lauf {number}/{args.runs}", flush=True)
        try:
            results.append(run(args, seed_dir, work if args.runs == 1 else work / f"lauf{number}"))
        except Exception as error:  # noqa: BLE001 - ein Absturz (Panik, Timeout) zaehlt als Lauf ohne Messwert
            # Fuer "n von m Laeufen" muss es mit dem naechsten Lauf weitergehen; der Exitcode bleibt dann 1.
            print(f"Lauf {number} gescheitert: {type(error).__name__}: {error}", flush=True)
            results.append([])
    medians = [last_median(lines) for lines in results]
    averages = [last_average_fps(lines) for lines in results]
    if args.runs > 1:
        known = [value for value in medians if value is not None]
        overall = f"{statistics.median(known)} ms" if known else "-"
        print(f"Zusammenfassung {args.id} aot={args.aot} accel={args.accel}: Bildzeit-Median letztes Fenster je Lauf "
              f"{medians} ms, Median ueber Laeufe {overall}", flush=True)
    # Peters Messlatte fuer ClassiCube (18.11.2026): Mittelwert der Bilder je Sekunde, nicht Median der Bildzeit.
    known_fps = [value for value in averages if value is not None]
    mean_fps = f"{statistics.mean(known_fps):.1f}" if known_fps else "-"
    print(f"Durchschnitt {args.id} aot={args.aot} accel={args.accel} move={args.move}: avg fps letztes Fenster je Lauf "
          f"{averages}, Mittel {mean_fps}", flush=True)
    return 0 if all(any(line.startswith("Anteile") for line in lines) for lines in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
