#!/usr/bin/env python3
"""Stufe b der Linux-ABI-Pruefung (Ladybird V5): N1-E-Programme im Gast starten, Ausgabe gegen experiments/LinuxAbi/expected/.

Ablauf: Die statischen Programme (experiments/LinuxAbi/build.sh, out/<satz>/) kommen auf ein FAT32-Image (mkfs.fat + mcopy,
nur eine frische Kopie unter --work), das der Gast als sata1 sieht. Das ISO ist ein Testbau mit -p:ZehOSLinuxRun=true (Befehl
"run <datei> [argumente...]", Textshell). Je Programm tippt das Skript "run <pfad> a1 "b 2"" (Argumente wie run-linux.py) und
liest das Ergebnis aus dem seriellen Protokoll. Verglichen werden Ausgabe (Bytes) und Status (exit <n> / signal <n>) mit
expected/<satz>/<name>.txt und .status.

Startschnittstelle (mit senior abzustimmen, Stand Entwurf; ohne sie laeuft nur der Vergleicher, siehe --selftest/Tests):
  Der Kernel schreibt je run-Aufruf in das serielle Protokoll
      LINUXRUN BEGIN <name>
      LINUXRUN OUT <hex>          (Ausgabebytes, beliebig in Stuecke geteilt; Hex wegen binaerer und zeilenfremder Ausgabe)
      LINUXRUN END exit <n>|signal <n>|error <text>
  Marken duerfen mitten in einer Zeile beginnen (SMP-Zeilen mischen sich), enden aber am Zeilenende.

Exit 0: alles gleich. Exit 1: Abweichung, fehlendes Programm/Erwartung oder Zeitueberschreitung. Exit 2: falsche Benutzung.
Vorhandene Images werden nie beschrieben; --work muss neu sein.
"""
import argparse
import importlib.util
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ABI = ROOT / "experiments" / "LinuxAbi"
ARGS = ["a1", "b 2"]  # wie experiments/LinuxAbi/run-linux.py
MARK = re.compile(r"LINUXRUN (BEGIN|OUT|END) ?(.*?)\r?$", re.MULTILINE)


def parse_log(text: str) -> dict[str, tuple[bytes, str | None]]:
    """name -> (Ausgabe, Status); Status None, wenn END fehlt. Spaeterer Lauf desselben Namens ersetzt den frueheren."""
    results: dict[str, tuple[bytes, str | None]] = {}
    name, out = None, bytearray()
    for kind, rest in MARK.findall(text):
        if kind == "BEGIN":
            if name is not None:
                results[name] = (bytes(out), None)  # vorheriger Lauf ohne END
            name, out = rest.strip(), bytearray()
        elif name is None:
            continue
        elif kind == "OUT":
            try:
                out += bytes.fromhex(rest.strip())
            except ValueError:
                out += b"<ungueltiges Hex>"
        else:
            results[name] = (bytes(out), rest.strip())
            name = None
    if name is not None:
        results[name] = (bytes(out), None)
    return results


def compare(results: dict, expected: Path, names: list[str]) -> list[str]:
    """Liste der Abweichungen (leer = alles gleich)."""
    problems = []
    for name in names:
        txt, status = expected / f"{name}.txt", expected / f"{name}.status"
        if not txt.is_file() or not status.is_file():
            problems.append(f"{name}: Erwartung fehlt")
            continue
        if name not in results:
            problems.append(f"{name}: kein Ergebnis im Gast")
            continue
        out, got = results[name]
        if got is None:
            problems.append(f"{name}: Lauf ohne Ende (END fehlt)")
            continue
        want = status.read_text(encoding="utf-8").strip()
        if got != want:
            problems.append(f"{name}: Status '{got}', erwartet '{want}'")
        want_out = txt.read_bytes()
        if out != want_out:
            problems.append(f"{name}: Ausgabe weicht ab ({first_difference(out, want_out)})")
    return problems


def first_difference(got: bytes, want: bytes) -> str:
    for index, (a, b) in enumerate(zip(got, want)):
        if a != b:
            line = want[:index].count(b"\n") + 1
            return f"Zeile {line}, Byte {index}: erhalten {got[index:index + 24]!r}, erwartet {want[index:index + 24]!r}"
    return f"Laenge {len(got)} statt {len(want)}"


def programs(bin_dir: Path) -> list[str]:
    return sorted(p.name for p in bin_dir.iterdir() if p.is_file() and not p.name.endswith((".o", ".c", ".h", ".sh")))


def tool(name: str) -> str:
    found = shutil.which(name) or str(Path.home() / ".local/opt/zehos-tools/root/usr" / ("sbin" if name.startswith("mkfs") else "bin") / name)
    if not Path(found).is_file():
        sys.exit(f"{name} nicht gefunden")
    return found


