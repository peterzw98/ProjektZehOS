# Tests für dispatcher.py ohne Netz und ohne echtes claude: ein Ersatzskript protokolliert den Aufruf und
# endet je nach Umgebungsvariable erfolgreich, mit Fehler oder erst nach einer Wartezeit.
import atexit, json, os, signal, stat, subprocess, sys, tempfile, time

from dispatcher import Dispatcher, mention_pattern, parse_args, parse_mention

# Als Prozess gestartete Dienste und Ersatzläufe immer beenden, auch wenn ein Test scheitert: Sonst halten sie
# die Ausgabe offen, und die CI hängt statt rot zu werden.
SPAWNED = []


def spawn(cmd, **kw):
    kw.setdefault("start_new_session", True)
    p = subprocess.Popen(cmd, **kw)
    SPAWNED.append(p)
    return p


@atexit.register
def _cleanup():
    for p in SPAWNED:
        try: os.killpg(p.pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError): pass

NICK = "agent-x"
FAKE = r'''#!{py}
import json, os, sys, time
with open(os.environ["FAKE_CALLS"], "a") as f:
    f.write(json.dumps(sys.argv[1:]) + "\n")
print(os.environ.get("FAKE_OUT", ""), flush=True)
time.sleep(float(os.environ.get("FAKE_SLEEP", "0")))
sys.exit(int(os.environ.get("FAKE_EXIT", "0")))
'''


def line(sender, target, text, cmd="PRIVMSG"):
    return f"2026-11-18 20:00:00 << :{sender}!~u@h.irc {cmd} {target} :{text}\n"


class Env:
    """Eigenes Verzeichnis je Test mit Protokoll, Outbox-FIFO, Ersatz-claude und steuerbarer Uhr."""

    def __init__(self, *extra, log_lines=()):
        self.dir = tempfile.mkdtemp()
        self.irc = os.path.join(self.dir, "irc"); os.makedirs(self.irc)
        self.log = os.path.join(self.irc, "log.txt")
        with open(self.log, "w") as f:
            f.writelines(log_lines)
        self.fifo = os.path.join(self.irc, "outbox.fifo"); os.mkfifo(self.fifo)
        self.reader = os.open(self.fifo, os.O_RDONLY | os.O_NONBLOCK)  # wie teambot.py, der die Outbox liest
        self.claude = os.path.join(self.dir, "claude")
        with open(self.claude, "w") as f:
            f.write(FAKE.format(py=sys.executable))
        os.chmod(self.claude, os.stat(self.claude).st_mode | stat.S_IEXEC)
        self.calls = os.path.join(self.dir, "calls.jsonl")
        os.environ.update(FAKE_CALLS=self.calls, FAKE_EXIT="0", FAKE_SLEEP="0", FAKE_OUT="")
        self.now = 1000.0
        self.args = parse_args(["--nick", NICK, "--irc-dir", self.irc, "--home", self.dir, "--model", "m",
                                "--claude", self.claude, "--quiet", "5", "--backoff", "60", *extra])
        self.d = self.new()

    def new(self):
        return Dispatcher(self.args, clock=lambda: self.now)

    def say(self, sender, text, target="#general", cmd="PRIVMSG", poll=True):
        with open(self.log, "a") as f:
            f.write(line(sender, target, text, cmd))
        if poll:
            self.d.poll_log()   # wie der echte Dienst, der alle paar Sekunden liest; die Ruhezeit zählt ab hier

    def calls_made(self):
        try:
            with open(self.calls) as f:
                return [json.loads(l) for l in f]
        except FileNotFoundError:
            return []

    def run_until_idle(self, d=None, limit=10.0):
        """Schritte, bis kein Lauf mehr aktiv ist (der Ersatzprozess braucht echte Zeit)."""
        d = d or self.d
        end = time.time() + limit
        d.step()
        while d.proc is not None and time.time() < end:
            time.sleep(0.05)
            d.step()
        assert d.proc is None, "Lauf endet nicht"

    def outbox(self):
        try:
            return os.read(self.reader, 65536).decode()
        except BlockingIOError:
            return ""


