import sys
sys.argv = ["teambot.py", "agent-x", "/tmp", "/dev/null"]
from teambot import split_message

assert split_message("hallo") == ["hallo"]
parts = split_message("agent-a agent-b: " + "wort " * 200, limit=120)
assert len(parts) > 1 and all(p.startswith("agent-a agent-b: ") for p in parts)
assert all(len(p.encode()) <= 120 + 8 for p in parts)  # +8 für " (i/n)"
assert parts[0].endswith(f"(1/{len(parts)})")
import time
t = time.time()
assert split_message("Tests für " + "a" * 40 + " bestanden") == ["Tests für " + "a" * 40 + " bestanden"]
assert split_message("Ergebnis der Tests: alles grün") == ["Ergebnis der Tests: alles grün"]  # kein Nick-Präfix
assert time.time() - t < 0.5  # früher exponentielles Backtracking
print("ok")
