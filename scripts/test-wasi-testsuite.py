#!/usr/bin/env python3
"""Fuehrt die wasi-testsuite-Pakete aus experiments/WasiTestsuite im ZehOS-Desktop unter QEMU aus.

Das ISO muss mit -p:ZehOSWasm=true und dem oeffentlichen Gegenstueck von --key (ausschliesslich Testschluessel) in
-p:ZehOSPackageKeyPath gebaut sein. --packages ist bin/packages aus experiments/WasiTestsuite/build.py. Das Skript
signiert jedes Paket mit --key in ein lokales Repository auf einer frischen Datenplatte (qcow2), installiert es mit
"pkg install-local" und startet es ueber das Startmenue. Ergebnis je Test aus der seriellen Ausgabe:
  "APP <id> beendet"      -> Exitcode 0 -> PASS
  "APP <id> Ende: <text>" -> Exitcode ungleich 0 oder Abbruch (Trap, fehlender Import) -> FAIL mit <text>
  nichts innerhalb --timeout -> FAIL (Zeitgrenze); die VM wird verworfen und die uebrigen Tests laufen in einer neuen.
Tests ausserhalb des Umfangs von Plan 12.8 laufen mit und erscheinen als AUSSERHALB mit Ergebnis und Grund.
Vorhandene VM-Images werden nicht gelesen. Protokolle bleiben unter .tools/wasi-testsuite-guest/ erhalten.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
VERSION = "1.0.0"


def load_tools():
    spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def outcome(content: str, package: str, start: int, find_marker) -> tuple[str, str, int] | None:
    """(PASS|FAIL, Text, Position danach) fuer die erste Ende-Zeile von package ab start, sonst None.

    find_marker ist die tolerante Suche aus test-wasm-doom-desktop.py (Cosmos zerteilt serielle Zeilen)."""
    passed = find_marker(content, f"APP {package} beendet", start)
    failed = find_marker(content, f"APP {package} Ende: ", start)
    if failed >= 0 and (passed < 0 or failed < passed):
        end = content.find("\n", failed)
        if end < 0:
            return None  # Zeile noch unvollstaendig
        return "FAIL", content[failed:end].strip(), end
    if passed >= 0:
        return "PASS", "", passed
    return None


def verdict(entry: dict, result: str) -> str:
    return result if entry["scope"] == "in" else "AUSSERHALB"


def table(entries: list[dict], results: dict[str, tuple[str, str]]) -> str:
    lines = ["| Test | Umfang | Ergebnis | Meldung |", "| --- | --- | --- | --- |"]
    for entry in entries:
        result, text = results.get(entry["test"], ("FAIL", "nicht gelaufen"))
        scope = "ja" if entry["scope"] == "in" else "nein: " + entry["reason"]
        shown = verdict(entry, result) + (f" ({result})" if entry["scope"] != "in" else "")
        lines.append(f"| `{entry['test']}` | {scope} | {shown} | {text.replace('|', '/')} |")
    return "\n".join(lines)


def build_repository(entries: list[dict], packages: Path, key: Path, seed: Path) -> None:
    for entry in entries:
        directory = packages / entry["test"]
        files = [f"{path.resolve()}={path.relative_to(directory).as_posix()}"
                 for path in sorted(directory.rglob("*")) if path.is_file()]
        subprocess.run([sys.executable, str(ROOT / "scripts/build-package.py"), "package", "--key", str(key.resolve()),
                        "--repo", str(seed / "pkgs"), "--id", entry["id"], "--version", VERSION, "--name", entry["name"],
                        "--entry", entry["entry"], *files], check=True, stdout=subprocess.DEVNULL)


def run_vm(tools, iso: Path, accel: str, work: Path, entries: list[dict], timeout: float,
           results: dict[str, tuple[str, str]]) -> list[dict]:
    """Bootet eine VM, installiert und startet die Tests nacheinander; liefert die nach einer Zeitgrenze offenen Tests."""
    work.mkdir(parents=True)
    tools.create_disk(work.parent / "seed", work / "data.qcow2")
    port = tools.free_port()
    vm = tools.start_qemu(iso, work / "data.qcow2", work, port, accel, None)
    guest = tools.Guest(work, port, "", time.monotonic())
    try:
        guest.wait("Desktop ist bereit", 180)
        guest.connect()
        guest.login(first=True)
        guest.shell()
        for entry in entries:
            guest.pkg(f"install-local /data/pkgs/{entry['id']}/{VERSION}", f"{entry['id']} {VERSION} installiert.", 300)
        guest.desktop()
        for index, entry in enumerate(entries):
            mark = guest.mark()
            guest.launch(entry["name"])
            deadline = time.monotonic() + timeout
            found = None
            while found is None and time.monotonic() < deadline:
                time.sleep(0.5)
                content = (work / "serial.log").read_text(encoding="utf-8", errors="replace")
                found = outcome(content, entry["id"], mark, tools.find_marker)
            if found is None:
                guest.shot(f"{entry['test']}-zeitgrenze")
                results[entry["test"]] = ("FAIL", f"kein Ende nach {timeout:.0f} s")
                print(f"{entry['test']}: Zeitgrenze", flush=True)
                return entries[index + 1:]
            results[entry["test"]] = found[:2]
            print(f"{entry['test']}: {found[0]} {found[1]}", flush=True)
            # Der naechste Start braucht den freien Wasm-Worker; die Speicherzeile folgt dem Aufraeumen.
            guest.wait("WASM Speicher", 60, found[2])
        return []
    finally:
        if vm.poll() is None:
            vm.kill()
            vm.wait()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--packages", type=Path, default=ROOT / "experiments/WasiTestsuite/bin/packages")
    parser.add_argument("--key", type=Path, required=True)
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    parser.add_argument("--timeout", type=float, default=60, help="Sekunden je Test bis zur Ende-Zeile")
    parser.add_argument("--only", nargs="*", help="nur diese Tests (Namen wie in tests.json)")
    args = parser.parse_args()
    entries = json.loads((args.packages / "tests.json").read_text(encoding="utf-8"))
    if args.only:
        unknown = set(args.only) - {entry["test"] for entry in entries}
        if unknown:
            parser.error(f"unbekannte Tests: {', '.join(sorted(unknown))}")
        entries = [entry for entry in entries if entry["test"] in args.only]
    tools = load_tools()
    work = ROOT / ".tools/wasi-testsuite-guest" / uuid.uuid4().hex[:12]
    build_repository(entries, args.packages, args.key, work / "seed")
    results: dict[str, tuple[str, str]] = {}
    remaining, attempt = entries, 0
    while remaining:
        attempt += 1
        remaining = run_vm(tools, args.iso.resolve(), args.accel, work / f"vm{attempt}", remaining, args.timeout, results)
    print(table(entries, results), flush=True)
    failed = [entry["test"] for entry in entries if entry["scope"] == "in" and results[entry["test"]][0] != "PASS"]
    passed = sum(1 for entry in entries if entry["scope"] == "in") - len(failed)
    print(f"WASI_TESTSUITE im Umfang PASS {passed}/{passed + len(failed)}; Protokolle: {work}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
