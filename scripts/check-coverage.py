#!/usr/bin/env python3
"""Wertet einen Cobertura-Abdeckungsbericht aus und prüft Grenzwerte."""

import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

MARKER = '/ProjektZeh/'


class InputError(Exception):
    pass


def relative_path(filename):
    path = '/' + filename.replace('\\', '/')
    if 'ProjektZeh.Tests/' in path:
        return None
    # rfind: Repository und Projekt dürfen gleich heißen, die Trenner überlappen dann.
    index = path.rfind(MARKER)
    return None if index < 0 else path[index + len(MARKER):]


def read_report(report):
    try:
        root = ET.parse(report).getroot()
    except OSError as error:
        raise InputError(f'Bericht kann nicht gelesen werden: {error}')
    except ET.ParseError as error:
        raise InputError(f'Bericht ist kein gültiges XML: {error}')
    files = {}
    for element in root.iter('class'):
        path = relative_path(element.get('filename', ''))
        if path is None:
            continue
        merged = files.setdefault(path, {})
        for line in element.iter('line'):
            number = line.get('number')
            merged[number] = merged.get(number, False) or int(line.get('hits', '0')) > 0
    return files


def read_exclusions(path):
    excluded = set()
    try:
        text = Path(path).read_text(encoding='utf-8')
    except OSError as error:
        raise InputError(f'Ausnahmedatei kann nicht gelesen werden: {error}')
    for number, line in enumerate(text.splitlines(), 1):
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        name, separator, reason = line.partition(' # ')
        if not separator or not reason.strip():
            raise InputError(f'Ausnahmedatei Zeile {number}: Grund nach " # " fehlt.')
        excluded.add(name.strip())
    return excluded


def percent(hit, total):
    return 100.0 * hit / total


def main(argv=None):
    parser = argparse.ArgumentParser(description='Prüft einen Cobertura-Bericht.')
    parser.add_argument('report')
    parser.add_argument('--min-total', type=float, default=0.0)
    parser.add_argument('--min-file', type=float, default=0.0)
    parser.add_argument('--exclusions')
    parser.add_argument('--summary')
    arguments = parser.parse_args(argv)

    try:
        files = read_report(arguments.report)
        excluded = read_exclusions(arguments.exclusions) if arguments.exclusions else set()
    except InputError as error:
        print(error, file=sys.stderr)
        return 2
    # Pro Datei: (getroffen, gesamt)
    stats = {path: (sum(lines.values()), len(lines))
             for path, lines in files.items() if path not in excluded and lines}
    total = sum(count for _, count in stats.values())
    if total == 0:
        print('Bericht enthält keine gezählten Zeilen.', file=sys.stderr)
        return 2

    hit = sum(value for value, _ in stats.values())
    exit_code = 0
    summary = f'Gesamt: {percent(hit, total):.1f} % ({hit}/{total} Zeilen)'
    print(summary)
    if percent(hit, total) < arguments.min_total:
        print(f'Gesamt {percent(hit, total):.1f} % liegt unter {arguments.min_total:.1f} %')
        exit_code = 1
    for path, (file_hit, count) in sorted(stats.items()):
        if percent(file_hit, count) < arguments.min_file:
            print(f'Unter Grenze: {path} {percent(file_hit, count):.1f} %')
            exit_code = 1

    if arguments.summary:
        worst = sorted(stats.items(), key=lambda item: (item[1][0] - item[1][1], item[0]))[:10]
        rows = [f'| {path} | {count - file_hit} | {percent(file_hit, count):.1f} % |'
                for path, (file_hit, count) in worst]
        with open(arguments.summary, 'a', encoding='utf-8') as file:
            file.write('\n'.join([summary, '', '| Datei | offen | Abdeckung |', '| --- | --- | --- |', *rows]) + '\n')
    return exit_code


if __name__ == '__main__':
    sys.exit(main())
