# Tests für dispatcher.py ohne Netz und ohne echtes claude: ein Ersatzskript protokolliert den Aufruf und
# endet je nach Umgebungsvariable erfolgreich, mit Fehler oder erst nach einer Wartezeit.
import json, os, stat, sys, tempfile, time

from dispatcher import Dispatcher, mention_pattern, parse_args, parse_mention

NICK = "agent-x"
FAKE = r'''#!{py}
import json, os, sys, time
with open(os.environ["FAKE_CALLS"], "a") as f:
    f.write(json.dumps(sys.argv[1:]) + "\n")
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
        os.environ.update(FAKE_CALLS=self.calls, FAKE_EXIT="0", FAKE_SLEEP="0")
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

# Abbruch nach dem Schreiben der Inbox, aber vor dem Offset: keine doppelten Einträge
e.say("agent-coordinator", "agent-x: dritter Auftrag", poll=False)      # nur d2 soll lesen
d2.poll_log()
with open(os.path.join(e.args.state, "offset"), "w") as f:
    f.write("0 0\n")                                                      # alter, unbrauchbarer Stand
d3 = e.new()
d3.poll_log()
assert [x["id"] for x in d3.entries] == [1, 2, 3], d3.entries

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

# --- Zeitlimit: Lauf wird beendet und gilt als gescheitert ------------------------------------------------
e = Env("--timeout", "0.3")
os.environ["FAKE_SLEEP"] = "30"
e.say("agent-coordinator", "agent-x: lange Aufgabe")
e.now += 6
e.d.step()
assert e.d.proc is not None
t = time.time()
while e.d.proc is not None and time.time() - t < 5:
    time.sleep(0.1); e.now += 0.2; e.d.step()
assert e.d.proc is None and e.d.acked == 0 and e.d.failures == 1
assert time.time() - t < 5                                                # nicht 30 s gewartet
os.environ["FAKE_SLEEP"] = "0"

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
