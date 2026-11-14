#!/usr/bin/env python3
"""Isolierte HTTP-/TLS-Fixtures und QEMU-Lauf für die Netzwerkprobe."""

import argparse
import http.server
import json
import os
from pathlib import Path
import socketserver
import shutil
import ssl
import socket
import subprocess
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_QEMU = Path(shutil.which('qemu-system-x86_64') or Path.home() / '.cosmos/tools/qemu/qemu-system-x86_64')
BODY = b'ZehOS network probe\n'


def openssl(*args):
    subprocess.run(['openssl', *map(str, args)], check=True, stdout=subprocess.DEVNULL,
                   stderr=subprocess.PIPE)


def prepare_fixtures():
    base = ROOT / '.tools' / 'network-probe'
    base.mkdir(parents=True, exist_ok=True)
    target = Path(tempfile.mkdtemp(prefix='run-', dir=base)).resolve()
    for name in ('ca', 'other-ca'):
        openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256',
                '-days', '3650', '-addext', 'keyUsage=critical,keyCertSign,cRLSign',
                '-subj', f'/CN=ZehOS network probe {name}',
                '-keyout', target / f'{name}.key', '-out', target / f'{name}.pem')
    (target / 'index.txt').write_text('', encoding='ascii')
    (target / 'serial').write_text('1000\n', encoding='ascii')
    config = target / 'openssl.cnf'
    config.write_text(f'''[ ca ]
default_ca = local
[ local ]
database = {target}/index.txt
new_certs_dir = {target}
certificate = {target}/ca.pem
private_key = {target}/ca.key
serial = {target}/serial
default_md = sha256
default_days = 3650
policy = policy
copy_extensions = copy
x509_extensions = server
unique_subject = no
[ server ]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature,keyEncipherment
extendedKeyUsage = serverAuth
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid,issuer
[ policy ]
commonName = supplied
''', encoding='ascii')
    for name, san in [('valid', 'DNS:localhost,IP:10.0.2.2'),
                      ('wrong-name', 'DNS:wrong.invalid'),
                      ('untrusted', 'DNS:localhost,IP:10.0.2.2'),
                      ('expired', 'DNS:localhost,IP:10.0.2.2'),
                      ('future', 'DNS:localhost,IP:10.0.2.2')]:
        key, csr, cert = (target / f'{name}.{ext}' for ext in ('key', 'csr', 'pem'))
        openssl('req', '-new', '-newkey', 'rsa:2048', '-nodes', '-sha256',
                '-subj', f'/CN={name}', '-addext', f'subjectAltName={san}',
                '-keyout', key, '-out', csr)
        if name == 'untrusted':
            ext = target / 'untrusted.ext'
            ext.write_text(f'subjectAltName={san}\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid,issuer\n', encoding='ascii')
            openssl('x509', '-req', '-in', csr, '-CA', target / 'other-ca.pem',
                    '-CAkey', target / 'other-ca.key', '-CAcreateserial', '-days', '3650',
                    '-sha256', '-extfile', ext, '-out', cert)
        else:
            dates = []
            if name == 'expired':
                dates = ['-startdate', '20200101000000Z', '-enddate', '20210101000000Z']
            elif name == 'future':
                dates = ['-startdate', '20400101000000Z', '-enddate', '20410101000000Z']
            openssl('ca', '-batch', '-config', config, '-in', csr, '-out', cert, *dates)
    return target


class FixtureServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


class SilentServer(socketserver.ThreadingMixIn, socketserver.TCPServer):
    daemon_threads = True
    allow_reuse_address = True