# --- Nennungen erkennen -------------------------------------------------------------------------------
p = mention_pattern(NICK)
assert parse_mention(line("agent-coordinator", "#second", "agent-x: bitte X").strip(), NICK, p)
assert parse_mention(line("a", "#g", "agent-x-junior: nicht du").strip(), NICK, p) is None   # längerer Nick
assert parse_mention(line("a", "#g", "foo-agent-x bar").strip(), NICK, p) is None
assert parse_mention(line(NICK, "#g", "agent-x: eigene Zeile").strip(), NICK, p) is None
assert parse_mention(line("HistServ", "#g", "agent-x alt").strip(), NICK, p) is None
assert parse_mention(line("peter", NICK, "privat ohne Nennung").strip(), NICK, p)         # Direktnachricht
assert parse_mention(line("a", NICK, "#second", cmd="INVITE").strip(), NICK, p)
assert parse_mention("2026-11-18 20:00:00 << :zeh.ernst.casa NOTICE agent-x :agent-x hallo", NICK, p) is None
assert parse_mention("2026-11-18 20:00:00 >> PRIVMSG #g :agent-x selbst gesendet", NICK, p) is None

# --- Erster Start: alte Nennungen gelten als erledigt; neue starten einen Lauf erst nach der Ruhezeit -----
e = Env(log_lines=[line("agent-coordinator", "#g", "agent-x: alter Auftrag")])
e.d.step()
assert e.d.pending() == [] and e.calls_made() == []
e.say("agent-coordinator", "agent-x: neuer Auftrag (1/2)")
e.d.step()
assert len(e.d.pending()) == 1 and e.d.proc is None                       # Ruhezeit läuft
e.say("agent-coordinator", "agent-x: neuer Auftrag (2/2)")
e.now += 6
e.run_until_idle()
calls = e.calls_made()
assert len(calls) == 1, calls
argv = calls[0]
assert argv[:2] == ["-p", "--model"] and "--continue" not in argv          # immer frischer Kontext
assert "(1/2)" in argv[-1] and "(2/2)" in argv[-1] and "alter Auftrag" not in argv[-1]
assert e.d.acked == 2 and e.d.pending() == []

# Neustart des Dienstes: Stand bleibt, nichts wird doppelt gestartet
e.now += 100
d2 = e.new()
d2.step()
assert d2.acked == 2 and d2.pending() == [] and len(e.calls_made()) == 1

# Abbruch nach dem Schreiben der Inbox, aber vor dem Offset: Der alte Stand derselben Datei bleibt stehen
with open(os.path.join(e.args.state, "offset")) as f:
    old_offset = f.read()
e.say("agent-coordinator", "agent-x: dritter Auftrag", poll=False)      # nur d2 soll lesen
d2.poll_log()
with open(os.path.join(e.args.state, "offset"), "w") as f:
    f.write(old_offset)                                                   # Offset-Schreiben „verloren“
d3 = e.new()
d3.poll_log()
assert [x["id"] for x in d3.entries] == [1, 2, 3], d3.entries

# Kaputte Offset-Datei: Start ab Dateiende statt doppelt von vorn
with open(os.path.join(e.args.state, "offset"), "w") as f:
    f.write("Unsinn\n")
d4 = e.new()
d4.poll_log()
assert [x["id"] for x in d4.entries] == [1, 2, 3]

