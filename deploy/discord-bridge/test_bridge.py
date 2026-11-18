import asyncio
import os
import sys
import types
from unittest.mock import patch

os.environ.setdefault("IRC_PASS", "x")
os.environ.setdefault("DISCORD_GUILD_ID", "1")
# Die geprüften Hilfsfunktionen brauchen discord.py nicht; die CI installiert es deshalb nicht.
class Client:
    async def close(self):
        self.base_closed = True


sys.modules.setdefault("discord", types.SimpleNamespace(Client=Client))

from bridge import Bridge, agent_count, clean_topic, irc_lines, parse, signed_payload
import bridge

assert parse(":alice!a@h PRIVMSG #general :hi there :)") == ("alice", "PRIVMSG", ["#general", "hi there :)"])
assert parse("PING :srv") == ("", "PING", ["srv"])
assert parse(":bob!b@h JOIN #general") == ("bob", "JOIN", ["#general"])
assert irc_lines("a\n\nb", 7) == ["a (1/2 m=7)", "b (2/2 m=7)"]
assert irc_lines("einzeilig", 7) == ["einzeilig"]
assert irc_lines("ä" * 60, 7, limit=101) == ["ä" * 44 + " (1/2 m=7)", "ä" * 16 + " (2/2 m=7)"]  # nie mitten im UTF-8-Zeichen
# Spezialisten lesen nur Zeilen mit ihrem Nick: das Präfix steht in jedem Teil, auch nach einem Umbruch
approval = irc_lines("agent-x agent-y: PR 190 freigegeben,\naber erst nach dem Gaststart.", 7)
assert approval == ["agent-x agent-y: PR 190 freigegeben, (1/2 m=7)",
                    "agent-x agent-y: aber erst nach dem Gaststart. (2/2 m=7)"]
# Empfänger allein in der ersten Zeile
alone = irc_lines("agent-x:\nPR 190 freigegeben,\naber erst nach Gaststart.", 7)
assert alone == ["agent-x: PR 190 freigegeben, (1/2 m=7)", "agent-x: aber erst nach Gaststart. (2/2 m=7)"]
assert irc_lines("agent-x:", 7) == ["agent-x:"]
# Viele Empfänger (117 Bytes) bleiben in jedem Teil; ein Präfix ohne Platz für Text wird abgelehnt
many = " ".join(f"agent-engineer-number-{i}" for i in range(5)) + ":"
long_line = irc_lines(many + " " + "w" * 450, 1234567890123456789)
assert len(long_line) > 1 and all(line.startswith(many + " ") and len(line.encode()) <= 200 for line in long_line)
try:
    irc_lines(" ".join(f"agent-engineer-number-{i}" for i in range(7)) + ": text", 1234567890123456789)
    raise AssertionError("zu langes Präfix angenommen")
except ValueError:
    pass
capped = irc_lines("agent-x: " + "\n".join(f"zeile {i}" for i in range(8)), 7)
assert len(capped) == 6 and capped[4] == "agent-x: zeile 4 (5/8 m=7)"
assert capped[5] == "agent-x: [… 3 more lines on Discord] (m=7)"
assert signed_payload("#General", 1, 2, "x") == b"#general\n1\n2\nx"
assert agent_count({"#a": {"agent-x", "discord"}, "#b": {"agent-x", "Agent-y"}}) == 2


# Ohne Zugangsdaten oder Discord-Verbindung: Uhr und Kanal-API sind kontrollierte Testdoubles.
assert clean_topic("\x0304rot\x0f\n@everyone <@123>\x00\u202ehallo") == "rot ＠everyone <＠123> hallo"
assert clean_topic("x" * 1025) == "x" * 1024
assert clean_topic("\x02\t\u200b") == ""


class HttpError(Exception):
    pass


class Forbidden(HttpError):
    pass


bridge.discord.Forbidden = Forbidden
bridge.discord.HTTPException = HttpError


class Channel:
    def __init__(self, topic=""):
        self.topic = topic
        self.calls = []
        self.error = None
        self.during_edit = None

    async def edit(self, *, topic, reason):
        self.calls.append(topic)
        if self.during_edit:
            self.during_edit()
        if self.error:
            raise self.error
        # discord.py liefert ein neues Objekt, statt das alte zu aktualisieren.
        updated = Channel(topic)
        updated.calls = self.calls
        return updated


