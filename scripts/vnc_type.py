#!/usr/bin/env python3
"""Kleiner RFB-Client für Tastatureingaben und Bildschirmfotos."""
import argparse
import os
import socket
import struct
import sys
import time

# DES-Tabellen nach FIPS 46-3; VNC kehrt zusätzlich die Bits jedes Schlüsselbytes um.
IP = (58,50,42,34,26,18,10,2,60,52,44,36,28,20,12,4,62,54,46,38,30,22,14,6,64,56,48,40,32,24,16,8,57,49,41,33,25,17,9,1,59,51,43,35,27,19,11,3,61,53,45,37,29,21,13,5,63,55,47,39,31,23,15,7)
FP = (40,8,48,16,56,24,64,32,39,7,47,15,55,23,63,31,38,6,46,14,54,22,62,30,37,5,45,13,53,21,61,29,36,4,44,12,52,20,60,28,35,3,43,11,51,19,59,27,34,2,42,10,50,18,58,26,33,1,41,9,49,17,57,25)
E = (32,1,2,3,4,5,4,5,6,7,8,9,8,9,10,11,12,13,12,13,14,15,16,17,16,17,18,19,20,21,20,21,22,23,24,25,24,25,26,27,28,29,28,29,30,31,32,1)
P = (16,7,20,21,29,12,28,17,1,15,23,26,5,18,31,10,2,8,24,14,32,27,3,9,19,13,30,6,22,11,4,25)
PC1 = (57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35,27,19,11,3,60,52,44,36,63,55,47,39,31,23,15,7,62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,28,20,12,4)
PC2 = (14,17,11,24,1,5,3,28,15,6,21,10,23,19,12,4,26,8,16,7,27,20,13,2,41,52,31,37,47,55,30,40,51,45,33,48,44,49,39,56,34,53,46,42,50,36,29,32)
SHIFTS = (1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1)
SBOX = (
(14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13),
(15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,14,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9),
(10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12),
(7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,1,13,8,9,4,5,11,12,7,2,14),
(2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3),
(12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13),
(4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12),
(13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11))


def permute(value, width, table):
    result = 0
    for bit in table:
        result = (result << 1) | ((value >> (width - bit)) & 1)
    return result


def vnc_response(password, challenge):
    try:
        key = password.encode('latin-1')[:8].ljust(8, b'\0')
    except UnicodeEncodeError as exc:
        raise ValueError('Das VNC-Passwort muss aus Latin-1-Zeichen bestehen') from exc
    if len(challenge) != 16:
        raise ValueError('Die VNC-Challenge muss 16 Bytes lang sein')
    key = bytes(int(f'{b:08b}'[::-1], 2) for b in key)
    key56 = permute(int.from_bytes(key, 'big'), 64, PC1)
    c, d = key56 >> 28, key56 & 0xfffffff
    keys = []
    for shift in SHIFTS:
        c = ((c << shift) | (c >> (28 - shift))) & 0xfffffff
        d = ((d << shift) | (d >> (28 - shift))) & 0xfffffff
        keys.append(permute((c << 28) | d, 56, PC2))
    result = b''
    for offset in (0, 8):
        block = permute(int.from_bytes(challenge[offset:offset+8], 'big'), 64, IP)
        left, right = block >> 32, block & 0xffffffff
        for subkey in keys:
            expanded = permute(right, 32, E) ^ subkey
            substituted = 0
            for i, box in enumerate(SBOX):
                six = (expanded >> (42 - 6 * i)) & 63
                row = ((six & 32) >> 4) | (six & 1)
                substituted = (substituted << 4) | box[row * 16 + ((six >> 1) & 15)]
            left, right = right, left ^ permute(substituted, 32, P)
        result += permute((right << 32) | left, 64, FP).to_bytes(8, 'big')
    return result


KEYS = {'return': 0xff0d, 'escape': 0xff1b, 'tab': 0xff09,
        'backspace': 0xff08, 'delete': 0xffff, 'up': 0xff52,
        'down': 0xff54, 'left': 0xff51, 'right': 0xff53}
KEYS.update({f'f{i}': 0xffbd + i for i in range(1, 13)})
MODIFIERS = {'ctrl': 0xffe3, 'alt': 0xffe9, 'shift': 0xffe1}


def char_keysym(char):
    if len(char) == 1 and (' ' <= char <= '~' or char in 'äöüÄÖÜß'):
        return ord(char)
    raise ValueError(f'Nicht unterstütztes Zeichen: {char!r}')


