"""IRC <-> Discord bridge: IRC traffic as embeds in Discord, Discord messages back to IRC."""
import asyncio
import base64
import datetime
import hashlib
import os
import re
import ssl
import time
import unicodedata

import discord

IRC_HOST = os.environ.get("IRC_HOST", "zeh.ernst.casa")
IRC_PORT = int(os.environ.get("IRC_PORT", "6697"))
IRC_PASS = os.environ["IRC_PASS"]
IRC_NICK = os.environ.get("IRC_NICK", "discord")
IRC_CHANNELS = os.environ.get("IRC_CHANNELS", "#general,#first,#second,#third").split(",")
GUILD_ID = int(os.environ["DISCORD_GUILD_ID"])
CATEGORY = os.environ.get("DISCORD_CATEGORY", "IRC")
SIGN_KEY = os.environ.get("SIGN_KEY", "bridge@zeh.ernst.casa")

FORMAT_CODES = re.compile(r"\x03\d{0,2}(,\d{1,2})?|[\x02\x0f\x11\x16\x1d\x1e\x1f]")
NICKCHARS = r"[A-Za-z0-9_\-\[\]\\^{}|`]+"
# Wie teambot.PREFIX: "nick1 nick2: text"; Trenner zwischen Nicks ist Pflicht, sonst backtrackt der Ausdruck exponentiell
PREFIX = re.compile(rf"^((?:{NICKCHARS}[ ,]+)*{NICKCHARS}:)\s+(.*)$")
HEADER = re.compile(rf"^(?:{NICKCHARS}[ ,]+)*{NICKCHARS}:$")
MAX_LINES = 100
IRC_PAYLOAD_BYTES = 390
MESSAGE_INTERVAL = 1
SIGNED_LINE = re.compile(r"\[id=\d+ t=\d+ sig=[A-Za-z0-9+/=]+\]$")
MIN_TEXT = 40
TOPIC_DEBOUNCE = 2
TOPIC_WINDOW = 600
TOPIC_EDIT_TIMEOUT = 10


def parse(line):
    """Return (prefix_nick, command, params) for one raw IRC line."""
    prefix = ""
    if line.startswith(":"):
        prefix, line = line[1:].split(" ", 1)
    trailing = None
    if " :" in line:
        line, trailing = line.split(" :", 1)
    params = line.split()
    command = params.pop(0).upper() if params else ""
    if trailing is not None:
        params.append(trailing)
    return prefix.split("!", 1)[0], command, params


def has_signed_line(text):
    return any(SIGNED_LINE.search(row) for row in text.splitlines())


def irc_lines(text, message_id, limit=200):
    """Split a Discord message into IRC lines of about `limit` UTF-8 bytes (room left for the signature).

    Ein führendes "nick:" steht in jedem Teil, auch wenn es allein in der ersten Zeile steht: Spezialisten erhalten nur
    Zeilen mit ihrem Nick. Mehrteilige Nachrichten tragen "(i/n m=<Discord-Nachrichten-ID>)" im signierten Text, damit
    sich Teile zweier Nachrichten derselben Person nicht zu einer scheinbar vollständigen Freigabe mischen lassen.
    Ein Präfix, neben dem kein Platz für Text bleibt, ist ein ValueError.
    """
    rows = [raw.strip() for raw in text.splitlines() if raw.strip()]
    prefix = ""
    match = PREFIX.match(rows[0]) if rows else None
    if match:
        prefix, rows[0] = match.group(1) + " ", match.group(2)
    elif rows and HEADER.match(rows[0]) and len(rows) > 1:
        prefix, rows = rows[0] + " ", rows[1:]
    budget = limit - len(prefix.encode()) - len(f" ({MAX_LINES}/{MAX_LINES} m={message_id})".encode())
    if budget < MIN_TEXT:
        raise ValueError(f"IRC text budget {limit} is too small for prefix and text")
    out = []
    for raw in rows:
        while raw:
            n = budget
            while len(raw[:n].encode()) > budget:
                n -= 1
            out.append(raw[:n])
            raw = raw[n:]
    total = len(out)
    if total > MAX_LINES:
        raise ValueError(f"message needs more than {MAX_LINES} IRC parts")
    if total > 1:
        if has_signed_line(text):
            raise ValueError("already signed lines must not be split")
        out = [f"{line} ({i + 1}/{total} m={message_id})" for i, line in enumerate(out)]
    return [prefix + line for line in out]


def discord_lines(text, limit=2000):
    if len(text) <= limit:
        return [text]
    if has_signed_line(text):
        raise ValueError("already signed lines must not be split")
    budget = limit - len(f" ({MAX_LINES}/{MAX_LINES})")
    if budget <= 0:
        raise ValueError("Discord part limit is too small")
    total = (len(text) + budget - 1) // budget
    if total > MAX_LINES:
        raise ValueError(f"message needs more than {MAX_LINES} Discord parts")
    return [text[start:start + budget] + f" ({i + 1}/{total})"
            for i, start in enumerate(range(0, len(text), budget))]