# --- Fehlschlag: nicht quittieren, mit Abstand wiederholen, nach max_attempts genau einmal warnen ----------
e = Env("--max-attempts", "2")
os.environ["FAKE_EXIT"] = "1"
e.say("agent-coordinator", "agent-x: Aufgabe")
e.now += 6
e.run_until_idle()
assert e.d.acked == 0 and e.d.failures == 1 and len(e.calls_made()) == 1
e.now += 30
e.run_until_idle()
assert len(e.calls_made()) == 1                                           # Abstand 60 s noch nicht um
e.now += 31
e.run_until_idle()
assert len(e.calls_made()) == 2 and e.d.failures == 2
warn = e.outbox()
assert warn.startswith("agent-coordinator: [dispatcher] agent-x:") and "bis #1" in warn, warn
e.now += 10000
e.run_until_idle()
assert len(e.calls_made()) == 2 and e.outbox() == ""                      # aufgegeben, kein Dauerversuch
os.environ["FAKE_EXIT"] = "0"
e.say("agent-coordinator", "agent-x: neuer Versuch")                      # neue Nennung setzt zurück
e.now += 6
e.run_until_idle()
assert len(e.calls_made()) == 3 and e.d.acked == 2 and e.d.failures == 0
assert "Aufgabe" in e.calls_made()[-1][-1]                                # alte Nennung wird mitgegeben

# --- Absenderliste: fremde Nennungen starten nichts, kommen aber als Information mit ----------------------
e = Env("--allow", "agent-coordinator,discord")
e.say("agent-irgendwer", "agent-x: tu etwas")
e.now += 6
e.run_until_idle()
assert e.calls_made() == [] and len(e.d.pending()) == 1
e.say("discord", "<Peter> agent-x: bitte Review")
e.now += 6
e.run_until_idle()
prompt = e.calls_made()[0][-1]
assert "tu etwas" in prompt and "nur Information" in prompt and "bitte Review" in prompt
assert e.d.acked == 2

# --- Zeitlimit: Lauf wird beendet, nicht wiederholt, und es wird sofort einmal gewarnt ---------------------
e = Env("--timeout", "0.3")
os.environ["FAKE_SLEEP"] = "30"
e.say("agent-coordinator", "agent-x: lange Aufgabe")
e.now += 6
e.d.step()
assert e.d.proc is not None
t = time.time()
while e.d.proc is not None and time.time() - t < 5:
    time.sleep(0.1); e.now += 0.2; e.d.step()
assert e.d.proc is None and e.d.acked == 0 and e.d.gave_up_at == 1
assert time.time() - t < 5                                                # nicht 30 s gewartet
warn = e.outbox()
assert "[dispatcher] agent-x" in warn and "Zeitlimit" in warn, warn
e.now += 10000
e.d.step()
assert e.d.proc is None and len(e.calls_made()) == 1                      # kein zweiter hängender Lauf
os.environ["FAKE_SLEEP"] = "0"
e.say("agent-coordinator", "agent-x: neue Aufgabe nach dem Zeitlimit")    # hebt das Aufgeben auf
e.now += 6
e.run_until_idle()
prompt = e.calls_made()[-1][-1]
assert "lange Aufgabe   [lief schon einmal ins Zeitlimit" in prompt and e.d.acked == 2, prompt
assert "neue Aufgabe nach dem Zeitlimit   [lief" not in prompt

# --- Gemeinsame Slots: höchstens ein Lauf je Rechner bei --max-parallel 1 ---------------------------------
slots = tempfile.mkdtemp()
a = Env("--slots-dir", slots, "--max-parallel", "1")
b = Env("--slots-dir", slots, "--max-parallel", "1")
os.environ["FAKE_CALLS"] = a.calls                                        # beide Ersatzläufe in eine Datei
os.environ["FAKE_SLEEP"] = "1"                                            # nach Env(), das sie zurücksetzt
a.say("agent-coordinator", "agent-x: A"); b.say("agent-coordinator", "agent-x: B")
a.now += 6; b.now += 6
a.d.step(); b.d.step()
assert a.d.proc is not None and b.d.proc is None                          # B wartet auf den Slot
a.run_until_idle()
b.run_until_idle()
assert b.d.acked == 1 and len(a.calls_made()) == 2
os.environ["FAKE_SLEEP"] = "0"

# --- Ratengrenze ------------------------------------------------------------------------------------------
e = Env("--max-runs-per-hour", "2")
for i in range(3):
    e.say("agent-coordinator", f"agent-x: Auftrag {i}")
    e.now += 6
    e.run_until_idle()
