#!/usr/bin/env python3
# IRC-Begleitprozess für einen Teamagenten: hält die Verbindung, beantwortet PING, verbindet neu,
# protokolliert nach <dir>/log.txt und sendet Zeilen aus <dir>/outbox.fifo.
#   teambot.py <nick> <dir> <keyfile> [kanal,kanal,...]   (Standard: #general)
# Outbox: "/raw BEFEHL" roh, "#kanal text" in diesen Kanal, sonst nach #general.
import os, re, socket, ssl, sys, threading, time

HOST, PORT = "zeh.ernst.casa", 6697
NICK, D, KEY = sys.argv[1], sys.argv[2], sys.argv[3]
START_CHANNELS = (sys.argv[4] if len(sys.argv) > 4 else "#general").split(",")
LOG, FIFO = os.path.join(D, "log.txt"), os.path.join(D, "outbox.fifo")
NICKCHARS = r"[A-Za-z0-9_\-\[\]\\^{}|`]+"
# "nick1 nick2: text"; Trenner zwischen Nicks ist Pflicht, sonst backtrackt der Ausdruck exponentiell
PREFIX = re.compile(rf"^((?:{NICKCHARS}[ ,]+)*{NICKCHARS}:)\s+(.*)$")
sock = None; lock = threading.Lock()
channels = set(c.lower() for c in START_CHANNELS)  # nach Neuverbindung wieder betreten


def log(s):
    with open(LOG, "a") as f: f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + s + "\n")


def send(line, quiet=False):
    """Sendet eine Zeile; False, wenn keine Verbindung besteht oder das Senden scheitert."""
    try:
        with lock:
            if not sock:
                log("!! nicht gesendet (keine Verbindung): " + ("PASS ***" if quiet else line))
                return False
            sock.sendall((line + "\r\n").encode())
    except OSError as e:
        log(f"!! nicht gesendet ({e}): " + ("PASS ***" if quiet else line))
        return False
    if not quiet: log(">> " + line)
    return True


def chunks(text, limit):
    # Server lehnt lange Zeilen ab (417); an Wortgrenzen in UTF-8-Bytes teilen
    out, cur = [], ""
    for w in text.split(" "):
        cand = (cur + " " + w) if cur else w
        if len(cand.encode()) <= limit: cur = cand; continue
        if cur: out.append(cur)
        while len(w.encode()) > limit:
            head = w.encode()[:limit].decode(errors="ignore"); out.append(head); w = w[len(head):]
        cur = w
    if cur: out.append(cur)
    return out


def split_message(text, limit=360):
    """Teile einer Nachricht; ein führendes "nick:"-Präfix steht in jedem Teil, weil Agenten nur Nennungen lesen."""
    m = PREFIX.match(text)
    prefix, body = (m.group(1) + " ", m.group(2)) if m else ("", text)
    parts = chunks(body, max(80, limit - len(prefix.encode())))
    if len(parts) > 1: parts = [f"{p} ({i + 1}/{len(parts)})" for i, p in enumerate(parts)]
    return [prefix + p for p in parts]


def outbox():
    while True:
        with open(FIFO) as f:
            for line in f:
                line = line.rstrip("\n")
                if not line.strip(): continue
                if line.startswith("/raw "): send(line[5:]); continue
                target, text = ("#general", line)
                if line.startswith("#") and " " in line: target, text = line.split(" ", 1)
                for part in split_message(text):
                    # Bei getrennter Verbindung warten, statt die Nachricht zu verlieren (höchstens ~2 min)
                    for _ in range(8):
                        if send(f"PRIVMSG {target} :{part}"): break
                        time.sleep(15)
                    time.sleep(0.6)


def main():
    global sock
    if not os.path.exists(FIFO): os.mkfifo(FIFO, 0o600)
    threading.Thread(target=outbox, daemon=True).start()
    while True:
        try:
            pw = open(KEY).read().strip()
            s = ssl.create_default_context().wrap_socket(
                socket.create_connection((HOST, PORT), timeout=300), server_hostname=HOST)
            with lock: sock = s
            send("PASS " + pw, quiet=True)  # PASS nie protokollieren
            send(f"NICK {NICK}"); send(f"USER {NICK} 0 * :ZehOS team {NICK}")
            buf = b""
            while True:
                data = s.recv(4096)
                if not data: raise ConnectionError("closed")
                buf += data
                while b"\r\n" in buf:
                    raw, buf = buf.split(b"\r\n", 1)
                    l = raw.decode(errors="replace")
                    if l.startswith("PING"): send("PONG" + l[4:], quiet=True); continue
                    log("<< " + l)
                    p = l.split(" ")
                    if len(p) > 2 and p[1] in ("001", "376", "422"):
                        send("JOIN " + ",".join(sorted(channels)))
                    elif len(p) > 2 and p[0].startswith(f":{NICK}!") and p[1] in ("JOIN", "PART"):
                        ch = p[2].lstrip(":").lower()
                        (channels.add if p[1] == "JOIN" else channels.discard)(ch)
        except Exception as e:
            log(f"!! disconnected: {e}; reconnect in 15s")
            with lock: sock = None
            time.sleep(15)


if __name__ == "__main__":
    main()
