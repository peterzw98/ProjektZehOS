"""Ladybird V43/V43b: Speicherdruck im Desktop-Terminal (KVM, Testbau -p:ZehOSLinuxRun=true), gedacht fuer -m 512M.

Nutzung: scripts/test-linux-mem.py <iso> <arbeitsordner> [smp] [speicher] [szenario[:grenze-mib] ...]
Vorher: CC=<musl-gcc> sh experiments/LinuxAbi/build.sh v43mem
Startet experiments/LinuxAbi/out/v43mem/memprobe je Szenario als eigenes Vordergrundprogramm (run bin/memprobe <szenario>), sammelt
seine PASS/FAIL/INFO-Zeilen aus dem seriellen Spiegel (LINUXRUN OUT) und prueft von aussen: Programm endet mit exit 0 (signal 9 hiesse,
der Kernel hat den Elternprozess statt eines Lastkinds beendet), SUMMARY vorhanden, danach startet ein neues Programm (memprobe ping),
und das serielle Protokoll zeigt keinen Kernel-Halt (PANIC, System halting, FAILFAST, OutOfMemory). Haelt der Kernel an, enden die
restlichen Szenarien als FAIL "nicht gelaufen", damit ein Halt nicht als fehlende Zeile durchgeht.
Ausgabe: je Pruefung PASS/FAIL/INFO <szenario>.<lauf>.<pruefung> ...; Schluss ERGEBNIS <bestanden>/<gesamt>; ergebnis.txt im Arbeitsordner.
"""
import importlib.util, os, re, sys, time, shutil
from pathlib import Path
R = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("t", R / "scripts/test-wasm-doom-desktop.py"); t = importlib.util.module_from_spec(spec); spec.loader.exec_module(t)

# kheap und maps zuletzt: Dort liegt der offene Restpunkt (Kernel-Heap ueber die Reserve), ein Halt dort soll die anderen nicht verdecken.
DEFAULT = ["anon", "memfd", "memfdmap", "threads", "forkmany", "slotfull", "concurrent", "concmix", "kheapfrei", "kheap", "mapsfrei", "maps"]
HALT = re.compile(r"PANIC|System halt|CPU EXCEPTION|FAILFAST|FailFast|OutOfMemory|Unhandled exception", re.I)
FREE_SLACK = 48  # MiB, die der Kernel-Heap ueber alle Szenarien behalten darf
RUN_TIMEOUT = 2 * 240 + 120  # memprobe: zwei Laeufe mit je hoechstens DEADLINE_S, plus Auswertung

iso, work = Path(sys.argv[1]), Path(sys.argv[2])
smp = sys.argv[3] if len(sys.argv) > 3 else "1"; memory = sys.argv[4] if len(sys.argv) > 4 else "512M"
scenarios = sys.argv[5:] or DEFAULT
work.mkdir()
os.environ["ZEHOS_QEMU_SMP"] = smp
d = work / "appdir"; d.mkdir(); (d / "t" / "bin").mkdir(parents=True)
# ZEHOS_MEMPROBE: anderes memprobe (etwa ein Zwischenstand neben einer laufenden Messreihe)
shutil.copy2(os.environ.get("ZEHOS_MEMPROBE") or R / "experiments/LinuxAbi/out/v43mem/memprobe", d / "t" / "bin" / "memprobe")
disk = work / "app.qcow2"; t.create_disk(d, disk)
port = t.free_port()
p = t.start_qemu(iso, disk, work, port, "kvm", None, memory)
ok = []
lines_out = []
frees = []  # freier Speicher (MiB) aus jedem ping


def serial(mark=0):
    return (work / "serial.log").read_text(encoding="utf-8", errors="replace")[mark:]


def out_since(mark):
    out = b""
    for line in serial(mark).splitlines():
        if "LINUXRUN OUT " in line:
            try: out += bytes.fromhex(line.split("LINUXRUN OUT ", 1)[1].split()[0])
            except ValueError: pass
    return out.decode("utf-8", errors="replace")


def end_since(mark):
    m = re.search(r"LINUXRUN END ([^\r\n]*)", serial(mark))
    return m.group(1).strip() if m else None


