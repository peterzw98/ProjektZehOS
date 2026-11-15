#!/usr/bin/env python3
"""Startet ZehOS in VMware Workstation (Linux) wiederholt neu und zaehlt das Ergebnis der PS/2-Tastaturbindung.

Zwei Arten des Neustarts:
  --iso    Die VM bootet nur vom ISO, ohne Datenlaufwerk und ohne Netz; neu gestartet wird mit "vmrun reset hard".
           Der Arbeitsordner (Standard .tools/vmware-ps2-reset) wird bei jedem Lauf neu angelegt.
  --vmx    Eine vorhandene, ausgeschaltete VM mit installiertem ZehOS (ohne ISO, VNC auf 127.0.0.1:--vnc-port).
           Nach jeder Bindung meldet sich das Skript per VNC an (--user, Kennwort aus der Umgebungsvariablen
           ZEHOS_PS2_PASSWORD), wechselt mit F4 in die Textshell und tippt "reboot". Das ist der Warmstart ueber ACPI,
           nach dem der Fehler beobachtet wurde. Scheitert die Bindung, startet "vmrun reset hard" die VM neu.
           serial.log der VM wird zu Beginn geleert; die Platte wird weiterbenutzt.

Nach jedem Start wartet das Skript auf die serielle Zeile "ps2:kbd offer Ps2KeyboardDriver -> ...". Am Ende steht je
Ergebnis die Anzahl; der Exitcode ist 1, wenn ein Start fehlte oder eine Bindung nicht "bound" war.
"""

import argparse
import collections
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VNC_TYPE = Path(__file__).resolve().with_name("vnc_type.py")
# Nur vollstaendige Zeilen zaehlen: VMware kann eine Zeile gerade schreiben, waehrend das Skript liest.
OFFER = re.compile(r"ps2:kbd offer Ps2KeyboardDriver -> ([^\r\n]*)\r?\n")
DESKTOP_READY = re.compile(r"Desktop: Desktop ist bereit\.\r?\n")
LOGGED_IN = re.compile(r"Session: Benutzer angemeldet\.\r?\n")
TEXT_SHELL = re.compile(r"Desktop: Textshell aktiv\.\r?\n")
SERIAL_FILE = re.compile(r'^serial0\.fileName\s*=\s*"([^"]*)"', re.MULTILINE)
# Ein Start bis zur Tastaturbindung dauert in VMware wenige Sekunden; die Frist faengt haengende Starts ab.
BOOT_TIMEOUT = 120
# PBKDF2 mit 600000 Iterationen braucht im Gast einige Sekunden.
STEP_TIMEOUT = 60

VMX = """.encoding = "UTF-8"
config.version = "8"
virtualHW.version = "21"
displayName = "ZehOS PS2 Reset"
guestOS = "other-64"
memsize = "1024"
numvcpus = "1"
firmware = "efi"
efi.secureBoot.enabled = "FALSE"
ide1:0.present = "TRUE"
ide1:0.deviceType = "cdrom-image"
ide1:0.fileName = "{iso}"
ide1:0.startConnected = "TRUE"
floppy0.present = "FALSE"
ethernet0.present = "FALSE"
sound.present = "FALSE"
serial0.present = "TRUE"
serial0.fileType = "file"
serial0.fileName = "{serial}"
serial0.startConnected = "TRUE"
msg.autoAnswer = "TRUE"
"""


def vmrun(*arguments: str) -> None:
    # vmrun meldet Fehler auf stdout; sie gehen sonst im CalledProcessError verloren.
    completed = subprocess.run(["vmrun", "-T", "ws", *arguments], capture_output=True, text=True)
    if completed.returncode != 0:
        raise RuntimeError(f"vmrun {' '.join(arguments)}: {(completed.stdout + completed.stderr).strip()}")


def prepare_work(work: Path) -> None:
    """Legt den Arbeitsordner neu an; einen fremden Ordner loescht das Skript nicht."""
    if work.exists():
        if not (work / "ps2-reset.vmx").is_file():
            sys.exit(f"{work} ist kein Arbeitsordner dieses Skripts (ps2-reset.vmx fehlt); bitte anderen --work waehlen")
        shutil.rmtree(work)
    work.mkdir(parents=True)


