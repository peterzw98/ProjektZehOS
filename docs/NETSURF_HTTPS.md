# HTTPS für NetSurf – Entwurf

**Status:** Entwurf vom 15. November 2026, nichts davon ist umgesetzt oder geprüft. Grundlage ist der Stand von
`fix/netsurf-net-fehlercodes` (Netz-ABI aus #69, NetSurf-Fetcher aus #70). Offene Entscheidungen sind mit **OFFEN**
markiert. Bezug: [ROADMAP](../ROADMAP.md) 12.2, [NETWORK_TESTS.md](NETWORK_TESTS.md), [NETSURF.md](NETSURF.md),
[PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md).

## Ausgangslage

- **Shell und Paketquellen** sprechen bereits HTTPS. `CosmosNetworkService.Exchange` legt `TlsTransport.Open` über einen
  blockierenden `PollingStream` und hält dabei die Sperre `CosmosNetworkService.Gate` für den ganzen Abruf.
  `TlsTransport` nutzt BouncyCastle 2.7.0 (`TlsClientProtocol`). Erlaubt sind nur TLS 1.2/1.3 mit ECDHE/AEAD,
  höchstens 8 Zertifikate und 32768 Bytes je Handshake-Nachricht. Ketten- und Namensprüfung übernimmt
  `ThirdParty/CosmosHttp/CertificateVerifier.cs`, Vertrauensanker sind die 121 Mozilla-Wurzeln aus der Ressource
  `ZehOS.TlsRoots`. Zufall kommt aus `CosmosSaltSource.Fill` (RDSEED/RDRAND, kein Rückfall), und UTC muss im Bereich
  2026–2100 liegen. Eine Option zum Abschalten der Prüfung gibt es nicht.
- **Wasm-Apps** erhalten über `zehos_v1.net_*` nur TCP. `WasmNetworkSession.Open` lehnt `flags != 0` mit `ENOTSUP`
  (58) ab. Der gemeinsame Worker in `CosmosWasmNetworkConnector` pumpt Bytes nicht blockierend zwischen 64-KiB-Puffern
  und Socket. Das Zeitlimit von 10 s gilt von `net_open` bis zum Verbindungsaufbau.
- **NetSurf** meldet `https://` in `frontend/fetch_http.c` ausdrücklich als nicht unterstützt und ruft `net_open`
  immer mit `flags = 0` auf. Der Linux-Prüfhost (`host/zehos_host_linux.c`) lehnt Flags ebenfalls mit `ENOTSUP` ab.

## Ziel und Nicht-Ziele

Ziel: NetSurf lädt `https://`-Seiten mit derselben Zertifikatsprüfung wie die Shell. Ein Zertifikatsfehler zeigt eine
Fehlerseite und lässt sich nicht übergehen. Ein HTTPS-Fehler führt nie zu einem Rückfall auf HTTP (ROADMAP 10.8).

Nicht Teil dieses Schritts: HTTP/2 beziehungsweise ALPN, Client-Zertifikate, Sperrprüfung (OCSP/CRL),
Sitzungswiederaufnahme, eigene Vertrauensanker je App und eine Pflege des Vertrauensspeichers zur Laufzeit.

## Entscheidung 1: TLS im Kernel statt im Gast

| Weg | Vorteile | Nachteile |
|-----|----------|-----------|
| **Kernel-TLS (empfohlen)** | Ein geprüfter Weg für Shell, Paketquellen und Apps; ein Vertrauensspeicher; die App sieht nie Schlüsselmaterial der Sitzung; Zufall und Uhr bleiben im Kernel | Neue Hostfunktionalität im Loader; Handshake-Rechenzeit im Netzwerkworker |
| TLS im Gast (z. B. BearSSL oder mbedTLS nach WASIp1) | Kein Kerneleingriff | Zweiter Vertrauensspeicher im Paket, eigene Pflege und Prüfung; Zufall über `random_get`, Uhr über `clock_time_get`; größeres Paket; jede App bringt eigenes TLS mit |

Für den Kernelweg spricht auch die Projektregel, fehlende Fähigkeiten als allgemeine Hostfunktion des Loaders zu
ergänzen und nicht pro App. Die Prüfung von Zertifikaten ist sicherheitskritisch und soll nur einmal existieren.

## Entscheidung 2: Flag in `net_open` statt eigenem Import

Vorschlag: Bit 0 von `flags` heißt `ZEHOS_NET_TLS` (Wert 1). Alle übrigen Bits bleiben reserviert und liefern
weiterhin `ENOTSUP`.

- **Abwärtskompatibel:** Ein älterer Kernel antwortet heute schon mit `ENOTSUP` (58). Eine App kann HTTPS dann
  ausdrücklich als nicht verfügbar anzeigen. Ein neuer Import `net_open_tls` würde dagegen auf älteren Kerneln
  schon beim Binden des Moduls scheitern, und die App startet dann gar nicht.
- **Gleiche Handles:** `net_status`, `net_send`, `net_recv` und `net_close` bleiben unverändert. Sie übertragen bei
  TLS Klartext der Anwendung; Verschlüsselung und Records erledigt der Kernel.
- **Prüfname:** Der an `net_open` übergebene `host` ist zugleich SNI und Prüfname. Ein IPv4-Literal wird ohne SNI
  gegen die IP-SAN geprüft, wie im Shellweg. Eine App kann damit keinen anderen Namen prüfen lassen, als sie verbindet.

**OFFEN:** Ob ein späteres Abfrage-Import für Zertifikatsdetails (`net_tls_info`) nötig wird, etwa für NetSurfs
Seiteninformationen. Für die erste Stufe genügt ein Fehlercode.

## Vorgeschlagener ABI-Vertrag

| Funktion | Verhalten mit `ZEHOS_NET_TLS` |
|----------|-------------------------------|
| `net_open` | Wie bisher DNS und TCP, danach der TLS-Handshake. Sofortiger Fehler bei ungültigem Host oder Port und ohne `capability=net` (`EACCES`). Unbekannte Bits: `ENOTSUP`. |
| `net_status` | `EAGAIN`, bis der Handshake einschließlich Zertifikatsprüfung abgeschlossen ist; danach 0 oder ein Fehler (siehe unten). Das bestehende Zeitlimit von 10 s umfasst DNS, TCP und Handshake. |
| `net_send` | Übernimmt Klartext in den 64-KiB-Sendepuffer; vor Handshake-Ende wie bisher der Status als Fehler. |
| `net_recv` | Liefert entschlüsselten Klartext. EOF wie bisher als Erfolg mit `out_n = 0`, auch ohne `close_notify` (siehe Grenzen). |
| `net_close` | Sendet nach Möglichkeit `close_notify` und schließt dann den Socket; ein Fehler dabei wird nicht an die App gemeldet. |

Die Grenzen von 4 Verbindungen und 64 KiB je Aufruf und Puffer gelten unverändert und beziehen sich auf Klartext.

## Kernelumsetzung (Skizze)

1. **Nicht blockierender BouncyCastle-Modus:** BouncyCastle 2.7.0 bietet `new TlsClientProtocol()` mit `Connect`,
   `OfferInput`, `ReadOutput`, `GetAvailableOutputBytes`, `WriteApplicationData`, `ReadInput`,
   `GetAvailableInputBytes` und `CloseInput` (in der Paketdokumentation geprüft). Das passt zum Schrittmodell des
   Workers: Socketbytes gehen nach `OfferInput`, `ReadOutput` geht zum Socket, Klartext der App geht nach
   `WriteApplicationData`, und `ReadInput` füllt den Empfangspuffer der App. Der Shellweg bleibt blockierend und
   unverändert.
2. **Gemeinsame Konfiguration:** `TlsTransport` so aufteilen, dass `Client`, `Authentication` und die
   Ankerauswahl (`IsUsableAnchor`) auch für den nicht blockierenden Modus nutzbar sind. Shell und Apps verwenden
   dann dieselben Suiten, Grenzen und Prüfungen. Eine zweite TLS-Konfiguration darf nicht entstehen.
3. **Sperre kurz halten:** Nur Socketaufrufe laufen unter `CosmosNetworkService.Gate`, die TLS-Rechenarbeit
   (Handshake, Entschlüsselung) außerhalb davon. Sonst blockiert ein langer Handshake jeden Shellabruf.
4. **Wurzeln einmal parsen:** `TlsTransport.Open` parst das 189-KB-Bundle heute bei jedem Aufruf. Für bis zu
   4 parallele App-Verbindungen sollten die gefilterten Anker einmal pro Kernellauf vorliegen.
   **OFFEN:** Messung von Parsezeit und Speicher im Gast, bevor zwischengespeichert wird.
5. **Ausnahmen bleiben im Worker:** Ein `TlsFatalAlert` und jede andere Ausnahme wird im Worker auf einen Fehlercode
   abgebildet. Wie bei `CosmosWasmNetwork.cs` darf nichts die native Grenze (`UnmanagedCallersOnly`) überqueren,
   und Aufräumen auf Fehlerwegen geschieht nach `catch`, nicht in `finally` (Cosmos 3.0.94).

**OFFEN:** Rechenzeit eines Handshakes (RSA-2048- oder ECDSA-P-256-Kette) im Gast. Der Round-Robin-Scheduler mit
20-ms-Zeitscheiben hält die Oberfläche zwar bedienbar, aber ein langer Handshake kann das 10-s-Zeitlimit
ausschöpfen. Messung in QEMU (TCG und KVM) vor der Festlegung eines eigenen Handshake-Limits.

## Zertifikatsprüfung und Vertrauensspeicher

- Gleiche Anker wie die Shell (`ZehOS.TlsRoots`), gleiche Prüfung (`CertificateVerifier.Verify`: Kette,
  Verwendungszweck `serverAuth`, Gültigkeit gegen UTC, Hostname einschließlich Platzhalter nur für das erste Label).
- Keine App-eigenen Anker und keine Übergehen-Option, auch nicht für Tests in normalen Builds. Testwurzeln
  gelangen wie bei der Netzwerkprobe nur in ausdrücklich gebaute Probe-Kernel.
- Ohne sichere Entropie oder bei unplausibler UTC-Zeit scheitert jeder TLS-Handshake; HTTP bleibt dann möglich.
- Keine Sperrprüfung und keine Aktualisierung des Bundles zur Laufzeit. Ein neues Bundle kommt nur mit einem
  Kernelupdate; das bleibt Teil von ROADMAP 12.2.

## Fehlercodes

Werte nach WASI Preview 1 (`wasi-libc` `__errno_values.h`):

| Fall | Code | Bemerkung |
|------|------|-----------|
| Kernel ohne TLS oder unbekanntes Flag | `ENOTSUP` (58) | sofort in `net_open` |
| Keine sichere Entropie, UTC unplausibel, Bundle fehlt | `ENOTSUP` (58) | Voraussetzung im Gast nicht erfüllt; **OFFEN:** eigener Code, damit die Fehlerseite Uhr oder CPU-Zufall als Ursache nennen kann |
| Zertifikat abgelehnt (`bad_certificate`, `unsupported_certificate`, Name, Kette, Zeit) | `EPERM` (63) | „Verbindung aus Sicherheitsgründen abgelehnt“; nicht `EACCES`, das bleibt der fehlenden Berechtigung vorbehalten |
| Sonstiger TLS-Protokollfehler oder Alert des Servers | `EPROTO` (65) | z. B. keine gemeinsame Suite oder Version |
| Zeitlimit bis Handshake-Ende | `ETIMEDOUT` (73) | wie bisher |
| Übrige Fehler | `EIO` (29) | wie bisher; Cosmos-Einschränkungen aus [NETSURF.md](NETSURF.md#netzwerkfehler) gelten weiter |

**OFFEN:** Die Zuordnung `EPERM`/`EPROTO`. Alternativ meldet `EPROTO` alle TLS-Fehler, dann kann die Fehlerseite aber
einen Zertifikatsfehler nicht mehr von einem Protokollfehler unterscheiden.

## NetSurf-Seite

- `fetch_http.c`: Bei `NSURL_SCHEME_HTTPS` den Standardport 443 setzen und `net_open` mit `ZEHOS_NET_TLS` aufrufen.
  `ENOTSUP` vom Kernel zeigt die bisherige Seite „HTTPS nicht unterstützt“.
- Zertifikatsfehler über `FETCH_ERROR` mit eigener Meldung melden, nicht über `FETCH_CERT_ERR`. NetSurfs
  Zertifikatsfehlerseite bietet ein Fortfahren an, das der Kernel nicht erlauben darf.
- Weiterleitungen von HTTPS auf HTTP: **OFFEN**, ob der Fetcher sie ablehnt oder NetSurfs Verhalten übernimmt. Für
  eine Herabstufung ohne Rückfrage spricht nichts; Vorschlag: ablehnen.
- Der Prüfhost `host/zehos_host_linux.c` erhält denselben Flag über OpenSSL mit einer Test-CA aus einer
  Umgebungsvariable, analog zu `ZEHOS_NET=1`. Er ersetzt keinen Kerneltest.

## Grenzen

- **Cosmos-Warteabschnitte:** Connect und Send in Cosmos 3.0.94 sind nicht sofort abbrechbar. Das Zeitlimit greift
  erst nach Rückkehr des Aufrufs, wie bei HTTP ([PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md)).
- **Kein verlässliches Ende ohne `close_notify`:** `RequiresCloseNotify` ist im Shellweg abgeschaltet. Eine Antwort
  ohne Länge (HTTP/1.0 bis Verbindungsende) kann deshalb unbemerkt abgeschnitten sein. Mit `Content-Length` oder
  `chunked` erkennt der Fetcher das Abschneiden. **OFFEN:** ob TLS-Verbindungen der Apps `close_notify` verlangen.
- **Keine Sitzungswiederaufnahme:** Jede Verbindung führt einen vollen Handshake aus. Mit höchstens 4 Verbindungen
  und dem Rechenaufwand im Gast wird das Laden vieler Ressourcen langsamer.
- **Uhr:** Die Gültigkeitsprüfung hängt an der vom Hypervisor gelieferten RTC; ein sicherer Zeitabgleich fehlt.
- **Plattformen:** VMware und echte Hardware sind für Netzwerk und TLS bisher ungeprüft.

## Testplan

1. **Hosttests (`ProjektZeh.Tests`):** Flag-Auswertung in `WasmNetworkSession` (Bit 0 erlaubt, übrige `ENOTSUP`),
   Abbildung von `TlsFatalAlert` und Voraussetzungsfehlern auf die Codes oben, nicht blockierender TLS-Pfad gegen
   einen lokalen Testserver mit eigener CA: Erfolg, falscher Name, fremde CA, abgelaufen, noch nicht gültig,
   Abbruch mitten im Handshake, 64-KiB-Grenzen für Klartext.
2. **Linux-Prüfhost (`host/run_tests.py`):** NetSurf lädt eine HTTPS-Seite von einem lokalen Python-`ssl`-Server;
   falscher Name ergibt die Zertifikatsfehlerseite, Weiterleitung HTTPS→HTTP wird nach Entscheidung geprüft.
3. **QEMU-Probe:** Probe-Kernel mit Test-CA, dazu die vorhandenen TLS-Fixtures aus `scripts/test-network.py`
   (18443–18448: gültig, falscher Name, fremde CA, abgelaufen, künftig). NetSurf-Gastprüfung analog zu
   `scripts/test-netsurf-desktop.py` mit Markerfarben; der Zertifikatsfehler ist über ein Bildschirmfoto visuell zu
   prüfen. Normale Builds enthalten keine Test-CA, das ist im Build zu prüfen.
4. **Messung:** Handshakezeit und Speicher je Verbindung bei 1 und 4 parallelen Verbindungen (TCG und KVM).
5. **Regression:** Shell-`fetch https://`, Paketquelle und HTTP-Seiten in NetSurf bleiben unverändert.
6. **VMware:** getrennt ausweisen, solange nicht geprüft.

## Betroffene Dateien bei einer Umsetzung

`ProjektZeh/Network/TlsTransport.cs`, `ProjektZeh/Runtime/Wasm/WasmNetwork.cs` und `CosmosWasmNetwork.cs`,
`ProjektZeh.Tests/`, `experiments/NetSurf.Wasi/frontend/fetch_http.c`, `host/zehos_host_linux.c`,
`host/run_tests.py`, `docs/PAKETE_ERSTELLEN.md`, `docs/PACKAGE_FORMAT.md` (neuer Flag als Vertrag) sowie
`docs/NETSURF.md`. Die Umsetzung ist ein eigener Auftrag und mit den Besitzern dieser Dateien abzustimmen.
