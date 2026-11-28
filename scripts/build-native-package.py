#!/usr/bin/env python3
"""Baut ein natives Paket an einem Linux-Host. Signiert und veröffentlicht nichts."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess
import sys
import tempfile
import tomllib
from urllib.parse import urlsplit
from urllib.request import HTTPSHandler, HTTPRedirectHandler, build_opener
from uuid import uuid4

import package2


SHA256 = re.compile(r'[a-f0-9]{64}')
IMAGE = re.compile(r'[a-z0-9][a-z0-9./:_-]*@sha256:[a-f0-9]{64}')
SOURCE_NAME = re.compile(r'[a-zA-Z0-9][a-zA-Z0-9._+-]*')
MAX_SOURCE_BYTES = package2.MAX_CONTENT


def require_https(url: str) -> None:
    parts = urlsplit(url)
    if parts.scheme != 'https' or not parts.hostname or parts.username is not None or parts.fragment:
        raise package2.PackageError('Quell-URL muss HTTPS ohne Zugangsdaten und Fragment verwenden.')


class HttpsRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        require_https(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def open_source(url: str):
    return build_opener(HTTPSHandler(), HttpsRedirect()).open(url, timeout=30)


def read_recipe(path: Path) -> tuple[str, list[dict[str, str]], list[str]]:
    data = package2.read_regular(path, package2.MAX_SPEC, 'Baurezept')
    try:
        fields = tomllib.loads(data.decode('utf-8'))
    except (UnicodeError, tomllib.TOMLDecodeError):
        raise package2.PackageError('Baurezept muss gültiges UTF-8-TOML sein.') from None

    image, sources = fields.get('image'), fields.get('source', [])
    local = fields.get('local', [])
    if set(fields) - {'image', 'source', 'local'} or not isinstance(image, str) or not IMAGE.fullmatch(image):
        raise package2.PackageError('Baurezept erwartet ein Container-Image mit vollständigem sha256-Digest.')
    if not isinstance(local, list) or not all(isinstance(name, str) and SOURCE_NAME.fullmatch(name) for name in local):
        raise package2.PackageError('local erwartet lokale Dateinamen ohne Unterordner.')
    local_names = {name.lower() for name in local}
    if local_names & {'package.toml', 'manifest.toml', 'build.sh', 'patches'} or len(local_names) != len(local):
        raise package2.PackageError('local erwartet eindeutige lokale Dateinamen ohne Unterordner.')
    if not isinstance(sources, list):
        raise package2.PackageError('Quellen als [[source]] angeben.')
    names = set()
    for source in sources:
        if (not isinstance(source, dict) or set(source) != {'name', 'url', 'sha256'}
                or not all(isinstance(value, str) for value in source.values())
                or not SOURCE_NAME.fullmatch(source['name']) or not SHA256.fullmatch(source['sha256'])):
            raise package2.PackageError('Quelle benötigt einen Dateinamen, eine URL und einen SHA-256-Wert.')
        name = source['name'].lower()
        if name in names:
            raise package2.PackageError('Quellname doppelt angegeben.')
        names.add(name)
        require_https(source['url'])
    return image, sources, local


def download_source(source: dict[str, str], directory: Path, remaining: int) -> int:
    digest = hashlib.sha256()
    with open_source(source['url']) as reader, (directory / source['name']).open('xb') as writer:
        while chunk := reader.read(min(package2.CHUNK, remaining + 1)):
            if len(chunk) > remaining:
                raise package2.PackageError('Downloadgrenze von 1 GiB für alle Quellen überschritten.')
            remaining -= len(chunk)
            digest.update(chunk)
            writer.write(chunk)
    if digest.hexdigest() != source['sha256']:
        raise package2.PackageError(f"SHA-256 stimmt für {source['name']} nicht überein.")
    return remaining


def collect_files(path: Path, spec: dict) -> dict[str, str]:
    if not stat.S_ISDIR(path.lstat().st_mode):
        raise package2.PackageError('Die Bauausgabe files muss ein echtes Verzeichnis sein.')
    executable = set(spec.get('exec', []))
    entries = []
    directories = [path]
    while directories:
        for item in directories.pop().iterdir():
            mode = item.lstat().st_mode
            if stat.S_ISDIR(mode):
                directories.append(item)
                continue
            if not stat.S_ISREG(mode):
                raise package2.PackageError('Die Bauausgabe darf nur reguläre Dateien enthalten.')
            target = item.relative_to(path).as_posix()
            size, digest = package2.hash_file(item)
            entries.append((target, size, digest, target in executable))
    if executable - {entry[0] for entry in entries}:
        raise package2.PackageError('Eine exec-Datei fehlt in der Bauausgabe.')
    package2.Manifest(spec, entries)
    return {target: digest for target, _, digest, _ in entries}


def snapshot_recipe(recipe: Path, snapshot: Path) -> tuple[str, list[dict[str, str]], dict[str, str]]:
    snapshot.mkdir()
    hashes = {}
    _, hashes['package.toml'] = package2.copy_hashed(recipe / 'package.toml', snapshot / 'package.toml', package2.MAX_SPEC)
    image, sources, local = read_recipe(snapshot / 'package.toml')
    patches = recipe / 'patches'
    if os.path.lexists(patches):
        if not stat.S_ISDIR(patches.lstat().st_mode):
            raise package2.PackageError('patches muss ein echtes Verzeichnis sein.')
        (snapshot / 'patches').mkdir()
        local = local + [path.relative_to(recipe).as_posix() for path in sorted(patches.glob('*.patch'))]
    for name in ['manifest.toml', 'build.sh', *local]:
        budget = package2.MAX_SPEC if name in ('manifest.toml', 'build.sh') else package2.MAX_CONTENT
        _, hashes[name] = package2.copy_hashed(recipe / name, snapshot / name, budget)
    return image, sources, hashes


def build(recipe: Path, output: Path, runtime: str) -> None:
    recipe = recipe.resolve(strict=True)
    output = output.parent.resolve() / output.name
    if os.path.lexists(output) or output.is_relative_to(recipe):
        raise package2.PackageError('Ausgabe muss neu sein und außerhalb des Rezepts liegen.')
    if os.name != 'nt' and (':' in str(recipe) or ':' in str(output)):
        raise package2.PackageError('Container-Volumes unterstützen hier keine Doppelpunkte im Pfad.')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.native-', dir=output.parent) as scratch:
        snapshot = Path(scratch) / 'recipe'
        source_directory = Path(scratch) / 'sources'
        stage = Path(scratch) / 'result'
        image, sources, recipe_hashes = snapshot_recipe(recipe, snapshot)
        spec_path = snapshot / 'manifest.toml'
        spec = package2.load_spec(spec_path)
        if spec['runtime'] != 'linux-x64-v1':
            raise package2.PackageError('Nur native Pakete mit runtime=linux-x64-v1 werden gebaut.')
        source_directory.mkdir()
        (stage / 'files').mkdir(parents=True)
        remaining = MAX_SOURCE_BYTES
        for source in sources:
            remaining = download_source(source, source_directory, remaining)

        container = f'zehos-native-{uuid4().hex}'
        command = [runtime, 'run', '--name', container, '--rm', '--pull=never', '--network=none', '--read-only',
                   '--platform=linux/amd64', '--cap-drop=ALL', '--security-opt=no-new-privileges',
                   '--tmpfs', '/tmp:rw,exec,nosuid,nodev', '--workdir', '/tmp',
                   '--env', 'SOURCE_DATE_EPOCH=0', '--env', 'TZ=UTC', '--env', 'LC_ALL=C',
                   '--volume', f'{snapshot}:/recipe:ro', '--volume', f'{source_directory}:/sources:ro',
                   '--volume', f'{stage}:/output:rw']
        if os.name == 'posix':
            if runtime == 'podman':
                command += ['--userns=keep-id']
            command += ['--user', f'{os.getuid()}:{os.getgid()}']
        command += ['--entrypoint', '/bin/sh', image, '/recipe/build.sh']
        try:
            subprocess.run(command, check=True)
        except KeyboardInterrupt:
            remove = [runtime, 'rm', '--force']
            if runtime == 'podman':
                remove.append('--ignore')
            result = subprocess.run([*remove, container], capture_output=True, text=True)
            if result.returncode and 'no such container' not in result.stderr.lower():
                print(f'Baufehler: Bereinigung von {container} fehlgeschlagen: {result.stderr.strip()}', file=sys.stderr)
            raise

        if set(item.name for item in stage.iterdir()) != {'files'}:
            raise package2.PackageError('Bauprodukte ausschließlich unter /output/files ablegen.')
        hashes = collect_files(stage / 'files', spec)
        (stage / 'manifest.toml').write_bytes(spec_path.read_bytes())
        with (stage / 'build-info.json').open('x', encoding='utf-8', newline='\n') as stream:
            json.dump({'image': image, 'sources': sources, 'recipe': recipe_hashes,
                       'license': spec['license'], 'files': hashes}, stream, indent=2, sort_keys=True)
            stream.write('\n')
        package2.rename_no_replace(stage, output)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('recipe', type=Path, help='Verzeichnis mit package.toml, manifest.toml und build.sh')
    parser.add_argument('-o', '--output', type=Path, required=True)
    parser.add_argument('--runtime', choices=('podman', 'docker'), default='podman')
    args = parser.parse_args()
    if not sys.platform.startswith('linux'):
        parser.error('Für native Paketbauten Linux oder WSL verwenden.')
    try:
        build(args.recipe, args.output, args.runtime)
    except subprocess.CalledProcessError as error:
        print(f'Baufehler: Container beendet mit Status {error.returncode}.', file=sys.stderr)
        return 1
    except (package2.PackageError, OSError, ValueError) as error:
        print(f'Baufehler: {error}', file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130
    return 0


def interrupt(_signum, _frame):
    raise KeyboardInterrupt


if __name__ == '__main__':
    for name in ('SIGTERM', 'SIGHUP'):
        if hasattr(signal, name):
            signal.signal(getattr(signal, name), interrupt)
    sys.exit(main())
