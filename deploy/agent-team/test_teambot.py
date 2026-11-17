import sys
sys.argv = ["teambot.py", "agent-x", "/tmp", "/dev/null"]
from teambot import split_message, PREFIX

assert split_message("hallo") == ["hallo"]
parts = split_message("agent-a agent-b: " + "wort " * 200, limit=120)
assert len(parts) > 1 and all(p.startswith("agent-a agent-b: ") for p in parts)
assert all(len(p.encode()) <= 120 + 8 for p in parts)  # +8 für " (i/n)"
assert parts[0].endswith(f"(1/{len(parts)})")

# Lange Nachrichten ohne Nick-Präfix wiederholen kein Präfix
parts_ohne = split_message("wort " * 200, limit=120)
assert len(parts_ohne) > 1
assert not any(PREFIX.match(p) for p in parts_ohne)

# Wortkombination mit Doppelpunkt matcht PREFIX syntaktisch; split_message wiederholt es bei langen Nachrichten
assert PREFIX.match("Ergebnis der Tests: alles grün")
long_prefix = split_message("Ergebnis der Tests: " + "wort " * 50, limit=100)
assert len(long_prefix) > 1 and all(p.startswith("Ergebnis der Tests: ") for p in long_prefix)

import time
t = time.time()
assert split_message("Tests für " + "a" * 40 + " bestanden") == ["Tests für " + "a" * 40 + " bestanden"]
assert time.time() - t < 0.5  # früher exponentielles Backtracking
print("ok")
