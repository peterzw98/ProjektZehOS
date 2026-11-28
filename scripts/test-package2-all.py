#!/usr/bin/env python3
"""Gesamtpruefung der Paketdienst-v2-Gastproben (N3-B1/B2/B3/E) mit einem Befehl.

Baut einmal das gemeinsame Test-ISO (-p:ZehOSWasm=true -p:ZehOSLinuxRun=true, oeffentlicher Testschluessel in
-p:ZehOSPackageKeyPath, -warnaserror) oder nimmt --iso und laesst je Beschleuniger/SMP-Stufe nacheinander laufen:
  guest  scripts/test-package2-guest.py          (update/install/resolve/upgrade/Neustart/remove, run --pkg)
  dyn    scripts/test-package2-dyn-guest.py      (dynamisch gelinkt mit musl aus dem Paketbestand; braucht --ld-musl)
  live   scripts/test-package2-upgrade-live-guest.py (Upgrade, waehrend run --pkg laeuft; braucht mdir aus mtools)
Am Ende steht eine Tabelle Probe x Stufe; Exit 0 nur, wenn alle ausgefuehrten Laeufe PASS sind. Eine Probe ohne ihre
Voraussetzung (dyn ohne --ld-musl) steht als "-" in der Tabelle und mit Grund darunter. Strg+C gibt die Tabelle der
bisherigen Laeufe aus und endet mit Exit 130. Aufraeumen: Die Datenplatten der Laeufe (data.qcow2, data.raw unter
.tools/package2-guest/) und das gebaute ISO im Arbeitsordner werden geloescht; --keep-images behaelt sie.

Nutzung (aus dem Repository-Wurzelordner):
  python3 -I scripts/test-package2-all.py --key <ordner>/private.pem [--public <ordner>/public.b64 | --iso <iso>]
         [--ld-musl <musl>/libc.so] [--probes guest dyn live] [--configs kvm:1 kvm:4 tcg:1] [--work <ordner>]
         [--cosmos-tools <ordner>] [--timeout <s>] [--live-sleep <s>] [--keep-images]

--key und der oeffentliche Teil (Standard: public.b64 neben --key) sind ausschliesslich Testschluessel. Voraussetzungen:
dotnet und Cosmos-Werkzeuge (Standard: .tools/cosmos-tools/<deploy/cosmos/VERSION>) fuer den Bau, QEMU/KVM, musl-gcc
(oder --cc) fuer dyn, mdir und qemu-img fuer live. Ein Gast zur selben Zeit (die Laeufe laufen nacheinander).
"""

import argparse
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
ISO = ROOT / "ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso"
GUEST_WORK = ROOT / ".tools/package2-guest"
PROBES = {
    "guest": ("scripts/test-package2-guest.py", "PACKAGE2_GUEST"),
    "dyn": ("scripts/test-package2-dyn-guest.py", "PACKAGE2_DYN_GUEST"),
    "live": ("scripts/test-package2-upgrade-live-guest.py", "PACKAGE2_UPGRADE_LIVE"),
}
BUILD_TIMEOUT = 3600  # Sekunden fuer den Kernelbau


def run(cmd, log, env=None, timeout=None):
    with open(log, "w", encoding="utf-8") as f:
        f.write("$ " + " ".join(str(c) for c in cmd) + "\n")
        f.flush()
        # Eigene Prozessgruppe: Beim Zeitlimit endet auch QEMU (Enkel der Probe), sonst bliebe der Gast ohne Aufsicht und
        # belegte den einen Gastplatz fuer den naechsten Lauf.
        proc = subprocess.Popen([str(c) for c in cmd], stdout=f, stderr=subprocess.STDOUT, env=env, cwd=ROOT, start_new_session=True)
        try:
            return proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            f.write(f"\nZeitlimit {timeout} s ueberschritten\n")
            return 124
        finally:
            # Auch nach normalem Ende und bei Strg+C: Reste der Gruppe (ein haengengebliebenes QEMU) beenden.
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            proc.wait()


def rel(p) -> str:
    """Pfad relativ zum Repository (keine Hostpfade/Benutzernamen in der Ausgabe)."""
    try:
        return str(Path(p).resolve().relative_to(ROOT))
    except ValueError:
        return Path(p).name


