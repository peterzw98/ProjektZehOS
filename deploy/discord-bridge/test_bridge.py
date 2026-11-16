import os

os.environ.setdefault("IRC_PASS", "x")
os.environ.setdefault("DISCORD_GUILD_ID", "1")

from bridge import irc_lines, parse, signed_payload

assert parse(":alice!a@h PRIVMSG #general :hi there :)") == ("alice", "PRIVMSG", ["#general", "hi there :)"])
assert parse("PING :srv") == ("", "PING", ["srv"])
assert parse(":bob!b@h JOIN #general") == ("bob", "JOIN", ["#general"])
assert irc_lines("a\n\nb") == ["a", "b"]
assert irc_lines("abcde", limit=2) == ["ab", "cd", "e"]
assert irc_lines("äää", limit=3) == ["ä", "ä", "ä"]  # nie mitten im UTF-8-Zeichen trennen
assert signed_payload("#General", 1, 2, "x") == b"#general\n1\n2\nx"
print("ok")
