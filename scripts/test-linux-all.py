#!/usr/bin/env python3
"""Gesamtpruefung der Linux-Gastsaetze (experiments/LinuxAbi) mit einem Befehl.

Je Satz: Programme mit experiments/LinuxAbi/build.sh bauen (musl-gcc), zu einem Buendel packen
(scripts/test-linux-run.py bundle), den Kernel mit -p:ZehOSLinuxRunProbe=true bauen (-warnaserror) und das ISO je
Beschleuniger/SMP-Stufe im Gast gegen experiments/LinuxAbi/expected laufen lassen (scripts/test-linux-run.py run).
Am Ende steht eine Tabelle Satz x Stufe; Exit 0 nur, wenn alle ausgefuehrten Laeufe PASS sind. Strg+C gibt die
Tabelle der bisherigen Laeufe aus und endet mit Exit 130. Aufraeumen: Die Datenplatten (data.qcow2 unter
.tools/linux-run/) und die Satz-ISOs im Arbeitsordner werden nach jedem Lauf geloescht; --keep-images behaelt sie.

Nutzung (aus dem Repository-Wurzelordner):
  python3 -I scripts/test-linux-all.py [--sets s1 s3 ...] [--configs kvm:1 kvm:4 tcg:1] [--work <ordner>]
                                       [--cosmos-tools <ordner>] [--timeout <s>] [--keep-images]

Voraussetzungen: musl-gcc (oder $CC), dotnet (DOTNET_ROOT/PATH), Cosmos-Werkzeuge (Standard: neuester Ordner unter
.tools/cosmos-tools), QEMU/KVM. Ein Gast zur selben Zeit (die Stufen laufen nacheinander).

Nicht abgedeckt (eigene Skripte, anderer Testbau): Desktop-Terminal-Tests (scripts/test-linux-stdin.py, test-linux-js.py)
und der Satz tools (Rust/toybox-Werkzeuge, eigener Bau).
"""

import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
ABI = ROOT / "experiments/LinuxAbi"
RUN = ROOT / "scripts/test-linux-run.py"
FULL = ABI / "run-guest-full.py"
ISO = ROOT / "ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso"

# Abweichungen vom Standard (alle Programme aus out/<satz>, Erwartung expected/<satz>, alle Stufen).
#   build:   Saetze fuer build.sh (Standard: der Satz selbst)
#   files:   (Satz, Programm), woraus das Buendel besteht (Standard: alle Dateien aus out/<satz>)
#   status:  Status fuer Programme ohne Erwartung (--status name=status)
#   expected: Erwartungsordner (None: keiner)
#   runner:  anderes Laufskript; configs: nur diese Stufen (Praefix des Beschleunigers)
SPECIAL = {
    # spin kommt aus v3neg: Es laeuft endlos und wird mit signal 9 beendet.
    "v3": {"build": ["v3", "v3neg"], "extra": [("v3neg", "spin")], "status": {"spin": "signal 9"}},
    # drei v7b-Programme ohne Erwartungsdatei, nur Exit 0; v7bfull braucht eine fast volle Datenplatte (Fuelldatei).
    "v7b": {"build": ["v7b"], "files": [("v7b", "v7bnames"), ("v7b", "v7bopen"), ("v7b", "v7bquota")], "expected": None,
            "status": {"v7bnames": "exit 0", "v7bopen": "exit 0", "v7bquota": "exit 0"}},
    "v7bfull": {"build": ["v7b"], "files": [("v7b", "v7bfull")], "expected": None, "status": {"v7bfull": "exit 0"},
                "runner": FULL, "accel": {"kvm"}},
}
DEFAULT_SETS = ["s1", "s3", "v3", "v7b", "v9", "v10", "v11", "v14", "v15", "v16", "v17", "v18", "v7bfull"]
# Saetze ohne Gast-Lauf ueber das Buendel (Hilfsprogramme anderer Saetze oder eigener Bau)
SKIPPED = {"v3neg": "Hilfsprogramme fuer v3 (spin)", "v3stdin": "Desktop-Terminal: scripts/test-linux-stdin.py",
           "tools": "eigener Bau (toybox/Rust), kein Buendellauf"}


