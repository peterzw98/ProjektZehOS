#!/usr/bin/env python3
"""Gastprobe N3-B2/B3: Upgrade eines Pakets, waehrend ein Programm aus diesem Paket laeuft.

ISO wie fuer scripts/test-package2-guest.py (-p:ZehOSWasm=true, -p:ZehOSLinuxRun=true, oeffentlicher Teil von --key in
-p:ZehOSPackageKeyPath, ausschliesslich Testschluessel). Frische Datenplatte (qcow2 aus vvfat), vorhandene Images werden nicht gelesen:
  /data/repo1  lang 1.0.0-r1, Katalog Generation 1
  /data/repo2  dasselbe plus lang 1.0.0-r2, Katalog Generation 2
Paket lang bringt ein winziges statisches ELF (von diesem Skript erzeugt, kein Uebersetzer noetig): Es gibt "lang rN start" aus,
schlaeft --sleep Sekunden, liest dann /usr/share/lang/text aus seiner Prozesswurzel und gibt den Inhalt aus ("text rN").

Ablauf: In der Textshell laeuft "run --pkg /usr/bin/lang" (r1). Waehrenddessen im Desktop-Terminal (eigene Sitzung): update und upgrade
auf r2, resolve zeigt r2, ein zweiter Start wird abgewiesen (ein Programm zur Zeit). Erwartet:
  1. Das laufende Programm liest nach dem Upgrade noch "text r1" (gebundener alter Indexstand, Dateien von r1 noch auf der Platte).
  2. Der naechste Start sieht r2 ("lang r2 start", "text r2").
  3. Nach dem Ende und dem naechsten Start (CollectUnused) liegt nur noch der Ordner von r2 auf der Platte. Den Paketordner darf der
     Testbenutzer im Gast nicht lesen ("Zugriff verweigert"); das Skript liest ihn deshalb nach dem Gastlauf offline aus der Laufplatte
     (qemu-img und mdir aus mtools; fehlt mdir, bleibt dieser Punkt offen). Dass r1 waehrend des Laufs noch da war, belegt Punkt 1:
     Das alte Programm oeffnet seine Datei erst nach dem Upgrade.
"""
import argparse
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]


