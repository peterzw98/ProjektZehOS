#!/usr/bin/env python3
"""Kleiner Minecraft-Classic-Server (Protokoll 7, ohne CPE) zum Pruefen des ClassiCube-Pakets.

Nur Standardbibliothek. Kann Login, flache Welt senden, Spawn, Positionen, Blockaenderungen und Chat an alle anderen
Clients weitergeben. Jedes Ereignis erscheint als JSON-Zeile auf stdout. Nur fuer lokale Tests: keine Anmeldung,
kein mppass, keine Rechte. Ohne --host lauscht er nur auf 127.0.0.1; ein QEMU-Gast mit Usermode-Netz erreicht ihn
dort als 10.0.2.2.
"""
import argparse
import gzip
import json
import socket
import struct
import sys
import threading
import time

PROTOCOL_VERSION = 7
# Paketgroessen einschliesslich Opcode (Client -> Server)
CLIENT_SIZES = {0x00: 131, 0x05: 9, 0x08: 10, 0x0D: 66}
# Paketgroessen einschliesslich Opcode (Server -> Client), fuer Testclients
SERVER_SIZES = {0x00: 131, 0x01: 1, 0x02: 1, 0x03: 1028, 0x04: 7, 0x06: 8, 0x07: 74, 0x08: 10, 0x0C: 2, 0x0D: 66,
                0x0E: 65}
SELF_ID = 0xFF  # -1: der eigene Spieler im Spawn-/Positionspaket
BEDROCK, STONE, DIRT, GRASS, AIR = 7, 1, 3, 2, 0


def text(value: str) -> bytes:
    """Classic-Zeichenkette: 64 Byte ASCII, mit Leerzeichen aufgefuellt."""
    return value.encode("ascii", "replace")[:64].ljust(64, b" ")


def untext(raw: bytes) -> str:
    return raw.decode("ascii", "replace").rstrip(" ")


def read_exact(connection: socket.socket, count: int) -> bytes:
    data = b""
    while len(data) < count:
        chunk = connection.recv(count - len(data))
        if not chunk:
            raise ConnectionError("Verbindung beendet")
        data += chunk
    return data


