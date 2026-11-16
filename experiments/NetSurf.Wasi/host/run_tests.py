#!/usr/bin/env python3
"""Spielt Eingabeszenarien über den Linux-Prüfhost ab und prüft das letzte Bild anhand der Markerfarben der Testseiten.

Aufruf vom Repository-Stamm: python3 experiments/NetSurf.Wasi/host/run_tests.py --wasm3 <zehos-wasm3>
(gleicher wasm3-Kern wie ZehOS) oder optional --iwasm <iwasm> --host <libzehos_host.so> (WAMR, nur Legacy-EH).
Voraussetzung: build.py ist gelaufen. Ereignisse folgen dem poll_event-ABI: <ms> <typ> <code> <x> <y>.
Die Klickpositionen gelten für die Startseite im 798×519-Bild (oben links ausgerichtet wie im 632×392-Bild davor). HTTP-Szenarien laden Seiten von einem lokalen Testserver
über das Netz-ABI des Prüfhosts.
"""

import argparse
import http.server
import os
import shutil
import socket
import ssl
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

    def send_page(self, status: int, body: bytes,
                  headers: dict[str, str] | list[tuple[str, str]] | None = None) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        # Liste statt dict, wenn eine Kopfzeile mehrfach vorkommt (zwei Set-Cookie).
        for name, value in (headers.items() if isinstance(headers, dict) else headers or []):
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
        elif not extra_get(self):
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


# Cookie-Szenario: Set-Cookie an einer Weiterleitung; das Ziel prüft den Cookie. Die Leerzeichen hinter Location
# entfernt auch nsurl_join, das Szenario belegt das Trimmen im Fetcher also nicht.
COOKIE_OK, COOKIE_MISSING = (0x30, 0x90, 0x30), (0x90, 0x30, 0x30)


REQUESTED: set[str] = set()
# Jede Anfrage einzeln, damit Szenarien "genau einmal angefragt" prüfen können.
REQUEST_LOG: list[str] = []


REFERERS: list[str] = []


def extra_get(handler: TestServer) -> bool:
    REQUESTED.add(handler.path)
    REQUEST_LOG.append(handler.path)
    REFERERS.append(handler.headers.get("Referer") or "")
    if handler.path == "/marker.css":
        body = b"body{background:#209090}"
        handler.send_response(200)
        handler.send_header("Content-Type", "text/css")
        handler.send_header("Content-Length", str(len(body)))
        handler.end_headers()
        handler.wfile.write(body)
    elif handler.path == "/gemischt":
        handler.send_page(200, mixed_page("/marker.css"))
    elif handler.path in ("/falsches-token", "/kurzes-token"):
        # Kurzes Token: der Vergleich darf nicht hinter die übergebenen Daten lesen.
        value = "0" * 32 if handler.path == "/falsches-token" else "1"
        handler.send_page(200, form_page("/ziel-token", "post", "zehos_bestaetigung", value))
    elif handler.path.startswith("/ziel-get-"):
        form_target(handler, handler.path)
    elif js_get(handler):
        pass
    elif handler.path == "/keks-setzen":
        handler.send_page(302, b"", {"Set-Cookie": "zehos=keks; Path=/", "Location": "/keks-pruefen  "})
    elif handler.path == "/keks-pruefen":
        sent = "zehos=keks" in (handler.headers.get("Cookie") or "")
        handler.send_page(200, page(COOKIE_OK, "Cookie da") if sent else page(COOKIE_MISSING, "Cookie fehlt"))
    else:
        return False
    return True


def cookie_scenarios(base: str) -> list:
    return [("http-cookie", address(1500, f"{base}/keks-setzen"), [COOKIE_OK], [COOKIE_MISSING, HTTP_MISSING, START]),
            # Gegenprobe zu https-gemischt: über HTTP lädt dasselbe Stylesheet und färbt die Seite.
            ("http-gemischt-gegenprobe", address(1500, f"{base}/gemischt"), [MIXED], [START])]


# HTTPS-Szenarien: lokale TLS-Server mit eigener Test-CA (openssl), der Prüfhost vertraut ihr über ZEHOS_TLS_CA.
DOWNGRADE_PAGE, INJECTED = (0xff, 0xf0, 0xd8), (0x90, 0x20, 0x90)
# Gemischte Seite: eigene Farbe, ein geladenes http://-Stylesheet überschriebe sie mit MIXED.
MIXED_PAGE, MIXED = (0x40, 0x40, 0x90), (0x20, 0x90, 0x90)


