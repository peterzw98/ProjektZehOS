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
        content_file.write_text(content, encoding="ascii")
        arguments.append(f"{content_file}={path}")
    subprocess.run([sys.executable, str(ROOT / "scripts/package2.py"), "package", "--key", str(key), "--repo", str(repo),
                    "--spec", str(source / "spec.toml"), *arguments], check=True, stdout=subprocess.DEVNULL)


def catalog(key: Path, repo: Path, generation: int) -> None:
    subprocess.run([sys.executable, str(ROOT / "scripts/package2.py"), "catalog", "--key", str(key), "--repo", str(repo),
                    "--generation", str(generation)], check=True, stdout=subprocess.DEVNULL)


def build_seed(key: Path, seed: Path, scratch: Path) -> None:
    repo1, repo2 = seed / "repo1", seed / "repo2"
    for entry in PACKAGES:
        package(key, repo1, scratch, entry)
    catalog(key, repo1, 1)
    shutil.copytree(repo1, repo2)
    package(key, repo2, scratch / "upgrade", UPGRADE)
    catalog(key, repo2, 2)


STEPS_BEFORE_REBOOT = [
    ("native update --from /data/repo1", "Katalog v2 geladen (Generation 1, 3 Pakete)."),
    ("native install --from /data/repo1 app", "v2 installiert: musl 1.2.5-r1, libfoo 1.0.0-r1, app 1.0.0-r1."),
    ("native resolve /usr/bin/app", "v2 resolve usr/bin/app: app 1.0.0-r1, 7 B, ausfuehrbar"),
    ("native resolve /usr/bin/app-link", "v2 resolve usr/bin/app: app 1.0.0-r1"),
    ("native resolve /usr/lib64/libc.so", "v2 resolve lib/ld-musl-x86_64.so.1: musl 1.2.5-r1, 8 B, ausfuehrbar"),
    ("native resolve /USR/LIB/libfoo.so.1", "v2 resolve usr/lib/libfoo.so.1.0: libfoo 1.0.0-r1, 7 B, nicht ausfuehrbar"),
    ("native resolve /usr/lib64", "v2 resolve usr/lib: Ordner"),
    ("native resolve /usr/bin/fehlt", "v2 resolve: Nicht im Paketbestand"),
    ("native resolve /usr/lib64/../../data", "v2 resolve: Kein kanonischer Pfad"),
    ("native remove libfoo", "v2: Paket "),
    ("native update --from /data/repo2", "Katalog v2 geladen (Generation 2, 3 Pakete)."),
    ("native upgrade --from /data/repo2", "v2 aktualisiert: musl 1.2.5-r2."),
    ("native resolve /usr/lib64/libc.so", "v2 resolve lib/ld-musl-x86_64.so.1: musl 1.2.5-r2, 12 B, ausfuehrbar"),
]
STEPS_AFTER_REBOOT = [
    ("native resolve /usr/lib/libc.so", "v2 resolve lib/ld-musl-x86_64.so.1: musl 1.2.5-r2, 12 B, ausfuehrbar"),
    ("native resolve /usr/bin/app-link", "v2 resolve usr/bin/app: app 1.0.0-r1"),
    ("native remove app", "v2 entfernt: app."),
    ("native resolve /usr/bin/app", "v2 resolve: Nicht im Paketbestand"),
    ("native remove --unused", "v2 entfernt: libfoo, musl."),
    ("native remove --unused", "v2: nichts zu tun."),
]


def run(tools, guest, steps) -> None:
    for command, expected in steps:
        guest.pkg(command, expected, 300)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--key", type=Path, required=True, help="privater Testschluessel (PEM), oeffentlicher Teil im ISO")
    parser.add_argument("--accel", choices=["tcg", "whpx", "kvm"], default="tcg")
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
        run(tools, guest, STEPS_BEFORE_REBOOT)
        mark = guest.mark()
        guest.qmp.line("reboot")
        guest.wait("Desktop ist bereit", 240, mark)
        time.sleep(1)
        guest.login(first=False)
        guest.shell()
        run(tools, guest, STEPS_AFTER_REBOOT)
        print(f"PACKAGE2_GUEST PASS {len(STEPS_BEFORE_REBOOT) + len(STEPS_AFTER_REBOOT)} Schritte; Protokolle: {work}", flush=True)
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
