import asyncio
import os
import re
import datetime
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
assert irc_lines("ä" * 60, 7, limit=101) == ["ä" * 43 + " (1/2 m=7)", "ä" * 17 + " (2/2 m=7)"]  # nie mitten im UTF-8-Zeichen
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
complete = irc_lines("agent-x: " + "\n".join(f"zeile {i}" for i in range(8)), 7)
assert len(complete) == 8
assert complete[0] == "agent-x: zeile 0 (1/8 m=7)"
assert complete[-1] == "agent-x: zeile 7 (8/8 m=7)"
assert all("more lines on Discord" not in part for part in complete)
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
        self.block = False

    async def edit(self, *, topic, reason):
        self.calls.append(topic)
        if self.block:
            await asyncio.Future()
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
        assert client.channels["#general"].topic == "no topic"
        assert client.channels["#general"].calls == ["Start", "Neuester Stand", "no topic"]
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
        client.channels["#second"].topic = "no topic"
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


async def check_topic_timeout():
    client = topic_bridge()
    client.channels["#general"].block = True
    with patch("bridge.TOPIC_DEBOUNCE", 0), patch("bridge.TOPIC_EDIT_TIMEOUT", 0.01, create=True):
        client.queue_topic("#general", "Wartet auf 429")
        client.queue_topic("#second", "Unabhaengig")
        await asyncio.wait_for(client.flush_topics(), timeout=0.2)
        assert client.channels["#second"].topic == "Unabhaengig"
        assert "#general" in client.topic_pending


async def check_close():
    client = topic_bridge()
    client.topic_task = asyncio.create_task(asyncio.sleep(3600))
    await client.close()
    assert client.topic_task.cancelled() and client.base_closed
    client.topic_task = None
    await client.close()


asyncio.run(check_topics())
asyncio.run(check_topic_timeout())
asyncio.run(check_close())


# Testorakel für den Empfängervertrag, kein neuer Freigabeprüfer in der Bridge.
def complete_set(parts):
    tags = [re.search(r"\((\d+)/(\d+) m=(\d+)\)$", part) for part in parts]
    return bool(tags) and all(tags) and all(
        tag.group(1) == str(i + 1) and tag.group(2) == str(len(parts))
        and tag.group(3) == tags[0].group(3) for i, tag in enumerate(tags))


assert complete_set(complete)
assert not complete_set(complete[:1])
assert not complete_set(complete[:-1])
assert not complete_set([complete[1], complete[0]] + complete[2:])
assert not complete_set([complete[0], complete[0]] + complete[2:])
other = irc_lines("agent-x: " + "\n".join(f"zeile {i}" for i in range(8)), 8)
assert not complete_set(complete[:3] + other[3:])
hundred = irc_lines("\n".join("x" for _ in range(100)), 1234567890123456789)
assert len(hundred) == 100 and complete_set(hundred)
assert hundred[-1].endswith("(100/100 m=1234567890123456789)")
unicode_parts = irc_lines("😀ä" * 200, 1234567890123456789)
assert all(len(part.encode()) <= 200 for part in unicode_parts)
assert "".join(re.sub(r" \(\d+/\d+ m=\d+\)$", "", part) for part in unicode_parts) == "😀ä" * 200
for text in ("\n".join("x" for _ in range(101)), "x" * 1000 + " [id=1 t=2 sig=AAAA]"):
    try:
        irc_lines(text, 7)
        raise AssertionError("unzulässigen Satz angenommen")
    except ValueError:
        pass
for count in (2000, 2001, 4500):
    raw = "ä" * count
    parts = bridge.discord_lines(raw)
    assert all(len(part) <= 2000 for part in parts)
    assert "".join(re.sub(r" \(\d+/\d+\)$", "", part) for part in parts) == raw
try:
    bridge.discord_lines("x" * 2001 + " [id=1 t=2 sig=AAAA]")
    raise AssertionError("signierte Originalzeile geteilt")
except ValueError:
    pass


class Writer:
    def __init__(self, clock):
        self.clock, self.sent, self.after_drain = clock, [], None

    def write(self, data):
        self.sent.append((self.clock[0], data))

    async def drain(self):
        if self.after_drain:
            self.after_drain()


class Message:
    def __init__(self, text, message_id=7):
        self.author = types.SimpleNamespace(bot=False, id=123456789012345678, display_name="😀" * 20 + "@x\r\n<bad>")
        self.guild = object()
        self.channel = types.SimpleNamespace(id=1)
        self.clean_content, self.attachments, self.id = text, [], message_id
        self.created_at = datetime.datetime.fromtimestamp(1000, datetime.timezone.utc)
        self.reactions, self.replies = [], []

    async def add_reaction(self, reaction):
        self.reactions.append(reaction)

    async def reply(self, text, **kwargs):
        self.replies.append(text)