assert len(e.calls_made()) == 2 and len(e.d.pending()) == 1
e.now += 3600
e.run_until_idle()
assert len(e.calls_made()) == 3 and e.d.pending() == []

# --- Protokoll neu angelegt (anderer Inode) und halbe Zeilen ----------------------------------------------
e = Env()
os.replace(e.log, e.log + ".alt")
with open(e.log, "w") as f:
    f.write(line("agent-coordinator", "#g", "agent-x: nach Rotation"))
    f.write("2026-11-18 20:00:01 << :agent-coordinator!~u@h PRIVMSG #g :agent-x: halb")
e.d.poll_log()
assert [x["text"] for x in e.d.entries] == ["agent-x: nach Rotation"]
with open(e.log, "a") as f:
    f.write(" fertig\n")
e.d.poll_log()
assert e.d.entries[-1]["text"] == "agent-x: halb fertig"

# --- Review P1: Protokoll auf demselben Inode geleert (copytruncate, > log.txt): neue Nennungen kommen an --
e = Env()
for i in range(5):
    e.say("agent-coordinator", f"agent-x: alter Auftrag {i} " + "x" * 50)
ino = os.stat(e.log).st_ino
with open(e.log, "r+") as f:
    f.truncate(0)
assert os.stat(e.log).st_ino == ino
e.say("agent-coordinator", "agent-x: nach dem Leeren")
assert e.d.entries[-1]["text"] == "agent-x: nach dem Leeren", e.d.entries[-1]
# und nach Neustart nicht doppelt
d2 = e.new(); d2.poll_log()
assert [x["text"] for x in d2.entries].count("agent-x: nach dem Leeren") == 1

# --- Review P2: halbe letzte Zeile in inbox.jsonl nach Absturz -------------------------------------------
e = Env()
e.say("agent-coordinator", "agent-x: eins")
with open(os.path.join(e.args.state, "acked"), "w") as f:
    f.write("1\n")
with open(e.d.inbox_path, "a") as f:
    f.write('{"id": 2, "ts": "x", "sen')                                  # Absturz mitten im Schreiben
e.say("agent-coordinator", "agent-x: zwei", poll=False)
d2 = e.new()
d2.poll_log()
assert [x["id"] for x in d2.entries] == [1, 2] and d2.entries[-1]["text"] == "agent-x: zwei", d2.entries
with open(d2.inbox_path) as f:
    assert all(json.loads(l) for l in f)                                  # Datei wieder gültig
# Nummern starten nie unter der Quittung, auch ohne Inbox
os.remove(d2.inbox_path)
with open(os.path.join(e.args.state, "acked"), "w") as f:
    f.write("7\n")
d3 = e.new()
e.say("agent-coordinator", "agent-x: drei", poll=False)
d3.poll_log()
assert d3.entries[-1]["id"] == 8 and len(d3.pending()) == 1

# --- Review P2: Groß- und Kleinschreibung der Nicks ---------------------------------------------------
assert parse_mention(line("a", "#g", "Agent-X: bitte").strip(), NICK, p)
assert parse_mention(line("a", "AGENT-X", "privat").strip(), NICK, p)
assert parse_mention(line("Agent-X", "#g", "agent-x selbst").strip(), NICK, p) is None
assert parse_mention(line("histserv", "#g", "agent-x alt").strip(), NICK, p) is None
e = Env("--allow", "Agent-Coordinator")
e.say("agent-coordinator", "agent-x: Liste ohne Rücksicht auf Schreibweise")
assert e.d.entries[-1]["trigger"] is True

# --- Nennung während eines laufenden Laufs bleibt offen und startet danach einen eigenen Lauf --------------
e = Env()
os.environ["FAKE_SLEEP"] = "0.5"
e.say("agent-coordinator", "agent-x: erster")
e.now += 6
e.d.step()
assert e.d.proc is not None
e.say("agent-coordinator", "agent-x: kommt während des Laufs")
e.run_until_idle()
assert e.d.acked == 1 and [x["id"] for x in e.d.pending()] == [2]
os.environ["FAKE_SLEEP"] = "0"
e.now += 6
e.run_until_idle()
assert e.d.acked == 2 and "kommt während des Laufs" in e.calls_made()[-1][-1]

