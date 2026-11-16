# HTTPS für NetSurf – Entwurf

**Status:** Entwurf vom 15. November 2026, nichts davon ist umgesetzt oder geprüft. Grundlage ist der Stand von
`fix/netsurf-net-fehlercodes` (Netz-ABI aus #69, NetSurf-Fetcher aus #70). Die Sicherheitsfragen hat laser am
15. November 2026 entschieden (Abschnitt [Entscheidungen](#entscheidungen-vom-15-november-2026)). Weiter offene Punkte
sind mit **OFFEN** markiert. Bezug: [ROADMAP](../ROADMAP.md) 12.2, [NETWORK_TESTS.md](NETWORK_TESTS.md),
[NETSURF.md](NETSURF.md), [PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md).

## Ausgangslage

- **Shell und Paketquellen** sprechen bereits HTTPS. `CosmosNetworkService.Exchange` legt `TlsTransport.Open` über einen
  blockierenden `PollingStream` und hält dabei die Sperre `CosmosNetworkService.Gate` für den ganzen Abruf.
  `TlsTransport` nutzt BouncyCastle 2.7.0 (`TlsClientProtocol`). Erlaubt sind nur TLS 1.2/1.3 mit ECDHE/AEAD,
  höchstens 8 Zertifikate und 32768 Bytes je Handshake-Nachricht. Ketten- und Namensprüfung übernimmt
  `ThirdParty/CosmosHttp/CertificateVerifier.cs`, Vertrauensanker sind die 121 Mozilla-Wurzeln aus der Ressource
  `ZehOS.TlsRoots`. Zufall kommt aus `CosmosSaltSource.Fill` (RDSEED, sonst RDRAND, kein Software-Rückfall), und
  UTC muss im Bereich 2026–2100 liegen. Eine Option zum Abschalten der Prüfung gibt es nicht.
- **Wasm-Apps** erhalten über `zehos_v1.net_*` nur TCP. `WasmNetworkSession.Open` lehnt `flags != 0` mit `ENOTSUP`
  (58) ab. Der gemeinsame Worker in `CosmosWasmNetworkConnector` bedient alle Verbindungen nacheinander und pumpt Bytes
  nicht blockierend zwischen 64-KiB-Puffern und Socket. Das Zeitlimit von 10 s gilt von `net_open` bis zum
  Verbindungsaufbau.
- **NetSurf** meldet `https://` in `frontend/fetch_http.c` ausdrücklich als nicht unterstützt und ruft `net_open`
  immer mit `flags = 0` auf. Der Linux-Prüfhost (`host/zehos_host_linux.c`) lehnt Flags ebenfalls mit `ENOTSUP` ab.

## Ziel und Nicht-Ziele

Ziel: NetSurf lädt `https://`-Seiten mit derselben Zertifikatsprüfung wie die Shell. Ein Zertifikatsfehler zeigt eine
Fehlerseite und lässt sich nicht übergehen. Ein HTTPS-Fehler führt nie zu einem selbsttätigen Rückfall auf HTTP
(ROADMAP 10.8).

Nicht Teil dieses Schritts: HTTP/2 beziehungsweise ALPN, Client-Zertifikate, Sperrprüfung (OCSP/CRL),
Sitzungswiederaufnahme, eigene Vertrauensanker je App und eine Pflege des Vertrauensspeichers zur Laufzeit.

## Entscheidungen vom 15. November 2026

Von laser über den Koordinator entschieden:

1. **Weiterleitung HTTPS → HTTP** wird nicht selbsttätig befolgt. Die Fehlerseite nennt das Ziel und bietet den Knopf
   „Trotzdem fortfahren“, der die HTTP-Adresse ausdrücklich lädt. Diesen Knopf gibt es nur für diesen Fall. Die
   Regel betrifft HTTP-3xx-Weiterleitungen, die der Fetcher erkennt; Meta-Refresh und gewöhnliche Links von einer
   HTTPS- auf eine HTTP-Seite fallen nicht darunter.
2. **Zertifikatsfehler** zeigen eine Fehlerseite ohne Möglichkeit zum Fortfahren.
3. **Antworten ohne Länge** (weder `Content-Length` noch `chunked`) gelten über TLS nur mit `close_notify` als
   vollständig; ohne `close_notify` meldet der Fetcher sie als abgeschnitten.
4. **Getrennte Fehlercodes:** `EPERM` (63) für abgelehnte Zertifikate, `EPROTO` (65) für TLS-Protokollfehler.
5. **Eigener Fehlercode für fehlende Voraussetzungen** (Uhr, CPU-Zufall, Wurzelbundle), getrennt von „HTTPS nicht
   unterstützt“: `ENOSYS` (52).
6. **Mixed Content:** Auf HTTPS-Seiten werden `http://`-Unterressourcen (Bilder, CSS, alles) blockiert, ohne
   Fortfahren-Knopf; die Seite lädt ohne sie.
7. **Formulare von einer HTTPS-Seite an ein `http://`-Ziel** zeigen eine Warnseite „Unverschlüsselter Versand“; erst
   „Trotzdem senden“ schickt die Felder. Das gilt für POST und GET.

Dazu vom Koordinator festgelegt: Ein Meta-Refresh von HTTPS auf HTTP wird ohne Knopf blockiert (strenger als 1;
laser kann den Knopf nachfordern). Endet TCP ohne `close_notify`, meldet `net_recv` nach den Restdaten `ECONNRESET`
(15). Weil viele Server ohne `close_notify` schließen, muss der Fetcher bei `Content-Length` oder dem letzten Chunk
sofort fertig sein und schließen, ohne auf EOF zu warten. Das tut er bereits: `http_response_feed` meldet dann
`HTTP_DONE`, und `fetch_http.c` beendet den Abruf ohne EOF. Nur bei Antworten ohne Länge zählt das Ende; dort
bedeutet `ECONNRESET` „abgeschnitten“ statt der heutigen Meldung „Verbindung abgebrochen“.

## Entscheidung A: TLS im Kernel statt im Gast

| Weg | Vorteile | Nachteile |
|-----|----------|-----------|
| **Kernel-TLS (empfohlen)** | Ein geprüfter Weg für Shell, Paketquellen und Apps; ein Vertrauensspeicher; die App sieht nie Schlüsselmaterial der Sitzung; Zufall und Uhr bleiben im Kernel | Neue Hostfunktionalität im Loader; Handshake-Rechenzeit im Netzwerkworker |
| TLS im Gast (z. B. BearSSL oder mbedTLS nach WASIp1) | Kein Kerneleingriff | Zweiter Vertrauensspeicher im Paket, eigene Pflege und Prüfung; Zufall über `random_get`, Uhr über `clock_time_get`; größeres Paket; jede App bringt eigenes TLS mit |

Für den Kernelweg spricht auch die Projektregel, fehlende Fähigkeiten als allgemeine Hostfunktion des Loaders zu
ergänzen und nicht pro App. Die Prüfung von Zertifikaten ist sicherheitskritisch und soll nur einmal existieren.

## Entscheidung B: Flag in `net_open` statt eigenem Import

Vorschlag: Bit 0 von `flags` heißt `ZEHOS_NET_TLS` (Wert 1). Alle übrigen Bits bleiben reserviert und liefern
weiterhin `ENOTSUP`.

- **Abwärtskompatibel:** Ein älterer Kernel antwortet heute schon mit `ENOTSUP` (58). Eine App kann HTTPS dann
  ausdrücklich als nicht verfügbar anzeigen. Ein neuer Import `net_open_tls` bliebe auf älteren Kerneln dagegen
  ungebunden: `link_wasi` in `Native/Wasm/host/zehos_wasm.c` bindet nur angebotene Funktionen, und wasm3 meldet
  `m3Err_functionImportMissing` erst beim ersten Aufruf. Die App startet also, endet aber beim ersten HTTPS-Versuch
  mit einem Trap.
- **Gleiche Handles:** `net_status`, `net_send`, `net_recv` und `net_close` bleiben unverändert. Sie übertragen bei
  TLS Klartext der Anwendung; Verschlüsselung und Records erledigt der Kernel.
- **Prüfname:** Der an `net_open` übergebene `host` ist zugleich SNI und Prüfname. Ein IPv4-Literal wird ohne SNI
  gegen die IP-SAN geprüft, wie im Shellweg. Eine App kann damit keinen anderen Namen prüfen lassen, als sie verbindet.

**OFFEN:** Ob ein späterer Import für Zertifikatsdetails (`net_tls_info`) nötig wird, etwa für NetSurfs
Seiteninformationen. Für die erste Stufe genügt ein Fehlercode.

## Vorgeschlagener ABI-Vertrag

| Funktion | Verhalten mit `ZEHOS_NET_TLS` |
|----------|-------------------------------|
| `net_open` | Wie bisher DNS und TCP, danach der TLS-Handshake. Sofortiger Fehler bei ungültigem Host oder Port und ohne `capability=net` (`EACCES`). Unbekannte Bits: `ENOTSUP`. |
| `net_status` | `EAGAIN`, bis der Handshake einschließlich Zertifikatsprüfung abgeschlossen ist; danach 0 oder ein Fehler (siehe unten). Das bestehende Zeitlimit von 10 s umfasst DNS, TCP und Handshake. |
| `net_send` | Übernimmt Klartext in den 64-KiB-Sendepuffer; vor Handshake-Ende wie bisher der Status als Fehler. |
| `net_recv` | Liefert entschlüsselten Klartext. Nach `close_notify` EOF als Erfolg mit `out_n = 0`. Endet TCP ohne `close_notify`, liefert der Aufruf nach den Restdaten `ECONNRESET` (15). |
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
   dann dieselben Suiten, Grenzen und Prüfungen. Eine zweite TLS-Konfiguration darf nicht entstehen. Einziger
   bewusster Unterschied ist `RequiresCloseNotify`: Die Shell bleibt bei `false`, der App-Pfad setzt `true` als
   Parameter des gemeinsamen Clients. Dann wirft BouncyCastle `TlsNoCloseNotifyException`, wenn die Gegenseite ohne
   `close_notify` schließt (laut Paketdokumentation 2.7.0), und der Worker bildet das auf `ECONNRESET` ab.
3. **Sperren kurz halten:** Nur Socketaufrufe laufen unter `CosmosNetworkService.Gate`. Die TLS-Rechenarbeit
   (Handshake, Entschlüsselung) läuft außerhalb davon und auch außerhalb der Sperre `gate` der Verbindung, sonst
   warten `net_send` und `net_recv` im App-Thread auf den Handshake. Weil ein Worker alle vier Verbindungen bedient,
   hält ein langer `OfferInput`-Aufruf auch die übrigen Verbindungen auf.
4. **Voraussetzungen vorab prüfen:** UTC, Wurzelbundle und einen Aufruf von `CosmosSaltSource.Fill` schon in
   `net_open` prüfen und bei Fehlern sofort `ENOSYS` (Entscheidung 5) zurückgeben; die Prüfungen sind billig. Ein Entropiefehler innerhalb von BouncyCastle käme sonst
   vermutlich als `TlsFatalAlert` an und erschiene als Protokollfehler (nicht im Quelltext geprüft).
5. **Fehler nach Herkunft zuordnen:** `EPERM` nur für Ausnahmen aus der eigenen `Authentication.NotifyServerCertificate`,
   nicht anhand der Alertnummer. Vom Server gesendete Alerts kommen als `TlsFatalAlertReceived` und ergeben `EPROTO`.
6. **Wurzeln einmal parsen:** `TlsTransport.Open` parst das 189-KB-Bundle heute bei jedem Aufruf. Für bis zu
   4 parallele App-Verbindungen sollten die gefilterten Anker einmal pro Kernellauf vorliegen.
   **OFFEN:** Messung von Parsezeit und Speicher im Gast, bevor zwischengespeichert wird.
7. **Ausnahmen bleiben im Worker:** Jede Ausnahme wird im Worker auf einen Fehlercode abgebildet. Wie bei
   `CosmosWasmNetwork.cs` darf nichts die native Grenze (`UnmanagedCallersOnly`) überqueren, und Aufräumen auf
   Fehlerwegen geschieht nach `catch`, nicht in `finally` (Cosmos 3.0.94).

**OFFEN:** Rechenzeit eines Handshakes (RSA-2048- oder ECDSA-P-256-Kette) im Gast. Der Round-Robin-Scheduler mit
20-ms-Zeitscheiben hält die Oberfläche zwar bedienbar, aber ein langer Handshake kann das 10-s-Zeitlimit
ausschöpfen. Messung in QEMU (TCG und KVM) vor der Festlegung eines eigenen Handshake-Limits.

## Zertifikatsprüfung und Vertrauensspeicher

- Gleiche Anker wie die Shell (`ZehOS.TlsRoots`), gleiche Prüfung (`CertificateVerifier.Verify`: Kette,
  Verwendungszweck `serverAuth`, Gültigkeit gegen UTC, Hostname einschließlich Platzhalter nur für das erste Label).
- Keine App-eigenen Anker und keine Übergehen-Option (Entscheidung 2), auch nicht für Tests in normalen Builds.
- **Test-CA im App-Pfad:** Die Netzwerkprobe übergibt ihre Test-CA heute nur an den eigenen Abruf (`rootsOverride`
  in `CosmosNetworkService.Fetch`); der App-Pfad läse `TlsRoots.Load()` und sähe sie nicht. **OFFEN:** Wie ein
  ausdrücklich gebauter Probe-Kernel (Wasm und Netzwerkprobe zugleich) die Test-CA für App-Verbindungen bereitstellt,
  ohne dass normale Builds einen solchen Weg enthalten.
- Ohne sichere Entropie oder bei unplausibler UTC-Zeit scheitert jeder TLS-Handshake mit dem Code aus Entscheidung 5;
  HTTP bleibt dann möglich.
- Keine Sperrprüfung und keine Aktualisierung des Bundles zur Laufzeit. Ein neues Bundle kommt nur mit einem
  Kernelupdate; das bleibt Teil von ROADMAP 12.2.

## Fehlercodes

Werte nach WASI Preview 1 (`wasi-libc` `__errno_values.h`):

| Fall | Code | Bemerkung |
|------|------|-----------|
| Kernel ohne TLS oder unbekanntes Flag | `ENOTSUP` (58) | sofort in `net_open`; Fehlerseite „HTTPS nicht unterstützt“ |
| Keine sichere Entropie, UTC unplausibel, Bundle fehlt | `ENOSYS` (52) | sofort in `net_open`; Entscheidung 5; Fehlerseite nennt Uhr oder CPU-Zufall als mögliche Ursache |
| Zertifikat abgelehnt (Kette, Zweck, Zeit, Name, nicht lesbarer Schlüssel) | `EPERM` (63) | Entscheidung 4; nicht `EACCES`, das bleibt der fehlenden Berechtigung vorbehalten |
| Sonstiger TLS-Protokollfehler oder Alert des Servers | `EPROTO` (65) | Entscheidung 4; z. B. keine gemeinsame Suite oder Version |
| Zeitlimit bis Handshake-Ende | `ETIMEDOUT` (73) | wie bisher |
| Übrige Fehler | `EIO` (29) | wie bisher; Cosmos-Einschränkungen aus [NETSURF.md](NETSURF.md#netzwerkfehler) gelten weiter |

## NetSurf-Seite

- `fetch_http.c`: Bei `NSURL_SCHEME_HTTPS` den Standardport 443 setzen und `net_open` mit `ZEHOS_NET_TLS` aufrufen.
  `ENOTSUP` vom Kernel zeigt die bisherige Seite „HTTPS nicht unterstützt“.
- Zertifikats- und TLS-Fehler über `FETCH_ERROR` mit eigener Meldung melden, nicht über `FETCH_CERT_ERR` oder
  `FETCH_SSL_ERR`. `FETCH_CERT_ERR` führt zu `about:query/ssl` mit einem Fortfahren-Knopf
  (`urldb_set_cert_permissions`), `FETCH_SSL_ERR` lässt `llcache` mit `downgrade_tls` erneut versuchen. Beides
  widerspricht Entscheidung 2.
- Weiterleitung HTTPS → HTTP: Fehlerseite mit „Trotzdem fortfahren“ (Entscheidung 1). Der Knopf löst einen neuen,
  vom Benutzer ausgelösten Abruf der HTTP-Adresse aus; es entsteht keine dauerhafte Ausnahme. NetSurf 3.11 hat dafür
  keine Seite: `FETCH_ERROR` führt zu `about:query/fetcherror`, das nur „Zurück“ und „Erneut versuchen“ (wieder die
  HTTPS-Adresse) anbietet. Deshalb erkennt der Fetcher die 3xx-Weiterleitung HTTPS → HTTP selbst und liefert eine
  eigene HTML-Seite (`no-store`) mit dem Link „Trotzdem fortfahren“ auf die HTTP-Adresse (mit dem Koordinator
  abgestimmt). Die Ziel-URL ist dabei Fremdeingabe: HTML-escapen (`& < > " '`) und nur Schema `http` zulassen, sonst
  Fehlerseite ohne Link.
- Mixed Content (Entscheidung 6): Der Fetcher lehnt `http://`-Abrufe ab, die nicht vom Benutzer ausgelöst wurden
  (nicht verifizierbar) und eine HTTPS-Seite als Referer haben. Weil `struct fetch` privat ist, liest er beides über
  zwei Getter aus `patches/netsurf-zehos-mixed-content.patch`. Derselbe Patch erzwingt in `llcache` für solche
  Abrufe einen Neuabruf (sonst kämen sie am Fetcher vorbei aus dem Cache), gibt Favicons die Seite als Referer und
  lässt einen Meta-Refresh von HTTPS auf HTTP unverifizierbar. Eine Unterressource mit Weiterleitung HTTPS → HTTP
  scheitert ohne Warnseite.
- Formulare (Entscheidung 7): Ein POST bleibt im Speicher des Fetchers (höchstens einer, 5 Minuten); die Warnseite
  postet nur ein Einmaltoken (16 Byte aus `getentropy`, zeitkonstanter Vergleich). Ein falsches, kurzes oder
  abgelaufenes Token ergibt eine lokale Fehlerseite ohne Netzabruf. GET-Formulare markiert `html/form.c` (Patch);
  die Warnseite verlinkt die GET-URL. Warnseiten laden nichts nach (eigenes Favicon über `resource:`), damit die
  Ziel-URL nicht vor dem Klick als Referer abfließt. Der Fetcher liefert die Warnseite als 303-Weiterleitung auf
  eine `data:`-URL statt unter der Ziel-URL. Lokale Navigation, Neuladen und Zurück/Vor nutzen deshalb die
  `data:`-URL: NetSurf trägt die URL des Inhalts nach der Weiterleitung in die History ein (`content_get_url`
  liefert das Zielobjekt von `llcache_fetch_redirect`), und `llcache` verwirft die Cache-Daten des umgeleiteten
  Objekts. Die Ziel-URL steht weiter nur im Speicher: in `current_parameters` des Browserfensters, in `urldb`
  (`urldb_add_url` für die Weiterleitungsquelle) und in den Metadaten des alten llcache-Objekts. `urldb` wird
  nicht gespeichert; das Framebuffer-Frontend schreibt nur Cookies (`urldb_save_cookies`). Weil NetSurf über
  `current_parameters` das Ziel ohne Formularmarke erneut abrufen kann („Zurück“ auf einer Fehlerseite,
  `browser_window__reload_current_parameters`, ebenso „Erneut versuchen“), merkt sich der Fetcher die gewarnten
  Ziele (Hash, 256 Einträge, ohne Frist) und warnt bei jedem Abruf mit HTTPS-Referer erneut. Den Referer liest er
  aus `struct fetch`, weil NetSurf von HTTPS an HTTP keinen `Referer`-Kopf mitgibt. Bestätigt wird nur von der
  `data:`-Seite aus: Der Link beziehungsweise das Token löst genau einen Abruf aus, und von dort sendet NetSurf
  keinen `Referer`-Kopf, weil das Schema nicht passt (`get_referer_header` in `llcache.c`). Grenzen: Bei POST
  steht das Token in der `data:`-URL (Adressleiste, History); es gilt nur einmal und höchstens 5 Minuten. Nach 256
  weiteren Formularwarnungen fällt ein Ziel aus der Liste; je Fenster ersetzt jede neue Warnung das dort
  erreichbare Ziel, verdrängen können also nur Warnungen in anderen Fenstern (Frames, iframes). Kommt ein POST-Ziel
  über „Zurück“ auf einer Fehlerseite zurück, gilt die Warnung einem GET-Abruf ohne Felder.
- HSTS: NetSurfs vorhandenes `urldb` wertet `Strict-Transport-Security` aus HTTPS-Antworten aus und hebt
  `http://`-Adressen solcher Hosts in `llcache` auf HTTPS, nur im Speicher bis zum Beenden. Eine Weiterleitung auf
  so einen Host gilt deshalb nicht als Herabstufung (keine Warnseite).
- Mit `Content-Length` oder `chunked` nach dem letzten Byte beziehungsweise Chunk sofort abschließen und schließen,
  ohne auf EOF zu warten. Bei Antworten ohne Länge ein `ECONNRESET` als abgeschnitten melden (Entscheidung 3).
- Der Prüfhost `host/zehos_host_linux.c` erhält denselben Flag über OpenSSL mit einer Test-CA aus einer
  Umgebungsvariable, analog zu `ZEHOS_NET=1`. Er bildet dieselben Codes nach (`ENOSYS`, `EPERM`, `EPROTO`,
  `ECONNRESET` bei EOF ohne `close_notify`), sonst sagt der Vergleich mit dem Kernel nichts. Er ersetzt keinen
  Kerneltest.

## Grenzen

- **Cosmos-Warteabschnitte:** Connect und Send in Cosmos 3.0.94 sind nicht sofort abbrechbar. Das Zeitlimit greift
  erst nach Rückkehr des Aufrufs, wie bei HTTP ([PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md)).
- **Ein Worker für alle Verbindungen:** Die Schritte der Verbindungen wechseln sich ab, aber jeder `OfferInput`-Aufruf
  hält die übrigen Verbindungen der App auf, solange er rechnet.
- **`close_notify`:** Server, die TLS ohne `close_notify` beenden, liefern Antworten ohne Länge nur als abgeschnittene
  Fehlerseite (Entscheidung 3). Mit `Content-Length` oder `chunked` erkennt der Fetcher Vollständigkeit selbst.
- **Keine Sitzungswiederaufnahme:** Jede Verbindung führt einen vollen Handshake aus. Mit höchstens 4 Verbindungen
  und dem Rechenaufwand im Gast wird das Laden vieler Ressourcen langsamer.
- **Uhr:** Die Gültigkeitsprüfung hängt an der vom Hypervisor gelieferten RTC; ein sicherer Zeitabgleich fehlt.
- **Plattformen:** VMware und echte Hardware sind für Netzwerk und TLS bisher ungeprüft.

## Testplan

1. **Hosttests (`ProjektZeh.Tests`):** Flag-Auswertung in `WasmNetworkSession` (Bit 0 erlaubt, übrige `ENOTSUP`),
   Abbildung auf die Codes oben einschließlich `TlsFatalAlertReceived` und vorab geprüfter Voraussetzungen, nicht
   blockierender TLS-Pfad gegen einen lokalen Testserver mit eigener CA: Erfolg, falscher Name, fremde CA, abgelaufen,
   noch nicht gültig, Ende ohne `close_notify`, Abbruch mitten im Handshake, 64-KiB-Grenzen für Klartext.
2. **Linux-Prüfhost (`host/run_tests.py`):** NetSurf lädt eine HTTPS-Seite von einem lokalen Python-`ssl`-Server;
   falscher Name ergibt die Zertifikatsfehlerseite ohne Fortfahren; HTTPS → HTTP zeigt „Trotzdem fortfahren“, und
   erst der Link lädt die HTTP-Seite, eine `Location` mit `<script>` oder `"`
   erscheint escaped, eine mit anderem Schema ohne Link; Antwort ohne Länge und ohne `close_notify` erscheint als abgeschnitten,
   Antworten mit `Content-Length` oder `chunked` ohne `close_notify` dagegen vollständig. Beide Seiten (Kernel-
   Hosttest und Prüfhost) prüfen diese Fälle.
3. **QEMU-Probe:** Probe-Kernel mit Wasm und Test-CA (Weg siehe OFFEN oben), dazu die vorhandenen TLS-Fixtures aus
   `scripts/test-network.py`: 18443 gültig, 18444 falscher Name, 18445–18447 fremde CA, abgelaufen und künftig sowie
   18448 schweigender Server für Zeitlimit und Abbruch im Handshake. NetSurf-Gastprüfung analog zu
   `scripts/test-netsurf-desktop.py` mit Markerfarben; die Fehlerseiten sind über Bildschirmfotos visuell zu prüfen.
   Normale Builds enthalten keine Test-CA, das ist im Build zu prüfen.
4. **Messung:** Handshakezeit und Speicher je Verbindung bei 1 und 4 parallelen Verbindungen (TCG und KVM).
5. **Regression:** Shell-`fetch https://`, Paketquelle und HTTP-Seiten in NetSurf bleiben unverändert.
6. **VMware:** getrennt ausweisen, solange nicht geprüft.

## Betroffene Dateien bei einer Umsetzung

`ProjektZeh/Network/TlsTransport.cs`, `ProjektZeh/Runtime/Wasm/WasmNetwork.cs` und `CosmosWasmNetwork.cs`,
`ProjektZeh.Tests/`, `experiments/NetSurf.Wasi/frontend/fetch_http.c`, `host/zehos_host_linux.c`,
`host/run_tests.py`, `docs/PAKETE_ERSTELLEN.md`, `docs/PACKAGE_FORMAT.md` (neuer Flag als Vertrag) sowie
`docs/NETSURF.md`. Die Umsetzung ist ein eigener Auftrag und mit den Besitzern dieser Dateien abzustimmen.
