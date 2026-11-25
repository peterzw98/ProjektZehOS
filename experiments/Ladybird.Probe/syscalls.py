#!/usr/bin/env python3
"""Wertet strace-Ausgaben aus (N1-C1).

Eingabe: Dateien aus `strace -ff -f -yy -v -o DIR/t ...` (je Prozess und
Thread eine Datei `t.<pid>`). Ausgabe: JSON mit Aufrufzahlen, Fehlern,
Flags, geöffneten Pfaden und Sockets.

    python3 syscalls.py --trace DIR --out FILE.json

Pfade: `paths` enthält Pfade aus Pfad-Aufrufen (auch fehlgeschlagene, also
berührte statt nur geöffnete) und aus den `-yy`-Anmerkungen. strace kürzt
Zeichenketten standardmäßig auf 32 Zeichen (`"..."...`); gekürzte Pfade werden
nicht aufgenommen. Für vollständige Pfade mit `strace -s 4096` aufnehmen.
Prozessname: erster erfolgreicher `execve`, sonst `pid-<n>` (n aus `t.<n>`).

Grenzen: Nur die Ausgabeform von strace wird gelesen, nichts wird ausgeführt.
Eine abgeschnittene letzte Zeile (Prozess abgebrochen, Platte voll) hat kein
Ergebnis und wird nicht gezählt. Ein `<unfinished ...>` zählt beim Beginn
des Aufrufs, auch wenn nie ein `resumed` folgt (Prozess endet im Aufruf);
das `resumed` liefert nur noch Fehler und Flags nach.
"""

import argparse
import json
import os
import re
import sys

PREFIX = re.compile(r"^(?:\[pid\s+\d+\]\s+|\d+\s+)?")
STRING = re.compile(r'"((?:[^"\\]|\\.)*)"(\.\.\.)?')
START = re.compile(r"^(\w+)\((.*)$")
RESUMED = re.compile(r"^<\.\.\. (\w+) resumed>(.*)$")
RESULT = re.compile(r"\)\s+=\s+(-?\d+|0x[0-9a-fA-F]+|\?)(?:<[^>]*>)?(?:\s+([A-Z][A-Z0-9_]+)\b)?[^=]*$")
FLAG = re.compile(r"\b[A-Z][A-Z0-9]*_[A-Z0-9_]+\b")
FD_PATH = re.compile(r"\b\d+<(/[^>]*)>")
FD_SOCKET = re.compile(r"\b\d+<((?:UNIX|TCP|UDP|UDPLITE|TCPv6|UDPv6|NETLINK|PACKET)(?:->|[^<>])*)>")
SOCKADDR = re.compile(r"sa_family=(AF_\w+)")
SUN_PATH = re.compile(r'sun_path=(?:@)?"((?:[^"\\]|\\.)*)"')
INET_ADDR = re.compile(r'sin6?_port=htons\((\d+)\).*?inet_(?:pton\(AF_INET6?, |addr\()"([^"]*)"')

# Aufrufe, deren erste Zeichenkette ein Pfad ist; die zweiten Pfade stehen in PFAD2.
PFAD = {
    "open", "openat", "openat2", "creat", "stat", "lstat", "newfstatat", "statx",
    "access", "faccessat", "faccessat2", "readlink", "readlinkat", "execve",
    "execveat", "chdir", "mkdir", "mkdirat", "unlink", "unlinkat", "rmdir",
    "chmod", "fchmodat", "truncate", "statfs", "utimensat", "getxattr",
    "lgetxattr", "rename", "renameat", "renameat2", "link", "linkat",
    "symlink", "symlinkat", "mount", "umount2", "chroot",
}
PFAD2 = {"rename", "renameat", "renameat2", "link", "linkat", "symlink", "symlinkat"}
SOCKET_AUFRUFE = {"connect", "bind", "sendto", "sendmsg", "accept", "accept4", "getsockname", "getpeername"}


def _ohne_zeichenketten(text):
    return STRING.sub('""', text)


def _argumente(text):
    """Schneidet das Ergebnis (`) = ...`) ab; dahinter stehen nur Erklärungen wie SA_RESTART."""
    m = RESULT.search(text)
    return text[:m.start() + 1] if m else text


def _sockaddr(args):
    """Kurzform einer Socketadresse oder None."""
    m = SUN_PATH.search(args)
    if m:
        return "AF_UNIX:" + m.group(1)
    fam = SOCKADDR.search(args)
    if not fam:
        return None
    if fam.group(1) in ("AF_INET", "AF_INET6"):
        m = INET_ADDR.search(args)
        if m:
            host = m.group(2)
            return "%s:%s:%s" % (fam.group(1), "[%s]" % host if ":" in host else host, m.group(1))
    return fam.group(1)