def mixed_page(stylesheet: str, icon: str = "") -> bytes:
    links = f'<link rel="stylesheet" href="{stylesheet}">' + (f'<link rel="icon" href="{icon}">' if icon else "")
    return page(MIXED_PAGE, "Gemischt").replace(b"</head>", f"{links}</head>".encode())


def make_tls_fixtures(work: Path) -> dict[str, Path]:
    def openssl(*arguments: str) -> None:
        subprocess.run(["openssl", *arguments], cwd=work, check=True, capture_output=True)
    openssl("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", "ca.key", "-out", "ca.pem", "-days", "2",
            "-subj", "/CN=ZehOS Test-CA", "-addext", "basicConstraints=critical,CA:TRUE",
            "-addext", "keyUsage=critical,keyCertSign")
    for name, san in (("gut", "IP:127.0.0.1,DNS:localhost"), ("falsch", "DNS:anders.example")):
        openssl("req", "-newkey", "rsa:2048", "-nodes", "-keyout", f"{name}.key", "-out", f"{name}.csr",
                "-subj", f"/CN={name}")
        (work / f"{name}.ext").write_text(f"subjectAltName={san}\nextendedKeyUsage=serverAuth\n")
        openssl("x509", "-req", "-in", f"{name}.csr", "-CA", "ca.pem", "-CAkey", "ca.key", "-CAcreateserial",
                "-out", f"{name}.pem", "-days", "1", "-extfile", f"{name}.ext")
    return {"ca": work / "ca.pem", "gut": work / "gut.pem", "falsch": work / "falsch.pem"}


def tls_server(work: Path, name: str, http_base: str) -> http.server.ThreadingHTTPServer:
    class TlsTestServer(TestServer):
        def do_GET(self) -> None:
            if self.path == "/zu-http":
                self.send_page(302, b"", {"Location": f"{http_base}/seite.html"})
            elif self.path == "/gemischt":
                # NetSurf lädt Favicons ohne Seitenbezug; der Patch setzt den Referer, sonst ginge der Abruf durch.
                self.send_page(200, mixed_page(f"{http_base}/marker.css", f"{http_base}/symbol-https.ico"))
            elif self.path == "/gemischt-umgeleitet":
                self.send_page(200, mixed_page("/css-zu-http"))
            elif self.path == "/css-zu-http":
                self.send_page(302, b"", {"Location": f"{http_base}/marker.css"})
            elif self.path == "/js-weg":
                # Felder in der Ziel-URL: ohne Sperre gingen sie unverschlüsselt hinaus.
                self.send_page(200, js_page(js_mark(JS_ON), f"location.href = '{http_base}/js-ziel?geheim=wert42';"))
            elif self.path == "/h":
                self.send_page(200, page(HTTP_CHUNKED, "HSTS gesetzt"), {"Strict-Transport-Security": "max-age=600"})
            elif self.path == "/hz":
                port = self.server.server_address[1]
                self.send_page(302, b"", {"Location": f"http://localhost:{port}/seite.html"})
            elif self.path.startswith("/formular?"):
                method, target = self.path.split("?", 1)[1].split("&")
                action = f"{http_base}/{target}".replace("-fragment", "#oben")
                self.send_page(200, form_page(action, method, "geheim", "wert42"))
            elif self.path == "/einschleusen":
                # Wäre der Link nicht maskiert, setzte der eingeschleuste Stil die Markerfarbe.
                self.send_page(302, b"", {"Location": f'{http_base}/"><style>body{{background:#902090}}</style>'})
            elif self.path in ("/ohne-laenge-sauber", "/ohne-laenge-abgebrochen"):
                # HTTP/1.0 ohne Content-Length: nur close_notify (unwrap) zeigt das echte Ende.
                self.wfile.write(b"HTTP/1.0 200 OK\r\nContent-Type: text/html\r\n\r\n" + page(HTTP_PAGE, "Ohne Länge"))
                self.wfile.flush()
                if self.path == "/ohne-laenge-sauber":
                    self.connection.unwrap().close()
                self.close_connection = True
            else:
                super().do_GET()

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), TlsTestServer)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(work / f"{name}.pem", work / f"{name}.key")
    server.socket = context.wrap_socket(server.socket, server_side=True)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