def load(name: str, file: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


base = load("package2_guest", "test-package2-guest.py")

# Maschinencode (GNU as, ohne Relokationen, Daten RIP-relativ dahinter): write(1, msg); nanosleep(ts); fd = open(path, O_RDONLY);
# n = read(fd, stack, 128); write(1, stack, n); exit_group(0). Scheitert open oder read: write(1, fmsg); exit_group(3).
# Danach msg "lang r1 start\n", fmsg "lang lesen fehlgeschlagen\n", path "/usr/share/lang/text\0", ts {Sekunden, 0} (letzte 16 Byte).
LANG_CODE = bytes.fromhex(
    "B801000000BF01000000488D3584000000BA0E0000000F05B823000000488D3DB400000031F60F05B802000000488D3D8900000031F631D20F0585C0"
    "783389C731C04881EC800000004889E6BA800000000F0585C07E1A89C2B801000000BF010000004889E60F05B8E700000031FF0F05B801000000BF01"
    "000000488D3521000000BA1A0000000F05B8E7000000BF030000000F056C616E672072312073746172740A6C616E67206C6573656E206665686C6765"
    "7363686C6167656E0A2F7573722F73686172652F6C616E672F7465787400900F1F4400005A000000000000000000000000000000")


def lang_elf(release: int, seconds: int) -> bytes:
    """Statisches x86-64-ELF mit einem PT_LOAD (R+X) ab 0x400000, Code direkt hinter den Koepfen (wie hello_elf)."""
    code = bytearray(LANG_CODE)
    at = code.find(b"r1 start")
    code[at:at + 2] = f"r{release}".encode("ascii")
    code[-16:-8] = seconds.to_bytes(8, "little")
    base_address, heads = 0x400000, 64 + 56
    size = heads + len(code)
    header = (b"\x7fELF\x02\x01\x01" + bytes(9) + (2).to_bytes(2, "little") + (0x3E).to_bytes(2, "little") + (1).to_bytes(4, "little")
              + (base_address + heads).to_bytes(8, "little") + (64).to_bytes(8, "little") + bytes(8) + bytes(4)
              + (64).to_bytes(2, "little") + (56).to_bytes(2, "little") + (1).to_bytes(2, "little") + bytes(6))
    program = ((1).to_bytes(4, "little") + (5).to_bytes(4, "little") + bytes(8) + base_address.to_bytes(8, "little")
               + base_address.to_bytes(8, "little") + size.to_bytes(8, "little") + size.to_bytes(8, "little") + (0x1000).to_bytes(8, "little"))
    return header + program + bytes(code)


def lang_package(release: int, seconds: int):
    return ("lang", "1.0.0", release, [], [("usr/bin/lang", lang_elf(release, seconds), True), ("usr/share/lang/text", f"text r{release}\n", False)], {})


def build_seed(key: Path, seed: Path, scratch: Path, seconds: int) -> None:
    repo1, repo2 = seed / "repo1", seed / "repo2"
    base.package(key, repo1, scratch, lang_package(1, seconds))
    base.catalog(key, repo1, 1)
    shutil.copytree(repo1, repo2)
    base.package(key, repo2, scratch / "upgrade", lang_package(2, seconds))
    base.catalog(key, repo2, 2)


def output_since(guest, mark: int) -> tuple[str, int]:
    """Sammelt alle LINUXRUN OUT-Stuecke ab mark (Text und Zeilenende kommen getrennt); dazu die Position der letzten END-Zeile oder -1."""
    log = (guest.work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]
    text = "".join(bytes.fromhex(line.split("LINUXRUN OUT ", 1)[1].split()[0]).decode("ascii", "replace")
                   for line in log.splitlines() if "LINUXRUN OUT " in line)
    return text, log.rfind("LINUXRUN END ")


def wait_output(guest, mark: int, expected: str, timeout: float) -> int:
    """Wartet, bis die gesammelte Ausgabe expected enthaelt; Ergebnis: Position im seriellen Protokoll zum Zeitpunkt des Funds."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        text, _ = output_since(guest, mark)
        if expected in text:
            return guest.mark()
        time.sleep(0.5)
    raise TimeoutError(f"Ausgabe {expected!r} nicht gesehen; bisher {output_since(guest, mark)[0]!r}")


def stored_versions(disk: Path, raw: Path) -> list[str] | None:
    """Liest die Versionsordner von lang offline aus der Laufplatte (FAT ab Sektor 63, wie vvfat sie anlegt); None ohne mdir."""
    mdir = shutil.which("mdir")
    if mdir is None:
        return None
    subprocess.run(["qemu-img", "convert", "-O", "raw", str(disk), str(raw)], check=True)
    try:
        listing = subprocess.run([mdir, "-b", "-i", f"{raw}@@{63 * 512}", "::/.zehos-packages/v2/lang"], check=True, capture_output=True,
                                 text=True, env={**os.environ, "MTOOLS_SKIP_CHECK": "1"}).stdout
    finally:
        raw.unlink(missing_ok=True)
    return sorted(line.rstrip("/").rsplit("/", 1)[-1] for line in listing.splitlines() if line.strip())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--key", type=Path, required=True, help="privater Testschluessel (PEM), oeffentlicher Teil im ISO")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="kvm")
    parser.add_argument("--sleep", type=int, default=150, help="Schlafdauer des Programms; muss Upgrade und Pruefungen ueberdauern")
    args = parser.parse_args()
    tools = base.load_tools()
    work = ROOT / ".tools/package2-guest" / ("live-" + uuid.uuid4().hex[:12])
    build_seed(args.key.resolve(), work / "seed", work / "scratch", args.sleep)
    vm_dir = work / "vm"
    vm_dir.mkdir(parents=True)
    tools.create_disk(work / "seed", vm_dir / "data.qcow2")
    port = tools.free_port()
    vm = tools.start_qemu(args.iso.resolve(), vm_dir / "data.qcow2", vm_dir, port, args.accel, None)
    guest = tools.Guest(vm_dir, port, "", time.monotonic())
    try:
        guest.wait("Desktop ist bereit", 240)
        guest.connect()
        guest.login(first=True)
        guest.shell()
        guest.pkg("native update --from /data/repo1", "Katalog v2 geladen (Generation 1, 1 Pakete).", 300)
        guest.pkg("native install --from /data/repo1 lang", "v2 installiert: lang 1.0.0-r1.", 300)

        # Altes Programm in der Textshell starten; die Shell dieser Sitzung gehoert ihm, bis es endet.
        old = guest.mark()
        begun = time.monotonic()
        guest.qmp.line("run --pkg /usr/bin/lang")
        guest.wait("LINUXRUN BEGIN /usr/bin/lang", 120, old)
        wait_output(guest, old, "lang r1 start\n", 60)

        # Zweite Sitzung: Desktop-Terminal. Upgrade, waehrend r1 schlaeft.
        guest.desktop()
        guest.launch("Terminal")
        time.sleep(4)
        guest.qmp.tap("ret")
        guest.pkg("native update --from /data/repo2", "Katalog v2 geladen (Generation 2, 1 Pakete).", 300)
        guest.pkg("native upgrade --from /data/repo2", "v2 aktualisiert: ", 300)
        guest.pkg("native resolve /usr/share/lang/text", "v2 resolve usr/share/lang/text: lang 1.0.0-r2", 60)
        second = guest.mark()
        guest.qmp.line("run --pkg /usr/bin/lang")
        time.sleep(5)
        if "LINUXRUN BEGIN" in (guest.work / "serial.log").read_text(encoding="utf-8", errors="replace")[second:]:
            raise RuntimeError("zweiter Start lief neben dem ersten")
        upgraded = guest.mark()
        print(f"Upgrade und Pruefungen fertig nach {time.monotonic() - begun:.0f} s (Programm schlaeft {args.sleep} s)", flush=True)
        text, end = output_since(guest, old)
        if end >= 0 or "text r" in text:
            raise RuntimeError(f"altes Programm endete vor Abschluss des Upgrades ({text!r}); --sleep erhoehen")

        # 1. Das alte Programm liest nach dem Upgrade seinen Stand r1.
        guest.wait("LINUXRUN END exit 0", args.sleep + 120, upgraded)
        text, _ = output_since(guest, old)
        if text != "lang r1 start\ntext r1\n":
            raise RuntimeError(f"altes Programm gab {text!r} aus, erwartet r1-Stand")
        print("altes Programm: lang r1 start / text r1, exit 0", flush=True)
        time.sleep(1)

        # 2. Neuer Start (raeumt vorher r1 ab) sieht r2.
        fresh = guest.mark()
        guest.qmp.line("run --pkg /usr/bin/lang")
        guest.wait("LINUXRUN BEGIN /usr/bin/lang", 120, fresh)
        wait_output(guest, fresh, "lang r2 start\n", 60)
        time.sleep(2)
        guest.wait("LINUXRUN END exit 0", args.sleep + 120, fresh)
        text, _ = output_since(guest, fresh)
        if text != "lang r2 start\ntext r2\n":
            raise RuntimeError(f"neuer Start gab {text!r} aus, erwartet r2")
        print("neuer Start: lang r2 start / text r2, exit 0", flush=True)
        time.sleep(1)
        vm.kill()
        vm.wait()
        versions = stored_versions(vm_dir / "data.qcow2", work / "data.raw")
        if versions is None:
            print("Versionsordner offline: nicht geprueft (mdir fehlt)", flush=True)
        elif versions != ["1.0.0-r2"]:
            raise RuntimeError(f"Versionsordner nach dem Ende: {versions}, erwartet nur 1.0.0-r2")
        else:
            print("Versionsordner offline: nur 1.0.0-r2", flush=True)
        print(f"PACKAGE2_UPGRADE_LIVE PASS; Protokolle: {work}", flush=True)
        return 0
    except (RuntimeError, TimeoutError) as error:
        guest.shot("fehler")
        print(f"PACKAGE2_UPGRADE_LIVE FAIL {error}; Protokolle: {work}", flush=True)
        return 1
    finally:
        if vm.poll() is None:
            vm.kill()
            vm.wait()


if __name__ == "__main__":
    sys.exit(main())