def topic_bridge():
    client = object.__new__(Bridge)
    client.channels = {"#general": Channel(), "#second": Channel()}
    client.topic_pending = {}
    client.topic_attempts = {}
    return client


async def check_topics():
    client = topic_bridge()
    events = []

    async def event(*args):
        events.append(args)

    client.event = event
    client.members = {}
    sent = []
    client.send_irc = sent.append
    await client.on_irc(bridge.IRC_NICK, "JOIN", ["#general"])
    assert sent == ["TOPIC #general"]
    with patch("bridge.time.monotonic", return_value=0) as clock:
        await client.on_irc("coordinator", "332", ["discord", "#General", "Start"])
        await client.flush_topics()
        assert client.channels["#general"].calls == []
        clock.return_value = 2
        await client.flush_topics()
        assert client.channels["#general"].topic == "Start" and not events
        await client.on_irc("coordinator", "TOPIC", ["#general", "Zwischenstand"])
        clock.return_value = 3
        await client.on_irc("coordinator", "TOPIC", ["#general", "Neuester Stand"])
        clock.return_value = 5
        await client.flush_topics()
        assert client.channels["#general"].calls == ["Start", "Neuester Stand"]
        assert len(events) == 2
        await client.on_irc("srv", "331", ["discord", "#general", "No topic is set"])
        await client.on_irc("srv", "332", ["discord"])
        await client.on_irc("srv", "331", [])
        client.queue_topic("#unknown", "ignoriert")
        client.queue_topic("#second", "Unabhaengig")
        clock.return_value = 7
        await client.flush_topics()
        assert client.channels["#general"].topic == "Neuester Stand"
        assert client.channels["#second"].topic == "Unabhaengig"
        clock.return_value = 602
        await client.flush_topics()
        assert client.channels["#general"].topic == ""
        assert client.channels["#general"].calls == ["Start", "Neuester Stand", ""]
        client.queue_topic("#general", "")
        clock.return_value = 604
        await client.flush_topics()
        assert len(client.channels["#general"].calls) == 3

    # Ein während edit() eintreffendes Topic muss nach dem laufenden Stand erhalten bleiben.
    client = topic_bridge()
    with patch("bridge.time.monotonic", return_value=0) as clock:
        client.queue_topic("#general", "Alt")
        client.channels["#general"].during_edit = lambda: client.queue_topic("#general", "Neu")
        clock.return_value = 2
        await client.flush_topics()
        assert client.channels["#general"].topic == "Alt"
        clock.return_value = 4
        await client.flush_topics()
        assert client.channels["#general"].calls == ["Alt", "Neu"]

    # Auch andere Kanäle können sich während eines laufenden API-Aufrufs ändern.
    client = topic_bridge()
    with patch("bridge.time.monotonic", return_value=0) as clock:
        client.queue_topic("#general", "Allgemein")
        client.queue_topic("#second", "")
        client.channels["#general"].during_edit = lambda: client.queue_topic("#second", "Neuester")
        clock.return_value = 2
        await client.flush_topics()
        clock.return_value = 4
        await client.flush_topics()
        assert client.channels["#second"].topic == "Neuester"

    for error in (Forbidden("denied"), HttpError("temporary"), OSError("offline")):
        client = topic_bridge()
        with patch("bridge.time.monotonic", return_value=0) as clock:
            client.channels["#general"].error = error
            client.queue_topic("#general", "Wunsch")
            client.queue_topic("#second", "Weiter")
            clock.return_value = 2
            await client.flush_topics()
            assert client.channels["#second"].topic == "Weiter"
            assert client.channels["#general"].calls == ["Wunsch"]
            clock.return_value = 3
            await client.flush_topics()
            assert client.channels["#general"].calls == ["Wunsch"]
            if not isinstance(error, Forbidden):
                client.channels["#general"].error = None
                clock.return_value = 32
                await client.flush_topics()
                assert client.channels["#general"].topic == "Wunsch"
            else:
                assert "#general" not in client.topic_pending


async def check_close():
    client = topic_bridge()
    client.topic_task = asyncio.create_task(asyncio.sleep(3600))
    await client.close()
    assert client.topic_task.cancelled() and client.base_closed
    client.topic_task = None
    await client.close()


asyncio.run(check_topics())
asyncio.run(check_close())
print("ok")