async def check_message_transport():
    clock = [1000.0]
    client = object.__new__(Bridge)
    client.channels = {"#general": types.SimpleNamespace(id=1)}
    client.message_lock, client.last_message_sent = asyncio.Lock(), None
    client.writer = Writer(clock)
    payloads = []
    real_sleep = asyncio.sleep

    async def sign(payload):
        payloads.append(payload)
        return "A" * 192

    async def sleep(delay):
        clock[0] += delay
        await real_sleep(0)

    with patch("bridge.sign", sign), patch("bridge.time.monotonic", side_effect=lambda: clock[0]), patch(
            "bridge.time.time", return_value=1000), patch("bridge.asyncio.sleep", sleep):
        message = Message("agent-x: " + "😀" * 200 + " @everyone\nEnde", 7)
        await client.on_message(message)
        assert not message.reactions
        rows = client.writer.sent
        assert len(rows) > 5
        assert all(b"\r" not in data[:-2] and data.endswith(b"\r\n") for _, data in rows)
        assert all(len(data.split(b" :", 1)[1][:-2]) <= bridge.IRC_PAYLOAD_BYTES for _, data in rows)
        assert all(rows[i][0] - rows[i - 1][0] >= 1 for i in range(1, len(rows)))
        texts = []
        for _, data in rows:
            match = re.fullmatch(r"PRIVMSG #general :<([^>]+)> (.*) \[id=(\d+) t=(\d+) sig=([A-Za-z0-9+/=]+)\]\r\n", data.decode())
            assert match and len(match[1].encode()) <= 24
            assert "@" not in match[2]
            assert bridge.signed_payload("#general", int(match[3]), int(match[4]), match[2]) in payloads
            texts.append(match[2])
        assert complete_set(texts) and any("＠everyone" in part for part in texts)
        first = Message("\n".join("a" for _ in range(3)), 8)
        second = Message("\n".join("b" for _ in range(3)), 9)
        before = len(rows)
        await asyncio.gather(client.on_message(first), client.on_message(second))
        sent_ids = [re.search(rb"m=(\d+)\)", data)[1] for _, data in rows[before:]]
        assert sent_ids == [b"8"] * 3 + [b"9"] * 3
        assert all(rows[i][0] - rows[i - 1][0] >= 1 for i in range(1, len(rows)))
        before = len(rows)
        oversized = Message("\n".join("x" for _ in range(101)))
        await client.on_message(oversized)
        assert len(rows) == before and oversized.reactions and oversized.replies
        replacement = Writer(clock)
        client.writer.after_drain = lambda: setattr(client, "writer", replacement)
        interrupted = Message("\n".join("x" for _ in range(3)))
        await client.on_message(interrupted)
        assert len(rows) == before + 1 and not replacement.sent and interrupted.reactions
        # Signierfehler und unerwartet große Signaturen müssen VOR dem ersten Teil abbrechen.
        for failure in ("signing", "size"):
            calls = [0]
            async def failing_sign(payload):
                calls[0] += 1
                if failure == "signing" and calls[0] == 3:
                    raise RuntimeError("test signing failure")
                return "A" * (500 if failure == "size" and calls[0] > 1 else 192)
            client.writer = Writer(clock)
            rejected = Message("first\nsecond\nthird")
            with patch("bridge.sign", failing_sign):
                await client.on_message(rejected)
            assert not client.writer.sent and rejected.replies
        for stamp in (399, 1061):
            client.writer = Writer(clock)
            rejected = Message("old or future")
            rejected.created_at = datetime.datetime.fromtimestamp(stamp, datetime.timezone.utc)
            await client.on_message(rejected)
            assert not client.writer.sent and rejected.replies


asyncio.run(check_message_transport())


for boundary in ("\n", "\r\n", "\u2028"):
    for leading in ("", "unsigned first" + boundary):
        signed_middle = leading + "x" * 2100 + " [id=1 t=2 sig=AAAA]" + boundary + "unsigned tail"
        for split in (lambda text: irc_lines(text, 7), bridge.discord_lines):
            try:
                split(signed_middle)
                raise AssertionError("signierte innere Originalzeile nachträglich geteilt")
            except ValueError:
                pass


class Embed:
    def __init__(self, *, description, **kwargs):
        self.description = description

    def set_author(self, **kwargs):
        pass

    def set_footer(self, **kwargs):
        pass


async def check_discord_transport():
    client = topic_bridge()
    shown = []
    async def post(target, embed):
        shown.append(embed.description)
    client.post = post
    with patch.object(bridge.discord, "Embed", Embed, create=True):
        raw = "@everyone " + "😀" * 4500
        await client.on_irc("agent-x", "PRIVMSG", ["#general", raw])
        assert len(shown) > 1 and all(len(part) <= 2000 for part in shown)
        assert "".join(re.sub(r" \(\d+/\d+\)$", "", part) for part in shown) == raw.replace("@", "＠")
        shown.clear()
        signed = "<Test> @everyone \x0304body [id=1 t=2 sig=AAAA]"
        await client.on_irc("discord", "PRIVMSG", ["#general", signed])
        assert shown == [signed]
        shown.clear()
        await client.on_irc("agent-x", "PRIVMSG", ["#general", "x" * 2100 + " [id=1 t=2 sig=AAAA]\ntail"])
        assert len(shown) == 1 and "not relayed" in shown[0]


asyncio.run(check_discord_transport())
print("ok")
