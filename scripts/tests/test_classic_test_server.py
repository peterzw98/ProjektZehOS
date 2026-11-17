import gzip
import importlib.util
import socket
import struct
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('classic_test_server', SCRIPTS / 'classic-test-server.py')
classic = importlib.util.module_from_spec(spec)
spec.loader.exec_module(classic)

# Paketgroessen einschliesslich Opcode (Server -> Client), Protokoll 7
SERVER_SIZES = {0x00: 131, 0x01: 1, 0x02: 1, 0x03: 1028, 0x04: 7, 0x06: 8, 0x07: 74, 0x08: 10, 0x0C: 2, 0x0D: 66,
                0x0E: 65}


class TestClient:
    def __init__(self, port, name):
        self.socket = socket.create_connection(('127.0.0.1', port), timeout=5)
        self.socket.sendall(struct.pack('>BB', 0x00, 7) + classic.text(name) + classic.text('-') + b'\x42')

    def packet(self):
        opcode = classic.read_exact(self.socket, 1)[0]
        return opcode, classic.read_exact(self.socket, SERVER_SIZES[opcode] - 1)

    def until(self, wanted):
        while True:
            opcode, body = self.packet()
            if opcode == wanted:
                return body

    def join(self):
        """Liest Identifikation und Level; liefert (Groesse, Bloecke, eigener Spawn)."""
        ident = self.until(0x00)
        assert ident[0] == 7
        data = b''
        while True:
            opcode, body = self.packet()
            if opcode == 0x03:
                length = struct.unpack('>H', body[:2])[0]
                data += body[2:2 + length]
            elif opcode == 0x04:
                size = struct.unpack('>hhh', body)
                break
        blocks = gzip.decompress(data)
        spawn = self.until(0x07)
        return size, blocks, spawn

    def close(self):
        self.socket.close()


class ClassicTestServerTests(unittest.TestCase):
    def setUp(self):
        self.server = classic.ClassicServer('127.0.0.1', 0, classic.World(16, 8, 16, ground=4)).start()
        self.addCleanup(self.server.stop)

    def client(self, name):
        client = TestClient(self.server.port, name)
        self.addCleanup(client.close)
        return client

    def test_login_sends_level_and_self_spawn(self):
        size, blocks, spawn = self.client('anna').join()
        self.assertEqual(size, (16, 8, 16))
        self.assertEqual(struct.unpack('>I', blocks[:4])[0], 16 * 8 * 16)
        world = blocks[4:]
        self.assertEqual(world[(0 * 16 + 3) * 16 + 5], classic.BEDROCK)
        self.assertEqual(world[(4 * 16 + 3) * 16 + 5], classic.GRASS)
        self.assertEqual(world[(5 * 16 + 3) * 16 + 5], classic.AIR)
        self.assertEqual(spawn[0], 0xFF)
        self.assertEqual(classic.untext(spawn[1:65]), 'anna')
        login = self.server.wait_for(lambda e: e['type'] == 'login', 5)
        self.assertEqual((login['player'], login['magic']), ('anna', 0x42))

    def test_block_change_reaches_both_clients_and_world(self):
        first, second = self.client('anna'), self.client('ben')
        first.join()
        self.server.wait_for(lambda e: e['type'] == 'joined' and e['player'] == 'anna', 5)
        second.join()
        self.server.wait_for(lambda e: e['type'] == 'joined' and e['player'] == 'ben', 5)
        # Platzieren (mode 1) und Abbauen (mode 0) kommen bei beiden an.
        first.socket.sendall(struct.pack('>BhhhBB', 0x05, 2, 5, 3, 1, classic.STONE))
        self.assertEqual(struct.unpack('>hhhB', second.until(0x06)), (2, 5, 3, classic.STONE))
        self.assertEqual(struct.unpack('>hhhB', first.until(0x06)), (2, 5, 3, classic.STONE))
        first.socket.sendall(struct.pack('>BhhhBB', 0x05, 2, 4, 3, 0, classic.STONE))
        self.assertEqual(struct.unpack('>hhhB', second.until(0x06)), (2, 4, 3, classic.AIR))
        self.assertEqual(self.server.world.get(2, 5, 3), classic.STONE)
        self.assertEqual(self.server.world.get(2, 4, 3), classic.AIR)
        event = self.server.wait_for(lambda e: e['type'] == 'block' and e['mode'] == 0, 5)
        self.assertEqual((event['player'], event['x'], event['y'], event['z']), ('anna', 2, 4, 3))

    def test_server_block_and_out_of_range_block(self):
        client = self.client('anna')
        client.join()
        self.server.wait_for(lambda e: e['type'] == 'joined', 5)
        self.server.set_block(1, 6, 1, classic.DIRT)
        self.assertEqual(struct.unpack('>hhhB', client.until(0x06)), (1, 6, 1, classic.DIRT))
        # Ausserhalb der Welt: weder Aenderung noch Absturz des Clientfadens.
        client.socket.sendall(struct.pack('>BhhhBB', 0x05, 99, 1, 1, 1, classic.STONE))
        client.socket.sendall(struct.pack('>BbhhhBB', 0x08, -1, 64, 200, 64, 10, 0))
        self.server.wait_for(lambda e: e['type'] == 'position', 5)

    def test_position_spawn_and_despawn_between_clients(self):
        first, second = self.client('anna'), self.client('ben')
        first.join()
        self.server.wait_for(lambda e: e['type'] == 'joined' and e['player'] == 'anna', 5)
        second.join()
        spawn = first.until(0x07)
        self.assertEqual(classic.untext(spawn[1:65]), 'ben')
        ben = spawn[0]
        second.socket.sendall(struct.pack('>BbhhhBB', 0x08, -1, 100, 200, 300, 64, 32))
        self.assertEqual(struct.unpack('>bhhhBB', first.until(0x08)), (ben, 100, 200, 300, 64, 32))
        event = self.server.wait_for(lambda e: e['type'] == 'position', 5)
        self.assertEqual((event['player'], event['x'], event['y'], event['z']), ('ben', 100, 200, 300))
        second.close()
        self.assertEqual(first.until(0x0C)[0], ben)
        self.server.wait_for(lambda e: e['type'] == 'disconnect' and e['player'] == 'ben', 5)

    def test_stop_closes_connections(self):
        client = self.client('anna')
        client.join()
        self.server.wait_for(lambda e: e['type'] == 'joined', 5)
        self.server.stop()
        with self.assertRaises((ConnectionError, OSError)):
            while True:
                client.packet()
        with self.assertRaises(OSError):
            socket.create_connection(('127.0.0.1', self.server.port), timeout=2).recv(1)

    def test_wrong_protocol_is_rejected(self):
        raw = socket.create_connection(('127.0.0.1', self.server.port), timeout=5)
        self.addCleanup(raw.close)
        raw.sendall(struct.pack('>BB', 0x00, 6) + b' ' * 129)
        self.assertEqual(classic.read_exact(raw, 1), b'\x0e')
        self.server.wait_for(lambda e: e['type'] == 'disconnect', 5)

    def test_text_is_padded_ascii(self):
        self.assertEqual(classic.text('ab'), b'ab' + b' ' * 62)
        self.assertEqual(len(classic.text('x' * 80)), 64)
        self.assertEqual(classic.untext(classic.text('Hallo')), 'Hallo')


if __name__ == '__main__':
    unittest.main()
