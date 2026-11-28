"""Ladybird T3: Fehlerisolation fuer Linux-Prozesse im Desktop-Terminal (KVM, Testbau -p:ZehOSLinuxRun=true).

Nutzung: scripts/test-linux-faults.py <iso> <arbeitsordner> [smp] [runden]
Vorher: CC=<musl-gcc> sh experiments/LinuxAbi/build.sh t3
Je Runde laufen vier Programme ohne Signalbehandler: Seitenfehler auf Adresse 0 (signal 11), ud2 (signal 4), div durch 0 (signal 8)
und kill(getpid(), SIGSEGV) (signal 11). Nach jedem Absturz muss ein weiterer Start (okprog, exit 0) gehen; am Ende muss das
Terminal weiter bedienbar sein, ohne dass der Kernel angehalten hat (keine CPU-EXCEPTION-Zeile, kein Wechsel in die Textshell).
"""
import importlib.util, sys, time, shutil, os
from pathlib import Path
R = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("t", R / "scripts/test-wasm-doom-desktop.py"); t = importlib.util.module_from_spec(spec); spec.loader.exec_module(t)
iso, work = Path(sys.argv[1]), Path(sys.argv[2]); smp = sys.argv[3] if len(sys.argv) > 3 else "1"
rounds = int(sys.argv[4]) if len(sys.argv) > 4 else 2
work.mkdir()
os.environ["ZEHOS_QEMU_SMP"] = smp
CASES = (("nullpf", 11), ("ud2", 4), ("div0", 8), ("segvself", 11))
d = work / "appdir"; d.mkdir(); (d / "t").mkdir()
for name in [c[0] for c in CASES] + ["okprog"]:
    shutil.copy2(R / "experiments/LinuxAbi/out/t3" / name, d / "t" / name)
disk = work / "app.qcow2"; t.create_disk(d, disk)
port = t.free_port()
p = t.start_qemu(iso, disk, work, port, "kvm", None)
ok = []
def serial(mark=0): return (work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]
def check(name, cond):
    ok.append((name, cond)); print(("PASS " if cond else "FAIL ") + name, flush=True)
try:
    g = t.Guest(work, port, "http://127.0.0.1:1", time.monotonic())
    g.wait("Desktop ist bereit", 240); g.connect(); time.sleep(1); g.login(first=True)
    g.shell("security adopt /data/t tester", *[f"security adopt /data/t/{n} tester" for n in [c[0] for c in CASES] + ["okprog"]])
    time.sleep(1); g.desktop(); g.launch("Terminal"); time.sleep(3)
    start = g.mark()
    def run(name, status, timeout=60):
        if os.environ.get("T3DEBUG"): g.shot("vor-"+name)
        m = g.mark(); g.qmp.line(f"run /data/t/{name}")
        try: g.wait(f"LINUXRUN END {status}", timeout, m); return True
        except (TimeoutError, RuntimeError): return False
    for r in range(1, rounds + 1):
        for name, sig in CASES:
            check(f"runde {r} {name} signal {sig}", run(name, f"signal {sig}"))
            check(f"runde {r} nach {name}: weiterer Start exit 0", run("okprog", "exit 0"))
    time.sleep(1); g.shot("nach-faellen")
    text = serial(start)
    check("kein Anhalten (CPU EXCEPTION)", "CPU EXCEPTION" not in text and "Interrupt Vector" not in text)
    check("kein Wechsel in die Textshell", "Textshell aktiv" not in text)
    check("Terminal weiter bedienbar (letzter Start)", run("okprog", "exit 0"))
    try: g.qmp.line("shutdown")
    except Exception: pass
    p.wait(timeout=60)
finally:
    if p.poll() is None: p.kill(); p.wait()
print(f"ERGEBNIS {sum(c for _, c in ok)}/{len(ok)}")
sys.exit(0 if all(c for _, c in ok) else 1)
