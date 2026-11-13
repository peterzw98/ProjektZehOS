#!/usr/bin/env python3
"""Stellt genau die Update-Dateien eines lokalen oder GitHub-Releases bereit."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import sys
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener, urlopen

MANIFEST = 'update-manifest.txt'
SIGNATURE = 'update-manifest.sig'
KERNEL = re.compile(r'ProjektZeh-(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.elf\Z')


def kernel_name(manifest):
    lines = manifest.decode('ascii').splitlines()
    matches = [line[7:] for line in lines if line.startswith('kernel=')]
    if len(matches) != 1 or not KERNEL.fullmatch(matches[0]):
        raise ValueError('Ungültiger Kernelname im Manifest')
    return matches[0]


def local_files(directory):
    directory = Path(directory).resolve(strict=True)
    def read(name):
        path = (directory / name).resolve(strict=True)
        if path.parent != directory or not path.is_file():
            raise ValueError(f'Ungültige Release-Datei: {name}')
        return path.read_bytes()
    manifest = read(MANIFEST)
    return {MANIFEST: manifest, SIGNATURE: read(SIGNATURE), kernel_name(manifest): read(kernel_name(manifest))}


def github_files(repo):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo):
        raise ValueError('Repository muss owner/name sein')
    headers = {'Accept': 'application/vnd.github+json', 'User-Agent': 'ZehOS-update-mirror'}
    if os.environ.get('GITHUB_TOKEN'):
        headers['Authorization'] = 'Bearer ' + os.environ['GITHUB_TOKEN']
    with urlopen(Request('https://api.github.com/repos/' + repo + '/releases/latest', headers=headers), timeout=30) as response:
        release = json.load(response)
    assets = {asset['name']: asset['url'] for asset in release['assets']}

    class SafeRedirect(HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, message, response_headers, new_url):
            if urlsplit(new_url).scheme != 'https':
                raise ValueError('Unsichere Weiterleitung für GitHub-Asset')
            redirected = super().redirect_request(request, fp, code, message, response_headers, new_url)
            if redirected and urlsplit(new_url).hostname != 'api.github.com':
                redirected.remove_header('Authorization')
            return redirected

    opener = build_opener(SafeRedirect())
    def read(name):
        url = assets[name]
        parsed = urlsplit(url)
        if parsed.scheme != 'https' or parsed.hostname != 'api.github.com':
            raise ValueError('Ungültige GitHub-Assetadresse')
        asset_headers = dict(headers, Accept='application/octet-stream')
        with opener.open(Request(url, headers=asset_headers), timeout=60) as response:
            return response.read()
    manifest = read(MANIFEST)
    name = kernel_name(manifest)
    return {MANIFEST: manifest, SIGNATURE: read(SIGNATURE), name: read(name)}


def handler_for(files):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            name = self.path[1:]
            if not self.path.startswith('/') or name not in files:
                self.send_error(404)
                return
            data = files[name]
            self.send_response(200)
            self.send_header('Content-Type', 'application/octet-stream')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)
    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument('--repo', help='GitHub-Repository owner/name')
    source.add_argument('--dir', help='Lokales Releaseverzeichnis')
    parser.add_argument('--bind', default='0.0.0.0')
    parser.add_argument('--port', type=int, default=8080)
    args = parser.parse_args()
    try:
        files = github_files(args.repo) if args.repo else local_files(args.dir)
        with ThreadingHTTPServer((args.bind, args.port), handler_for(files)) as server:
            print(f'Update-Spiegel auf {args.bind}:{server.server_port}', flush=True)
            server.serve_forever()
    except (OSError, ValueError, KeyError) as error:
        print(f'Update-Spiegel: {error}', file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == '__main__':
    sys.exit(main())