def signed_payload(channel, user_id, timestamp, text):
    return f"{channel.lower()}\n{user_id}\n{timestamp}\n{text}".encode()


async def sign(payload):
    """Detached OpenPGP signature of payload with the bridge key, base64 without line breaks."""
    proc = await asyncio.create_subprocess_exec(
        "gpg", "--batch", "--yes", "--detach-sign", "--local-user", SIGN_KEY,
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE)
    signature, _ = await proc.communicate(payload)
    if proc.returncode != 0:
        raise RuntimeError(f"gpg exited with {proc.returncode}")
    return base64.b64encode(signature).decode()


def nick_color(nick):
    return int(hashlib.md5(nick.lower().encode()).hexdigest()[:6], 16) | 0x404040


def agent_count(members):
    """Anzahl verschiedener Agenten-Nicks über alle gespiegelten Kanäle."""
    return len({nick for nicks in members.values() for nick in nicks if nick.lower().startswith("agent-")})


def clean_topic(text):
    text = FORMAT_CODES.sub("", text)
    text = "".join(" " if unicodedata.category(c) == "Cc" else c
                   for c in text if unicodedata.category(c) not in ("Cf", "Cs"))
    # AllowedMentions gilt nicht für Kanalbeschreibungen; Vollbreiten-@ kann niemanden erwähnen.
    return " ".join(text.split()).replace("@", "＠")[:1024]


def channel_name(irc_channel):
    return irc_channel.lstrip("#&").lower()


