#!/usr/bin/env python3
"""Gastprobe N3-E (Vorarbeit): dynamisch gelinktes Paketprogramm mit gemeinsamer Bibliothek ueber "run --pkg".

Wie scripts/test-package2-guest.py (ISO mit -p:ZehOSWasm=true -p:ZehOSLinuxRun=true und dem oeffentlichen Gegenstueck von --key
in -p:ZehOSPackageKeyPath), aber mit einem echten musl als Testpaket, bis N3-D den Sysroot liefert. Das Skript baut mit musl-gcc
(oder --cc) und legt die Programme in signierte v2-Pakete auf einer frischen Datenplatte; ein Gast-Dateibuendel gibt es nicht,
Interpreter und Bibliotheken kommen nur aus dem Paketbestand:
  musl 1.2.5-r1     lib/ld-musl-x86_64.so.1 (--ld-musl, etwa die libc.so aus musl), Link usr/lib/libc.so
  libgreet 1.0.0-r1 usr/lib/libgreet.so.1, gibt "r1" zurueck; in /data/repo2 libgreet 1.0.0-r2 mit "r2"
  dyn 1.0.0-r1      usr/bin/dynhello (PT_INTERP /lib/ld-musl-x86_64.so.1, NEEDED libgreet.so.1),
                    usr/bin/ohne-interp (PT_INTERP fehlt im Bestand), usr/bin/ohne-lib (NEEDED libfehlt.so.1),
                    usr/bin/kaputt (dynhello nach 100 Bytes abgeschnitten)
Ablauf: update, install dyn (zieht libgreet und musl), dynhello gibt "dyn r1" aus; fehlender Interpreter und kaputtes ELF
starten nicht, fehlende Bibliothek endet im Lader mit exit 127; update/upgrade auf libgreet r2, ein Neustart des Programms
gibt "dyn r2" aus. Protokolle unter .tools/package2-guest/.
Nicht abgedeckt: Upgrade, waehrend das Programm laeuft (Hosttest PackageInterpreterTests haelt den gebundenen Stand fest).
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


def load(name: str, file: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


GREET = 'const char *greet_version(void) { return GREET; }\n'
HELLO = ('#include <stdio.h>\nconst char *greet_version(void);\n'
         'int main(void) { printf("dyn %s\\n", greet_version()); return 0; }\n')
NOLIB = ('#include <stdio.h>\nconst char *fehlt_version(void);\n'
         'int main(void) { printf("%s\\n", fehlt_version()); return 0; }\n')


def compile_all(cc: str, out: Path) -> dict:
    """Baut die Testprogramme und gibt {Name: Bytes} zurueck."""
    src = out / "src"
    src.mkdir(parents=True)
    (src / "greet.c").write_text(GREET, encoding="ascii")
    (src / "hello.c").write_text(HELLO, encoding="ascii")
    (src / "nolib.c").write_text(NOLIB, encoding="ascii")
    (src / "fehlt.c").write_text('const char *fehlt_version(void) { return "x"; }\n', encoding="ascii")

    def cc_run(*args: str) -> None:
        subprocess.run([cc, "-O1", "-Wall", "-Werror", *args], check=True, cwd=src)

    for release in ("r1", "r2"):
        cc_run("-fPIC", "-shared", f'-DGREET="{release}"', "-Wl,-soname,libgreet.so.1", "-o", f"libgreet-{release}.so", "greet.c")
    shutil.copy(src / "libgreet-r1.so", src / "libgreet.so.1")
    cc_run("-o", "dynhello", "hello.c", "-L.", "-l:libgreet.so.1")
    cc_run("-Wl,--dynamic-linker=/lib/ld-fehlt.so.1", "-o", "ohne-interp", "hello.c", "-L.", "-l:libgreet.so.1")
    cc_run("-fPIC", "-shared", "-Wl,-soname,libfehlt.so.1", "-o", "libfehlt.so.1", "fehlt.c")
    cc_run("-o", "ohne-lib", "nolib.c", "-L.", "-l:libfehlt.so.1")
    built = {name: (src / name).read_bytes() for name in ("libgreet-r1.so", "libgreet-r2.so", "dynhello", "ohne-interp", "ohne-lib")}
    built["kaputt"] = built["dynhello"][:100]
    return built


def packages(ld_musl: bytes, built: dict):
    musl = ("musl", "1.2.5", 1, [], [("lib/ld-musl-x86_64.so.1", ld_musl, True)], {"usr/lib/libc.so": "../../lib/ld-musl-x86_64.so.1"})
    greet1 = ("libgreet", "1.0.0", 1, ["musl>=1.2.5-r1"], [("usr/lib/libgreet.so.1", built["libgreet-r1.so"], True)], {})
    greet2 = ("libgreet", "1.0.0", 2, ["musl>=1.2.5-r1"], [("usr/lib/libgreet.so.1", built["libgreet-r2.so"], True)], {})
    dyn = ("dyn", "1.0.0", 1, ["libgreet>=1.0.0-r1"],
           [("usr/bin/dynhello", built["dynhello"], True), ("usr/bin/ohne-interp", built["ohne-interp"], True),
            ("usr/bin/ohne-lib", built["ohne-lib"], True), ("usr/bin/kaputt", built["kaputt"], True)], {})
    return [musl, greet1, dyn], greet2


def ran(guest, command: str, output: str, status: str) -> None:
    """Startet und wartet auf das Ende; die Ausgabe darf in mehreren LINUXRUN-OUT-Stuecken kommen (Text und Zeilenende einzeln)."""
    mark = guest.mark()
    begun = time.monotonic()
    guest.qmp.line(command)
    guest.wait("LINUXRUN BEGIN " + command.split()[2], 120, mark)
    guest.wait("LINUXRUN END " + status, 120, mark)
    log = (guest.work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]
    text = b"".join(bytes.fromhex(line.split("LINUXRUN OUT ", 1)[1].strip()) for line in log.splitlines()
                    if "LINUXRUN OUT " in line).decode("ascii", errors="replace")
    if output not in text:
        raise RuntimeError(f"{command}: Ausgabe {text!r}, erwartet {output!r}")
    print(f"{command}: {status}, {text!r} in {time.monotonic() - begun:.0f} s", flush=True)
    time.sleep(1)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--key", type=Path, required=True, help="privater Testschluessel (PEM), oeffentlicher Teil im ISO")
    parser.add_argument("--ld-musl", type=Path, required=True, help="musl-Lader (libc.so von musl), wird lib/ld-musl-x86_64.so.1")
    parser.add_argument("--cc", default="musl-gcc", help="musl-Uebersetzer (Standard musl-gcc)")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
    args = parser.parse_args()
    tools = load("package_guest_tools", "test-wasm-doom-desktop.py")
    base = load("package2_guest", "test-package2-guest.py")
    work = ROOT / ".tools/package2-guest" / ("dyn-" + uuid.uuid4().hex[:12])
    key = args.key.resolve()
    initial, upgrade = packages(args.ld_musl.resolve().read_bytes(), compile_all(args.cc, work / "build"))
    repo1, repo2 = work / "seed" / "repo1", work / "seed" / "repo2"
    for entry in initial:
        base.package(key, repo1, work / "scratch", entry)
    base.catalog(key, repo1, 1)
    shutil.copytree(repo1, repo2)
    base.package(key, repo2, work / "scratch" / "upgrade", upgrade)
    base.catalog(key, repo2, 2)

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
        guest.pkg("native update --from /data/repo1", "Katalog v2 geladen (Generation 1, 3 Pakete).", 300)
        guest.pkg("native install --from /data/repo1 dyn", "v2 installiert: musl 1.2.5-r1, libgreet 1.0.0-r1, dyn 1.0.0-r1.", 300)
        ran(guest, "run --pkg /usr/bin/dynhello", "dyn r1\n", "exit 0")
        base.refused(guest, "run --pkg /usr/bin/ohne-interp", "Interpreter /lib/ld-fehlt.so.1 nicht lesbar (errno 2)")
        base.refused(guest, "run --pkg /usr/bin/kaputt", "kein gueltiges ELF")
        # Fehlende Bibliothek merkt erst ld-musl im Ring 3: Meldung auf stderr, Ende mit 127.
        ran(guest, "run --pkg /usr/bin/ohne-lib", "libfehlt.so.1", "exit 127")
        guest.pkg("native update --from /data/repo2", "Katalog v2 geladen (Generation 2, 3 Pakete).", 300)
        guest.pkg("native upgrade --from /data/repo2", "v2 aktualisiert: ", 300)
        ran(guest, "run --pkg /usr/bin/dynhello", "dyn r2\n", "exit 0")
        print(f"PACKAGE2_DYN_GUEST PASS 6 Starts; Protokolle: {work}", flush=True)
        return 0
    except (RuntimeError, TimeoutError) as error:
        guest.shot("fehler")
        print(f"PACKAGE2_DYN_GUEST FAIL {error}; Protokolle: {work}", flush=True)
        return 1
    finally:
        if vm.poll() is None:
            vm.kill()
            vm.wait()


if __name__ == "__main__":
    sys.exit(main())