# Formulare von HTTPS an http:// (Entscheidung 7): erst Warnseite, Felder gehen erst nach "Trotzdem senden" raus.
FORM_SOURCE, FORM_OK = (0x50, 0x50, 0x20), (0x20, 0x70, 0x20)
CLICK_SUBMIT, CLICK_CONFIRM = (250, 150), (78, 205)
# Werkzeugleiste "blfsrutc" mit Abstand 2: Zurück 2–24, Verlauf 26–52, Vor 54–76, Stopp 78–104, Neu laden 106–130.
CLICK_BACK, CLICK_FORWARD, CLICK_RELOAD = (13, 15), (65, 15), (118, 15)
POSTS: list[tuple[str, str]] = []


def form_page(action: str, method: str, name: str, value: str) -> bytes:
    form = (f'<form method="{method}" action="{action}"><input type="hidden" name="{name}" value="{value}">'
            '<input type="submit" value="Senden" style="width:500px;height:250px"></form>')
    return page(FORM_SOURCE, "Formular").replace(b"<p>Formular</p>", form.encode())


def form_target(handler: TestServer, query: str) -> None:
    sent = "geheim=wert42" in query
    handler.send_page(200, page(FORM_OK, "Angekommen") if sent else page(HTTP_MISSING, "Felder fehlen"))


def record_post(handler: TestServer) -> None:
    body = handler.rfile.read(int(handler.headers.get("Content-Length") or 0)).decode()
    POSTS.append((handler.path, body))
    form_target(handler, body)


TestServer.do_POST = record_post


def hsts_scenarios(port: int) -> list:
    # NetSurfs urldb merkt sich HSTS nur im Speicher: erst setzen, dann hebt llcache http://localhost:<port> auf https.
    # Kurze Pfade, weil address() nur 32 Zeichen der vorigen Adresse löscht.
    named = f"https://localhost:{port}"
    plain = f"http://localhost:{port}"
    return [
        ("https-hsts-link", address(1500, f"{named}/h") + address(3500, f"{plain}/seite.html"),
         [HTTP_PAGE], [DOWNGRADE_PAGE, START]),
        ("https-hsts-weiterleitung", address(1500, f"{named}/h") + address(3500, f"{named}/hz"),
         [HTTP_PAGE], [DOWNGRADE_PAGE, START]),
        # Gegenprobe ohne HSTS: dieselbe Weiterleitung zeigt die Warnseite.
        ("https-ohne-hsts-weiterleitung", address(1500, f"{named}/hz"), [DOWNGRADE_PAGE], [HTTP_PAGE, START]),
    ]


def address_over(at: int, url: str) -> list[str]:
    # Wie address(), aber ohne Löschen: die lange data:-URL der Warnseite bleibt hinter "#" als Fragment stehen.
    events = click(at, (300, 15)) + [f"{at + 200} 1 71 0 0", f"{at + 210} 2 71 0 0"]
    t = at + 300
    for char in url + "#":
        events.append(f"{t} 3 {ord(char)} 0 0")
        t += 10
    return events + [f"{t} 1 28 0 0", f"{t + 10} 2 28 0 0"]


