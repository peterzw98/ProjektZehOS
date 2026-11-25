#!/usr/bin/env python3
"""Gastprobe Ladybird N1-A6/A7: Ring-3-Faelle starten QEMU und werten nur die USER_PROBE-Zeilen des Seriallogs aus.

Braucht ein ISO mit -p:ZehOSUserModeProbe=true (N1-A6). Je Fall schreibt der Kernel `USER_PROBE <name> ok=<0|1> ...`, am
Ende `USER_PROBE done failed=<n>`. Exit 1 bei fehlendem Fall, ok=0, fehlender Schlusszeile, failed>0 oder Zeitlimit.
Jeder Lauf nutzt eine frische Wegwerfplatte unter .tools/user-mode-probe/.
"""

import argparse
import contextlib
import functools
import importlib.util
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
CASE = re.compile(r"USER_PROBE ([A-Za-z0-9_.-]+) ok=([01])(?=\s|$)")
DONE = re.compile(r"USER_PROBE done failed=(\d+)(?=\s|$)")
HALT_START = "USER_PROBE gegenprobe pf in kernel thread ring3=1"
NMI_OPEN = "USER_PROBE nmi window open"
NMI_CLOSED = "USER_PROBE nmi window closed"
SHOWN = re.compile(r"[^\r\n]*USER_PROBE[^\r\n]*")
_tools = None


def tools():
    """Laedt die gemeinsamen QEMU-Helfer erst bei Bedarf; die Auswertung braucht sie nicht."""
    global _tools
    if _tools is None:
        spec = importlib.util.spec_from_file_location("package_guest", ROOT / "scripts/test-wasm-doom-desktop.py")
        _tools = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(_tools)
    return _tools


def parse_args(argv=None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--accel", choices=["kvm", "tcg", "whpx"], default="kvm")
    parser.add_argument("--smp", type=int, default=1, help="Anzahl der Gast-CPUs")
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--expect", type=lambda value: [name for name in value.split(",") if name], default=[],
                        help="Faelle, die mit ok=1 vorkommen muessen (kommagetrennt)")
    parser.add_argument("--negative", action="store_true",
                        help="ISO mit ZehOSUserModeProbeNegative: #PF im Kernelthread muss anhalten")
    parser.add_argument("--timeout", type=float, default=300, help="Sekunden je Lauf bis zur Schlusszeile")
    args = parser.parse_args(argv)
    if args.smp < 1 or args.runs < 1 or args.timeout <= 0:
        parser.error("--smp, --runs und --timeout muessen positiv sein")
    return args


def evaluate(log: str, expect: list[str]) -> list[str]:
    """Fehlende Belege eines Laufs; leer heisst bestanden. Andere Zeilen des Logs zaehlen nicht."""
    results: dict[str, list[bool]] = {}
    for name, ok in CASE.findall(log):
        results.setdefault(name, []).append(ok == "1")
    missing = []
    for name in expect:
        if name not in results:
            missing.append(f"Fall {name} fehlt")
    for name, values in results.items():
        if not all(values):
            missing.append(f"Fall {name} ok=0")
    if not results:
        missing.append("kein Fall im Log")
    done = DONE.findall(log)
    if not done:
        missing.append("Schlusszeile 'USER_PROBE done' fehlt")
    else:
        failed = int(done[-1])
        bad = sum(not all(values) for values in results.values())
        if failed != 0:
            missing.append(f"Schlusszeile meldet failed={failed}")
        elif bad:
            missing.append("Schlusszeile meldet failed=0, obwohl ein Fall ok=0 hat")
    return missing


def evaluate_negative(log: str) -> list[str]:
    """Gegenprobe: Ring 3 lief, danach haelt ein #PF im Kernelthread das System an."""
    start = log.find(HALT_START)
    if start < 0:
        return ["Gegenprobe nicht begonnen oder Ring 3 lief nicht"]
    after = log[start:]
    missing = []
    if "NOT HALTED" in after:
        missing.append("Kernelthread lief nach dem #PF weiter")
    if not re.search(r"CPU EXCEPTION|FATAL: Exception 14", after):
        missing.append("keine Ausnahmemeldung")
    return missing


def wait_for_done(read, timeout: float, clock=None, sleep=None, alive=lambda: True) -> bool:
    """Wartet, bis die Schlusszeile im Log steht; false bei Zeitlimit oder wenn QEMU vorher endet (alive() false, nach einem
    letzten Blick ins Log)."""
    clock = clock or time.monotonic
    sleep = sleep or time.sleep
    deadline = clock() + timeout
    while True:
        if DONE.search(read()):
            return True
        if clock() >= deadline or not alive():
            return DONE.search(read()) is not None
        sleep(1)