class SilentHandler(socketserver.BaseRequestHandler):
    def handle(self):
        self.server.stop_event.wait(120)


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, format, *args):
        pass

    def do_GET(self):
        try:
            if self.path == '/stall':
                self.server.stop_event.wait(120)
                return
            if self.path == '/partial':
                self.wfile.write(b'HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\nshort')
                self.close_connection = True
                return
            if self.path == '/oversize':
                self.wfile.write(b'HTTP/1.1 200 OK\r\nContent-Length: 10485760\r\nConnection: close\r\n\r\n')
                self.close_connection = True
                return
            if self.path == '/encoding':
                self.wfile.write(b'HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 4\r\nConnection: close\r\n\r\nnope')
                self.close_connection = True
                return
            if self.path == '/slow':
                self.wfile.write(b'HTTP/1.1 200 OK\r\nContent-Length: 20\r\nConnection: close\r\n\r\n')
                self.wfile.flush()
                for byte in BODY:
                    if self.server.stop_event.wait(1):
                        break
                    self.wfile.write(bytes([byte]))
                    self.wfile.flush()
                self.close_connection = True
                return
            if self.path != '/ok':
                self.send_error(404)
                return
            self.wfile.write(b'HTTP/1.1 200 OK\r\nContent-Length: 20\r\nConnection: close\r\n\r\n')
            self.wfile.flush()
            for fragment in (BODY[:5], BODY[5:13], BODY[13:]):
                self.wfile.write(fragment)
                self.wfile.flush()
                time.sleep(0.05)
            self.close_connection = True
        except (BrokenPipeError, ConnectionResetError, ssl.SSLError, OSError):
            self.close_connection = True


class Monitor:
    def __init__(self, path):
        self.connection = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.connection.settimeout(5)
        self.connection.connect(str(path))
        self.stream = self.connection.makefile('rb')
        self.stream.readline()
        self.command('qmp_capabilities')

    def command(self, name, arguments=None):
        request = {'execute': name}
        if arguments is not None:
            request['arguments'] = arguments
        self.connection.sendall((json.dumps(request) + '\n').encode())
        while True:
            line = self.stream.readline()
            if not line:
                raise RuntimeError('QMP-Verbindung geschlossen')
            response = json.loads(line)
            if 'error' in response:
                raise RuntimeError(f'QMP-Fehler: {response["error"]}')
            if 'return' in response:
                return response['return']

    def key(self, key):
        result = self.command('human-monitor-command', {'command-line': f'sendkey {key} 30'})
        if result:
            raise RuntimeError(f'QMP sendkey: {result}')
        time.sleep(0.06)

    def write_line(self, value):
        keys = {' ': 'spc', '.': 'dot', '/': 'slash', ':': 'shift-semicolon',
                '_': 'shift-minus', '-': 'minus'}
        for char in value:
            key = keys.get(char, f'shift-{char.lower()}' if char.isupper() else char)
            self.key(key)
        self.key('ret')
        time.sleep(2)

    def screenshot(self, path):
        self.command('screendump', {'filename': str(path)})
        print(f'Bild zur Sichtpruefung: {path}', flush=True)

    def close(self):
        self.stream.close()
        self.connection.close()


def smoke(qemu, socket_path, serial, prefix, recovery, timeout, provision=False):
    deadline = time.monotonic() + timeout
    marker = 'Textshell ist bereit.' if recovery else 'Desktop ist bereit.'
    while time.monotonic() < deadline:
        output = serial.read_text(encoding='utf-8', errors='replace')
        if marker in output and socket_path.exists():
            break
        if qemu.poll() is not None:
            raise RuntimeError(f'QEMU vor Oberflaechenpruefung beendet; Protokoll: {serial}')
        time.sleep(0.1)
    else:
        raise TimeoutError(f'Oberflaeche nicht bereit; Protokoll: {serial}')
    monitor = Monitor(socket_path)
    try:
        time.sleep(1)
        monitor.screenshot(prefix.with_name(prefix.name + '-boot.ppm'))
        if not recovery:
            monitor.key('esc')
            time.sleep(0.5)
        if provision:
            monitor.write_line('disk init sata0')
            time.sleep(12)
            monitor.screenshot(prefix.with_name(prefix.name + '-disk-init.ppm'))
            monitor.write_line('disk mount sata0')
            time.sleep(3)
            monitor.screenshot(prefix.with_name(prefix.name + '-setup-prompt.ppm'))
            monitor.write_line('netadmin')
            monitor.write_line('testpassword12345')
            monitor.write_line('testpassword12345')
            time.sleep(12)
            monitor.screenshot(prefix.with_name(prefix.name + '-provision.ppm'))
            monitor.key('f4')
            time.sleep(2)
            monitor.screenshot(prefix.with_name(prefix.name + '-unlocked-desktop.ppm'))
            monitor.key('alt-f1')
            monitor.key('ctrl-a')
            monitor.write_line('terminal')
            time.sleep(2)
            monitor.write_line('fetch http://10.0.2.2:18080/stall')
            monitor.screenshot(prefix.with_name(prefix.name + '-terminal-fetch.ppm'))
            monitor.key('alt-f1')
            monitor.key('ctrl-a')
            monitor.write_line('terminal')
            time.sleep(2)
            monitor.write_line('echo NETWORK_UI_SECOND_TERMINAL')
            monitor.screenshot(prefix.with_name(prefix.name + '-second-terminal.ppm'))
            monitor.key('esc')
            time.sleep(1)
        monitor.write_line('echo NETWORK_UI_ALIVE')
        monitor.screenshot(prefix.with_name(prefix.name + '-shell.ppm'))
        monitor.write_line('fetch http://10.0.2.2:18080/stall')
        monitor.write_line('echo NETWORK_UI_DURING_FETCH')
        monitor.screenshot(prefix.with_name(prefix.name + '-during-fetch.ppm'))
        monitor.write_line('net cancel')
        monitor.write_line('net status')
        monitor.screenshot(prefix.with_name(prefix.name + '-cancel.ppm'))
        monitor.key('f4')
        time.sleep(1)
        monitor.screenshot(prefix.with_name(prefix.name + '-desktop.ppm'))
        monitor.key('esc')
        time.sleep(0.5)
        monitor.screenshot(prefix.with_name(prefix.name + '-returned.ppm'))
        monitor.write_line('shutdown')
        try:
            qemu.wait(timeout=10)
            print(f'QEMU beendet: {qemu.returncode}', flush=True)
        except subprocess.TimeoutExpired:
            print('Gast hat sich nicht selbst beendet; Oberflaechenbilder pruefen.', flush=True)
    finally:
        monitor.close()


