import os
import queue
import socket
import struct
import tempfile
import threading
import unittest

from scripts import vnc_type as vnc


def read_exact(sock, count):
    data = b''
    while len(data) < count:
        chunk = sock.recv(count - len(data))
        if not chunk:
            raise EOFError('Verbindung beendet')
        data += chunk
    return data


class MemorySocket:
    def __init__(self, incoming, outgoing):
        self.incoming, self.outgoing = incoming, outgoing
        self.buffer = b''

    def sendall(self, data):
        self.outgoing.put(data)

    def recv(self, size):
        if not self.buffer:
            self.buffer = self.incoming.get(timeout=2)
        result, self.buffer = self.buffer[:size], self.buffer[size:]
        return result

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.outgoing.put(b'')


def local_pair():
    left, right = socket.socketpair()
    try:
        left.sendall(b'x')
        if right.recv(1) != b'x':
            raise OSError('Socketpaar nicht nutzbar')
    except OSError:
        left.close()
        right.close()
        first, second = queue.Queue(), queue.Queue()
        return MemorySocket(first, second), MemorySocket(second, first)
    left.settimeout(2)
    right.settimeout(2)
    return left, right


class VncTests(unittest.TestCase):
    def test_vnc_des_vector(self):
        # Referenzwert unabhängig mit OpenSSL DES-ECB und bitgespiegeltem Schlüssel erzeugt.
        self.assertEqual(vnc.vnc_response('password', bytes(range(16))).hex(),
                         'b866924125c8eebb9debc1db61c538e2')
        self.assertEqual(vnc.vnc_response('password-extra', bytes(range(16))),
                         vnc.vnc_response('password', bytes(range(16))))

    def test_keys(self):
        for char in 'aZ09 !äöüÄÖÜß':
            self.assertEqual(vnc.char_keysym(char), ord(char))
        self.assertEqual(vnc.key_sequence('ctrl+alt+Delete'), [0xffe3, 0xffe9, 0xffff])
        self.assertEqual(vnc.key_sequence('shift+F12'), [0xffe1, 0xffc9])
        with self.assertRaises(ValueError):
            vnc.char_keysym('€')
        with self.assertRaises(ValueError):
            vnc.key_sequence('ctrl+ctrl+Return')

    def test_script(self):
        self.assertEqual(vnc.parse_script('# Kommentar\ntext:  Hallo \nkey: Return\nsleep: 2.5\n'),
                         [('text', ' Hallo '), ('key', 'Return'), ('sleep', 2.5)])
        for script in ('sleep: nan', 'sleep: -1', 'key: unbekannt', 'text: €', 'falsch'):
            with self.subTest(script=script), self.assertRaisesRegex(ValueError, 'Zeile 1'):
                vnc.parse_script(script)

    def run_server(self, version, security, screenshot=False):
        client_sock, server_sock = local_pair()
        errors = []
        events = []

        def server():
            try:
                with server_sock as sock:
                    sock.sendall(f'RFB 003.{version:03d}\n'.encode())
                    self.assertEqual(read_exact(sock, 12), f'RFB 003.{version:03d}\n'.encode())
                    if version == 3:
                        sock.sendall(struct.pack('>I', security))
                    else:
                        sock.sendall(bytes([1, security]))
                        self.assertEqual(read_exact(sock, 1), bytes([security]))
                    if security == 2:
                        sock.sendall(bytes(range(16)))
                        self.assertEqual(read_exact(sock, 16).hex(), 'b866924125c8eebb9debc1db61c538e2')
                    if security == 2 or version == 8:
                        sock.sendall(struct.pack('>I', 0))
                    self.assertEqual(read_exact(sock, 1), b'\x01')
                    sock.sendall(struct.pack('>HH16sI', 2, 1, bytes(16), 4) + b'Test')
                    for _ in range(8):
                        events.append(struct.unpack('>BBHI', read_exact(sock, 8)))
                    if screenshot:
                        pixel_format = read_exact(sock, 20)
                        self.assertEqual(pixel_format, struct.pack('>B3xBBBBHHHBBB3x', 0, 32, 24, 0, 1, 255, 255, 255, 16, 8, 0))
                        self.assertEqual(read_exact(sock, 8), struct.pack('>BBHi', 2, 0, 1, 0))
                        self.assertEqual(read_exact(sock, 10), struct.pack('>BBHHHH', 3, 0, 0, 0, 2, 1))
                        sock.sendall(struct.pack('>BBH', 0, 0, 1) + struct.pack('>HHHHi', 0, 0, 2, 1, 0) + bytes([3, 2, 1, 0, 6, 5, 4, 0]))
            except BaseException as exc:
                errors.append(exc)

        thread = threading.Thread(target=server)
        thread.start()
        try:
            with client_sock:
                client = vnc.RFBClient(client_sock)
                client.handshake('password' if security == 2 else None)
                client.press([vnc.char_keysym('ä')], 0, 0)
                client.press(vnc.key_sequence('ctrl+alt+Delete'), 0, 0)
                if screenshot:
                    with tempfile.TemporaryDirectory() as directory:
                        path = os.path.join(directory, 'screen.ppm')
                        client.screenshot(path)
                        with open(path, 'rb') as image:
                            self.assertEqual(image.read(), b'P6\n2 1\n255\n\x01\x02\x03\x04\x05\x06')
        finally:
            thread.join(3)
        self.assertFalse(thread.is_alive())
        if errors:
            raise errors[0]
        self.assertEqual(events, [(4, 1, 0, 0xe4), (4, 0, 0, 0xe4),
                                  (4, 1, 0, 0xffe3), (4, 1, 0, 0xffe9),
                                  (4, 1, 0, 0xffff), (4, 0, 0, 0xffff),
                                  (4, 0, 0, 0xffe9), (4, 0, 0, 0xffe3)])

    def test_handshakes_and_events(self):
        for version in (3, 7, 8):
            for security in (1, 2):
                with self.subTest(version=version, security=security):
                    self.run_server(version, security, screenshot=(version == 8 and security == 2))


if __name__ == '__main__':
    unittest.main()
