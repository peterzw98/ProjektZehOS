"""IRC <-> Discord bridge: IRC traffic as embeds in Discord, Discord messages back to IRC."""
import asyncio
import base64
import datetime
import hashlib
import os
import re
import ssl

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


def irc_lines(text, limit=200):
    """Split a Discord message into chunks of at most `limit` UTF-8 bytes (room left for the signature)."""
    out = []
    for raw in text.splitlines():
        raw = raw.strip()
        while raw:
            n = limit
            while len(raw[:n].encode()) > limit:
                n -= 1
            out.append(raw[:n])
            raw = raw[n:]
    return out


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
        self.members = {}  # IRC-Kanal (klein) -> Nicks, für Präsenz und QUIT/NICK-Meldungen

    async def setup_hook(self):
        self.loop.create_task(self.irc_loop())
        self.loop.create_task(self.presence_loop())

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
            text = FORMAT_CODES.sub("", text)
            action = text.startswith("\x01ACTION ") and text.endswith("\x01")
            if action:
                text = f"*{discord.utils.escape_markdown(nick)} {text[8:-1]}*"
            embed = discord.Embed(description=text[:4096], color=nick_color(nick),
                                  timestamp=datetime.datetime.now(datetime.timezone.utc))
            embed.set_author(name=nick)
            embed.set_footer(text=f"{target} · IRC")
            await self.post(target, embed)
        elif command == "353" and len(params) >= 4:
            self.members.setdefault(params[2].lower(), set()).update(n.lstrip("~&@%+") for n in params[3].split())
        elif command == "JOIN" and params:
            self.members.setdefault(params[0].lower(), set()).add(nick)
            if nick != IRC_NICK:
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
        elif command == "TOPIC" and len(params) == 2:
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
                        print("IRC connected", flush=True)
                    elif command == "433":
                        self.send_irc(f"NICK {IRC_NICK}_")
                    else:
                        await self.on_irc(nick, command, params)
                    await self.writer.drain()
            except (OSError, ssl.SSLError) as error:
                print(f"IRC error: {error}", flush=True)
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
        text = message.clean_content
        for attachment in message.attachments:
            text += "\n" + attachment.url
        lines = irc_lines(text)
        if len(lines) > 5:
            # shortcut: lange Discord-Nachrichten werden gekappt, statt Flood-Schutz des Servers zu riskieren
            lines = lines[:5] + [f"[… {len(lines) - 5} more lines on Discord]"]
        name = message.author.display_name.replace(" ", "_")[:24]
        user_id = message.author.id
        try:
            for line in lines:
                timestamp = int(message.created_at.timestamp())
                sig = await sign(signed_payload(target, user_id, timestamp, line))
                self.send_irc(f"PRIVMSG {target} :<{name}> {line} [id={user_id} t={timestamp} sig={sig}]")
        except (OSError, RuntimeError) as error:
            # Unsigniert nichts weiterleiten: Freigaben hängen an gültiger Signatur
            print(f"signing failed: {error}", flush=True)
            await message.add_reaction("⚠️")
        if self.writer:
            await self.writer.drain()


if __name__ == "__main__":
    Bridge().run(os.environ["DISCORD_TOKEN"])
