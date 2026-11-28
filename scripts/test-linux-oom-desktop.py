"""Ladybird OOM: Desktop-Terminal nach vollem Speicher (KVM, Testbau -p:ZehOSLinuxRun=true).

Nutzung: scripts/test-linux-oom-desktop.py <iso> <arbeitsordner> [smp] [runden]
Vorher: CC=<musl-gcc> sh experiments/LinuxAbi/build.sh t3 v50
Je Runde treibt oom_loop (12 Kinder fuellen den 512-MiB-Speicher bis SIGKILL) den Speicher voll; danach muss ein weiterer Start (okprog, exit 0) gehen.
Am Ende: keine CPU-EXCEPTION-Zeile, kein Wechsel in die Textshell, Terminal bedienbar.
"""
import importlib.util, sys, time, shutil, os
from pathlib import Path
R = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("t", R / "scripts/test-wasm-doom-desktop.py"); t = importlib.util.module_from_spec(spec); spec.loader.exec_module(t)
iso, work = Path(sys.argv[1]), Path(sys.argv[2]); smp = sys.argv[3] if len(sys.argv) > 3 else "1"
rounds = int(sys.argv[4]) if len(sys.argv) > 4 else 2
work.mkdir()
os.environ["ZEHOS_QEMU_SMP"] = smp
CASES = (("oom_loop", None),)
d = work / "appdir"; d.mkdir(); (d / "t").mkdir()
shutil.copy2(R / "experiments/LinuxAbi/out/t3/okprog", d / "t/okprog")
shutil.copy2(R / "experiments/LinuxAbi/out/v50/oom_loop", d / "t/oom_loop")
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
            check(f"runde {r} {name} exit 0", run(name, "exit 0", 240))
            check(f"runde {r} nach {name}: weiterer Start exit 0", run("okprog", "exit 0"))
    time.sleep(1); g.shot("nach-faellen")
    text = serial(start)
    check("kein Anhalten (CPU EXCEPTION)", "CPU EXCEPTION" not in text and "Interrupt Vector" not in text)
    check("kein Wechsel in die Textshell", "Textshell aktiv" not in text)
    check("Terminal weiter bedienbar (letzter Start)", run("okprog", "exit 0"))
    try: g.qmp.line("shutdown")
    except Exception: pass
    try: p.wait(timeout=60)
    except Exception: pass  # finally beendet QEMU notfalls
finally:
    if p.poll() is None: p.kill(); p.wait()
print(f"ERGEBNIS {sum(c for _, c in ok)}/{len(ok)}")
sys.exit(0 if all(c for _, c in ok) else 1)
