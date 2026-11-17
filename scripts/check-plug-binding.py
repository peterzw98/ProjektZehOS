#!/usr/bin/env python3
"""Prüft im Cosmos-Buildlog, dass jeder ZehOS-Plug sein Ziel gefunden hat.

Cosmos.Patcher meldet ein fehlendes Ziel nur als Warnung und beendet den Build erfolgreich; der Plug ist dann still
wirkungslos. Benötigt das Log von `cosmos build --verbose`.
"""

from pathlib import Path
import re
import sys

PLUG_CLASS = re.compile(r'\[Plug\(')
PLUG_MEMBER = re.compile(r'\[PlugMember\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*([^(;{=]*)(.)')
METHOD_NAME = re.compile(r'\b(\w+)\s*(?:<[^>]*>)?\s*$')
CLASS = re.compile(r'\bclass\s+(\w+)')
NAMESPACE = re.compile(r'^\s*namespace\s+([\w.]+)', re.MULTILINE)


def expected_plugs(source_root):
    plugs = set()
    for path in sorted(Path(source_root).rglob('*.cs')):
        text = path.read_text(encoding='utf-8-sig')
        if not PLUG_CLASS.search(text):
            continue
        namespace = NAMESPACE.search(text)
        prefix = namespace.group(1) + '.' if namespace else ''
        for member in PLUG_MEMBER.finditer(text):
            name = METHOD_NAME.search(member.group(1)) if member.group(2) == '(' else None
            classes = CLASS.findall(text, 0, member.start())
            # Felder, Properties oder unerwartete Formen nicht still auslassen, sonst bliebe ihr Plug ungeprüft.
            if name is None or not classes:
                raise ValueError(f'{path}: [PlugMember] nicht auswertbar')
            plugs.add(f'{prefix}{classes[-1]}::{name.group(1)}')
    return plugs


def unwrap(lines):
    # Der Patcher bricht lange Zeilen am Konsolenrand um; Fortsetzungen beginnen ohne Einrückung.
    joined = []
    for line in lines:
        line = line.rstrip('\r\n')
        if joined and line and not line[0].isspace() and joined[-1].lstrip().startswith('['):
            joined[-1] += line
        else:
            joined.append(line)
    return joined


def bound_plugs(lines, expected):
    # Schlüssel ist die volle Signatur, damit eine bindende Überladung keine nicht bindende verdeckt.
    bound, missed, current = set(), set(), None
    for line in unwrap(lines):
        if 'Starting method resolution for' in line:
            # Teilstring statt Zerlegung: beim Zusammenfügen umbrochener Zeilen kann das Leerzeichen vor dem Namen fehlen.
            plug = next((plug for plug in expected if plug + '(' in line), None)
            current = line[line.index(plug + '('):].split(')')[0] + ')' if plug else None
        elif current and 'Found matching method' in line:
            bound.add(current)
            current = None
        elif current and 'Target method not found' in line:
            # Cosmos versucht Plugs gegen jede Assembly mit passendem Typnamen; ein Fehlversuch allein ist kein Fehler.
            missed.add(current)
            current = None
    return bound, missed


def main(arguments):
    if len(arguments) != 2:
        print('Aufruf: check-plug-binding.py <kernel-build.log> <Quellordner>', file=sys.stderr)
        return 2
    try:
        expected = expected_plugs(arguments[1])
        with open(arguments[0], encoding='utf-8', errors='replace') as log:
            bound, missed = bound_plugs(log, expected)
    except (OSError, ValueError) as error:
        print(f'Prüfung nicht möglich: {error}', file=sys.stderr)
        return 2
    if not expected:
        print(f'Keine Plugs in {arguments[1]} gefunden.', file=sys.stderr)
        return 2
    unbound = sorted(f'{signature} (Ziel nicht gefunden)' for signature in missed - bound)
    unbound += sorted(f'{plug} (nicht im Log, Build ohne --verbose?)' for plug in expected
                      if not any(signature.startswith(plug + '(') for signature in bound | missed))
    for plug in unbound:
        print(f'Plug bindet nicht: {plug}', file=sys.stderr)
    if unbound:
        return 1
    print(f'{len(expected)} ZehOS-Plugs gebunden.')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