def parse_configs(values):
    configs = []
    for c in values:
        m = re.fullmatch(r"(kvm|tcg):(\d+)", c)
        if not m or int(m.group(2)) < 1:
            raise ValueError(f"Stufe {c}: erwartet kvm:<n> oder tcg:<n> mit n >= 1")
        configs.append((m.group(1), int(m.group(2))))
    return configs


def cosmos_tools(explicit):
    if explicit:
        return Path(explicit)
    base = ROOT / ".tools/cosmos-tools"
    versions = sorted(p for p in base.glob("*") if p.is_dir()) if base.is_dir() else []
    wanted = (ROOT / "deploy/cosmos/VERSION").read_text().strip() if (ROOT / "deploy/cosmos/VERSION").exists() else None
    for p in versions:
        if p.name == wanted:
            return p
    return versions[-1] if versions else None


def work_dirs():
    return set(GUEST_WORK.iterdir()) if GUEST_WORK.is_dir() else set()


def drop_images(before):
    """Loescht die Platten der Laeufe, die seit der Momentaufnahme entstanden sind; Protokolle bleiben."""
    for d in work_dirs() - before:
        for name in ("vm/data.qcow2", "data.raw"):
            (d / name).unlink(missing_ok=True)


def summary_line(log: Path, marker: str) -> str:
    """Letzte Abschlusszeile der Probe (<MARKE> PASS/FAIL ...), sonst das Ende des Logs."""
    text = log.read_text(encoding="utf-8", errors="replace").splitlines() if log.exists() else []
    lines = [l for l in text if l.startswith(marker + " ")]
    last = lines[-1] if lines else (text[-1] if text else "kein Log")
    return last.replace(str(ROOT), ".").replace(str(Path.home()), "~")[:200]


