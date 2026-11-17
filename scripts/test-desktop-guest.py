#!/usr/bin/env python3
"""Gasttest fuer Boot, Anmeldung, Desktop und Textshell eines ZehOS-ISOs unter QEMU; bedient wird per QMP.

Normales ISO: Boot bis "Desktop ist bereit", Ersteinrichtung des Kontos auf einer leeren Wegwerfplatte, Terminal
oeffnen, tippen und ueber X schliessen, Dateien und Einstellungen oeffnen, Textshell mit Ab- und Anmeldung, zurueck
zum Desktop (F4), shutdown ueber die Textshell. Danach darf das serielle Protokoll keine Error-Zeile enthalten.
Mit --recovery: Boot bis "Textshell ist bereit.", kein Desktop, uname, shutdown.

Geprueft wird ueber serielle Marken und Ausschnitte der Bildschirmfotos (ohne Leiste mit Uhr); der Fensterinhalt
selbst ist Sache der Hosttests. Jeder Schritt hat eine Zeitgrenze; bei einem Fehlschlag bleiben Bildschirmfoto,
serial.log und stderr.log unter .tools/desktop-guest/<zufaellig>/ liegen. Nur QEMU; VMware ist damit nicht geprueft.
"""
import argparse
import importlib.util
from pathlib import Path
import re
import socket
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)

# Aus ProjektZeh/Graphics/Desktop.cs (BarHeight, OpenTerminal) und Window.cs (Schliessen-Feld 38 px am rechten Rand).
BAR_HEIGHT = 56
# Format von SystemLogEntry.ToString(): "[<n> ms] <Level> <Komponente>: <Text>". Nicht am Zeilenanfang verankert:
# Debugausgaben anderer Threads koennen den Zeilenanfang ueberschreiben, eine Fehlerzeile darf dadurch nicht verschwinden.
ERROR_LINE = re.compile(r"\[\d+ ms\] Error [^:\n]+: ")


def region(ppm: bytes, box: tuple[int, int, int, int]) -> bytes:
    """Ausschnitt (links, oben, rechts, unten; rechts/unten exklusiv) aus einem P6-Bild mit 8 Bit je Kanal."""
    magic, size, maximum, pixels = ppm.split(b"\n", 3)
    if magic != b"P6" or maximum != b"255":
        raise ValueError("Nur P6 mit Maximalwert 255")
    width, height = map(int, size.split())
    left, top, right, bottom = box
    if not (0 <= left < right <= width and 0 <= top < bottom <= height):
        raise ValueError(f"Ausschnitt {box} liegt nicht in {width}x{height}")
    return b"".join(pixels[(row * width + left) * 3:(row * width + right) * 3] for row in range(top, bottom))


def error_lines(serial: str) -> list[str]:
    """Zeilen des seriellen Protokolls mit einem Eintrag der Stufe Error."""
    return [line for line in serial.splitlines() if ERROR_LINE.search(line)]