def terminate(vm: subprocess.Popen) -> None:
    """Beendet QEMU samt Kindprozessen (der Cosmos-Wrapper startet QEMU als Kind): erst die Prozessgruppe, dann der Prozess."""
    if hasattr(os, "killpg"):
        # vm.pid, nicht getpgid: QEMU ist Sitzungsfuehrer (own_session); ginge das verloren, trifft killpg nie die eigene Gruppe.
        with contextlib.suppress(ProcessLookupError, PermissionError):
            os.killpg(vm.pid, signal.SIGKILL)
    if vm.poll() is None:
        vm.kill()
    vm.wait()


@contextlib.contextmanager
def own_session(smp: int):
    """start_qemu startet QEMU ohne eigene Sitzung und liest die CPU-Zahl aus der Umgebung; beides nur fuer den Aufruf setzen.
    Das Popen-Patch wirkt prozessweit, solange der Block laeuft (nur start_qemu ruft darin Popen auf)."""
    module = tools().subprocess
    original = module.Popen
    previous = os.environ.get("ZEHOS_QEMU_SMP")
    module.Popen = functools.partial(original, start_new_session=True)
    os.environ["ZEHOS_QEMU_SMP"] = str(smp)
    try:
        yield
    finally:
        module.Popen = original
        if previous is None:
            os.environ.pop("ZEHOS_QEMU_SMP", None)
        else:
            os.environ["ZEHOS_QEMU_SMP"] = previous


def run(args: argparse.Namespace, number: int) -> bool:
    work = ROOT / ".tools/user-mode-probe" / uuid.uuid4().hex[:12]
    serial = work / "serial.log"
    vm = None

    def text() -> str:
        return serial.read_text(encoding="utf-8", errors="replace") if serial.exists() else ""

    try:
        helper = tools()
        (work / "seed").mkdir(parents=True)
        helper.create_disk(work / "seed", work / "data.qcow2")
        port = helper.free_port()
        with own_session(args.smp):
            vm = helper.start_qemu(args.iso.resolve(), work / "data.qcow2", work, port, args.accel, None)
        if args.negative:
            deadline = time.monotonic() + args.timeout
            while HALT_START not in text() and time.monotonic() < deadline and vm.poll() is None:
                time.sleep(1)
            time.sleep(10)
            log = text()
            missing = evaluate_negative(log)
            print(f"Lauf {number} {args.accel} smp={args.smp} negative: "
                  f"{'PASS' if not missing else 'FAIL ' + '; '.join(missing)} ({work})", flush=True)
            return not missing
        qmp = None

        def read() -> str:
            # Mit einer CPU kann nur der Rechner von aussen einen NMI schicken, waehrend das Programm im Ring 3 ist.
            nonlocal qmp
            log = text()
            if NMI_OPEN in log and NMI_CLOSED not in log:
                qmp = qmp or helper.Qmp(port)
                qmp.command("inject-nmi")
            return log

        finished = wait_for_done(read, args.timeout, sleep=lambda _: time.sleep(0.2), alive=lambda: vm.poll() is None)
        log = text()
        missing = evaluate(log, args.expect)
        if not finished:
            why = "QEMU vorzeitig beendet" if vm.poll() is not None else f"Zeitlimit {args.timeout:g} s"
            missing.insert(0, f"{why} ohne Schlusszeile")
        for line in SHOWN.findall(log):
            print("  " + line.strip(), flush=True)
        print(f"Lauf {number} {args.accel} smp={args.smp}: {'PASS' if not missing else 'FAIL ' + '; '.join(missing)} ({work})",
              flush=True)
        return not missing
    except (RuntimeError, OSError, TimeoutError, ConnectionError, subprocess.CalledProcessError) as error:
        print(f"Lauf {number} {args.accel} smp={args.smp}: FAIL {type(error).__name__}: {error} ({work})", flush=True)
        return False
    finally:
        if vm is not None:
            terminate(vm)


def main(argv=None) -> int:
    args = parse_args(argv)
    passed = sum(run(args, number) for number in range(1, args.runs + 1))
    print(f"USER_MODE_PROBE {args.accel} smp={args.smp}: {passed}/{args.runs} bestanden", flush=True)
    return 0 if passed == args.runs else 1


if __name__ == "__main__":
    sys.exit(main())