def form_scenarios(good: str, base: str, refused: int) -> list:
    submit = click(3600, CLICK_SUBMIT)
    confirm = [*submit, *click(4600, CLICK_CONFIRM)]
    reload = [*submit, *click(4600, CLICK_RELOAD)]
    back_forward = [*submit, *click(4600, CLICK_BACK), *click(5300, CLICK_FORWARD)]
    # Von der Warnseite zu einer abgelehnten Adresse, auf der Fehlerseite "Zurück": NetSurf lädt dann die alten
    # Parameter (Ziel-URL mit HTTPS-Referer, ohne Formularmarke) neu, browser_window__reload_current_parameters.
    error_back = [*submit, *address_over(4200, f"http://127.0.0.1:{refused}/"), *click(5300, CLICK_BACK)]
    return [
        ("https-formular-warnung", address(1500, f"{good}/formular?post&ziel-warnung") + submit, [DOWNGRADE_PAGE], [FORM_OK]),
        ("https-formular-senden", address(1500, f"{good}/formular?post&ziel-senden") + confirm, [FORM_OK], [HTTP_MISSING]),
        ("https-formular-get-warnung", address(1500, f"{good}/formular?get&ziel-get-warnung") + submit,
         [DOWNGRADE_PAGE], [FORM_OK]),
        # Fragment in der Action: llcache ruft ohne Fragment ab, die Warnung muss trotzdem greifen.
        ("https-formular-get-fragment", address(1500, f"{good}/formular?get&ziel-get-warnung-fragment") + submit,
         [DOWNGRADE_PAGE], [FORM_OK]),
        ("https-formular-get-senden", address(1500, f"{good}/formular?get&ziel-get-senden") + confirm,
         [FORM_OK], [DOWNGRADE_PAGE, HTTP_MISSING]),
        # Neuladen, Zurück/Vor und "Zurück" auf einer Fehlerseite zeigen wieder die Warnung, ohne das Ziel
        # anzufragen (form_checks prüft das Netz).
        ("https-formular-get-neuladen", address(1500, f"{good}/formular?get&ziel-get-neuladen") + reload,
         [DOWNGRADE_PAGE], [FORM_OK, HTTP_MISSING]),
        ("https-formular-get-zurueck-vor", address(1500, f"{good}/formular?get&ziel-get-zurueck") + back_forward,
         [DOWNGRADE_PAGE], [FORM_OK, HTTP_MISSING]),
        ("https-formular-neuladen", address(1500, f"{good}/formular?post&ziel-neuladen") + reload,
         [DOWNGRADE_PAGE], [FORM_OK, HTTP_MISSING]),
        ("https-formular-get-fehler-zurueck", address(1500, f"{good}/formular?get&ziel-get-fehler") + error_back,
         [DOWNGRADE_PAGE], [FORM_OK, HTTP_MISSING]),
        ("https-formular-fehler-zurueck", address(1500, f"{good}/formular?post&ziel-fehler") + error_back,
         [DOWNGRADE_PAGE], [FORM_OK, HTTP_MISSING]),
        # Gegenprobe: Nach dem Neuladen funktioniert die Bestätigung weiter, das Ziel kommt genau einmal an.
        ("https-formular-get-neuladen-senden",
         address(1500, f"{good}/formular?get&ziel-get-neu-senden") + reload + click(5300, CLICK_CONFIRM),
         [FORM_OK], [DOWNGRADE_PAGE, HTTP_MISSING]),
        ("https-formular-neuladen-senden",
         address(1500, f"{good}/formular?post&ziel-neu-senden") + reload + click(5300, CLICK_CONFIRM),
         [FORM_OK], [DOWNGRADE_PAGE, HTTP_MISSING]),
        # Eine fremde Seite postet ein erfundenes Token: lokale Fehlerseite, nichts geht ins Netz.
        ("http-formular-falsches-token", address(1500, f"{base}/falsches-token") + submit, [], [FORM_OK, FORM_SOURCE]),
        ("http-formular-kurzes-token", address(1500, f"{base}/kurzes-token") + submit, [], [FORM_OK, FORM_SOURCE]),
    ]


def form_checks() -> list[str]:
    paths = [path for path, _ in POSTS]
    problems = []
    if "/ziel-warnung" in paths or "/ziel-token" in paths:
        problems.append("Formular ohne Bestätigung gesendet")
    if ("/ziel-senden", "geheim=wert42") not in POSTS:
        problems.append("bestätigtes Formular nicht mit Originaldaten angekommen")
    # Auch über Nebenwege wie den Favicon-Referer dürfen die Felder vor der Bestätigung nicht ankommen.
    if any(path.startswith("/ziel-get-warnung") for path in REQUESTED) or any("ziel-get-warnung?" in r for r in REFERERS):
        problems.append("GET-Formular ohne Bestätigung gesendet")
    # Neuladen und Zurück/Vor dürfen das Ziel weder per GET noch per POST erreichen, auch ohne Felder.
    for target in ("/ziel-get-neuladen", "/ziel-get-zurueck", "/ziel-neuladen", "/ziel-get-fehler", "/ziel-fehler"):
        if any(path.split("?")[0] == target for path in REQUEST_LOG + paths) or any(f"{target}?" in r for r in REFERERS):
            problems.append(f"{target} ohne Bestätigung angefragt")
    # Ein Klick auf die Bestätigung ruft das Ziel genau einmal ab, auch nach Neuladen der Warnseite.
    for prefix in ("/ziel-get-senden?", "/ziel-get-neu-senden?"):
        count = sum(path.startswith(prefix) for path in REQUEST_LOG)
        if count != 1:
            problems.append(f"bestätigtes GET-Formular {prefix[:-1]} {count}-mal statt einmal angekommen")
    if paths.count("/ziel-senden") != 1 or POSTS.count(("/ziel-neu-senden", "geheim=wert42")) != 1 \
            or paths.count("/ziel-neu-senden") != 1:
        problems.append("bestätigtes POST-Formular nicht genau einmal angekommen")
    if any("zehos_bestaetigung" in body for _, body in POSTS):
        problems.append("Bestätigungstoken ins Netz gelangt")
    return problems