class Run:
    """Ein Gastlauf: Wegwerfplatte, QEMU, Guest aus test-wasm-doom-desktop.py und Bildvergleiche."""

    def __init__(self, work: Path, start_vm, boot_timeout: float):
        self.work, self.boot_timeout = work, boot_timeout
        self.port = tools.free_port()
        self.guest = tools.Guest(work, self.port, "", time.monotonic())
        self.vm: subprocess.Popen = start_vm(self.port)

    def connect(self, deadline: float) -> None:
        """Verbindet QMP frueh, damit auch ein vor der Bootmarke haengender Gast ein Bildschirmfoto liefert.

        Ohne QMP bis deadline laeuft der Ablauf ohne Foto weiter; die Bootmarke entscheidet dann ueber den Fehlschlag.
        """
        while time.monotonic() < deadline and self.vm.poll() is None:
            try:
                socket.create_connection(("127.0.0.1", self.port), timeout=2).close()
                self.guest.connect()
                return
            except OSError:
                time.sleep(0.5)
        print("QMP nicht erreichbar; weiter ohne Bildschirmfoto bis zur Bootmarke.", flush=True)

    def boot(self, marker: str) -> None:
        deadline = time.monotonic() + self.boot_timeout
        self.connect(deadline)
        self.guest.wait(marker, max(1.0, deadline - time.monotonic()))
        self.guest.log(marker)
        if self.guest.qmp is None:
            self.guest.connect()

    def screen(self, name: str) -> bytes:
        """Bildschirmfoto als PNG ablegen und den Ausschnitt der Arbeitsflaeche ohne Leiste liefern."""
        ppm = self.work / "screen.ppm"
        self.guest.qmp.command("screendump", {"filename": str(ppm)})
        data = ppm.read_bytes()
        _, size, _ = data.split(b"\n", 3)[:3]
        width, height = map(int, size.split())
        self.guest.size = (width, height)
        self.guest.shot(name)
        return region(data, (0, 0, width, height - BAR_HEIGHT))

    def park_pointer(self) -> None:
        # Zeiger in die linke untere Ecke der Leiste: dort liegt kein Bedienelement, und der Ausschnitt enthaelt ihn nicht.
        width, height = self.guest.size
        self.guest.qmp.move(0, height - 1, width, height)
        time.sleep(0.3)

    def until(self, name: str, test, timeout: float = 60) -> bytes:
        """Fotografiert, bis test(Ausschnitt) gilt; scheitert nach timeout mit dem letzten Foto."""
        deadline = time.monotonic() + timeout
        while True:
            current = self.screen(name)
            if test(current):
                return current
            if time.monotonic() > deadline:
                raise AssertionError(f"{name}: erwartete Bildaenderung blieb aus")
            time.sleep(1)

    def changed(self, name: str, previous: bytes, timeout: float = 60) -> bytes:
        return self.until(name, lambda current: current != previous, timeout)

    def stable(self, name: str, timeout: float = 30) -> bytes:
        """Wartet, bis zwei aufeinanderfolgende Fotos gleich sind; Uebergaenge sollen nicht ins Ausgangsbild geraten."""
        previous = [self.screen(name)]

        def same(current: bytes) -> bool:
            result, previous[0] = current == previous[0], current
            return result
        return self.until(name, same, timeout)

    def close_terminal(self) -> None:
        # Erstes Terminal nach dem Boot: Position 72/72, Breite wie Desktop.OpenTerminal; X 20 px vor dem rechten Rand.
        width, height = self.guest.size
        window_width = min(max(width * 3 // 4, 640), 1100)
        self.guest.qmp.click(72 + window_width - 20, 72 + 20, width, height)

    def powered_off(self) -> None:
        """Nach shutdown muss QEMU binnen 60 s mit Exitcode 0 enden (-action shutdown=poweroff)."""
        try:
            code = self.vm.wait(60)
        except subprocess.TimeoutExpired:
            raise AssertionError("QEMU endete nicht binnen 60 s nach shutdown") from None
        if code != 0:
            raise AssertionError(f"QEMU endete nach shutdown mit Exitcode {code}")
        self.guest.log("ausgeschaltet")

    def desktop_flow(self) -> None:
        guest = self.guest
        self.boot("Desktop ist bereit")
        time.sleep(1)
        self.screen("boot")
        guest.login(first=True)
        self.park_pointer()
        # Nach der Anmeldung ist die leere Arbeitsflaeche das Ausgangsbild fuer Terminal und Schliessen.
        empty = self.stable("arbeitsflaeche")

        guest.launch("Terminal")
        terminal = self.changed("terminal", empty)
        # Getippte Zeichen koennen unter Last verloren gehen; dann nur die Eingabe wiederholen.
        for attempt in range(3):
            guest.qmp.line("echo e2e")
            try:
                self.changed("echo", terminal, 20)
                break
            except AssertionError:
                if attempt == 2:
                    raise
        self.close_terminal()
        self.park_pointer()
        self.until("terminal-geschlossen", lambda current: current == empty)

        before = self.screen("vor-dateien")
        guest.launch("Dateien")
        after = self.changed("dateien", before)
        guest.launch("Einstellungen")
        self.changed("einstellungen", after)

        mark = guest.mark()
        guest.shell("logout")
        guest.wait("Benutzer abgemeldet.", 30, mark)
        guest.login(first=False)
        guest.desktop()
        self.screen("wieder-desktop")
        guest.shell("shutdown")
        self.powered_off()

    def recovery_flow(self) -> None:
        guest = self.guest
        self.boot("Textshell ist bereit.")
        guest.qmp.tap("ret")
        guest.qmp.line("uname")
        time.sleep(1)
        self.screen("uname")
        if tools.find_marker(self.serial(), "Desktop ist bereit", 0) >= 0:
            raise AssertionError("Recovery-Build startete den Desktop")
        # Die Textshell ist schon aktiv; shell() wartet sonst vergeblich auf "Textshell aktiv.".
        guest.qmp.line("shutdown")
        self.powered_off()

    def serial(self) -> str:
        log = self.work / "serial.log"
        return log.read_text(encoding="utf-8", errors="replace") if log.exists() else ""

    def failed(self) -> None:
        """Sichert ein letztes Bildschirmfoto, solange QEMU laeuft; Protokolle bleiben im Arbeitsordner."""
        if self.guest.qmp is not None and self.vm.poll() is None:
            try:
                self.screen("fehlschlag")
            except (OSError, RuntimeError, ValueError):
                pass

    def stop(self) -> None:
        if self.vm.poll() is None:
            self.vm.kill()
            self.vm.wait()


def run(work: Path, start_vm, recovery: bool, boot_timeout: float) -> int:
    """Fuehrt den Ablauf aus; start_vm(port) startet den Gast und liefert seinen Prozess."""
    current = Run(work, start_vm, boot_timeout)
    try:
        current.recovery_flow() if recovery else current.desktop_flow()
        errors = error_lines(current.serial())
        if errors:
            raise AssertionError("Error-Zeilen im seriellen Protokoll:\n" + "\n".join(errors))
        current.guest.log("fertig")
        return 0
    except BaseException:
        current.failed()
        raise
    finally:
        current.stop()
        (work / "data.qcow2").unlink(missing_ok=True)
        print(f"Bilder und Log: {work}", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--accel", choices=["tcg", "kvm", "whpx"], default="tcg")
    parser.add_argument("--recovery", action="store_true", help="ISO mit -p:ZehOSRecovery=true")
    parser.add_argument("--boot-timeout", type=float, default=300, help="Sekunden bis zur Bootmarke")
    args = parser.parse_args()
    iso = args.iso.resolve()
    if not iso.is_file():
        parser.error(f"ISO nicht gefunden: {iso}")
    work = ROOT / ".tools/desktop-guest" / uuid.uuid4().hex[:12]
    (work / "empty").mkdir(parents=True)
    disk = work / "data.qcow2"
    tools.create_disk(work / "empty", disk)
    return run(work, lambda port: tools.start_qemu(iso, disk, work, port, args.accel, None), args.recovery,
               args.boot_timeout)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (AssertionError, TimeoutError, RuntimeError) as error:
        sys.exit(f"FEHLSCHLAG: {error}")