BUILD_TIMEOUT = 3600  # Sekunden je build.sh-/dotnet-Aufruf


def run(cmd, log, env=None, cwd=ROOT, timeout=None):
    with open(log, "w", encoding="utf-8") as f:
        f.write("$ " + " ".join(str(c) for c in cmd) + "\n")
        f.flush()
        try:
            return subprocess.run([str(c) for c in cmd], stdout=f, stderr=subprocess.STDOUT, env=env, cwd=cwd, timeout=timeout).returncode
        except subprocess.TimeoutExpired:
            f.write(f"\nZeitlimit {timeout} s ueberschritten\n")
            return 124


def rel(p) -> str:
    """Pfad relativ zum Repository (keine Hostpfade/Benutzernamen in der Ausgabe)."""
    try:
        return str(Path(p).resolve().relative_to(ROOT))
    except ValueError:
        return Path(p).name


def drop_images(before):
    """Loescht die Datenplatten der Laeufe, die seit der Momentaufnahme entstanden sind."""
    base = ROOT / ".tools/linux-run"
    if base.is_dir():
        for d in set(base.iterdir()) - before:
            (d / "data.qcow2").unlink(missing_ok=True)


def image_dirs():
    base = ROOT / ".tools/linux-run"
    return set(base.iterdir()) if base.is_dir() else set()


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