class Bridge(discord.Client):
    def __init__(self):
        intents = discord.Intents.default()
        intents.message_content = True
        super().__init__(intents=intents, allowed_mentions=discord.AllowedMentions.none())
        self.writer = None
        self.channels = {}  # IRC-Kanal (klein) -> Discord-Textkanal
        self.ready_event = asyncio.Event()
        self.topic_pending = {}  # Kanal -> (neuester Solltext, frühester Versuch)
        self.topic_attempts = {}  # Kanal -> höchstens zwei monotone Zeitpunkte
        self.topic_task = None
        self.message_lock = asyncio.Lock()
        self.last_message_sent = None
        self.members = {}  # IRC-Kanal (klein) -> Nicks, für Präsenz und QUIT/NICK-Meldungen

    async def setup_hook(self):
        self.loop.create_task(self.irc_loop())
        self.loop.create_task(self.presence_loop())
        self.topic_task = self.loop.create_task(self.topic_loop())

    async def close(self):
        if self.topic_task is not None:
            self.topic_task.cancel()
            await asyncio.gather(self.topic_task, return_exceptions=True)
        await super().close()

    def queue_topic(self, irc_channel, text):
        target = irc_channel.lower()
        if target not in self.channels:
            return
        topic = clean_topic(text) or "no topic"
        if target in self.topic_pending and self.topic_pending[target][0] == topic:
            return
        self.topic_pending[target] = (topic, time.monotonic() + TOPIC_DEBOUNCE)

    async def flush_topics(self):
        for target in list(self.topic_pending):
            # Nach einem await kann sich auch der nächste Kanal geändert haben.
            topic, due = self.topic_pending[target]
            now = time.monotonic()
            if now < due:
                continue
            channel = self.channels[target]
            if (channel.topic or "") == topic:
                self.topic_pending.pop(target, None)
                continue
            attempts = [stamp for stamp in self.topic_attempts.get(target, []) if now - stamp < TOPIC_WINDOW]
            self.topic_attempts[target] = attempts
            if len(attempts) >= 2:
                continue
            # Auch Fehler zählen, damit fehlende Rechte keine schnelle Wiederholungsschleife auslösen.
            attempts.append(now)
            try:
                # discord.py kann intern auf 429 warten; andere Kanäle sollen trotzdem weiterlaufen.
                updated = await asyncio.wait_for(
                    channel.edit(topic=topic, reason="IRC topic sync"), timeout=TOPIC_EDIT_TIMEOUT)
                self.channels[target] = updated
            except discord.Forbidden:
                print(f"Topic sync {target}: missing Manage Channels permission", flush=True)
            except (discord.HTTPException, OSError) as error:
                print(f"Topic sync {target}: {error!r}", flush=True)
                if self.topic_pending.get(target) == (topic, due):
                    self.topic_pending[target] = (topic, time.monotonic() + 30)
                continue
            # Während edit() kann ein neuerer IRC-Stand eintreffen; diesen nicht verwerfen.
            if self.topic_pending.get(target) == (topic, due):
                self.topic_pending.pop(target, None)

    async def topic_loop(self):
        await self.ready_event.wait()
        while True:
            await self.flush_topics()
            await asyncio.sleep(1)

    async def presence_loop(self):
        # Discord begrenzt Präsenz-Updates; daher nur alle 30 s und nur bei Änderung senden
        await self.ready_event.wait()
        shown = None
        while True:
            count = agent_count(self.members)
            if count != shown:
                await self.change_presence(activity=discord.Activity(
                    type=discord.ActivityType.watching,
                    name=f"{count} agent{'' if count == 1 else 's'} in IRC"))
                shown = count
            await asyncio.sleep(30)

    async def on_ready(self):
        guild = self.get_guild(GUILD_ID) or await self.fetch_guild(GUILD_ID)
        category = discord.utils.get(guild.categories, name=CATEGORY)
        if category is None:
            category = await guild.create_category(CATEGORY)
        for irc_channel in IRC_CHANNELS:
            name = channel_name(irc_channel)
            channel = discord.utils.get(category.text_channels, name=name)
            if channel is None:
                channel = await guild.create_text_channel(
                    name, category=category,
                    topic=f"Mirror of {irc_channel} on {IRC_HOST} - messages here are relayed to IRC")
            self.channels[irc_channel.lower()] = channel
        self.ready_event.set()
        print(f"Discord ready as {self.user}, mapped {list(self.channels)}", flush=True)

    async def post(self, irc_channel, embed):
        channel = self.channels.get(irc_channel.lower())
        if channel:
            await channel.send(embed=embed)

    async def event(self, irc_channel, text, color=0x5c5f66):
        await self.post(irc_channel, discord.Embed(description=text, color=color))

    async def on_irc(self, nick, command, params):
        if command == "PRIVMSG" and len(params) == 2 and params[0].lower() in self.channels:
            target, text = params
            original = text
            signed = has_signed_line(original)
            text = FORMAT_CODES.sub("", text).replace("@", "＠")
            action = text.startswith("\x01ACTION ") and text.endswith("\x01")
            if action:
                text = f"*{discord.utils.escape_markdown(nick)} {text[8:-1]}*"
            try:
                if signed:
                    # Ein zu großes Original darf auch nach bereinigter Anzeige nicht aufgeteilt werden.
                    discord_lines(original)
                parts = discord_lines(text)
            except ValueError as error:
                print(f"not relayed to Discord: {error}", flush=True)
                await self.event(target, f"⚠️ IRC message not relayed: {error}")
                return
            for part in parts:
                embed = discord.Embed(description=part, color=nick_color(nick),
                                      timestamp=datetime.datetime.now(datetime.timezone.utc))
                embed.set_author(name=nick)
                footer = f"{target} · IRC"
                if signed and text != original:
                    footer += " · Bereinigte Anzeige; Original im IRC prüfen"
                embed.set_footer(text=footer)
                await self.post(target, embed)
        elif command == "353" and len(params) >= 4:
            self.members.setdefault(params[2].lower(), set()).update(n.lstrip("~&@%+") for n in params[3].split())
        elif command == "JOIN" and params:
            self.members.setdefault(params[0].lower(), set()).add(nick)
            if nick.lower() == IRC_NICK.lower():
                # Beim JOIN garantiert IRC für leere Topics keine 331; ausdrücklich nur abfragen.
                self.send_irc(f"TOPIC {params[0]}")
            else:
                await self.event(params[0], f"➡️ **{nick}** joined", 0x3ba55c)
        elif command in ("PART", "KICK") and params:
            gone = params[1] if command == "KICK" and len(params) > 1 else nick
            self.members.get(params[0].lower(), set()).discard(gone)
            if gone == IRC_NICK:
                self.members.pop(params[0].lower(), None)
            elif command == "PART":
                reason = f" ({params[1]})" if len(params) > 1 else ""
                await self.event(params[0], f"⬅️ **{nick}** left{reason}", 0xed4245)
        elif command == "QUIT":
            reason = f" ({params[0]})" if params else ""
            for irc_channel, nicks in self.members.items():
                if nick in nicks:
                    nicks.discard(nick)
                    await self.event(irc_channel, f"⬅️ **{nick}** quit{reason}", 0xed4245)
        elif command == "NICK" and params:
            for irc_channel, nicks in self.members.items():
                if nick in nicks:
                    nicks.discard(nick)
                    nicks.add(params[0])
                    await self.event(irc_channel, f"🔁 **{nick}** is now **{params[0]}**")
        elif command == "332" and len(params) >= 3:
            self.queue_topic(params[1], params[2])
        elif command == "331" and len(params) >= 2:
            self.queue_topic(params[1], "")
        elif command == "TOPIC" and len(params) == 2:
            self.queue_topic(params[0], params[1])
            await self.event(params[0], f"📌 **{nick}** set the topic: {params[1]}", 0xfaa61a)

    def send_irc(self, line):
        if self.writer:
            self.writer.write((line + "\r\n").encode())

    async def irc_loop(self):
        await self.ready_event.wait()
        delay = 5
        while True:
            try:
                reader, self.writer = await asyncio.open_connection(
                    IRC_HOST, IRC_PORT, ssl=ssl.create_default_context())
                self.send_irc(f"PASS {IRC_PASS}")
                self.send_irc(f"NICK {IRC_NICK}")
                self.send_irc(f"USER {IRC_NICK} 0 * :Discord bridge")
                while line := await reader.readline():
                    nick, command, params = parse(line.decode(errors="replace").rstrip("\r\n"))
                    if command == "PING":
                        self.send_irc("PONG :" + (params[0] if params else ""))
                    elif command == "001":
                        delay = 5
                        for irc_channel in IRC_CHANNELS:
                            self.send_irc(f"JOIN {irc_channel}")
                            # Topics (Projekt je Kanal) setzt der Koordinator; ohne Fehlerfolge, falls die Bridge kein Op ist
                            self.send_irc(f"MODE {irc_channel} -t")
                        print("IRC connected", flush=True)
                    elif command == "433":
                        # Kein Ausweichnick: Agenten erkennen Freigaben nur vom Nick discord; später erneut versuchen
                        raise ConnectionError(f"nick {IRC_NICK} in use")
                    else:
                        await self.on_irc(nick, command, params)
                    await self.writer.drain()
            except Exception as error:  # jeder Fehler führt zur Neuverbindung, sonst bliebe die Bridge stumm
                print(f"IRC error: {error!r}", flush=True)
            if self.writer:
                self.writer.close()
            self.writer = None
            self.members.clear()
            print(f"IRC disconnected, retrying in {delay}s", flush=True)
            await asyncio.sleep(delay)
            delay = min(delay * 2, 300)

    async def on_message(self, message):
        if message.author.bot or message.guild is None:
            return
        target = next((irc for irc, ch in self.channels.items() if ch.id == message.channel.id), None)
        if target is None:
            return
        if self.writer is None:
            await message.add_reaction("⚠️")
            return
        text = message.clean_content.replace("@", "＠")
        for attachment in message.attachments:
            text += "\n" + attachment.url
        # IRC-Steuerzeichen dürfen weder zusätzliche Befehle noch unsichtbare Textteile einschleusen.
        text = "".join(c for c in text if unicodedata.category(c) != "Cc" or c in "\n\r\t")
        name = clean_topic(message.author.display_name).replace(" ", "_").replace("<", "_").replace(">", "_")
        name = name.encode()[:24].decode(errors="ignore") or "Discord"
        user_id = message.author.id
        timestamp = int(message.created_at.timestamp())
        try:
            async with self.message_lock:
                writer = self.writer
                if writer is None:
                    raise ConnectionError("IRC disconnected before message")
                # Signaturlänge hängt am Schlüssel; mit Reserve berechnen und alle fertigen Zeilen erneut prüfen.
                sample = await sign(signed_payload(target, user_id, timestamp, ""))
                overhead = len(f"<{name}>  [id={user_id} t={timestamp} sig={sample}]".encode()) + 8
                lines = irc_lines(text, message.id, limit=min(200, IRC_PAYLOAD_BYTES - overhead))
                packets = []
                for line in lines:
                    sig = await sign(signed_payload(target, user_id, timestamp, line))
                    payload = f"<{name}> {line} [id={user_id} t={timestamp} sig={sig}]"
                    if len(payload.encode()) > IRC_PAYLOAD_BYTES:
                        raise ValueError("signed IRC line exceeds byte budget")
                    packets.append(f"PRIVMSG {target} :{payload}")
                age = time.time() - timestamp
                if age < -60 or age + len(packets) * MESSAGE_INTERVAL > 600:
                    raise ValueError("message falls outside signature time window")
                for packet in packets:
                    if self.last_message_sent is not None:
                        delay = self.last_message_sent + MESSAGE_INTERVAL - time.monotonic()
                        if delay > 0:
                            await asyncio.sleep(delay)
                    if self.writer is not writer:
                        raise ConnectionError("IRC connection changed during message")
                    self.last_message_sent = time.monotonic()
                    writer.write((packet + "\r\n").encode())
                    await writer.drain()
        except (OSError, RuntimeError, ValueError) as error:
            # Nie einen unsignierten Ersatz oder einen Restsatz über eine neue Verbindung senden.
            print(f"not forwarded: {error}", flush=True)
            await message.add_reaction("⚠️")
            reason = str(error) if isinstance(error, ValueError) else "IRC forwarding interrupted; resend the complete message"
            await message.reply(f"⚠️ Not forwarded completely: {reason}", mention_author=False)


if __name__ == "__main__":
    Bridge().run(os.environ["DISCORD_TOKEN"])