def key_sequence(name):
    parts = name.split('+')
    mods = [part.lower() for part in parts[:-1]]
    if any(part not in MODIFIERS for part in mods) or len(set(mods)) != len(mods):
        raise ValueError(f'Ungültige Tastenkombination: {name!r}')
    key = parts[-1]
    if key.lower() in KEYS:
        symbol = KEYS[key.lower()]
    elif len(key) == 1:
        symbol = char_keysym(key)
    else:
        raise ValueError(f'Unbekannte Taste: {key!r}')
    return [MODIFIERS[part] for part in mods] + [symbol]


def parse_script(content):
    actions = []
    for number, line in enumerate(content.splitlines(), 1):
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        kind, sep, value = line.partition(':')
        if not sep or kind.strip() not in ('text', 'key', 'sleep'):
            raise ValueError(f'Zeile {number}: unbekannte Anweisung')
        kind = kind.strip()
        value = value[1:] if value.startswith(' ') else value
        try:
            if kind == 'text':
                for char in value:
                    char_keysym(char)
            elif kind == 'key':
                value = value.strip()
                key_sequence(value)
            else:
                value = float(value.strip())
                if not 0 <= value < float('inf'):
                    raise ValueError('Ungültige Wartezeit')
        except ValueError as exc:
            raise ValueError(f'Zeile {number}: {exc}') from exc
        actions.append((kind, value))
    return actions


class RFBError(Exception):
    pass


class RFBClient:
    def __init__(self, sock):
        self.sock = sock
        self.width = self.height = 0

    def read(self, size):
        data = bytearray()
        while len(data) < size:
            chunk = self.sock.recv(size - len(data))
            if not chunk:
                raise RFBError('Verbindung unerwartet beendet')
            data.extend(chunk)
        return bytes(data)

    def send(self, data):
        self.sock.sendall(data)

    def reason(self):
        length = struct.unpack('>I', self.read(4))[0]
        if length > 1048576:
            raise RFBError('Fehlermeldung des Servers ist zu lang')
        return self.read(length).decode('utf-8', errors='replace')

    def handshake(self, password=None):
        banner = self.read(12)
        if len(banner) != 12 or banner[:4] != b'RFB ' or banner[7:8] != b'.' or banner[11:] != b'\n' or not (banner[4:7].isdigit() and banner[8:11].isdigit()):
            raise RFBError('Ungültige RFB-Protokollversion')
        major, minor = int(banner[4:7]), int(banner[8:11])
        if major != 3 or minor < 3:
            raise RFBError('Nicht unterstützte RFB-Protokollversion')
        version = 8 if minor >= 8 else 7 if minor >= 7 else 3
        self.send(f'RFB 003.{version:03d}\n'.encode('ascii'))
        if version == 3:
            security = struct.unpack('>I', self.read(4))[0]
            if security == 0:
                raise RFBError('Server lehnt Verbindung ab: ' + self.reason())
        else:
            count = self.read(1)[0]
            if not count:
                raise RFBError('Server lehnt Verbindung ab: ' + self.reason())
            offered = self.read(count)
            security = 2 if password is not None and 2 in offered else 1 if 1 in offered else 2 if 2 in offered else 0
            if not security:
                raise RFBError('Kein unterstützter Sicherheitstyp angeboten')
            self.send(bytes([security]))
        if security == 2:
            if password is None:
                raise RFBError('VNC-Passwort benötigt; --password-env angeben')
            self.send(vnc_response(password, self.read(16)))
        elif security != 1:
            raise RFBError(f'Nicht unterstützter Sicherheitstyp: {security}')
        if security == 2 or (security == 1 and version >= 8):
            status = struct.unpack('>I', self.read(4))[0]
            if status:
                detail = ': ' + self.reason() if version >= 8 else ''
                raise RFBError('Authentifizierung fehlgeschlagen' + detail)
        self.send(b'\x01')
        header = self.read(24)
        self.width, self.height = struct.unpack('>HH', header[:4])
        name_length = struct.unpack('>I', header[20:24])[0]
        if not self.width or not self.height or name_length > 1048576:
            raise RFBError('Ungültige ServerInit-Daten')
        self.read(name_length)

    def event(self, symbol, down):
        self.send(struct.pack('>BBHI', 4, int(down), 0, symbol))

    def press(self, symbols, delay, hold):
        for symbol in symbols:
            self.event(symbol, True)
        time.sleep(hold)
        for symbol in reversed(symbols):
            self.event(symbol, False)
        time.sleep(delay)

    def screenshot(self, path):
        # 32 Bit, Little Endian, True Colour mit je acht Bits für RGB.
        self.send(struct.pack('>B3xBBBBHHHBBB3x', 0, 32, 24, 0, 1, 255, 255, 255, 16, 8, 0))
        self.send(struct.pack('>BBHi', 2, 0, 1, 0))
        self.send(struct.pack('>BBHHHH', 3, 0, 0, 0, self.width, self.height))
        pixels = bytearray(self.width * self.height * 3)
        while True:
            message = self.read(1)[0]
            if message == 2:
                continue
            if message == 3:
                length = struct.unpack('>xxxI', self.read(7))[0]
                if length > 1048576:
                    raise RFBError('ServerCutText ist zu lang')
                self.read(length)
                continue
            if message != 0:
                raise RFBError(f'Unerwartete Servernachricht: {message}')
            count = struct.unpack('>xH', self.read(3))[0]
            for _ in range(count):
                x, y, width, height, encoding = struct.unpack('>HHHHi', self.read(12))
                if encoding != 0 or x + width > self.width or y + height > self.height:
                    raise RFBError('Ungültiges oder nicht unterstütztes Bildrechteck')
                for row in range(height):
                    raw = self.read(width * 4)
                    pos = ((y + row) * self.width + x) * 3
                    for col in range(width):
                        pixels[pos + col*3:pos + col*3 + 3] = raw[col*4:col*4 + 3][::-1]
            with open(path, 'wb') as output:
                output.write(f'P6\n{self.width} {self.height}\n255\n'.encode('ascii'))
                output.write(pixels)
            return