def https_scenarios(good: str, wrong: str) -> list:
    pages = [START, TARGET, FORM, LONG, HTTP_PAGE, HTTP_CHUNKED, HTTP_MISSING, DOWNGRADE_PAGE]
    return [
        ("https-seite", address(1500, f"{good}/seite.html"), [HTTP_PAGE], [START]),
        ("https-chunked", address(1500, f"{good}/chunked.html"), [HTTP_CHUNKED], [START]),
        ("https-umleitung", address(1500, f"{good}/umleitung"), [HTTP_PAGE], [START]),
        ("https-gemischt", address(1500, f"{good}/gemischt"), [MIXED_PAGE], [MIXED, START]),
        ("https-gemischt-umgeleitet", address(1500, f"{good}/gemischt-umgeleitet"), [MIXED_PAGE],
         [MIXED, DOWNGRADE_PAGE, START]),
        ("https-falscher-name", address(1500, f"{wrong}/seite.html"), [], pages),
        ("https-zu-http", address(1500, f"{good}/zu-http"), [DOWNGRADE_PAGE], [HTTP_PAGE, START]),
        ("https-einschleusen", address(1500, f"{good}/einschleusen"), [DOWNGRADE_PAGE], [INJECTED, START]),
        ("https-ohne-laenge-sauber", address(1500, f"{good}/ohne-laenge-sauber"), [HTTP_PAGE], [START]),
        ("https-ohne-laenge-abgebrochen", address(1500, f"{good}/ohne-laenge-abgebrochen"), [], pages),
    ]


# JavaScript-Szenarien (Policy a–e, docs/NETSURF_HTTPS.md): laufen auf einer Paketkopie mit enable_javascript:1,
# das Paket selbst bleibt bei 0. Skripte färben per document.write einen Block; JS_ON belegt, dass Skripte laufen.
JS_PAGE, JS_ON, JS_AFTER_LOOP = (0x30, 0x30, 0x60), (0x10, 0xa0, 0x60), (0x60, 0xa0, 0x10)
JS_TIMERS_OK, JS_TIMERS_BAD = (0x20, 0x80, 0xa0), (0xa0, 0x20, 0x20)
JS_RANGE_OK, JS_DATA_PAGE, JS_DATA_MARK, JS_TARGET = (0x80, 0x80, 0x20), (0x50, 0x20, 0x50), (0xa0, 0x50, 0xa0), (0x20, 0x50, 0x20)
SECURE_SAFE, SECURE_LEAK = (0x30, 0x70, 0x70), (0x70, 0x30, 0x30)
# Längeres Ende als die übrigen Szenarien: wasm3 interpretiert Duktape, das Zeitlimit allein belegt 2 s.
JS_EXIT_MS = 12000


def js_mark(colour: tuple[int, int, int]) -> str:
    hex_colour = "".join(f"{c:02x}" for c in colour)
    return f"document.write('<div style=\"background:#{hex_colour};width:300px;height:100px\"></div>');"


def js_page(*scripts: str) -> bytes:
    body = "".join(f"<script>{script}</script>" for script in scripts)
    return page(JS_PAGE, "Skript").replace(b"</body>", f"{body}</body>".encode())