def run(args):
    fixtures = args.fixtures.resolve()
    serial_fd, serial_name = tempfile.mkstemp(prefix='serial-', suffix='.log', dir=fixtures)
    os.close(serial_fd)
    serial = Path(serial_name)
    stop_event = threading.Event()
    servers = []
    qemu = None
    socket_path = fixtures / (serial.stem + '.sock')
    disk = None
    if args.provision_desktop:
        disk_fd, disk_name = tempfile.mkstemp(prefix='test-disk-', suffix='.img', dir=fixtures)
        try:
            os.ftruncate(disk_fd, 64 * 1024 * 1024)
        finally:
            os.close(disk_fd)
        disk = Path(disk_name)
        disk.with_suffix('.owned').write_text('ZehOS network probe disk\n', encoding='ascii')
        print(f'Neue isolierte Testplatte: {disk}', flush=True)
    elif args.test_disk:
        disk = args.test_disk.resolve()
        if (disk.parent != fixtures or not disk.name.startswith('test-disk-')
                or disk.suffix != '.img' or not disk.is_file()
                or disk.stat().st_size != 64 * 1024 * 1024
                or not disk.with_suffix('.owned').is_file()
                or disk.with_suffix('.owned').read_text(encoding='ascii') != 'ZehOS network probe disk\n'):
            raise ValueError('Testplatte muss eine vom Runner erzeugte Platte im Fixture-Verzeichnis sein')
    try:
        for port, name in [(18080, None), (18443, 'valid'), (18444, 'wrong-name'),
                           (18445, 'untrusted'), (18446, 'expired'), (18447, 'future')]:
            server = FixtureServer(('127.0.0.1', port), Handler)
            server.stop_event = stop_event
            try:
                if name:
                    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
                    context.load_cert_chain(fixtures / f'{name}.pem', fixtures / f'{name}.key')
                    server.socket = context.wrap_socket(server.socket, server_side=True)
                threading.Thread(target=server.serve_forever, daemon=True).start()
                servers.append(server)
            except Exception:
                server.server_close()
                raise
        silent = SilentServer(('127.0.0.1', 18448), SilentHandler)
        silent.stop_event = stop_event
        threading.Thread(target=silent.serve_forever, daemon=True).start()
        servers.append(silent)
        command = [str(args.qemu), '-machine', 'q35', '-cpu', 'max', '-m', '512',
                   '-vga', 'std', '-netdev', 'user,id=net0', '-device', 'e1000e,netdev=net0',
                   '-cdrom', str(args.iso.resolve()), '-boot', 'd', '-serial', f'file:{serial}',
                   '-monitor', 'none', '-display', 'none', '-action', 'shutdown=poweroff,reboot=reset']
        share = args.qemu.resolve().parent / 'share' / 'qemu'
        if share.is_dir():
            command[1:1] = ['-L', str(share)]
        if disk:
            command.extend(['-drive', f'if=none,id=testdisk,format=raw,file={disk}',
                            '-device', 'ich9-ahci,id=testahci',
                            '-device', 'ide-hd,drive=testdisk,bus=testahci.0'])
        if args.smoke or args.boot_smoke:
            command.extend(['-qmp', f'unix:{socket_path},server=on,wait=off'])
        qemu = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        if args.boot_smoke:
            print('Boot-/Oberflaechenablauf ohne Netzwerkprobe; Screenshots manuell pruefen.', flush=True)
            smoke(qemu, socket_path, serial, serial.with_suffix(''), args.recovery, args.timeout, args.provision_desktop)
            return
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            output = serial.read_text(encoding='utf-8', errors='replace') if serial.exists() else ''
            if 'NETWORK_PROBE FAIL' in output:
                raise RuntimeError(f'Gast meldet NETWORK_PROBE FAIL; siehe {serial}')
            if 'NETWORK_PROBE PASS' in output:
                print(f'NETWORK_PROBE PASS; Protokoll: {serial}', flush=True)
                if args.smoke:
                    smoke(qemu, socket_path, serial, serial.with_suffix(''), args.recovery, args.timeout, args.provision_desktop)
                return
            if qemu.poll() is not None:
                error = qemu.stderr.read().decode(errors='replace')
                raise RuntimeError(f'QEMU beendet (Code {qemu.returncode}): {error}; Protokoll: {serial}')
            time.sleep(0.1)
        raise TimeoutError(f'Keine erfolgreiche Gastprobe nach {args.timeout}s; Protokoll: {serial}')
    finally:
        stop_event.set()
        socket_path.unlink(missing_ok=True)
        if qemu and qemu.poll() is None:
            qemu.terminate()
            try:
                qemu.wait(timeout=5)
            except subprocess.TimeoutExpired:
                qemu.kill()
                qemu.wait()
        if qemu and qemu.stderr:
            qemu.stderr.close()
        for server in servers:
            server.shutdown()
            server.server_close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prepare-fixtures', action='store_true', help='Zertifikate erzeugen und Verzeichnis ausgeben')
    parser.add_argument('--fixtures', type=Path, help='Vorbereitetes Fixture-Verzeichnis')
    parser.add_argument('--iso', type=Path, help='ISO mit aktivierter Netzwerkprobe')
    parser.add_argument('--qemu', type=Path, default=DEFAULT_QEMU)
    parser.add_argument('--timeout', type=float, default=240)
    parser.add_argument('--smoke', action='store_true', help='Nach PASS Oberflaechenpruefung mit Bildern')
    parser.add_argument('--boot-smoke', action='store_true', help='Boot-/Oberflaechenablauf ohne Netzwerkprobe; Bilder manuell pruefen')
    parser.add_argument('--recovery', action='store_true', help='Recovery-ISO beginnt in der Textshell')
    parser.add_argument('--provision-desktop', action='store_true', help='Neue isolierte Testplatte einrichten; Recovery-Build erforderlich')
    parser.add_argument('--test-disk', type=Path, help='Zuvor vom Runner angelegte Testplatte verwenden')
    args = parser.parse_args()
    if args.provision_desktop and (not args.smoke or not args.recovery or args.test_disk):
        parser.error('--provision-desktop erfordert --smoke --recovery und eine neue Testplatte')
    if args.smoke and args.boot_smoke:
        parser.error('--smoke und --boot-smoke schliessen sich aus')
    if args.recovery and not (args.smoke or args.boot_smoke):
        parser.error('--recovery erfordert --smoke oder --boot-smoke')
    if args.prepare_fixtures:
        if args.fixtures or args.iso:
            parser.error('--prepare-fixtures kann nicht mit --fixtures oder --iso kombiniert werden')
        print(prepare_fixtures())
        return
    if not args.fixtures or not args.iso or args.timeout <= 0:
        parser.error('--fixtures, --iso und ein positiver --timeout sind erforderlich')
    if not args.fixtures.is_dir() or not args.iso.is_file() or not args.qemu.is_file():
        parser.error('Fixture-Verzeichnis, ISO oder QEMU fehlt')
    run(args)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError, TimeoutError) as error:
        print(f'Netzwerkprobe fehlgeschlagen: {error}', file=sys.stderr)
        sys.exit(1)