def wait_end(mark, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        e = end_since(mark)
        if e is not None: return e
        if p.poll() is not None or HALT.search(serial(mark)): return None  # angehalten: nicht bis zum Zeitlimit warten
        time.sleep(1)
    return None


def check(name, cond, extra=""):
    ok.append((name, cond)); line = ("PASS " if cond else "FAIL ") + name + (" " + extra if extra else "")
    lines_out.append(line); print(line, flush=True)


def halted(mark):
    m = HALT.search(serial(mark))
    return m.group(0) if m else None


try:
    g = t.Guest(work, port, "http://127.0.0.1:1", time.monotonic())
    g.wait("Desktop ist bereit", 240); g.connect(); time.sleep(1); g.login(first=True)
    g.shell("security adopt /data/t tester", "security adopt /data/t/bin tester", "security adopt /data/t/bin/memprobe tester")
    time.sleep(1); g.desktop(); g.launch("Terminal"); time.sleep(3)
    g.qmp.line("cd /data/t"); time.sleep(1)
    m = g.mark(); g.qmp.line("run bin/memprobe ping")
    e = wait_end(m, 120)
    pong = re.search(r"PONG frei=(\d+) gesamt=(\d+)", out_since(m))
    if pong: frees.append(int(pong.group(1)))
    check("aussen.ping", e == "exit 0" and pong is not None, f"end={e!r} " + (pong.group(0) if pong else ""))
    dead = None
    for item in scenarios:
        name, _, cap = item.partition(":")
        if dead:
            check(f"aussen.{name}.gelaufen", False, f"nicht gelaufen, Kernel vorher angehalten ({dead})"); continue
        m = g.mark(); s = time.monotonic()
        g.qmp.line(f"run bin/memprobe {name} {cap}".rstrip())
        e = wait_end(m, RUN_TIMEOUT)
        dur = time.monotonic() - s
        text = out_since(m)
        for l in text.splitlines():
            if l.startswith(("PASS ", "FAIL ", "INFO ", "SUMMARY ", "START ")):
                lines_out.append(l); print(l, flush=True)
                if l.startswith(("PASS ", "FAIL ")): ok.append((l.split()[1], l.startswith("PASS ")))
        prog = [l for l in text.splitlines() if l.startswith("PROG ")]
        if prog:
            # letzter Stand der Kinder (memprobe schreibt Stufe und Abbildungszahl auch direkt ins Terminal)
            info = f"INFO aussen.{name}.fortschritt " + " | ".join(prog[-4:])
            lines_out.append(info); print(info, flush=True)
        h = halted(m)
        check(f"aussen.{name}.ende", e == "exit 0", f"end={e!r} dauer={dur:.0f}s")
        check(f"aussen.{name}.summary", f"SUMMARY {name} " in text)
        check(f"aussen.{name}.kein_halt", h is None, f"marke={h!r}")
        g.shot(name)
        if e is None or h is not None:
            dead = h or "kein LINUXRUN END"
            continue
        m = g.mark(); g.qmp.line("run bin/memprobe ping")
        e2 = wait_end(m, 120)
        pong = re.search(r"PONG frei=(\d+) gesamt=(\d+)", out_since(m))
        if pong: frees.append(int(pong.group(1)))
        check(f"aussen.{name}.ping_danach", e2 == "exit 0" and pong is not None, f"end={e2!r} " + (pong.group(0) if pong else ""))
        if e2 is None: dead = "ping ohne Ende"
    if not dead and len(frees) >= 2:
        # Speicher nach allen Szenarien wieder frei: letzter gegen ersten ping (Kernel-Heap darf FREE_SLACK behalten)
        check("aussen.speicher_gesamt", frees[-1] >= frees[0] - FREE_SLACK, f"erster={frees[0]} letzter={frees[-1]} MiB")
    if not dead:
        try: g.qmp.line("shutdown")
        except Exception: pass
        p.wait(timeout=60)
finally:
    if p.poll() is None: p.kill(); p.wait()
    (work / "ergebnis.txt").write_text("\n".join(lines_out) + "\n", encoding="utf-8")
print(f"ERGEBNIS {sum(c for _, c in ok)}/{len(ok)}")
sys.exit(0 if all(c for _, c in ok) else 1)