def probe_command(probe, args, iso, accel):
    """Befehl einer Probe; None mit Grund, wenn eine Voraussetzung fehlt."""
    script = ROOT / PROBES[probe][0]
    cmd = [sys.executable, "-I", script, "--iso", iso, "--key", args.key, "--accel", accel]
    if probe == "dyn":
        if not args.ld_musl:
            return None, "--ld-musl fehlt"
        cmd += ["--ld-musl", args.ld_musl, "--cc", args.cc]
    if probe == "live" and args.live_sleep:
        cmd += ["--sleep", args.live_sleep]
    return cmd, None


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--key", type=Path, required=True, help="privater Testschluessel (PEM)")
    src = ap.add_mutually_exclusive_group()
    src.add_argument("--public", type=Path, help="oeffentlicher Teil fuer den Bau (Standard: public.b64 neben --key)")
    src.add_argument("--iso", type=Path, help="fertiges Test-ISO statt eines Baus")
    ap.add_argument("--ld-musl", type=Path, help="musl-Lader (libc.so von musl) fuer dyn; ohne ihn entfaellt dyn")
    ap.add_argument("--cc", default="musl-gcc", help="musl-Uebersetzer fuer dyn (Standard musl-gcc)")
    ap.add_argument("--probes", nargs="+", choices=list(PROBES), default=list(PROBES), help="Proben (Standard: alle)")
    ap.add_argument("--configs", nargs="+", default=["kvm:1", "kvm:4", "tcg:1"], help="<kvm|tcg>:<smp> je Stufe")
    ap.add_argument("--work", type=Path, help="Ausgabeordner (Standard: .tools/package2-all/<Zeit>)")
    ap.add_argument("--cosmos-tools", help="Werkzeugordner (Standard: .tools/cosmos-tools/<deploy/cosmos/VERSION>)")
    ap.add_argument("--timeout", type=float, default=2400, help="Sekunden je Gastlauf (TCG braucht deutlich laenger als KVM)")
    ap.add_argument("--live-sleep", type=int, help="--sleep fuer live (Standard der Probe; unter TCG bei Bedarf hoeher)")
    ap.add_argument("--keep-images", action="store_true", help="Datenplatten und gebautes ISO nicht loeschen")
    args = ap.parse_args(argv)

    try:
        configs = parse_configs(args.configs)
    except ValueError as error:
        ap.error(str(error))
    if not args.key.is_file():
        ap.error(f"--key {args.key}: Datei fehlt")
    if args.iso and not args.iso.is_file():
        ap.error(f"--iso {args.iso}: Datei fehlt")
    public = args.public or args.key.parent / "public.b64"
    if not args.iso and not public.is_file():
        ap.error(f"oeffentlicher Schluessel {public} fehlt (--public oder --iso angeben)")
    args.key = args.key.resolve()
    if args.ld_musl:
        args.ld_musl = args.ld_musl.resolve()

    work = (args.work or ROOT / ".tools/package2-all" / time.strftime("%Y%m%d-%H%M%S")).resolve()
    work.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    t0 = time.time()

    built = False
    if args.iso:
        iso = args.iso.resolve()
    else:
        tools = cosmos_tools(args.cosmos_tools)
        if tools:
            env["PATH"] = f"{tools}{os.pathsep}{env.get('PATH', '')}"
        build = ["dotnet", "build", ROOT / "ProjektZeh/ProjektZeh.csproj", "--configuration", "Release", "-warnaserror",
                 "-p:ZehOSWasm=true", "-p:ZehOSLinuxRun=true", f"-p:ZehOSPackageKeyPath={public.resolve()}"]
        ISO.unlink(missing_ok=True)  # kein Alt-ISO, falls der Bau still nichts erzeugt
        if run(build, work / "build.log", env=env, timeout=BUILD_TIMEOUT) != 0 or not ISO.exists():
            print(f"Kernelbau fehlgeschlagen, siehe {rel(work / 'build.log')}", file=sys.stderr)
            return 2
        iso = work / "package2.iso"
        shutil.copy2(ISO, iso)
        built = True
        print(f"ISO gebaut: {rel(iso)}", flush=True)

    results = {}  # probe -> {stufe: (ok, text)}
    interrupted = False
    try:
        for accel, smp in configs:
            key = f"{accel}{smp}"
            # Die Proben starten QEMU ueber start_qemu aus test-wasm-doom-desktop.py; die CPU-Zahl kommt aus der Umgebung.
            env["ZEHOS_QEMU_SMP"] = str(smp)
            for probe in args.probes:
                row = results.setdefault(probe, {})
                cmd, reason = probe_command(probe, args, iso, accel)
                if cmd is None:
                    row[key] = (None, reason)
                    continue
                log = work / f"{probe}.{key}.log"
                before = work_dirs()
                try:
                    rc = run(cmd, log, env=env, timeout=args.timeout)
                finally:
                    if not args.keep_images:
                        drop_images(before)
                row[key] = (rc == 0, summary_line(log, PROBES[probe][1]))
                print(f"{probe} {key}: {'PASS' if rc == 0 else 'FAIL'}", flush=True)
    except KeyboardInterrupt:
        interrupted = True
        print("\nAbbruch durch Strg+C, Tabelle der bisherigen Laeufe:", file=sys.stderr)
    finally:
        if built and not args.keep_images:
            iso.unlink(missing_ok=True)

    cols = [f"{a}{n}" for a, n in configs]
    print(f"\n| Probe | {' | '.join(cols)} |\n| --- |{' --- |' * len(cols)}")
    failed, skipped, total = [], [], 0
    for probe, row in results.items():
        cells = []
        for c in cols:
            if c not in row:
                cells.append(" ")  # nach Strg+C nicht mehr gelaufen
                continue
            ok, text = row[c]
            cells.append("PASS" if ok else "-" if ok is None else "FAIL")
            if ok is None:
                skipped.append((probe, c, text))
            else:
                total += 1
            if ok is False:
                failed.append((probe, c, text))
        print(f"| {probe} | {' | '.join(cells)} |")
    print(f"\n{total - len(failed)}/{total} Laeufe PASS in {time.time() - t0:.0f} s; Logs: {rel(work)}")
    for probe, c, text in skipped:
        print(f"entfaellt {probe} {c}: {text}")
    for probe, c, text in failed:
        print(f"FAIL {probe} {c}: {text}")
    if interrupted:
        return 130
    return 1 if failed or total == 0 else 0


if __name__ == "__main__":
    sys.exit(main())
