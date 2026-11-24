#!/usr/bin/env python3
"""Hilfe für Cosmos-Updates: check (nur lesend), pin und verify. Ablauf: docs/regeln/COSMOS_UPDATE.md."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
UPSTREAM = 'CosmosOS/Cosmos'
NUGET_VERSIONS = 'https://api.nuget.org/v3-flatcontainer/cosmos.kernel/index.json'
COMMIT = re.compile(r'[0-9a-f]{40}\Z')
VERSION = re.compile(r'\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\Z')
SDK_KEY = re.compile(r'("Cosmos\.Sdk"\s*:\s*")([^"]*)(")')


def read_pin(root):
    """Alle Pin-Stellen als Wörterbuch; fehlende Stellen werden zum Fehler."""
    sdks = json.loads((root / 'global.json').read_text(encoding='utf-8'))['msbuild-sdks']
    return {'COMMIT': (root / 'deploy/cosmos/COMMIT').read_text().strip(),
            'VERSION': (root / 'deploy/cosmos/VERSION').read_text().strip(),
            'global.json': sdks['Cosmos.Sdk']}


def verify(root):
    """Liefert die Abweichungen der Pin-Stellen; leere Liste heißt konsistent."""
    pin = read_pin(root)
    problems = []
    if not COMMIT.match(pin['COMMIT']):
        problems.append(f"COMMIT ist kein voller Commit: {pin['COMMIT']}")
    if not VERSION.match(pin['VERSION']):
        problems.append(f"VERSION hat kein gültiges Format: {pin['VERSION']}")
    if pin['global.json'] != pin['VERSION']:
        problems.append(f"global.json (Cosmos.Sdk {pin['global.json']}) weicht von VERSION {pin['VERSION']} ab")
    return problems


def replace_line(path, value):
    """Ersetzt den Inhalt einer Einzeilen-Datei und behält ihr Zeilenende."""
    old = path.read_bytes()
    ending = b'\r\n' if old.endswith(b'\r\n') else b'\n' if old.endswith(b'\n') else b''
    path.write_bytes(value.encode('ascii') + ending)


def write_pin(root, version, commit):
    if not COMMIT.match(commit):
        raise ValueError('commit muss der volle 40-stellige Commit des Branches cosmos sein')
    if not VERSION.match(version):
        raise ValueError('version hat kein gültiges Format (3.0.99-zehos.0)')
    old = read_pin(root)
    # Regel aus deploy/cosmos/README.md: neuer Commit, neue Version (build-cosmos.sh verweigert sonst den Bau).
    if commit != old['COMMIT'] and version == old['VERSION']:
        raise ValueError(f"Commit ändert sich, Version bleibt {version}: neue Version wählen")
    replace_line(root / 'deploy/cosmos/COMMIT', commit)
    replace_line(root / 'deploy/cosmos/VERSION', version)
    path = root / 'global.json'
    text = path.read_bytes().decode('utf-8')
    text, count = SDK_KEY.subn(lambda m: m.group(1) + version + m.group(3), text)
    if count != 1:
        raise ValueError('global.json: Cosmos.Sdk nicht eindeutig gefunden')
    path.write_bytes(text.encode('utf-8'))


def http_json(url):
    headers = {'Accept': 'application/vnd.github+json', 'User-Agent': 'ZehOS-cosmos-update'}
    if os.environ.get('GITHUB_TOKEN') and 'api.github.com' in url:
        headers['Authorization'] = 'Bearer ' + os.environ['GITHUB_TOKEN']
    with urlopen(Request(url, headers=headers), timeout=30) as response:
        return json.load(response)


def latest_stable(versions):
    stable = [v for v in versions if re.fullmatch(r'\d+\.\d+\.\d+', v)]
    return max(stable, key=lambda v: tuple(int(p) for p in v.split('.')), default=None)


def git(*args):
    return subprocess.run(['git', *args], cwd=ROOT, capture_output=True, text=True, check=True).stdout


def patch_files(text):
    """Dateinamen aus dem Feld „Dateien“ einer patches/*.md (Teile in Backticks mit Dateiendung)."""
    for line in text.splitlines():
        if line.startswith('- **Dateien:**'):
            return sorted(set(re.findall(r'`([\w./-]+\.\w+)`', line)))
    return []


def affected(patches, changed):
    """Je Patch die geänderten Upstream-Dateien; Patchdateien sind teils nur Dateiname, daher Endvergleich."""
    return {name: sorted(c for c in changed if any(c == f or c.endswith('/' + f) for f in files))
            for name, files in patches.items()}


def cosmos_patches():
    """Liest patches/*.md aus origin/cosmos (nach git fetch origin)."""
    names = git('ls-tree', '--name-only', 'origin/cosmos', 'patches/').split()
    return {Path(n).stem: patch_files(git('show', 'origin/cosmos:' + n)) for n in names if n.endswith('.md')}


def upstream_base():
    """Grundstand-Commit aus PATCHES.md (erste Tabellenzeile mit einem vollen Commit)."""
    match = re.search(r'`([0-9a-f]{40})`', git('show', 'origin/cosmos:PATCHES.md'))
    return match.group(1) if match else None


def changed_files(base, head):
    # Die Vergleichs-API liefert die Dateien nur auf der ersten Seite und höchstens 300.
    files = [f['filename'] for f in http_json(f'https://api.github.com/repos/{UPSTREAM}/compare/{base}...{head}')['files']]
    return files, len(files) >= 300


def check(args):
    pin = read_pin(ROOT)
    print(f"Pin: {pin['VERSION']} (cosmos-Commit {pin['COMMIT'][:8]})")
    print('Neueste Upstream-Version (NuGet Cosmos.Kernel): ' + str(latest_stable(http_json(NUGET_VERSIONS)['versions'])))
    if not args.to:
        return 0
    base = args.base or upstream_base()
    if not base:
        print('Grundstand nicht gefunden (git fetch origin; PATCHES.md im Branch cosmos); --base angeben', file=sys.stderr)
        return 1
    changed, truncated = changed_files(base, args.to)
    print(f'Upstream {base[:8]}...{args.to}: {len(changed)} geänderte Dateien' + (' (VERGLEICH ABGESCHNITTEN, Rest per git prüfen)' if truncated else ''))
    for name, hits in affected(cosmos_patches(), changed).items():
        if hits:
            print(f'  {name}: ' + ', '.join(hits))
    print('Patches ohne Treffer sind nicht automatisch unberührt (Namen in patches/*.md sind teils unvollständig).')
    return 0


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    c = sub.add_parser('check', help='Pin, neueste Version, Upstream-Änderungen je Patch (nur lesend)')
    c.add_argument('--to', help='Upstream-Tag oder -Commit, bis zu dem verglichen wird (etwa v3.0.99)')
    c.add_argument('--base', help='Upstream-Ausgangsstand (Standard: Grundstand aus PATCHES.md)')
    p = sub.add_parser('pin', help='Pin-Stellen setzen und prüfen')
    p.add_argument('version')
    p.add_argument('commit')
    sub.add_parser('verify', help='Pin-Stellen auf Übereinstimmung prüfen')
    args = parser.parse_args()
    if args.command == 'check':
        return check(args)
    if args.command == 'pin':
        try:
            write_pin(ROOT, args.version, args.commit)
        except ValueError as error:
            print(error, file=sys.stderr)
            return 1
    problems = verify(ROOT)
    print('\n'.join(problems) or 'Pin konsistent: ' + read_pin(ROOT)['VERSION'])
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
