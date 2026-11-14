#!/usr/bin/env python3
"""Signiert Manifestbytes oder prüft eine rohe Ed25519-Signatur mit OpenSSL."""
import argparse
import base64
import binascii
from pathlib import Path
import subprocess
import sys
import tempfile


def run(args):
    subprocess.run(args, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('arguments', nargs='+', help='Manifest und PEM; oder verify Manifest Signatur öffentlicher-Schlüssel-Base64')
    args = parser.parse_args().arguments
    try:
        if args[0] == 'verify' and len(args) == 4:
            public = base64.b64decode(args[3], validate=True)
            if len(public) != 32 or Path(args[2]).stat().st_size != 64:
                raise ValueError('Öffentlicher Schlüssel oder Signatur hat eine ungültige Länge')
            with tempfile.TemporaryDirectory() as temp:
                key = Path(temp) / 'public.der'
                key.write_bytes(bytes.fromhex('302a300506032b6570032100') + public)
                run(['openssl', 'pkeyutl', '-verify', '-pubin', '-inkey', str(key), '-keyform', 'DER', '-rawin', '-in', args[1], '-sigfile', args[2]])
        elif len(args) == 2 and args[0] != 'verify':
            manifest = Path(args[0])
            with tempfile.TemporaryDirectory() as temp:
                signature = Path(temp) / 'signature'
                run(['openssl', 'pkeyutl', '-sign', '-inkey', args[1], '-rawin', '-in', str(manifest), '-out', str(signature)])
                data = signature.read_bytes()
                if len(data) != 64:
                    raise ValueError('Der Schlüssel erzeugt keine rohe Ed25519-Signatur')
                manifest.with_name('update-manifest.sig').write_bytes(data)
        else:
            parser.error('Manifest PEM oder verify Manifest Signatur Schlüssel erwartet')
    except (OSError, ValueError, binascii.Error, subprocess.CalledProcessError) as error:
        print(f'Signaturfehler: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