def wait_for_match(serial: Path, pattern: re.Pattern[str], seen: int, timeout: float) -> re.Match[str] | None:
    """Liefert den Treffer mit Index seen, sobald er im Protokoll steht, oder None nach der Frist."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if serial.is_file():
            matches = list(pattern.finditer(serial.read_text(encoding="utf-8", errors="replace")))
            if len(matches) > seen:
                return matches[seen]
        time.sleep(1)
    return None


def wait_for_offer(serial: Path, seen: int) -> str | None:
    """Liefert das Ergebnis der naechsten noch nicht gezaehlten Bindung oder None nach der Frist."""
    match = wait_for_match(serial, OFFER, seen, BOOT_TIMEOUT)
    return match.group(1).strip() if match else None


class GuestReboot:
    """Startet den Gast ueber Anmeldung, Textshell und "reboot" neu; zaehlt die eigenen Schritte im Protokoll."""

    def __init__(self, serial: Path, work: Path, port: int, user: str, password: str):
        self.serial = serial
        self.script = work / "ps2-reboot-keys.txt"
        self.port = port
        self.user = user
        self.password = password
        self.desktops = self.logins = self.shells = 0

    def type(self, *lines: str) -> None:
        # Das Testkennwort steht nur kurz in dieser Datei, nicht in der Prozessliste.
        self.script.write_text("\n".join(lines) + "\n", encoding="utf-8")
        try:
            completed = subprocess.run([sys.executable, str(VNC_TYPE), "--port", str(self.port), "--script", str(self.script)],
                                       capture_output=True, text=True)
        finally:
            self.script.unlink(missing_ok=True)
        if completed.returncode != 0:
            raise RuntimeError(f"vnc_type: {completed.stderr.strip()}")

    def wait(self, pattern: re.Pattern[str], seen: int, what: str) -> None:
        if wait_for_match(self.serial, pattern, seen, STEP_TIMEOUT) is None:
            raise RuntimeError(f"{what} nicht erreicht")

    def __call__(self, vmx: Path) -> None:
        self.wait(DESKTOP_READY, self.desktops, "Anmeldebildschirm")
        self.desktops += 1
        self.type(f"text: {self.user}", "key: return", "sleep: 1", f"text: {self.password}", "key: return")
        self.wait(LOGGED_IN, self.logins, "Anmeldung")
        self.logins += 1
        self.type("sleep: 1", "key: f4")
        self.wait(TEXT_SHELL, self.shells, "Textshell")
        self.shells += 1
        self.type("text: reboot", "key: return")

    def skip_boot(self) -> None:
        """Ein Start ohne Tastatur wird hart neu gestartet; seine Desktopmarke darf den Zaehler nicht verschieben."""
        if wait_for_match(self.serial, DESKTOP_READY, self.desktops, STEP_TIMEOUT) is not None:
            self.desktops += 1


def run_loop(vmx: Path, serial: Path, runs: int, guest: GuestReboot | None = None) -> tuple[collections.Counter[str], bool]:
    """Liefert die Ergebnisse und ob alle angeforderten Starts ausgewertet wurden."""
    results: collections.Counter[str] = collections.Counter()
    try:
        vmrun("start", str(vmx), "nogui")
        for run in range(runs):
            result = wait_for_offer(serial, run)
            if result is None:
                results["kein Ergebnis (Frist abgelaufen)"] += 1
                print(f"Start {run + 1}: kein Ergebnis", flush=True)
                break
            results[result] += 1
            print(f"Start {run + 1}: {result}", flush=True)
            if run + 1 == runs:
                break
            if guest is not None and result == "bound":
                guest(vmx)
            else:
                if guest is not None:
                    guest.skip_boot()
                vmrun("reset", str(vmx), "hard")
    except (RuntimeError, OSError, KeyboardInterrupt) as error:
        print(f"Abbruch: {error!r}", file=sys.stderr)
    finally:
        # Die VM wird auch nach einem Fehler beendet, damit kein Testgast weiterlaeuft.
        subprocess.run(["vmrun", "-T", "ws", "stop", str(vmx), "hard"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return results, sum(results.values()) == runs


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--iso", type=Path, help="ISO fuer Hardware-Resets ohne Datenlaufwerk")
    source.add_argument("--vmx", type=Path, help="ausgeschaltete VM mit installiertem ZehOS fuer Gast-reboot")
    parser.add_argument("--runs", type=int, default=50, help="Anzahl der Starts (erster Start plus Neustarts)")
    parser.add_argument("--work", type=Path, default=ROOT / ".tools" / "vmware-ps2-reset", help="nur mit --iso")
    parser.add_argument("--vnc-port", type=int, default=5974, help="VNC-Port der VM (nur mit --vmx)")
    parser.add_argument("--user", default="tester", help="Konto fuer die Anmeldung (nur mit --vmx)")
    args = parser.parse_args()
    if shutil.which("vmrun") is None:
        sys.exit("vmrun nicht gefunden (VMware Workstation unter Linux)")

    guest = None
    if args.iso is not None:
        work = args.work.resolve()
        prepare_work(work)
        serial = work / "serial.log"
        vmx = work / "ps2-reset.vmx"
        vmx.write_text(VMX.format(iso=args.iso.resolve(), serial=serial), encoding="utf-8")
    else:
        vmx = args.vmx.resolve()
        password = os.environ.get("ZEHOS_PS2_PASSWORD")
        found = SERIAL_FILE.search(vmx.read_text(encoding="utf-8"))
        if not password or found is None:
            sys.exit("ZEHOS_PS2_PASSWORD und serial0.fileName in der VMX werden benoetigt")
        serial = Path(found.group(1))
        serial.write_text("", encoding="utf-8")
        guest = GuestReboot(serial, vmx.parent, args.vnc_port, args.user, password)

    results, complete = run_loop(vmx, serial, args.runs, guest)
    for result, count in results.most_common():
        print(f"{count:4d}  {result}")
    print(f"Protokoll: {serial}")
    return 0 if complete and set(results) == {"bound"} else 1


if __name__ == "__main__":
    sys.exit(main())
