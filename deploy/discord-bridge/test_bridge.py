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
print("ok")
