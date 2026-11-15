#!/usr/bin/env python3
"""Spielt Eingabeszenarien über den Linux-Prüfhost ab und prüft das letzte Bild anhand der Markerfarben der Testseiten.

Aufruf vom Repository-Stamm: python3 experiments/NetSurf.Wasi/host/run_tests.py --iwasm <iwasm> --host <libzehos_host.so>
Voraussetzung: build.py ist gelaufen. Ereignisse folgen dem poll_event-ABI: <ms> <typ> <code> <x> <y>.
Die Klickpositionen gelten für die Startseite im 632×392-Bild. HTTP-Szenarien laden Seiten von einem lokalen Testserver
über das Netz-ABI des Prüfhosts.
"""

import argparse
import http.server
import os
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

PACKAGE = Path(__file__).resolve().parents[1] / "bin" / "package"
START, TARGET, FORM, LONG = (0x00, 0x50, 0xa0), (0x00, 0x80, 0x00), (0x60, 0x00, 0xa0), (0xa0, 0x60, 0x00)
LONG_BACKGROUND = (0xff, 0xf4, 0xd0)
HTTP_PAGE, HTTP_CHUNKED, HTTP_MISSING = (0x00, 0x60, 0x60), (0x60, 0x60, 0x00), (0x80, 0x00, 0x30)
CLICK_TARGET, CLICK_LONG, CLICK_FORM, CLICK_BROKEN = (80, 209), (80, 230), (80, 251), (80, 272)


def click(at: int, position: tuple[int, int]) -> list[str]:
    x, y = position
    return [f"{at} 5 1 {x} {y}", f"{at + 50} 6 1 {x} {y}"]


def address(at: int, url: str) -> list[str]:
    # Adressleiste anklicken, mit Pos1 und Entf leeren, URL als Texte tippen, Enter (normalisierte Tastencodes 71/83/28).
    events = click(at, (300, 15)) + [f"{at + 200} 1 71 0 0", f"{at + 210} 2 71 0 0"]
    t = at + 300
    for _ in range(32):
        events += [f"{t} 1 83 0 0", f"{t + 5} 2 83 0 0"]
        t += 10
    for char in url:
        events.append(f"{t} 3 {ord(char)} 0 0")
        t += 10
    return events + [f"{t} 1 28 0 0", f"{t + 10} 2 28 0 0"]


def page(colour: tuple[int, int, int], text: str) -> bytes:
    hex_colour = "".join(f"{c:02x}" for c in colour)
    return (f'<!DOCTYPE html><html><head><meta charset="utf-8"><style>body{{background:#{hex_colour}}}</style></head>'
            f"<body><p>{text}</p></body></html>").encode()


# Warteschlange: mehr Stylesheets als max_fetchers (4) und Kernelgrenze (4 Verbindungen). Jedes antwortet verzögert,
# damit alle gleichzeitig angefordert sind; NetSurf startet die übrigen erst, wenn ein Platz frei wird.
QUEUE_PAGE = (0xe0, 0xe0, 0xd0)
QUEUE_MARKERS = [(0x10, 0x30, 0x50), (0x10, 0x50, 0x30), (0x30, 0x10, 0x50),
                 (0x30, 0x50, 0x10), (0x50, 0x10, 0x30), (0x50, 0x30, 0x10)]
QUEUE_DELAY_S = 0.5


def queue_page() -> bytes:
    links = "".join(f'<link rel="stylesheet" href="/warte-{i}.css">' for i in range(len(QUEUE_MARKERS)))
    blocks = "".join(f'<div id="w{i}" style="width:40px;height:20px"></div>' for i in range(len(QUEUE_MARKERS)))
    return page(QUEUE_PAGE, "Warteschlange").replace(b"</head>", f"{links}</head>".encode()).replace(
        b"</body>", f"{blocks}</body>".encode())


def queued_stylesheet(handler: "TestServer") -> None:
    index = int(handler.path[len("/warte-"):-len(".css")])
    colour = "".join(f"{c:02x}" for c in QUEUE_MARKERS[index])
    time.sleep(QUEUE_DELAY_S)
    body = f"#w{index}{{background:#{colour}}}".encode()
    handler.send_response(200)
    handler.send_header("Content-Type", "text/css")
    handler.send_header("Content-Length", str(len(body)))
    handler.end_headers()
    handler.wfile.write(body)