class World:
    """Flache Welt: Grundgestein, Stein, Erde, Gras in Hoehe ground; darueber Luft."""

    def __init__(self, width: int = 64, height: int = 32, length: int = 64, ground: int = 15):
        self.width, self.height, self.length, self.ground = width, height, length, ground
        layer = width * length
        self.blocks = bytearray(BEDROCK.to_bytes(1, "big") * layer + bytes([STONE]) * layer * (ground - 3)
                                + bytes([DIRT]) * layer * 2 + bytes([GRASS]) * layer
                                + bytes([AIR]) * layer * (height - ground - 1))

    def index(self, x: int, y: int, z: int) -> int | None:
        if 0 <= x < self.width and 0 <= y < self.height and 0 <= z < self.length:
            return (y * self.length + z) * self.width + x
        return None

    def get(self, x: int, y: int, z: int) -> int:
        position = self.index(x, y, z)
        return AIR if position is None else self.blocks[position]

    def set(self, x: int, y: int, z: int, block: int) -> bool:
        position = self.index(x, y, z)
        if position is None:
            return False
        self.blocks[position] = block
        return True

    def packets(self) -> bytes:
        """Level Initialize, Datenstuecke zu 1024 Byte und Level Finalize."""
        data = gzip.compress(struct.pack(">I", len(self.blocks)) + bytes(self.blocks))
        out = bytearray(b"\x02")
        for offset in range(0, len(data), 1024):
            chunk = data[offset:offset + 1024]
            percent = min(100, (offset + len(chunk)) * 100 // len(data))
            out += struct.pack(">BH", 0x03, len(chunk)) + chunk.ljust(1024, b"\0") + bytes([percent])
        out += struct.pack(">Bhhh", 0x04, self.width, self.height, self.length)
        return bytes(out)

    def spawn(self) -> tuple[int, int, int]:
        """Blockmitte nahe der Weltmitte in Augenhoehe ueber dem Gras (Festkomma, 32 Einheiten je Block).

        Nicht auf eine Blockkante: Steht das Auge genau auf x=32.0, beginnt ClassiCubes Zielstrahl im Nachbarblock und
        meldet beim Blick nach unten eine Seitenflaeche; Setzen landet dann im Boden und unterbleibt (Gast 17.11.2026).
        """
        return self.width * 16 + 16, (self.ground + 1) * 32 + 51, self.length * 16 + 16


class Client:
    def __init__(self, connection: socket.socket, address, player_id: int):
        self.connection, self.address, self.id = connection, address, player_id
        self.name = ""
        self.position = (0, 0, 0, 0, 0)
        self.lock = threading.Lock()
        self.joined = False

    def send(self, data: bytes) -> None:
        with self.lock:
            self.connection.sendall(data)


class ClassicServer:
    def __init__(self, host: str = "127.0.0.1", port: int = 0, world: World | None = None, name: str = "ZehOS-Test",
                 on_event=None):
        self.world = world or World()
        self.name = name
        self.on_event = on_event or (lambda event: None)
        self.listener = socket.create_server((host, port))
        self.port = self.listener.getsockname()[1]
        self.clients: dict[int, Client] = {}
        self.lock = threading.Lock()
        self.events: list[dict] = []
        self.changed = threading.Condition(self.lock)
        self.running = True
        self.thread = threading.Thread(target=self._accept, daemon=True)

    def start(self) -> "ClassicServer":
        self.thread.start()
        return self

    def stop(self) -> None:
        """Simuliert einen Serverausfall: Annahme beenden und alle Verbindungen sofort schliessen."""
        self.running = False
        self.listener.close()
        with self.lock:
            clients = list(self.clients.values())
        for client in clients:
            try:
                client.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            client.connection.close()

    def event(self, kind: str, **values) -> None:
        entry = {"type": kind, "time": round(time.time(), 3), **values}
        with self.changed:
            self.events.append(entry)
            self.changed.notify_all()
        self.on_event(entry)

    def wait_for(self, predicate, timeout: float, start: int = 0) -> dict:
        """Wartet auf das erste Ereignis ab Index start, das predicate erfuellt; sonst TimeoutError."""
        deadline = time.monotonic() + timeout
        with self.changed:
            while True:
                for entry in self.events[start:]:
                    if predicate(entry):
                        return entry
                left = deadline - time.monotonic()
                if left <= 0:
                    raise TimeoutError("Ereignis nicht eingetroffen")
                self.changed.wait(left)

    def mark(self) -> int:
        with self.lock:
            return len(self.events)

    def broadcast(self, data: bytes, skip: Client | None = None) -> None:
        with self.lock:
            clients = [client for client in self.clients.values() if client.joined and client is not skip]
        for client in clients:
            try:
                client.send(data)
            except OSError:
                pass  # Der Lesefaden dieses Clients bemerkt das Ende und raeumt auf.

    def set_block(self, x: int, y: int, z: int, block: int) -> None:
        """Setzt einen Block serverseitig und meldet ihn allen Clients."""
        if self.world.set(x, y, z, block):
            self.broadcast(struct.pack(">BhhhB", 0x06, x, y, z, block))

    def _accept(self) -> None:
        while self.running:
            try:
                connection, address = self.listener.accept()
            except OSError:
                return
            with self.lock:
                free = next(i for i in range(127) if i not in self.clients)
                client = Client(connection, address, free)
                self.clients[free] = client
            threading.Thread(target=self._serve, args=(client,), daemon=True).start()

    def _serve(self, client: Client) -> None:
        try:
            self._login(client)
            while True:
                opcode = read_exact(client.connection, 1)[0]
                if opcode not in CLIENT_SIZES or opcode == 0x00:
                    self.event("invalid", player=client.name, opcode=opcode)
                    client.send(b"\x0e" + text(f"Unbekanntes Paket {opcode}"))
                    raise ConnectionError(f"Unbekanntes Paket {opcode}")
                self._handle(client, opcode, read_exact(client.connection, CLIENT_SIZES[opcode] - 1))
        except (ConnectionError, OSError) as error:
            reason = str(error)
        finally:
            with self.lock:
                self.clients.pop(client.id, None)
            client.connection.close()
        if client.joined:
            self.broadcast(struct.pack(">Bb", 0x0C, client.id))
        self.event("disconnect", player=client.name, reason=reason)

    def _login(self, client: Client) -> None:
        packet = read_exact(client.connection, 131)
        if packet[0] != 0x00 or packet[1] != PROTOCOL_VERSION:
            client.send(b"\x0e" + text("Nur Protokoll 7"))
            raise ConnectionError(f"Login ungueltig: {packet[:2].hex()}")
        client.name = untext(packet[2:66])
        self.event("login", player=client.name, key=untext(packet[66:130]), magic=packet[130],
                   address=client.address[0])
        client.send(struct.pack(">BB", 0x00, PROTOCOL_VERSION) + text(self.name) + text("Lokaler Testserver") + b"\x00")
        client.send(self.world.packets())
        x, y, z = self.world.spawn()
        client.position = (x, y, z, 0, 0)
        client.send(struct.pack(">Bb", 0x07, -1) + text(client.name) + struct.pack(">hhhBB", x, y, z, 0, 0))
        with self.lock:
            others = [other for other in self.clients.values() if other.joined]
            client.joined = True
        for other in others:
            client.send(self.spawn_packet(other))
        self.broadcast(self.spawn_packet(client), skip=client)
        self.event("joined", player=client.name, id=client.id)

    @staticmethod
    def spawn_packet(client: Client) -> bytes:
        x, y, z, yaw, pitch = client.position
        return struct.pack(">Bb", 0x07, client.id) + text(client.name) + struct.pack(">hhhBB", x, y, z, yaw, pitch)

    def _handle(self, client: Client, opcode: int, body: bytes) -> None:
        if opcode == 0x05:
            x, y, z, mode, block = struct.unpack(">hhhBB", body)
            placed = block if mode == 1 else AIR
            # An alle, auch den Absender: Classic-Server bestaetigen so die Aenderung.
            self.set_block(x, y, z, placed)
            self.event("block", player=client.name, x=x, y=y, z=z, mode=mode, block=block)
        elif opcode == 0x08:
            _, x, y, z, yaw, pitch = struct.unpack(">BhhhBB", body)
            if (x, y, z, yaw, pitch) != client.position:
                client.position = (x, y, z, yaw, pitch)
                self.broadcast(struct.pack(">BbhhhBB", 0x08, client.id, x, y, z, yaw, pitch), skip=client)
                self.event("position", player=client.name, x=x, y=y, z=z, yaw=yaw, pitch=pitch)
        elif opcode == 0x0D:
            message = untext(body[1:])
            self.broadcast(struct.pack(">Bb", 0x0D, client.id) + text(f"{client.name}: {message}"))
            self.event("chat", player=client.name, message=message)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=25565)
    args = parser.parse_args()
    server = ClassicServer(args.host, args.port, on_event=lambda event: print(json.dumps(event), flush=True))
    print(json.dumps({"type": "listening", "host": args.host, "port": server.port}), flush=True)
    server.start()
    try:
        while server.thread.is_alive():
            server.thread.join(1)
    except KeyboardInterrupt:
        server.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