# --- Fehlschläge überstehen einen Neustart des Dienstes ------------------------------------------------
e = Env("--max-attempts", "2")
os.environ["FAKE_EXIT"] = "1"
e.say("agent-coordinator", "agent-x: scheitert")
e.now += 6; e.run_until_idle()
e.now += 61; e.run_until_idle()
assert e.d.gave_up_at == 1
d2 = e.new()
e.now += 10000
d2.step()
assert d2.proc is None and len(e.calls_made()) == 2                       # kein neuer Dreierversuch
os.environ["FAKE_EXIT"] = "0"

# --- Dienst als Prozess: SIGHUP (screen -X quit) beendet ihn samt Lauf, zweite Instanz wird abgewiesen ------
import signal, subprocess
e = Env()
os.environ["FAKE_SLEEP"] = "30"
here = os.path.dirname(os.path.abspath(__file__))
cmd = [sys.executable, "-I", os.path.join(here, "dispatcher.py"), "--nick", NICK, "--irc-dir", e.irc,
       "--home", e.dir, "--model", "m", "--claude", e.claude, "--quiet", "0", "--poll", "0.1"]
svc = spawn(cmd)
time.sleep(0.5)
second = subprocess.run(cmd, capture_output=True, text=True, timeout=10)
assert second.returncode != 0 and "läuft bereits" in second.stderr
e.say("agent-coordinator", "agent-x: lang", poll=False)
running = os.path.join(e.args.state, "running")
t = time.time()
while not os.path.exists(running) and time.time() - t < 5:
    time.sleep(0.1)
with open(running) as f:
    run_pid = int(f.read().split()[0])
svc.send_signal(signal.SIGHUP)
svc.wait(timeout=20)
time.sleep(0.2)
try:
    os.killpg(run_pid, 0); alive = True
except ProcessLookupError:
    alive = False
assert not alive and not os.path.exists(running)                          # Lauf nicht verwaist

# --- Harter Abbruch des Dienstes (kill -9): der nächste Start beendet den verwaisten Lauf ------------------
svc = spawn(cmd)
e.say("agent-coordinator", "agent-x: noch einmal", poll=False)
t = time.time()
while not os.path.exists(running) and time.time() - t < 5:
    time.sleep(0.1)
with open(running) as f:
    run_pid = int(f.read().split()[0])
svc.kill(); svc.wait()
os.killpg(run_pid, 0)                                                     # läuft verwaist weiter
d2 = e.new()                                                              # Start räumt auf
time.sleep(0.2)
try:
    os.killpg(run_pid, 0); alive = True
except ProcessLookupError:
    alive = False
assert not alive and not os.path.exists(running)
os.environ["FAKE_SLEEP"] = "0"

# --- Ein Fehler im Schritt beendet den Dienst nicht --------------------------------------------------------
e = Env()
svc = spawn([sys.executable, "-I", os.path.join(here, "dispatcher.py"), "--nick", NICK,
                        "--irc-dir", e.irc, "--home", e.dir, "--model", "m", "--claude", e.claude,
                        "--quiet", "0", "--poll", "0.1"])
time.sleep(0.5)
e.say("agent-coordinator", "agent-x: macht das Protokoll größer", poll=False)
os.chmod(e.log, 0)                                                        # Lesen scheitert im Schritt
time.sleep(0.5)
assert svc.poll() is None                                                 # läuft weiter
os.chmod(e.log, 0o600)
with open(os.path.join(e.args.state, "dispatch.log")) as f:
    assert "Fehler im Schritt: PermissionError" in f.read()
svc.terminate(); svc.wait(timeout=20)

# --- Nachprüfung P2: Protokoll bei ausgeschaltetem Dienst geleert und über die alte Länge gewachsen ------
e = Env()
e.say("agent-coordinator", "agent-x: alt 1 " + "x" * 30)
d2_offset = e.d.offset[1]
with open(e.log, "r+") as f:
    f.truncate(0)