def js_get(handler: TestServer) -> bool:
    if handler.path == "/js-an":
        handler.send_page(200, js_page(js_mark(JS_ON)))
    elif handler.path == "/js-schleife":
        # Das dritte Skript läuft erst nach dem Abbruch der Schleife und markiert nur, wenn er nach höchstens 5 s kam
        # (Limit 2 s plus Spielraum für wasm3); ein Limit von 10 s scheitert damit unabhängig vom Prüfende.
        handler.send_page(200, js_page("var t0 = Date.now();", "while (true) {}",
                                       f"if (Date.now() - t0 < 5000) {{ {js_mark(JS_AFTER_LOOP)} }}"))
    elif handler.path == "/js-timer":
        handler.send_page(200, js_page(
            "var n = 0; for (var i = 0; i < 70; i++) { if (setInterval(function () {}, 100000) !== undefined) n++; }"
            f"if (n === 64) {{ {js_mark(JS_TIMERS_OK)} }} else {{ {js_mark(JS_TIMERS_BAD)} }}"))
    elif handler.path == "/js-rekursion":
        handler.send_page(200, js_page(
            # Über eine native Funktion (map), sonst rekursiert Duktape ohne C-Stapel und die native Grenze greift nie.
            f"function f() {{ [0].map(f); }} try {{ f(); }} catch (e) {{ if (e instanceof RangeError) {{ {js_mark(JS_RANGE_OK)} }} }}"))
    elif handler.path == "/js-data":
        hex_colour = "".join(f"{c:02x}" for c in JS_DATA_MARK)
        handler.send_page(200, js_page(js_mark(JS_ON),
                                       f"location.href = 'data:text/html,<body style=background:%23{hex_colour}>x</body>';"))
    elif handler.path == "/sk":
        # Das normale Cookie belegt, dass Cookies überhaupt gespeichert werden.
        handler.send_page(200, page(HTTP_PAGE, "Secure gesetzt"), [("Set-Cookie", "zehos_sicher=1; Secure; Path=/"),
                                                                   ("Set-Cookie", "zehos_offen=1; Path=/")])
    elif handler.path == "/sp":
        sent = handler.headers.get("Cookie") or ""
        safe = "zehos_offen=1" in sent and "zehos_sicher" not in sent
        handler.send_page(200, page(SECURE_SAFE, "Cookies richtig") if safe else page(SECURE_LEAK, "Cookies falsch"))
    elif handler.path in ("/ts", "/tp", "/ow"):
        # /ts (https) setzt ein Secure-Cookie, /tp (https) ein normales, /ow (http) versucht das Secure-Cookie zu ersetzen.
        cookie = {"/ts": "tsec=1; Secure; Path=/", "/tp": "tplain=1; Path=/", "/ow": "tsec=evil; Path=/"}[handler.path]
        handler.send_page(200, page(HTTP_PAGE, "Cookie gesetzt"), {"Set-Cookie": cookie})
    elif handler.path in ("/c2", "/c3"):
        sent = handler.headers.get("Cookie") or ""
        # /c2 (http): normales Cookie ja, Secure-Cookie nein. /c3 (https): Secure-Cookie unverändert.
        safe = ("tplain=1" in sent and "tsec" not in sent) if handler.path == "/c2" else ("tsec=1" in sent and "evil" not in sent)
        handler.send_page(200, page(SECURE_SAFE, "Cookies richtig") if safe else page(SECURE_LEAK, "Cookies falsch"))
    elif handler.path == "/js-httponly":
        # HttpOnly mit Path=/ auf einer tieferen Seite: document.cookie darf es nicht zeigen (3.11 filterte nur exakt);
        # das normale Cookie muss dort erscheinen, sonst bestünde die Prüfung auch ohne lesbare Cookies.
        handler.send_page(200, js_page(
            "var c = document.cookie;"
            f"if (c.indexOf('zoffen=1') >= 0 && c.indexOf('zgeheim') < 0) {{ {js_mark(SECURE_SAFE)} }} else {{ {js_mark(SECURE_LEAK)} }}"),
            [("Set-Cookie", "zgeheim=1; HttpOnly; Path=/"), ("Set-Cookie", "zoffen=1; Path=/")])
    elif handler.path == "/js-leerpfad":
        # Leerer Path aus Kopfzeile und Skript darf die App nicht beenden; JS_ON danach belegt den Weiterlauf.
        handler.send_page(200, js_page("document.cookie = 'e=1; path=';", js_mark(JS_ON)), {"Set-Cookie": "k=1; Path="})
    elif handler.path.startswith("/js-ziel"):
        handler.send_page(200, page(JS_TARGET, "JS-Ziel"))
    else:
        return False
    return True