class TestServer(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def send_page(self, status: int, body: bytes, headers: dict[str, str] | None = None) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        if self.path.startswith("/warte-") and self.path.endswith(".css"):
            queued_stylesheet(self)
        elif self.path == "/viele":
            self.send_page(200, queue_page())
        elif self.path == "/seite.html":
            self.send_page(200, page(HTTP_PAGE, "HTTP-Seite"))
        elif self.path == "/umleitung":
            # Relative Weiterleitung über zwei Stufen; NetSurf muss sie selbst zum Ziel auflösen.
            self.send_page(302, b"", {"Location": "zwischen"})
        elif self.path == "/zwischen":
            self.send_page(301, b"", {"Location": "/seite.html"})
        elif self.path == "/chunked.html":
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            body = page(HTTP_CHUNKED, "In Stücken")
            for start in range(0, len(body), 7):
                piece = body[start:start + 7]
                self.wfile.write(b"%x\r\n%s\r\n" % (len(piece), piece))
                self.wfile.flush()
            self.wfile.write(b"0\r\n\r\n")
        elif self.path == "/abbruch":
            # SO_LINGER mit Frist 0 schließt mit RST; der Fetcher muss den Abbruch als Fehler melden.
            self.connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
            self.connection.close()
            self.close_connection = True
        elif not cookie_get(self):
            self.send_page(404, page(HTTP_MISSING, "Nicht gefunden"))

    def log_message(self, format: str, *args: object) -> None:
        pass


def closed_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def http_scenarios(base: str, refused: int) -> list:
    pages = [START, TARGET, FORM, LONG, HTTP_PAGE, HTTP_CHUNKED, HTTP_MISSING]
    return [
        ("http-warteschlange", address(1500, f"{base}/viele"), [QUEUE_PAGE] + QUEUE_MARKERS, [START]),
        ("http-seite", address(1500, f"{base}/seite.html"), [HTTP_PAGE], [START]),
        ("http-umleitung", address(1500, f"{base}/umleitung"), [HTTP_PAGE], [START]),
        ("http-chunked", address(1500, f"{base}/chunked.html"), [HTTP_CHUNKED], [START]),
        ("http-404", address(1500, f"{base}/fehlt.html"), [HTTP_MISSING], [START]),
        ("http-abgelehnt", address(1500, f"http://127.0.0.1:{refused}/"), [], pages),
        ("http-abgebrochen", address(1500, f"{base}/abbruch"), [], pages),
        # Ohne Netzwerkberechtigung (kein ZEHOS_NET) darf die erreichbare Seite nicht laden.
        ("http-ohne-berechtigung", address(1500, f"{base}/seite.html"), [], pages),
        ("https-nicht-unterstuetzt", address(1500, f"https://127.0.0.1:{refused}/"), [], pages),
    ]


# Cookie-Szenario: Set-Cookie an einer Weiterleitung, deren Location hinten Leerzeichen trägt; das Ziel prüft den Cookie.
COOKIE_OK, COOKIE_MISSING = (0x30, 0x90, 0x30), (0x90, 0x30, 0x30)


def cookie_get(handler: TestServer) -> bool:
    if handler.path == "/keks-setzen":
        handler.send_page(302, b"", {"Set-Cookie": "zehos=keks; Path=/", "Location": "/keks-pruefen  "})
    elif handler.path == "/keks-pruefen":
        sent = "zehos=keks" in (handler.headers.get("Cookie") or "")
        handler.send_page(200, page(COOKIE_OK, "Cookie da") if sent else page(COOKIE_MISSING, "Cookie fehlt"))
    else:
        return False
    return True


def cookie_scenarios(base: str) -> list:
    return [("http-cookie", address(1500, f"{base}/keks-setzen"), [COOKIE_OK], [COOKIE_MISSING, HTTP_MISSING, START])]


# Name, Ereignisse, erwartete Farben, verbotene Farben
SCENARIOS = [
    ("start", [], [START], [TARGET]),
    ("klick-ohne-bewegung", click(1500, CLICK_TARGET), [TARGET], [START]),
    ("druck-dann-reset", ["1500 4 0 80 209", "1550 5 1 80 209", "1600 8 0 0 0", "2500 4 0 300 300"], [START], [TARGET]),
    ("reset-dann-klick", ["1500 5 1 80 209", "1600 8 0 0 0", *click(1700, CLICK_FORM)], [FORM], [TARGET]),
    ("kaputter-link", click(1500, CLICK_BROKEN), [], [START, TARGET, FORM, LONG]),
    ("rad-abwaerts", [*click(1500, CLICK_LONG), "2500 7 -5 300 200"], [LONG_BACKGROUND], [LONG]),
    ("adressleiste-entf", address(1500, "file:///pages/ziel.html"), [TARGET], [START]),
]


def colours(path: Path) -> set[tuple[int, int, int]]:
    data = path.read_bytes()
    header, width_height, depth, pixels = data.split(b"\n", 3)
    if header != b"P6" or depth != b"255":
        raise ValueError(f"{path} ist kein P6-Bild")
    return {tuple(pixels[i:i + 3]) for i in range(0, len(pixels), 3)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iwasm", type=Path, required=True)
    parser.add_argument("--host", type=Path, required=True)
    args = parser.parse_args()
    failures = 0
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), TestServer)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{server.server_address[1]}"
    scenarios = SCENARIOS + http_scenarios(base, closed_port()) + cookie_scenarios(base)
    with tempfile.TemporaryDirectory() as work:
        for name, events, expected, forbidden in scenarios:
            script, frame = Path(work) / f"{name}.txt", Path(work) / f"{name}.ppm"
            script.write_text("".join(f"{line}\n" for line in events))
            env = dict(os.environ, ZEHOS_EVENTS=str(script), ZEHOS_PPM=str(frame), ZEHOS_EXIT_MS="6000")
            env.pop("ZEHOS_NET", None)
            if name != "http-ohne-berechtigung":
                env["ZEHOS_NET"] = "1"
            run = subprocess.run([str(args.iwasm), f"--native-lib={args.host.resolve()}", f"--map-dir=/::{PACKAGE}",
                                  "--stack-size=1048576", str(PACKAGE / "netsurf.wasm")],
                                 env=env, capture_output=True, text=True, timeout=60)
            seen = colours(frame) if frame.exists() else set()
            problems = []
            if run.returncode != 0:
                problems.append(f"Exitcode {run.returncode}")
            problems += [f"Farbe {c} fehlt" for c in expected if c not in seen]
            problems += [f"Farbe {c} unerwartet" for c in forbidden if c in seen]
            failures += bool(problems)
            print(f"{'FAIL' if problems else 'ok  '} {name}" + (f": {', '.join(problems)}" if problems else ""))
    server.shutdown()
    print(f"{len(scenarios) - failures}/{len(scenarios)} bestanden")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
