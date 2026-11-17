import os
import sys
import types

os.environ.setdefault("IRC_PASS", "x")
os.environ.setdefault("DISCORD_GUILD_ID", "1")
# Die geprüften Hilfsfunktionen brauchen discord.py nicht; die CI installiert es deshalb nicht.
sys.modules.setdefault("discord", types.SimpleNamespace(Client=object))

from bridge import agent_count, irc_lines, parse, signed_payload

assert parse(":alice!a@h PRIVMSG #general :hi there :)") == ("alice", "PRIVMSG", ["#general", "hi there :)"])
assert parse("PING :srv") == ("", "PING", ["srv"])
assert parse(":bob!b@h JOIN #general") == ("bob", "JOIN", ["#general"])
assert irc_lines("a\n\nb") == ["a (1/2)", "b (2/2)"]
assert irc_lines("einzeilig") == ["einzeilig"]
assert irc_lines("abcdefghij", limit=13) == ["abcde (1/2)", "fghij (2/2)"]
assert irc_lines("äää", limit=13) == ["ää (1/2)", "ä (2/2)"]  # nie mitten im UTF-8-Zeichen trennen
# Spezialisten lesen nur Zeilen mit ihrem Nick: das Präfix steht in jedem Teil, auch nach einem Umbruch
approval = irc_lines("agent-x agent-y: PR 190 freigegeben,\naber erst nach dem Gaststart.")
assert approval == ["agent-x agent-y: PR 190 freigegeben, (1/2)", "agent-x agent-y: aber erst nach dem Gaststart. (2/2)"]
long_line = irc_lines("agent-x: " + "w" * 500)
assert len(long_line) > 1 and all(line.startswith("agent-x: ") and len(line.encode()) <= 200 for line in long_line)
capped = irc_lines("agent-x: " + "\n".join(f"zeile {i}" for i in range(8)))
assert len(capped) == 6 and capped[4].endswith("(5/8)") and capped[5] == "agent-x: [… 3 more lines on Discord]"
assert signed_payload("#General", 1, 2, "x") == b"#general\n1\n2\nx"
assert agent_count({"#a": {"agent-x", "discord"}, "#b": {"agent-x", "Agent-y"}}) == 2
print("ok")