def js_data_url(base: str) -> str:
    # Getippt in die Adressleiste; ein Skript auf data: darf nicht laufen (js_checks: kein Abruf von /js-von-data),
    # die Seitenfarbe belegt das Laden.
    page_colour = "".join(f"{c:02x}" for c in JS_DATA_PAGE)
    return f"data:text/html,<body style=background:%23{page_colour}><script>location.href='{base}/js-von-data'</script>"


def js_scenarios(base: str, good: str) -> list:
    pages = [START, HTTP_PAGE, HTTP_MISSING]
    return [
        # Gegenprobe zu allen JS-Szenarien: Skripte laufen überhaupt.
        ("js-an", address(1500, f"{base}/js-an"), [JS_PAGE, JS_ON], [START]),
        ("js-endlosschleife", address(1500, f"{base}/js-schleife"), [JS_PAGE, JS_AFTER_LOOP], [START]),
        ("js-timerflut", address(1500, f"{base}/js-timer"), [JS_TIMERS_OK], [JS_TIMERS_BAD, START]),
        ("js-rekursion", address(1500, f"{base}/js-rekursion"), [JS_RANGE_OK], [START]),
        ("js-location-data", address(1500, f"{base}/js-data"), [JS_PAGE, JS_ON], [JS_DATA_MARK, START]),
        ("js-auf-data-seite", address(1500, js_data_url(base)), [JS_DATA_PAGE], [START]),
        # Von HTTPS per location.href auf http:// mit Feldern: der Fetcher blockiert mit Fehlerseite (keine Warnseite,
        # der Abruf ist unverifizierbar), der Server sieht keinen Abruf (js_checks). Ohne JS bliebe JS_PAGE stehen.
        ("js-location-downgrade", address(1500, f"{good}/js-weg"), [],
         [JS_TARGET, JS_PAGE, DOWNGRADE_PAGE, HTTP_MISSING, START]),
        # Kurze Pfade, weil address() nur 32 Zeichen der vorigen Adresse löscht.
        ("js-secure-cookie-http", address(1500, f"{base}/sk") + address(3500, f"{good}/sp"),
         [SECURE_SAFE], [SECURE_LEAK] + pages),
        ("js-an-https", address(1500, f"{good}/js-an"), [JS_PAGE, JS_ON], [START]),
        ("js-httponly-verborgen", address(1500, f"{base}/js-httponly"), [SECURE_SAFE], [SECURE_LEAK, START]),
        ("js-cookie-leerer-pfad", address(1500, f"{base}/js-leerpfad"), [JS_PAGE, JS_ON], [START]),
        # Ein Secure-Cookie von https geht nicht an http:// desselben Hosts, ein normales schon (NetSurf 3.11 sandte beide).
        ("secure-cookie-nicht-an-http", address(1500, f"{good}/ts") + address(3500, f"{good}/tp") + address(5500, f"{base}/c2"),
         [SECURE_SAFE], [SECURE_LEAK] + pages),
        # http:// darf ein Secure-Cookie nicht durch ein gleichnamiges ohne Secure ersetzen.
        ("secure-cookie-nicht-ersetzbar", address(1500, f"{good}/ts") + address(3500, f"{base}/ow") + address(5500, f"{good}/c3"),
         [SECURE_SAFE], [SECURE_LEAK] + pages),
    ]


def js_checks() -> list[str]:
    problems = []
    if any(path.startswith("/js-ziel") for path in REQUEST_LOG):
        problems.append("location.href von HTTPS auf HTTP hat das Ziel abgerufen")
    if "/js-von-data" in REQUEST_LOG:
        problems.append("Skript auf einer data:-Seite ist gelaufen")
    return problems


