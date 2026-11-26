"""Ladybird Stufe c: das echte js (LibJS, statisch musl, Branch story/ladybird-bin) im Desktop-Terminal (KVM, Testbau -p:ZehOSLinuxRun=true).

Nutzung: scripts/test-linux-js.py <iso> <ladybird-ordner> <arbeitsordner> [smp] [speicher]
<ladybird-ordner> ist ladybird/ aus story/ladybird-bin (bin/js, share/Lagom/icu/icudt78l.dat); er landet als /data/t auf der
Datenplatte, das Terminal wechselt dorthin und startet bin/js: /data/t ist dann die Prozesswurzel, js findet seine ICU-Daten ueber
/proc/self/exe (/bin/js) unter /share/Lagom/icu (V12).
Prueft: run js -c 1+1 -> Ausgabe 2, exit 0; danach die REPL: Eingabeaufforderung, 1+1 -> 2, eine zweite Rechnung
((7*6).toString(2); das deutsche Layout hat ohne AltGr keine Klammern [ ] { }), Strg+D beendet; Strg+C bricht eine Endlosschleife ab und das Terminal bleibt bedienbar.
Marken aus dem seriellen Spiegel (LINUXRUN BEGIN/OUT/END); Bildschirmfotos im Arbeitsordner.
"""
import importlib.util, os, re, sys, time, shutil
from pathlib import Path
R = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("t", R / "scripts/test-wasm-doom-desktop.py"); t = importlib.util.module_from_spec(spec); spec.loader.exec_module(t)
iso, lb, work = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
smp = sys.argv[4] if len(sys.argv) > 4 else "1"; memory = sys.argv[5] if len(sys.argv) > 5 else None
work.mkdir()
os.environ["ZEHOS_QEMU_SMP"] = smp
d = work / "appdir"; d.mkdir()
shutil.copytree(lb, d / "t")
disk = work / "app.qcow2"; t.create_disk(d, disk)
port = t.free_port()
p = t.start_qemu(iso, disk, work, port, "kvm", None, memory)
ok = []
ANSI = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]")
def out_since(mark):
    """Ausgabe des Programms seit mark, ohne Steuerfolgen (libedit zeichnet die Zeile selbst)."""
    text = (work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]
    out = b""
    for line in text.splitlines():
        if "LINUXRUN OUT " in line:
            try: out += bytes.fromhex(line.split("LINUXRUN OUT ", 1)[1].split()[0])
            except ValueError: pass
    return ANSI.sub("", out.decode("utf-8", errors="replace"))
def wait_out(want, timeout, mark):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if want in out_since(mark): return True
        time.sleep(0.5)
    return False
def lines_since(mark):
    return [l.strip() for l in re.split(r"\r\n|\r|\n", out_since(mark))]
def wait_line(want, timeout, mark):
    """Wartet auf eine Ausgabezeile, die genau want ist (nicht nur irgendwo enthaelt)."""
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if want in lines_since(mark): return True
        time.sleep(0.5)
    return False
def ended_since(mark):
    return "LINUXRUN END" in (work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]
def wait_serial(marker, timeout, mark):
    try: g.wait(marker, timeout, mark); return True
    except (TimeoutError, RuntimeError): return False
def check(name, cond):
    ok.append((name, cond)); print(("PASS " if cond else "FAIL ") + name, flush=True)
try:
    g = t.Guest(work, port, "http://127.0.0.1:1", time.monotonic())
    g.wait("Desktop ist bereit", 240); g.connect(); time.sleep(1); g.login(first=True)
    g.shell("security adopt /data/t tester", "security adopt /data/t/bin tester", "security adopt /data/t/bin/js tester")
    time.sleep(1); g.desktop(); g.launch("Terminal"); time.sleep(3)
    g.qmp.line("cd /data/t"); time.sleep(1)
    # 1) Einzelrechnung; -l (print-last-result, js.rs) gibt das Ergebnis aus, -c allein schweigt wie unter Linux
    m = g.mark(); g.qmp.line("run bin/js -l -c 1+1")
    check("js -c start", wait_serial("LINUXRUN BEGIN bin/js", 60, m))
    check("js -c ende exit 0", wait_serial("LINUXRUN END exit 0", 120, m))
    check("js -c ausgabe 2", "2" in lines_since(m))
    print("AUSGABE -c: " + repr(out_since(m)[-200:]), flush=True)
    time.sleep(1); g.shot("js-c")
    # 2) REPL
    m = g.mark(); g.qmp.line("run bin/js")
    check("repl aufforderung", wait_out("> ", 120, m))
    time.sleep(2); g.shot("repl-prompt")
    m = g.mark(); g.qmp.line("1+1")
    check("repl 1+1 = 2", wait_line("2", 60, m))
    print("AUSGABE 1+1: " + repr(out_since(m)[-200:]), flush=True)
    m = g.mark(); g.qmp.line("(7*6).toString(2)")
    check("repl toString(2)", wait_line('"101010"', 60, m))
    print("AUSGABE toString: " + repr(out_since(m)[-200:]), flush=True)
    time.sleep(1); g.shot("repl-antwort")
    m = g.mark(); g.qmp.tap("ctrl", "d")
    check("strg+d beendet", wait_serial("LINUXRUN END exit 0", 60, m))
    print("ENDE: " + repr([l for l in (work / "serial.log").read_text(errors="replace")[m:].splitlines() if "LINUXRUN END" in l]), flush=True)
    time.sleep(1); g.shot("repl-ende")
    # 3) Endlosschleife, Strg+C bricht ab, danach wieder frei
    m = g.mark(); g.qmp.line("run bin/js -c while(1);")
    check("schleife start", wait_serial("LINUXRUN BEGIN bin/js", 60, m))
    time.sleep(5)
    check("schleife laeuft noch", not ended_since(m))  # sonst belegte Strg+C nichts
    m = g.mark(); g.qmp.tap("ctrl", "c")
    check("strg+c beendet mit SIGINT", wait_serial("LINUXRUN END signal 2", 30, m))
    m = g.mark(); g.qmp.line("run bin/js -l -c 6*7")
    check("danach wieder frei", wait_serial("LINUXRUN END exit 0", 120, m) and "42" in lines_since(m))
    time.sleep(1); g.shot("ende")
    try: g.qmp.line("shutdown")
    except Exception: pass
    p.wait(timeout=60)
finally:
    if p.poll() is None: p.kill(); p.wait()
print(f"ERGEBNIS {sum(c for _, c in ok)}/{len(ok)}")
sys.exit(0 if all(c for _, c in ok) else 1)
