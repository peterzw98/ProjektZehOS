"""Ladybird V3 stdin im Desktop-Terminal (KVM, Testbau -p:ZehOSLinuxRun=true).

Nutzung: scripts/test-linux-stdin.py <iso> <arbeitsordner> [smp]
Vorher: CC=<musl-gcc> sh experiments/LinuxAbi/build.sh v3stdin v3neg
Prueft: REPL-Zeilen hin und zurueck (poll ohne Frist, dann fgets/readv), Strg+D -> exit 0, Strg+C bei rechnendem
Programm -> signal 2, Esc bei wartendem Programm -> signal 2 ohne den Desktop zu verlassen, danach run wieder frei.
Marken aus dem seriellen Spiegel (LINUXRUN BEGIN/OUT/END); Bildschirmfotos im Arbeitsordner.
"""
import importlib.util, sys, time, shutil
from pathlib import Path
R = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("t", R / "scripts/test-wasm-doom-desktop.py"); t = importlib.util.module_from_spec(spec); spec.loader.exec_module(t)
iso, work = Path(sys.argv[1]), Path(sys.argv[2]); smp = sys.argv[3] if len(sys.argv) > 3 else "1"
work.mkdir()
import os; os.environ["ZEHOS_QEMU_SMP"] = smp
d = work / "appdir"; d.mkdir(); (d / "t").mkdir()
for name, sub in (("repl", "v3stdin"), ("spin", "v3neg")):
    shutil.copy2(R / "experiments/LinuxAbi/out" / sub / name, d / "t" / name)
disk = work / "app.qcow2"; t.create_disk(d, disk)
port = t.free_port()
p = t.start_qemu(iso, disk, work, port, "kvm", None)
H = lambda s: s.encode().hex().upper()
ok = []
def check(name, cond):
    ok.append((name, cond)); print(("PASS " if cond else "FAIL ") + name, flush=True)
try:
    g = t.Guest(work, port, "http://127.0.0.1:1", time.monotonic())
    g.wait("Desktop ist bereit", 240); g.connect(); time.sleep(1); g.login(first=True)
    g.shell("security adopt /data/t tester", "security adopt /data/t/repl tester", "security adopt /data/t/spin tester")
    time.sleep(1); g.desktop(); g.launch("Terminal"); time.sleep(3)
    def step(line, marker, timeout=30):
        m = g.mark(); g.qmp.line(line) if line else None
        try: g.wait(marker, timeout, m); return True
        except (TimeoutError, RuntimeError): return False
    # 1) REPL: Zeile hin, Antwort zurueck, Strg+D -> exit 0
    check("repl start", step("run /data/t/repl", "LINUXRUN OUT " + H("> ")))
    time.sleep(1); g.shot("repl-prompt")
    check("repl zeile", step("hallo welt", "LINUXRUN OUT " + H("= hallo welt (10)\n")))
    check("repl zweite zeile", step("1+1", "LINUXRUN OUT " + H("= 1+1 (3)\n")))
    g.shot("repl-antwort")
    m = g.mark(); g.qmp.tap("ctrl", "d")
    try: g.wait("LINUXRUN END exit 0", 30, m); check("strg+d exit 0", True)
    except (TimeoutError, RuntimeError): check("strg+d exit 0", False)
    time.sleep(1); g.shot("repl-ende")
    # 2) Programm im Ring 3 dreht: Strg+C -> signal 2, Terminal danach bedienbar
    check("spin start", step("run /data/t/spin", "LINUXRUN BEGIN /data/t/spin"))
    time.sleep(3); g.shot("spin-laeuft")
    m = g.mark(); g.qmp.tap("ctrl", "c")
    try: g.wait("LINUXRUN END signal 2", 30, m); check("strg+c spin signal 2", True)
    except (TimeoutError, RuntimeError): check("strg+c spin signal 2", False)
    # 3) REPL wartet in read/poll: Esc -> signal 2 (Desktop bleibt)
    check("repl2 start", step("run /data/t/repl", "LINUXRUN OUT " + H("> ")))
    time.sleep(1); m = g.mark(); g.qmp.tap("esc")
    try: g.wait("LINUXRUN END signal 2", 30, m); check("esc repl signal 2", True)
    except (TimeoutError, RuntimeError): check("esc repl signal 2", False)
    m = g.mark(); time.sleep(2)
    check("esc verlaesst desktop nicht", "Textshell aktiv" not in (work / "serial.log").read_text(errors="replace")[m:])
    # 4) danach wieder frei: quit -> exit 3
    check("repl3 start", step("run /data/t/repl", "LINUXRUN OUT " + H("> ")))
    check("quit exit 3", step("quit", "LINUXRUN END exit 3"))
    time.sleep(1); g.shot("ende")
    try: g.qmp.line("shutdown")
    except Exception: pass
    p.wait(timeout=60)
finally:
    if p.poll() is None: p.kill(); p.wait()
print(f"ERGEBNIS {sum(c for _, c in ok)}/{len(ok)}")
sys.exit(0 if all(c for _, c in ok) else 1)