def summary_line(log: Path) -> str:
    """Letzte 'Lauf'-Zeile des Laufskripts (PASS/FAIL mit Grund), sonst das Ende des Logs."""
    text = log.read_text(encoding="utf-8", errors="replace").splitlines() if log.exists() else []
    lines = [l for l in text if l.startswith("Lauf ")]
    last = lines[-1] if lines else (text[-1] if text else "kein Log")
    return last.replace(str(ROOT), ".").replace(str(Path.home()), "~")[:200]


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--sets", nargs="+", default=DEFAULT_SETS, help="Saetze (Standard: alle Buendelsaetze)")
    ap.add_argument("--configs", nargs="+", default=["kvm:1", "kvm:4", "tcg:1"], help="<kvm|tcg>:<smp> je Stufe")
    ap.add_argument("--work", type=Path, help="Ausgabeordner (Standard: .tools/linux-all/<Zeit>)")
    ap.add_argument("--cosmos-tools", help="Werkzeugordner (Standard: .tools/cosmos-tools/<deploy/cosmos/VERSION>)")
    ap.add_argument("--timeout", type=float, default=300, help="Sekunden je Gastlauf")
    ap.add_argument("--keep-images", action="store_true", help="Datenplatten und Satz-ISOs nicht loeschen")
    args = ap.parse_args(argv)

    configs = []
    for c in args.configs:
        m = re.fullmatch(r"(kvm|tcg):(\d+)", c)
        if not m:
            ap.error(f"Stufe {c}: erwartet kvm:<n> oder tcg:<n>")
        configs.append((m.group(1), int(m.group(2))))
    unknown = [s for s in args.sets if s not in DEFAULT_SETS and s not in SPECIAL]
    for s in unknown:
        if s in SKIPPED:
            print(f"Satz {s} nicht per Buendel pruefbar: {SKIPPED[s]}", file=sys.stderr)
        elif not (ABI / "expected" / s).is_dir() or not (ABI / "src" / s).is_dir():
            ap.error(f"Satz {s} unbekannt (src/ und expected/ noetig)")
    sets = [s for s in args.sets if s not in SKIPPED]

    work = args.work or ROOT / ".tools/linux-all" / time.strftime("%Y%m%d-%H%M%S")
    work.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    tools = cosmos_tools(args.cosmos_tools)
    if tools:
        env["PATH"] = f"{tools}{os.pathsep}{env.get('PATH', '')}"
    results = {}  # satz -> {stufe: (ok, text)}
    t0 = time.time()

    # 1. alle Programme bauen (ein build.sh-Aufruf)
    need = []
    for s in sets:
        for b in SPECIAL.get(s, {}).get("build", [s]):
            if b not in need:
                need.append(b)
    if run(["sh", ABI / "build.sh", *need], work / "build.sh.log", env=env, timeout=BUILD_TIMEOUT) != 0:
        print(f"build.sh fehlgeschlagen, siehe {rel(work / 'build.sh.log')}", file=sys.stderr)
        return 2

    interrupted = False
    try:
        for s in sets:
            cfg = SPECIAL.get(s, {})
            row = results[s] = {}
            if "files" in cfg:
                files = [ABI / "out" / d / n for d, n in cfg["files"]]
            else:
                files = sorted(p for p in (ABI / "out" / s).iterdir() if p.is_file())
            files += [ABI / "out" / d / n for d, n in cfg.get("extra", [])]
            programs = [f.name for f in files]
            bundle = work / f"{s}.bundle"
            if run([sys.executable, "-I", RUN, "bundle", bundle, *files], work / f"{s}.bundle.log", env=env) != 0:
                row["*"] = (False, "Buendel fehlgeschlagen")
                continue
            build = ["dotnet", "build", ROOT / "ProjektZeh/ProjektZeh.csproj", "--configuration", "Release", "-warnaserror",
                     "-p:ZehOSLinuxRunProbe=true", f"-p:ZehOSLinuxRunBundle={bundle}"]
            ISO.unlink(missing_ok=True)  # kein Alt-ISO, falls der Bau still nichts erzeugt
            if run(build, work / f"{s}.build.log", env=env, timeout=BUILD_TIMEOUT) != 0:
                row["*"] = (False, "Kernelbau fehlgeschlagen")
                continue
            if not ISO.exists():
                row["*"] = (False, "Kernelbau lieferte kein ISO")
                continue
            iso = work / f"{s}.iso"
            shutil.copy2(ISO, iso)
            expected = ["--expected", ABI / "expected" / s] if cfg.get("expected", True) is not None else []
            status = [x for n, v in cfg.get("status", {}).items() for x in ("--status", f"{n}={v}")]
            for accel, smp in configs:
                key = f"{accel}{smp}"
                if "accel" in cfg and accel not in cfg["accel"]:
                    row[key] = (None, "entfaellt")
                    continue
                log = work / f"{s}.{key}.log"
                runner = cfg.get("runner", RUN)
                cmd = [sys.executable, "-I", runner, "run", "--iso", iso, *expected, *status, "--programs", *programs,
                       "--accel", accel, "--smp", smp, "--timeout", args.timeout]
                before = image_dirs()
                try:
                    rc = run(cmd, log, env=env)
                finally:
                    if not args.keep_images:
                        drop_images(before)
                row[key] = (rc == 0, summary_line(log))
            if not args.keep_images:
                iso.unlink(missing_ok=True)
            print(f"{s}: " + " ".join(f"{k}={'PASS' if v[0] else 'FAIL' if v[0] is False else '-'}" for k, v in row.items()), flush=True)
    except KeyboardInterrupt:
        interrupted = True
        print("\nAbbruch durch Strg+C, Tabelle der bisherigen Laeufe:", file=sys.stderr)

    # Tabelle
    cols = [f"{a}{n}" for a, n in configs]
    print(f"\n| Satz | {' | '.join(cols)} |\n| --- |{' --- |' * len(cols)}")
    failed = []
    total = 0
    for s, row in results.items():
        cells = []
        for c in cols:
            ok, text = row.get(c, row.get("*", (False, "")))
            cells.append("PASS" if ok else "-" if ok is None else "FAIL")
            if ok is not None:
                total += 1
            if ok is False:
                failed.append((s, c, text))
        print(f"| {s} | {' | '.join(cells)} |")
    print(f"\n{total - len(failed)}/{total} Laeufe PASS in {time.time() - t0:.0f} s; Logs: {rel(work)}")
    for s, c, text in failed:
        print(f"FAIL {s} {c}: {text}")
    if interrupted:
        return 130
    return 1 if failed or not results else 0


if __name__ == "__main__":
    sys.exit(main())