with open(e.log, "a") as f:                                               # anderer Zeitstempel am Anfang
    f.write("2026-11-19 07:00:00 << :agent-coordinator!~u@h PRIVMSG #g :agent-x: neu 1\n")
    f.write("2026-11-19 07:00:01 << :agent-coordinator!~u@h PRIVMSG #g :agent-x: neu 2\n")
    f.write("2026-11-19 07:00:02 << :agent-coordinator!~u@h PRIVMSG #g :agent-x: neu 3\n")
assert os.stat(e.log).st_size > d2_offset
d2 = e.new(); d2.poll_log()
assert [x["text"] for x in d2.entries][-3:] == ["agent-x: neu 1", "agent-x: neu 2", "agent-x: neu 3"], d2.entries

# --- Nachprüfung P2: running nicht schreibbar, der Lauf wird trotzdem überwacht und beendet --------------
e = Env("--slots-dir", tempfile.mkdtemp(), "--max-parallel", "1")
orig_write = e.d._write
def failing_write(name, text):
    if name == "running":
        raise OSError(28, "No space left on device")
    orig_write(name, text)
e.d._write = failing_write
e.say("agent-coordinator", "agent-x: trotz voller Platte")
e.now += 6
e.run_until_idle()
assert e.d.acked == 1 and e.d.slot_fd is None

# --- Nachprüfung P3: wiederverwendete PID eines fremden Prozesses wird nicht beendet ----------------------
import subprocess
from dispatcher import _start_time
fremd = spawn(["sleep", "30"])
e = Env()
with open(os.path.join(e.args.state, "running"), "w") as f:
    f.write(f"{fremd.pid} 1\n")                                           # Startzeit passt nicht
e.new()
assert fremd.poll() is None                                               # lebt noch
fremd.kill(); fremd.wait()
# Toter Sitzungsführer, Kind lebt weiter: das Kind wird beendet
sh = spawn(["sh", "-c", "sleep 30 & echo $!"], stdout=subprocess.PIPE, text=True)
kind = int(sh.stdout.readline()); sh.wait()
assert _start_time(sh.pid) is None and _start_time(kind) is not None
with open(os.path.join(e.args.state, "running"), "w") as f:
    f.write(f"{sh.pid} 999\n")
e.new()
time.sleep(0.2)
assert _start_time(kind) is None or open(f"/proc/{kind}/stat").read().split(") ")[1][0] == "Z"

# --- Kontingent: Zeitpunkt aus der Limitmeldung ----------------------------------------------------------
from dispatcher import parse_reset
assert parse_reset("Claude AI usage limit reached|1795030000", 0) == 1795030000
assert parse_reset("You've hit your usage limit. Try again in 30 minutes.", 1000) == 1000 + 1800
assert parse_reset("Limit erreicht, wieder frei in 2 Stunden", 1000) == 1000 + 7200
r = parse_reset("Your limit resets at 3pm", time.time())
assert r and time.localtime(r).tm_hour == 15 and r > time.time()
assert parse_reset("ohne Zeitangabe", 0) is None

# --- Kontingent leer (kurze Limitmeldung, Exit 0): keine Quittung, Sperre für alle Dienste des Rechners -----
quota = tempfile.mkdtemp()
e = Env("--quota-dir", quota, "--cooldown", "600")
os.environ["FAKE_OUT"] = "Claude AI usage limit reached"
e.say("agent-coordinator", "agent-x: Aufgabe bei leerem Kontingent")
e.now += 6
e.run_until_idle()
assert e.d.acked == 0 and e.d.failures == 0, (e.d.acked, e.d.failures)  # kein Fehlschlag, nur gesperrt
assert e.d.quota.blocked("claude", e.now)[0] == e.now + 600
other = Env("--quota-dir", quota)                                          # anderer Agent desselben Rechners
os.environ["FAKE_CALLS"] = other.calls
other.say("agent-coordinator", "agent-x: anderer Auftrag")
other.now = e.now + 6
other.d.step()
assert other.d.proc is None and other.calls_made() == []                  # startet nicht
warn = other.outbox()
assert "[dispatcher] agent-x: Kontingent claude gesperrt bis" in warn and "Aufträge warten" in warn, warn
other.now += 30
other.d.step()
assert other.outbox() == ""                                               # nur eine Warnung je Sperre
os.environ["FAKE_OUT"] = ""
other.now += 600
other.run_until_idle()
assert other.d.acked == 1                                                 # nach der Sperre läuft er
os.environ["FAKE_CALLS"] = e.calls
e.now += 700
e.run_until_idle()
assert e.d.acked == 1 and len(e.calls_made()) == 2                        # Auftrag lief erneut und ist erledigt