class Prozess:
    def __init__(self, datei):
        self.datei = datei
        self.name = None
        self.syscalls = {}

    def eintrag(self, name):
        return self.syscalls.setdefault(name, {"count": 0, "errors": {}, "flags": set()})

    def als_json(self):
        calls = {}
        for name in sorted(self.syscalls):
            e = self.syscalls[name]
            calls[name] = {"count": e["count"], "errors": dict(sorted(e["errors"].items())),
                           "flags": sorted(e["flags"])}
        return {"name": self.name or "pid-" + self.datei.rsplit(".", 1)[-1], "syscalls": calls}


def _ergebnis(zeile):
    """(hat_ergebnis, fehlername oder None)"""
    m = RESULT.search(zeile)
    if not m:
        return False, None
    wert, name = m.group(1), m.group(2)
    if wert in ("-1", "?") and name and name.startswith("E"):
        return True, name
    return True, None


def _flags(args):
    return set(FLAG.findall(_ohne_zeichenketten(args)))


def _sammle(proc, name, args, pfade, sockets):
    e = proc.eintrag(name)
    args = _argumente(args)
    e["flags"] |= _flags(args)
    # Von strace gekürzte Zeichenketten ("..."...) sind keine vollständigen Pfade.
    strings = [t for t, gekuerzt in STRING.findall(args) if not gekuerzt]
    if name in PFAD and strings:
        for s in (strings if name in PFAD2 else strings[:1]):
            if s.startswith("/") or name in ("execve", "execveat"):
                pfade.add(s)
    for p in FD_PATH.findall(args):
        pfade.add(p)
    for s in FD_SOCKET.findall(args):
        sockets.add(s)
    if name in SOCKET_AUFRUFE:
        adr = _sockaddr(args)
        if adr:
            sockets.add(adr)
    return e


def auswerten_zeilen(zeilen, proc, pfade, sockets):
    offen = {}  # name -> True, nur zur Kontrolle von resumed ohne Anfang
    for roh in zeilen:
        zeile = PREFIX.sub("", roh.rstrip("\r\n"), count=1)
        if not zeile or zeile.startswith(("---", "+++")):
            continue  # Signalzeilen und Prozessende
        m = RESUMED.match(zeile)
        if m:
            name, rest = m.groups()
            e = _sammle(proc, name, rest, pfade, sockets)
            hat, fehler = _ergebnis(zeile)
            if not offen.pop(name, False):
                e["count"] += 1  # unfinished fehlte (Dateianfang abgeschnitten)
            if fehler:
                e["errors"][fehler] = e["errors"].get(fehler, 0) + 1
            continue
        m = START.match(zeile)
        if not m:
            continue
        name, args = m.groups()
        if args.rstrip().endswith("<unfinished ...>"):
            e = _sammle(proc, name, args, pfade, sockets)
            e["count"] += 1
            offen[name] = True
            continue
        hat, fehler = _ergebnis(zeile)
        if not hat:
            continue  # abgeschnitten, kein Ergebnis
        e = _sammle(proc, name, args, pfade, sockets)
        e["count"] += 1
        if fehler:
            e["errors"][fehler] = e["errors"].get(fehler, 0) + 1
        if name in ("execve", "execveat") and proc.name is None and not fehler:
            s = [t for t, gekuerzt in STRING.findall(args) if not gekuerzt]
            if s:
                proc.name = os.path.basename(s[0]) or s[0]


def auswerten(verzeichnis):
    prozesse, pfade, sockets = [], set(), set()
    namen = sorted(os.listdir(verzeichnis), key=lambda n: (len(n), n))
    for datei in namen:
        voll = os.path.join(verzeichnis, datei)
        if not datei.startswith("t.") or not os.path.isfile(voll):
            continue
        proc = Prozess(datei)
        with open(voll, "r", encoding="utf-8", errors="replace") as f:
            auswerten_zeilen(f, proc, pfade, sockets)
        prozesse.append(proc.als_json())
    return {"processes": prozesse, "paths": sorted(pfade), "sockets": sorted(sockets)}


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--trace", required=True, help="Verzeichnis mit t.<pid>-Dateien")
    p.add_argument("--out", required=True, help="JSON-Ausgabe")
    a = p.parse_args(argv)
    if not os.path.isdir(a.trace):
        print("Kein Verzeichnis: " + a.trace, file=sys.stderr)
        return 2
    ergebnis = auswerten(a.trace)
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(ergebnis, f, indent=2, sort_keys=True)
        f.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
