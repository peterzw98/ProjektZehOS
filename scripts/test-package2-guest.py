#!/usr/bin/env python3
"""Gastprobe N3-B1: Paketdienst v2 ueber "pkg native" und Paketindex im ZehOS-Desktop unter QEMU.

Das ISO muss mit -p:ZehOSWasm=true und dem oeffentlichen Gegenstueck von --key (ausschliesslich Testschluessel) in
-p:ZehOSPackageKeyPath gebaut sein. Das Skript baut mit scripts/package2.py zwei lokale Repositories auf einer frischen
Datenplatte (qcow2 aus vvfat, vorhandene Images werden nicht gelesen):
  /data/repo1  musl 1.2.5-r1, libfoo 1.0.0-r1, app 1.0.0-r1 (Kette app -> libfoo -> musl), Katalog Generation 1
  /data/repo2  dasselbe plus musl 1.2.5-r2, Katalog Generation 2
app bringt eine ausfuehrbare Datei, einen Dateilink und den Ordnerlink usr/lib64 -> lib; musl den Link usr/lib/libc.so.
Ablauf: update, install (ganze Kette in einem Wechsel), resolve (Datei, Links, Ordnerlink, Grossschreibung, fehlend,
Wurzelausbruch), update auf Generation 2, upgrade (Index wechselt), Neustart (Index aus active2 neu), remove,
remove --unused. Jeder Schritt wartet auf seine serielle "PKG ..."-Zeile. Protokolle unter .tools/package2-guest/.

N3-B2 (ISO zusaetzlich mit -p:ZehOSLinuxRun=true, sonst --no-run): Paket hello bringt ein winziges statisches ELF
(von diesem Skript erzeugt, kein Uebersetzer noetig), das seine Version ausgibt. "run --pkg" startet es aus dem Paketindex
vor und nach einem Upgrade von hello (neuer Start sieht den neuen Stand) und nach dem Neustart; ein Pfad ohne Ausfuehrrecht
und ein fehlender Pfad starten nicht.
"""
import argparse
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]


def load_tools():
    spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# id, version, release, depends, Dateien (Pfad, Inhalt, ausfuehrbar), Links (Pfad, Ziel)
PACKAGES = [
    ("musl", "1.2.5", 1, [], [("lib/ld-musl-x86_64.so.1", "musl r1\n", True)], {"usr/lib/libc.so": "../../lib/ld-musl-x86_64.so.1"}),
    ("libfoo", "1.0.0", 1, ["musl>=1.2.5-r1"], [("usr/lib/libfoo.so.1.0", "foo r1\n", False)], {"usr/lib/libfoo.so.1": "libfoo.so.1.0"}),
    ("app", "1.0.0", 1, ["libfoo>=1.0.0-r1"], [("usr/bin/app", "app r1\n", True), ("usr/share/app/readme", "lies\n", False)],
     {"usr/bin/app-link": "app", "usr/lib64": "lib"}),
]
def hello_elf(text: str) -> bytes:
    """Statisches x86-64-ELF: write(1, text) und exit_group(0); ein PT_LOAD ab 0x400000, Code direkt hinter den Koepfen."""
    message = text.encode("ascii")
    code = (b"\xb8\x01\x00\x00\x00" b"\xbf\x01\x00\x00\x00" b"\x48\x8d\x35" + (16).to_bytes(4, "little")
            + b"\xba" + len(message).to_bytes(4, "little") + b"\x0f\x05" b"\xb8\xe7\x00\x00\x00" b"\x31\xff" b"\x0f\x05")
    base, heads = 0x400000, 64 + 56
    size = heads + len(code) + len(message)
    header = (b"\x7fELF\x02\x01\x01" + bytes(9) + (2).to_bytes(2, "little") + (0x3E).to_bytes(2, "little") + (1).to_bytes(4, "little")
              + (base + heads).to_bytes(8, "little") + (64).to_bytes(8, "little") + bytes(8) + bytes(4)
              + (64).to_bytes(2, "little") + (56).to_bytes(2, "little") + (1).to_bytes(2, "little") + bytes(6))
    program = ((1).to_bytes(4, "little") + (5).to_bytes(4, "little") + bytes(8) + base.to_bytes(8, "little") + base.to_bytes(8, "little")
               + size.to_bytes(8, "little") + size.to_bytes(8, "little") + (0x1000).to_bytes(8, "little"))
    return header + program + code + message


HELLO = ("hello", "1.0.0", 1, ["musl>=1.2.5-r1"], [("usr/bin/hello", hello_elf("hallo r1\n"), True), ("usr/share/hello/text", "kein programm\n", False)],
         {"usr/bin/hallo": "hello"})
HELLO_UPGRADE = ("hello", "1.0.0", 2, ["musl>=1.2.5-r1"], [("usr/bin/hello", hello_elf("hallo r2\n"), True), ("usr/share/hello/text", "kein programm\n", False)],
                 {"usr/bin/hallo": "hello"})