def build_image(image: Path, sets: dict[str, Path]) -> None:
    """FAT32-Image mit <satz>/<name> je Programm (Label ABIPROG, 64 MiB)."""
    with image.open("wb") as f:
        f.truncate(64 * 1024 * 1024)
    subprocess.run([tool("mkfs.fat"), "-F", "32", "-n", "ABIPROG", str(image)], check=True, stdout=subprocess.DEVNULL)
    for name, directory in sets.items():
        subprocess.run([tool("mcopy"), "-i", str(image), "-s", "-D", "o", str(directory), f"::{name}"], check=True)


def load_guest_tools():
    spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run_guest(args, sets: dict[str, Path], work: Path) -> str:
    """Bootet das ISO, fuehrt alle Programme aus und liefert das serielle Protokoll."""
    tools = load_guest_tools()
    image = work / "abiprog.img"
    build_image(image, sets)
    system = work / "system.img"  # leere sata0 (die Textshell braucht kein Konto)
    with system.open("wb") as f:
        f.truncate(64 * 1024 * 1024)
    machine = list(tools.MACHINE)
    for port, disk in enumerate([system, image]):
        machine += ["-drive", f"file={disk},format=raw,if=none,id=d{port}", "-device", f"ide-hd,drive=d{port},bus=ahci.{port}"]
    tools.MACHINE = machine
    unused = work / "unused.qcow2"
    tool_img, environment = tools.qemu_img()
    subprocess.run([*tool_img, "create", "-f", "qcow2", str(unused), "1M"], check=True, stdout=subprocess.DEVNULL, env=environment)
    port = tools.free_port()
    process = tools.start_qemu(args.iso, unused, work, port, args.accel, None)
    try:
        tools.wait_serial(work, "Textshell ist bereit.", args.boot_timeout)
        qmp = tools.Qmp(port)
        time.sleep(3)
        qmp.tap("ret")
        if args.mount_command:
            qmp.line(args.mount_command)
            time.sleep(5)
        log = work / "serial.log"
        for satz, directory in sets.items():
            for name in programs(directory):
                mark = len(log.read_text(encoding="utf-8", errors="replace"))
                arguments = " ".join(f'"{a}"' if " " in a else a for a in ARGS)
                qmp.line(f"run {args.guest_dir}/{satz}/{name} {arguments}")
                try:
                    tools.wait_serial(work, f"LINUXRUN END", args.run_timeout, mark)
                except TimeoutError:
                    print(f"{satz}/{name}: Zeitueberschreitung ({args.run_timeout} s)", flush=True)
                    break  # ein haengendes Programm blockiert die Shell; Rest als fehlend melden
        try:
            qmp.line("shutdown")
        except (ConnectionError, OSError):
            pass
        process.wait(timeout=60)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
    return (work / "serial.log").read_text(encoding="utf-8", errors="replace")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, help="ISO mit -p:ZehOSLinuxRun=true (Pflicht ausser mit --log)")
    parser.add_argument("--set", action="append", dest="sets", help="Satz (s1, s3, tools); Standard s1")
    parser.add_argument("--out", type=Path, default=ABI / "out", help="Ordner mit <satz>/<programm> (build.sh)")
    parser.add_argument("--expected", type=Path, default=ABI / "expected")
    parser.add_argument("--work", type=Path, help="neuer Arbeitsordner (darf nicht existieren)")
    parser.add_argument("--accel", choices=["tcg", "kvm", "whpx"], default="kvm")
    parser.add_argument("--boot-timeout", type=float, default=300)
    parser.add_argument("--run-timeout", type=float, default=150)
    parser.add_argument("--guest-dir", default="/abi", help="Pfad der Platte im Gast")
    parser.add_argument("--mount-command", default="", help="Shellbefehl, der die Platte einbindet (Stand der Schnittstelle offen)")
    parser.add_argument("--log", type=Path, help="vorhandenes serielles Protokoll nur auswerten (kein Gast)")
    args = parser.parse_args()
    sets = {s: args.out / s for s in args.sets or ["s1"]}
    for satz, directory in sets.items():
        if not re.fullmatch(r"[A-Za-z0-9_-]+", satz) or not directory.is_dir():
            print(f"Satz {satz}: Ordner {directory} fehlt (experiments/LinuxAbi/build.sh)", file=sys.stderr)
            return 2
    if args.log:
        text = args.log.read_text(encoding="utf-8", errors="replace")
    else:
        if not args.iso or not args.work or not args.iso.is_file():
            print("--iso (vorhanden) und --work sind Pflicht", file=sys.stderr)
            return 2
        if args.work.exists() or args.work.is_symlink():
            print(f"Arbeitsordner existiert bereits: {args.work}", file=sys.stderr)
            return 2
        args.work.mkdir(parents=True)
        text = run_guest(args, sets, args.work)
    results = parse_log(text)
    problems = []
    for satz, directory in sets.items():
        names = programs(directory)
        for p in compare({n: results[n] for n in results}, args.expected / satz, names):
            problems.append(f"{satz}/{p}")
    total = sum(len(programs(d)) for d in sets.values())
    for p in problems:
        print("ABWEICHUNG", p)
    print(f"{total - len({p.split(':')[0] for p in problems})}/{total} Programme gleich")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