def main(argv=None):
    parser = argparse.ArgumentParser(description='Tasten über VNC an einen ZehOS-Gast senden.')
    parser.add_argument('--host', default='127.0.0.1', help='VNC-Host (Vorgabe: 127.0.0.1)')
    parser.add_argument('--port', type=int, default=5900, help='VNC-Port (Vorgabe: 5900)')
    parser.add_argument('--password-env', help='Name der Umgebungsvariablen mit dem VNC-Passwort')
    parser.add_argument('--delay-ms', type=float, default=50, help='Pause zwischen Tasten in Millisekunden (Vorgabe: 50)')
    parser.add_argument('--hold-ms', type=float, default=50, help='Pause zwischen Drücken und Loslassen in Millisekunden (Vorgabe: 50)')
    parser.add_argument('--timeout', type=float, default=10, help='Socket-Zeitlimit in Sekunden (Vorgabe: 10)')
    parser.add_argument('--screenshot', metavar='DATEI', help='Bildschirm als PPM speichern')
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--text', help='Zu sendender Text')
    group.add_argument('--key', help='Taste oder Kombination, z. B. ctrl+alt+Delete')
    group.add_argument('--script', help='UTF-8-Datei mit text:, key: und sleep:')
    args = parser.parse_args(argv)
    try:
        if not 1 <= args.port <= 65535 or not 0 <= args.delay_ms < float('inf') or not 0 <= args.hold_ms < float('inf') or not 0 < args.timeout < float('inf'):
            raise ValueError('Port, Pausen oder Zeitlimit sind ungültig')
        password = None
        if args.password_env is not None:
            if args.password_env not in os.environ:
                raise ValueError(f'Umgebungsvariable {args.password_env!r} ist nicht gesetzt')
            password = os.environ[args.password_env]
        if args.script is not None:
            with open(args.script, encoding='utf-8') as source:
                actions = parse_script(source.read())
        elif args.text is not None:
            for char in args.text:
                char_keysym(char)
            actions = [('text', args.text)]
        else:
            key_sequence(args.key)
            actions = [('key', args.key)]
        with socket.create_connection((args.host, args.port), timeout=args.timeout) as sock:
            client = RFBClient(sock)
            client.handshake(password)
            for kind, value in actions:
                if kind == 'sleep':
                    time.sleep(value)
                elif kind == 'key':
                    client.press(key_sequence(value), args.delay_ms / 1000, args.hold_ms / 1000)
                else:
                    for char in value:
                        client.press([char_keysym(char)], args.delay_ms / 1000, args.hold_ms / 1000)
            if args.screenshot:
                client.screenshot(args.screenshot)
        return 0
    except (OSError, ValueError, RFBError) as exc:
        print(f'Fehler: {exc}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