UPGRADE = ("musl", "1.2.5", 2, [], [("lib/ld-musl-x86_64.so.1", "musl r2 neu\n", True)], {"usr/lib/libc.so": "../../lib/ld-musl-x86_64.so.1"})


def toml_string(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def package(key: Path, repo: Path, scratch: Path, entry) -> None:
    pid, version, release, depends, files, links = entry
    source = scratch / f"{pid}-{version}-r{release}"
    source.mkdir(parents=True)
    spec = [f"id = {toml_string(pid)}", f"version = {toml_string(version)}", f"release = {release}",
            'runtime = "linux-x64-v1"', 'license = "MIT"',
            "depends = [" + ", ".join(toml_string(d) for d in depends) + "]",
            "exec = [" + ", ".join(toml_string(path) for path, _, executable in files if executable) + "]", "", "[link]"]
    spec += [f"{toml_string(path)} = {toml_string(target)}" for path, target in links.items()]
    (source / "spec.toml").write_text("\n".join(spec) + "\n", encoding="utf-8")
    arguments = []
    for index, (path, content, _) in enumerate(files):
        content_file = source / f"datei{index}"
        if isinstance(content, bytes):
            content_file.write_bytes(content)
        else:
            content_file.write_text(content, encoding="ascii")
        arguments.append(f"{content_file}={path}")
    subprocess.run([sys.executable, str(ROOT / "scripts/package2.py"), "package", "--key", str(key), "--repo", str(repo),
                    "--spec", str(source / "spec.toml"), *arguments], check=True, stdout=subprocess.DEVNULL)


def catalog(key: Path, repo: Path, generation: int) -> None:
    subprocess.run([sys.executable, str(ROOT / "scripts/package2.py"), "catalog", "--key", str(key), "--repo", str(repo),
                    "--generation", str(generation)], check=True, stdout=subprocess.DEVNULL)


def build_seed(key: Path, seed: Path, scratch: Path) -> None:
    repo1, repo2 = seed / "repo1", seed / "repo2"
    for entry in PACKAGES + [HELLO]:
        package(key, repo1, scratch, entry)
    catalog(key, repo1, 1)
    shutil.copytree(repo1, repo2)
    package(key, repo2, scratch / "upgrade", UPGRADE)
    package(key, repo2, scratch / "upgrade", HELLO_UPGRADE)
    catalog(key, repo2, 2)


STEPS_BEFORE_REBOOT = [
    ("native update --from /data/repo1", "Katalog v2 geladen (Generation 1, 4 Pakete)."),
    ("native install --from /data/repo1 app", "v2 installiert: musl 1.2.5-r1, libfoo 1.0.0-r1, app 1.0.0-r1."),
    ("native install --from /data/repo1 hello", "v2 installiert: hello 1.0.0-r1."),
    ("native resolve /usr/bin/app", "v2 resolve usr/bin/app: app 1.0.0-r1, 7 B, ausfuehrbar"),
    ("native resolve /usr/bin/app-link", "v2 resolve usr/bin/app: app 1.0.0-r1"),
    ("native resolve /usr/lib64/libc.so", "v2 resolve lib/ld-musl-x86_64.so.1: musl 1.2.5-r1, 8 B, ausfuehrbar"),
    ("native resolve /USR/LIB/libfoo.so.1", "v2 resolve usr/lib/libfoo.so.1.0: libfoo 1.0.0-r1, 7 B, nicht ausfuehrbar"),
    ("native resolve /usr/lib64", "v2 resolve usr/lib: Ordner"),
    ("native resolve /usr/bin/fehlt", "v2 resolve: Nicht im Paketbestand"),
    ("native resolve /usr/lib64/../../data", "v2 resolve: Kein kanonischer Pfad"),
    ("native remove libfoo", "v2: Paket "),
    ("native update --from /data/repo2", "Katalog v2 geladen (Generation 2, 4 Pakete)."),
    ("native upgrade --from /data/repo2", "v2 aktualisiert: "),
    ("native resolve /usr/lib64/libc.so", "v2 resolve lib/ld-musl-x86_64.so.1: musl 1.2.5-r2, 12 B, ausfuehrbar"),
]
STEPS_AFTER_REBOOT = [
    ("native resolve /usr/lib/libc.so", "v2 resolve lib/ld-musl-x86_64.so.1: musl 1.2.5-r2, 12 B, ausfuehrbar"),
    ("native resolve /usr/bin/app-link", "v2 resolve usr/bin/app: app 1.0.0-r1"),
    ("native remove app", "v2 entfernt: app."),
    ("native resolve /usr/bin/app", "v2 resolve: Nicht im Paketbestand"),
    ("native remove hello", "v2 entfernt: hello."),
    ("native remove --unused", "v2 entfernt: libfoo, musl."),
    ("native remove --unused", "v2: nichts zu tun."),
]


# N3-B2: (Befehl, erwartete Ausgabe als Text oder None, erwartetes Ende). Vor der Liste STEPS_BEFORE_REBOOT installiert
# "native install hello" das Programm; RUN_AFTER_UPGRADE laeuft nach dem Upgrade, RUN_AFTER_REBOOT nach dem Neustart.
RUN_BEFORE_UPGRADE = [
    ("run --pkg /usr/bin/hello", "hallo r1\n", "exit 0"),
    ("run --pkg /usr/bin/hallo eins", "hallo r1\n", "exit 0"),
    ("run --pkg /USR/BIN/HELLO", "hallo r1\n", "exit 0"),
]
RUN_AFTER_UPGRADE = [("run --pkg /usr/bin/hello", "hallo r2\n", "exit 0")]
RUN_AFTER_REBOOT = [("run --pkg /usr/bin/hallo", "hallo r2\n", "exit 0")]
# Startet nicht: Datei ohne Ausfuehrrecht, fehlender Pfad (Meldung in der Shell, kein LINUXRUN BEGIN).
RUN_REFUSED = [("run --pkg /usr/share/hello/text", "keine ausfuehrbare Paketdatei"), ("run --pkg /usr/bin/fehlt", "Nicht im Paketbestand")]


def start(guest, command: str, output: str, status: str) -> None:
    mark = guest.mark()
    begun = time.monotonic()
    guest.qmp.line(command)
    guest.wait("LINUXRUN BEGIN " + command.split()[2], 120, mark)
    guest.wait("LINUXRUN OUT " + output.encode("ascii").hex().upper(), 120, mark)
    guest.wait("LINUXRUN END " + status, 120, mark)
    print(f"{command}: {time.monotonic() - begun:.0f} s", flush=True)
    time.sleep(1)


def refused(guest, command: str, text: str) -> None:
    """Startet nicht: kein LINUXRUN BEGIN binnen 5 s (die Meldung text steht nur am Bildschirm, nicht im seriellen Protokoll)."""
    mark = guest.mark()
    guest.qmp.line(command)
    time.sleep(5)
    log = (guest.work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]
    if "LINUXRUN BEGIN" in log:
        raise RuntimeError(f"{command}: gestartet, erwartet abgelehnt ({text})")
    print(f"{command}: nicht gestartet (erwartet: {text})", flush=True)


def run(tools, guest, steps) -> None:
    for command, expected in steps:
        guest.pkg(command, expected, 300)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--key", type=Path, required=True, help="privater Testschluessel (PEM), oeffentlicher Teil im ISO")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    parser.add_argument("--no-run", action="store_true", help="ISO ohne -p:ZehOSLinuxRun=true: nur pkg native, kein run --pkg")
    args = parser.parse_args()
    tools = load_tools()
    work = ROOT / ".tools/package2-guest" / uuid.uuid4().hex[:12]
    build_seed(args.key.resolve(), work / "seed", work / "scratch")
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
        upgrade = next(i for i, (command, _) in enumerate(STEPS_BEFORE_REBOOT) if command.startswith("native update --from /data/repo2"))
        run(tools, guest, STEPS_BEFORE_REBOOT[:upgrade])
        runs = 0
        if not args.no_run:
            for step in RUN_BEFORE_UPGRADE:
                start(guest, *step)
            for step in RUN_REFUSED:
                refused(guest, *step)
            runs += len(RUN_BEFORE_UPGRADE) + len(RUN_REFUSED)
        run(tools, guest, STEPS_BEFORE_REBOOT[upgrade:])
        if not args.no_run:
            for step in RUN_AFTER_UPGRADE:
                start(guest, *step)
            runs += len(RUN_AFTER_UPGRADE)
        mark = guest.mark()
        guest.qmp.line("reboot")
        guest.wait("Desktop ist bereit", 240, mark)
        time.sleep(1)
        guest.login(first=False)
        guest.shell()
        if not args.no_run:
            for step in RUN_AFTER_REBOOT:
                start(guest, *step)
            runs += len(RUN_AFTER_REBOOT)
        run(tools, guest, STEPS_AFTER_REBOOT)
        print(f"PACKAGE2_GUEST PASS {len(STEPS_BEFORE_REBOOT) + len(STEPS_AFTER_REBOOT)} Schritte, {runs} Starts; Protokolle: {work}", flush=True)
        return 0
    except (RuntimeError, TimeoutError) as error:
        guest.shot("fehler")
        print(f"PACKAGE2_GUEST FAIL {error}; Protokolle: {work}", flush=True)
        return 1
    finally:
        if vm.poll() is None:
            vm.kill()
            vm.wait()


if __name__ == "__main__":
    sys.exit(main())