def javascript_package(work: Path) -> Path:
    # Nur die Testkopie schaltet JavaScript ein; das ausgelieferte res/Choices bleibt bei enable_javascript:0.
    copy = work / "js-paket"
    shutil.copytree(PACKAGE, copy)
    choices = copy / "res" / "Choices"
    text = choices.read_text()
    if "enable_javascript:0" not in text:
        raise ValueError("res/Choices ohne enable_javascript:0")
    choices.write_text(text.replace("enable_javascript:0", "enable_javascript:1"))
    return copy


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
    parser.add_argument("--wasm3", type=Path, help="Prüfhost aus zehos_host_wasm3.c")
    parser.add_argument("--iwasm", type=Path, help="optional: WAMR iwasm, zusammen mit --host")
    parser.add_argument("--host", type=Path, help="optional: libzehos_host.so für --iwasm")
    args = parser.parse_args()
    if (args.wasm3 is None) == (args.iwasm is None) or (args.iwasm is None) != (args.host is None):
        parser.error("entweder --wasm3 oder --iwasm mit --host angeben")

    def command(package: Path) -> list[str]:
        if args.wasm3 is not None:
            return [str(args.wasm3), str(package), str(package / "netsurf.wasm")]
        return [str(args.iwasm), f"--native-lib={args.host.resolve()}", f"--map-dir=/::{package}",
                "--stack-size=1048576", str(package / "netsurf.wasm")]
    failures = 0
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), TestServer)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{server.server_address[1]}"
    scenarios = SCENARIOS + http_scenarios(base, closed_port()) + cookie_scenarios(base)
    with tempfile.TemporaryDirectory() as work:
        fixtures = make_tls_fixtures(Path(work))
        tls = [tls_server(Path(work), name, base) for name in ("gut", "falsch")]
        scenarios += https_scenarios(*(f"https://127.0.0.1:{s.server_address[1]}" for s in tls))
        scenarios += form_scenarios(f"https://127.0.0.1:{tls[0].server_address[1]}", base, closed_port())
        scenarios += hsts_scenarios(tls[0].server_address[1])
        javascript = js_scenarios(base, f"https://127.0.0.1:{tls[0].server_address[1]}")
        scenarios += javascript
        # Gegenprobe auf dem ausgelieferten Paket: dort bleibt JavaScript aus.
        scenarios.append(("js-aus-im-paket", address(1500, f"{base}/js-an"), [JS_PAGE], [JS_ON, START]))
        js_package = javascript_package(Path(work))
        for name, events, expected, forbidden in scenarios:
            script, frame = Path(work) / f"{name}.txt", Path(work) / f"{name}.ppm"
            script.write_text("".join(f"{line}\n" for line in events))
            is_js = any(name == entry[0] for entry in javascript)
            env = dict(os.environ, ZEHOS_EVENTS=str(script), ZEHOS_PPM=str(frame),
                       ZEHOS_EXIT_MS=str(JS_EXIT_MS) if is_js else "6000")
            env.pop("ZEHOS_NET", None)
            env["ZEHOS_TLS_CA"] = str(fixtures["ca"])
            if name != "http-ohne-berechtigung":
                env["ZEHOS_NET"] = "1"
            run = subprocess.run(command(js_package if is_js else PACKAGE), env=env, capture_output=True, text=True,
                                 timeout=60)
            seen = colours(frame) if frame.exists() else set()
            problems = []
            if run.returncode != 0:
                problems.append(f"Exitcode {run.returncode}")
            problems += [f"Farbe {c} fehlt" for c in expected if c not in seen]
            problems += [f"Farbe {c} unerwartet" for c in forbidden if c in seen]
            failures += bool(problems)
            print(f"{'FAIL' if problems else 'ok  '} {name}" + (f": {', '.join(problems)}" if problems else ""))
        for tls_server_running in tls:
            tls_server_running.shutdown()
    for problem in form_checks():
        failures += 1
        print(f"FAIL https-formular-netz: {problem}")
    scenarios.append(("https-formular-netz", [], [], []))
    for problem in js_checks():
        failures += 1
        print(f"FAIL js-netz: {problem}")
    scenarios.append(("js-netz", [], [], []))
    # Mixed Content ohne Farbwirkung: das http://-Favicon der HTTPS-Seite darf nie angefragt werden.
    if "/symbol-https.ico" in REQUESTED:
        failures += 1
        print("FAIL https-gemischt-favicon: http://-Favicon wurde geladen")
    else:
        print("ok   https-gemischt-favicon")
    scenarios.append(("https-gemischt-favicon", [], [], []))
    server.shutdown()
    print(f"{len(scenarios) - failures}/{len(scenarios)} bestanden")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