# --- Lange Antwort, die nur über Ratengrenzen spricht, sperrt nichts ----------------------------------------
quota = tempfile.mkdtemp()
e = Env("--quota-dir", quota)
os.environ["FAKE_OUT"] = "Ergebnis: die Ratengrenze (rate limit) gilt je Nick. " + "Begründung. " * 80
e.say("agent-coordinator", "agent-x: prüfe die Ratengrenze")
e.now += 6
e.run_until_idle()
assert e.d.acked == 1 and e.d.quota.blocked("claude", e.now) is None
os.environ["FAKE_OUT"] = ""

# --- Codex: Aufruf, Tagesbudget gemeinsam je Rechner, Ausweichen auf Claude --------------------------------
quota = tempfile.mkdtemp()
role = os.path.join(tempfile.mkdtemp(), "role.md")
with open(role, "w") as f:
    f.write("ROLLE: letzter Reviewer")
e = Env("--backend", "codex", "--codex-model", "gpt-x", "--quota-dir", quota, "--budget-codex", "1",
        "--role", role)
e.args.codex = e.claude                                                   # gleiches Ersatzskript
e.say("agent-coordinator", "agent-x: letztes Review vor dem PR")
e.now += 6
e.run_until_idle()
argv = e.calls_made()[-1]
assert argv[:3] == ["exec", "-m", "gpt-x"] and argv[-1].startswith("ROLLE: letzter Reviewer"), argv[:3]
assert e.d.acked == 1
e.say("agent-coordinator", "agent-x: noch ein Review")
e.now += 6
e.d.step()
assert e.d.proc is None and len(e.calls_made()) == 1                      # Tagesbudget 1 erreicht
warn = e.outbox()
assert "Kontingent codex gesperrt" in warn and "Tagesbudget 1" in warn and "Aufträge warten" in warn, warn
# Mit Ersatz: derselbe Auftrag läuft über Claude und sagt das im Auftrag
e2 = Env("--backend", "codex", "--fallback", "claude", "--quota-dir", quota, "--budget-codex", "1")
e2.args.codex = e2.claude
os.environ["FAKE_CALLS"] = e2.calls
e2.say("agent-coordinator", "agent-x: Review mit Ersatz")
e2.now += 6
e2.run_until_idle()
argv = e2.calls_made()[-1]
assert argv[0] == "-p" and "ersatzweise über claude" in argv[-1] and e2.d.acked == 1, argv[:2]
assert "weiche aus" in e2.outbox()

# --- Relative Pfade werden absolut: der Lauf startet in --home und muss state.md dort finden -------------
cwd = os.getcwd()
os.chdir(e.dir)
rel = parse_args(["--nick", NICK, "--irc-dir", "irc", "--home", ".", "--model", "m", "--role", "r.md"])
os.chdir(cwd)
assert all(os.path.isabs(x) for x in (rel.irc_dir, rel.home, rel.role, rel.state, rel.log, rel.outbox))

# --- Herzschlag ist lesbares JSON -------------------------------------------------------------------------
e.d.step()
with open(os.path.join(e.args.state, "heartbeat")) as f:
    hb = json.load(f)
assert hb["nick"] == NICK and "pending" in hb

print("ok")
