# NetSurf als ZehOS-Paket (WASIp1)

NetSurf 3.11 mit dem Framebuffer-Frontend als `wasm32-wasip1`-Modul. Eine eigene libnsfb-Oberfläche (`frontend/zehos.c`) zeichnet über `zehos_v1.present` und liest Eingaben über `zehos_v1.poll_event`. Quellen und Werkzeuge werden nicht eingecheckt; `build.py` lädt sie mit festen SHA-256-Werten nach `.tools/netsurf/`.

## Bauen

Auf einem Linux-Host mit `make`, `cc`, `c++`, `m4`, `perl`, `bison`, `patch` sowie den Entwicklungsdateien von libpng und zlib (für NetSurfs Hostwerkzeug `convert_image`). gperf 3.1 und flex 2.6.4 baut `build.py` selbst aus den Upstream-Archiven, weil andere gperf-Versionen und Ubuntus gepatchtes flex abweichende Generatorausgaben erzeugen. `nsgenbind` (erzeugt die JavaScript-Bindings) baut es mit dem Host-`cc` aus dem NetSurf-Archiv. Für den letzten Schritt lädt es binaryen `version_133` als vorgebautes Release (SHA-256 geprüft). Das Modul enthält keine absoluten Baupfade (`-ffile-prefix-map`), damit es auf jedem Rechner gleich ausfällt:

```bash
python3 experiments/NetSurf.Wasi/build.py
```

Ergebnis ist `experiments/NetSurf.Wasi/bin/package/`:

| Pfad | Inhalt |
| --- | --- |
| `netsurf.wasm` | Einstieg `_start` |
| `res/` | Messages, CSS, interne Seiten, `Choices` mit Startseite `file:///pages/index.html` und ausgeschaltetem JavaScript (`enable_javascript:0`) |
| `pages/` | Testseiten: Start (blau), Linkziel (grün), langer Text (orange), Eingabefeld (lila), kaputter Link |
| `licenses/` | NetSurf (GPL-2.0), NetSurf-Bibliotheken (MIT), Duktape (MIT, aus dem Kopf von `duktape.h`), utf8proc, zlib |

Bestandteile:

| Baustein | Stand |
| --- | --- |
| NetSurf | `netsurf-all-3.11.tar.gz` |
| wasi-sdk | 25.0 für Linux (dieselbe Version wie für Doom) |
| zlib | 1.3.1 |
| Duktape | 2.7.0, im NetSurf-Archiv enthalten |
| binaryen | `version_133`, nur Bauwerkzeug (`wasm-opt`) |

Zwei Bibliotheken fehlen bewusst: libsvgtiny (SVG bleibt aus), und libdom verzichtet auf expat und nutzt hubbub.

Patches (`patches/`, mit `patch -p1` auf den Quellbaum):

- `libnsfb-wasi-dump.patch`: `nsfb_dump` ohne `dup()`, das WASI nicht kennt.
- `libnsfb-zehos-surface.patch`: Oberflächentyp `NSFB_SURFACE_ZEHOS` vor `RAM`, damit das Frontend ihn ohne Kommandozeile wählt.
- `netsurf-curl-include.patch`: `curl.h` nur mit `WITH_CURL` einbinden (Upstream-Fehler bei `NETSURF_USE_CURL=NO`).
- `netsurf-testament-reproducible.patch`: Das Build-Testament (`about:testament`) enthält feste Werte statt Benutzername, vollem Namen, Rechnername, Baupfad und Tagesdatum. Das schützt persönliche Daten im Paket und macht den Build reproduzierbar (Datum aus `SOURCE_DATE_EPOCH`).
- `netsurf-pointer-cancel.patch`: Steuercode `FBTK_CONTROL_CANCEL_POINTER` beendet eine gehaltene Maustaste oder ein Ziehen ohne Klick (Fokusverlust). Das gilt auch für Rollbalken, die den Zeiger selbst greifen (`fbtk_cancel_grab`). Eine weitere Taste während eines Rollbalken-Ziehens startet kein zweites Ziehen, das den Grab umschalten und lösen würde.
- `netsurf-fbtk-text.patch`: Tastencodes ab `0x40000000` tragen Unicode-Zeichen aus `poll_event`-Texten. Die Adressleiste speichert ein Byte je Zeichen und nimmt deshalb nur druckbares ASCII an. Zusätzlich löscht Entf im Textfeld das Zeichen rechts der Einfügemarke; das fehlte im fbtk.

Ausgeschaltet sind (`Makefile.config`): curl/OpenSSL (HTTP liefert stattdessen der eigene Fetcher, siehe unten), PNG, JPEG, WebP und SVG. Duktape ist eingebaut, JavaScript aber aus (NetSurf-Standard, in `Choices` ausdrücklich `enable_javascript:0`). Duktape meldet Fehler über setjmp/longjmp: Der Compiler übersetzt sie mit `-mllvm -wasm-enable-sjlj` und `-lsetjmp` in das alte Wasm-Exception-Handling, das wasm3 nicht kennt. Deshalb wandelt `wasm-opt --enable-exception-handling --translate-to-exnref` das gelinkte Modul in die exnref-Form um. Der C-Stapel ist auf 1 MiB erhöht (`-z stack-size`), weil 64 KiB mit Duktape nicht reichen. `mmap` und `signal` kommen aus der Emulation der wasi-libc, `iconv` aus der wasi-libc selbst.

## Lizenztexte

`licenses/` enthält die Texte, die weder NetSurf noch wasi-sdk 25 mitliefern; sie gehören in jedes Paket:

| Datei | Quelle | Bezug |
| --- | --- | --- |
| `MPL-2.0.txt` | mozilla.org/MPL/2.0 | `public_suffix_list.dat` in libnspsl |
| `wasi-libc-*` | wasi-libc `574b88da481569b65a237cb80daf9a2d5aeaf82d` (Submodul von wasi-sdk-25) | statisch gelinkte libc einschließlich musl-Teilen und Emulationsbibliotheken |
| `compiler-rt-LICENSE.TXT` | llvm-project `ab4b5a2db582958af1ee308a790cfdb42bd24720`, `compiler-rt/` (Submodul von wasi-sdk-25) | statisch gelinkte Laufzeitfunktionen des Compilers |

Ein veröffentlichbares Paket entsteht nur aus einem sauberen Commit auf `origin/master`. Sonst schreibt `build.py` eine Warnung in `SOURCES.txt`, weil der Quellverweis (GPL) dann nicht dauerhaft erreichbar ist.

## Schnittstelle zum Loader

Importe des Moduls:

- `zehos_v1`: `present`, `poll_event`, `ticks_ms`, `sleep_ms`, `should_exit`
- WASI Preview 1: `args_get`, `args_sizes_get`, `environ_get`, `environ_sizes_get`, `clock_time_get`, `fd_close`, `fd_fdstat_get`, `fd_fdstat_set_flags`, `fd_pread`, `fd_prestat_get`, `fd_prestat_dir_name`, `fd_read`, `fd_readdir`, `fd_seek`, `fd_tell`, `fd_write`, `path_create_directory`, `path_filestat_get`, `path_open`, `path_readlink`, `proc_exit`

Annahmen:

- **Bild:** festes Bild 798×519 (Inhalt des Paketfensters beim Öffnen, 800×560), 0x00RRGGBB, immer vollständig je `present`.
- **Laufzeit:** Die Hauptschleife wartet höchstens 16 ms je `sleep_ms` und fragt dazwischen `should_exit` ab. Danach endet `_start` regulär.
- **Dateien:** `stat()`/`access()` (Ressourcen, `file:`-URLs) brauchen `path_filestat_get`, `realpath()` braucht `path_readlink` (für Nicht-Links `EINVAL`). `fd_readdir` ist nur für Verzeichnislisten nötig.
- **Tasten:** normalisierte ZehOS-Tastencodes (Set-1-Nummern ohne E0-Merkmal), zum Beispiel Pfeile 72/75/77/80 und Entf 83.
- **Text:** Zeichen kommen ausschließlich aus `poll_event`-Texten (Typ 3, Unicode). Zeichentasten ohne Strg erzeugen kein Tastenereignis, damit nichts doppelt getippt wird. Strg-Kürzel nutzen die US-Zuordnung der Taste.
- **Maus:** Maustasten-Code 1/2/4 = links/rechts/Mitte. Das Rad liefert je Rastung Maustaste 4 (aufwärts) oder 5 (abwärts) an fbtk. Maustasten und Rad wirken an der Position ihres eigenen Ereignisses (vorab Bewegung dorthin). `reset` lässt gehaltene Tasten los und bricht gehaltene Maustasten ohne Klick ab; Loslassen ohne vorheriges Drücken wird verworfen.

## Ohne VM prüfen

Der Prüfhost `zehos-wasm3` übersetzt denselben wasm3-Kern (`ProjektZeh/ThirdParty/wasm3`) und dieselbe Importbindung (`ProjektZeh/Native/Wasm/host/zehos_wasm.c`) wie der Kernel, mit dessen Grenzen: 1 MiB Wasm-Stack, 256 MiB Speicher, 7 MiB natives Budget auf einem 8-MiB-Thread, kein Gas. Er prüft also dasselbe Parsen, dieselben Wasm-Features (Exception-Handling nur als exnref) und dieselben Gastprüfungen. `-Dd_m3HasPosixHost=0 -Dd_m3GuardedMemory=0` sind dafür nötig: Unter Linux schaltet wasm3 sonst auf Schutzseiten statt der expliziten Speichergrenzprüfungen des Kernels um. Es bleibt der Unterschied der C-Laufzeit (Kernel: `-nostdinc -fno-builtin` mit eigener libc). Pfade gehen roh statt UTF-8-dekodiert an das Linux-Dateisystem, das Groß- und Kleinschreibung unterscheidet; der Prüfhost ist dort strenger als FAT im Kernel. `host/zehos_host_wasm3.c` liefert nur die Linux-Seite der WASI-Rückrufe (Paketordner nur lesend) und den Start; `host/zehos_host_core.c` bildet `zehos_v1` nach. Er schreibt jedes Bild als PPM, liefert Ereignisse aus einer Datei und meldet `should_exit` nach einer festen Zeit. Exitcode ist der des Gasts (Werte außerhalb 0–99 als 99), bei einem Loaderfehler 100 plus `ZEHOS_WASM_*`-Status (etwa 105 Trap).

```bash
W=ProjektZeh/ThirdParty/wasm3/source
mkdir -p .tools/netsurf-host
gcc -O2 -Dd_m3MaxNativeStack=7340032 -Dd_m3HasPosixHost=0 -Dd_m3GuardedMemory=0 -I$W -Iexperiments/NetSurf.Wasi/host $W/*.c ProjektZeh/Native/Wasm/host/zehos_wasm.c \
  experiments/NetSurf.Wasi/host/zehos_host_core.c experiments/NetSurf.Wasi/host/zehos_host_wasm3.c \
  -o .tools/netsurf-host/zehos-wasm3 -lssl -lcrypto -lm -lpthread
ZEHOS_NET=1 ZEHOS_PPM=frame.ppm ZEHOS_EXIT_MS=8000 .tools/netsurf-host/zehos-wasm3 \
  experiments/NetSurf.Wasi/bin/package experiments/NetSurf.Wasi/bin/package/netsurf.wasm
```

Automatische Szenarien (Klick, Reset, kaputter Link, Rad, Adressleiste mit Pos1/Entf) prüfen das letzte Bild anhand der Markerfarben der Testseiten:

```bash
python3 experiments/NetSurf.Wasi/host/run_tests.py --wasm3 .tools/netsurf-host/zehos-wasm3
```

Optional bleibt der WAMR-Weg: `host/zehos_host_linux.c` stellt `zehos_v1` als Native-Bibliothek für `iwasm` bereit (`iwasm` mit `-rdynamic` gebaut). WAMR führt Exception-Handling nur im Legacy-Format aus; ein Modul mit exnref, etwa mit setjmp/longjmp über `--translate-to-exnref`, lädt dort nicht. Dieser Weg prüft deshalb nur Module ohne Exception-Handling.

```bash
cd .tools/netsurf-host
cmake -S wamr/product-mini/platforms/linux -B build-iwasm -DCMAKE_BUILD_TYPE=Release \
  -DWAMR_BUILD_LIBC_WASI=1 -DWAMR_BUILD_LIB_PTHREAD=0 -DCMAKE_EXE_LINKER_FLAGS=-rdynamic
cmake --build build-iwasm -j
gcc -O2 -shared -fPIC -Iwamr/core/iwasm/include -I../../experiments/NetSurf.Wasi/host \
  ../../experiments/NetSurf.Wasi/host/zehos_host_linux.c ../../experiments/NetSurf.Wasi/host/zehos_host_core.c \
  -o libzehos_host.so -lssl -lcrypto
cd ../..
python3 experiments/NetSurf.Wasi/host/run_tests.py --iwasm .tools/netsurf-host/build-iwasm/iwasm \
  --host .tools/netsurf-host/libzehos_host.so
```

Die HTTP-Szenarien starten dazu einen lokalen Testserver und prüfen eine Seite, eine zweistufige relative Weiterleitung, `chunked`, 404, eine abgelehnte Verbindung, eine abgebrochene Verbindung (RST), eine fehlende Netzwerkberechtigung, einen Cookie über eine Weiterleitung und `https://` ohne Server. Die HTTPS-Szenarien erzeugen mit `openssl` eine Test-CA und prüfen gegen lokale TLS-Server eine Seite, `chunked`, eine Weiterleitung, einen falschen Zertifikatsnamen, die Warnseite bei HTTPS→HTTP, blockierten Mixed Content (direkt, über eine Weiterleitung und als Favicon, mit HTTP-Gegenprobe), eine Antwort ohne Länge mit und ohne `close_notify`, HSTS (mit Gegenprobe) sowie Formulare an `http://` (Warnseite, Senden nach Bestätigung für POST und GET, falsches und kurzes Token, keine Felder vor der Bestätigung im Netz, Neuladen und Zurück/Vor der Warnseite sowie „Zurück“ auf einer Fehlerseite ohne Abruf des Ziels, genau ein Abruf nach Bestätigung auch nach Neuladen). Der Prüfhost bildet `net_*` mit nicht blockierenden POSIX-Sockets nach; DNS blockiert dort, im Kernel nicht. Er hält die Kernelgrenzen ein (4 Verbindungen, 64 KiB je net_send/net_recv, EACCES ohne Berechtigung); ZEHOS_NET=1 ersetzt dort capability=net. TLS bildet er mit OpenSSL nach (bauen mit `-lssl -lcrypto`); `ZEHOS_TLS_CA=<PEM-Pfad>` setzt die vertraute CA, ohne sie meldet `net_open` mit TLS `ENOSYS`.

`wamr` ist WAMR-2.4.5 (Commit `25bd7eb63e828e4bd242cc9b38d260b4b31c6605`). Ereignisdateien (`ZEHOS_EVENTS`) enthalten je Zeile `<ms> <typ> <code> <x> <y>` nach dem `poll_event`-ABI.

## HTTP

`frontend/fetch_http.c` ist ein eigener NetSurf-Fetcher für `http://`; `build.py` kopiert ihn als `content/fetchers/zehos_http.c` in den Quellbaum, `patches/netsurf-zehos-http.patch` registriert ihn. Die Verbindung kommt über `zehos_v1.net_open`/`net_status`/`net_send`/`net_recv`/`net_close` vom Kernel. Den Antwortparser `frontend/http_response.c` (Statuszeile, Kopfzeilen, `Content-Length`, `chunked`, Lesen bis Verbindungsende) prüft `tests/http.c` nativ, auch byteweise zerstückelt.

- HTTP/1.1 mit `Connection: close`, GET und URL-codierte POST-Formulare; Dateiupload-Formulare werden abgelehnt.
- Weiterleitungen, 304 und Cache behandelt NetSurfs llcache wie beim curl-Fetcher.
- Grenzen je Antwort: Zeile 8 KiB, Kopf 64 KiB und 128 Zeilen, Rumpf 8 MiB, 30 s ohne Daten. Komprimierte Übertragung wird nicht angefordert; andere Transfer-Codierungen als `chunked` gelten als Fehler.
- Cookies verwaltet NetSurfs urldb wie beim curl-Fetcher: `Set-Cookie` wird übernommen, passende Cookies gehen als `Cookie` mit. Sie gelten nur bis zum Beenden: Das ZehOS-Frontend lädt und speichert keine Cookie-Datei, das Paket ist nur lesend eingebunden.
- Keine HTTP-Anmeldung (401); Seiten mit Login über HTTP-Authentifizierung funktionieren noch nicht.
- Höchstens 4 gleichzeitige Verbindungen (Kernelgrenze); `res/Choices` setzt `max_fetchers` und `max_fetchers_per_host` auf 4. Gesendet wird in Stücken bis 64 KiB.
- `https://` (Port 443) verschlüsselt und prüft der Kernel über `net_open` mit `ZEHOS_NET_TLS`, mit denselben Vertrauensankern wie die Shell. Ein Kernel ohne TLS meldet „HTTPS wird von diesem ZehOS-Kernel noch nicht unterstützt.“
- Zertifikatsfehler zeigen eine Fehlerseite ohne Fortfahren. Eigene Meldungen gibt es für TLS-Protokollfehler und für fehlende Uhr oder fehlenden CPU-Zufall.
- Eine Weiterleitung von HTTPS auf HTTP ersetzt der Fetcher durch eine Warnseite; erst „Trotzdem fortfahren“ lädt die HTTP-Adresse. Unterressourcen mit solcher Weiterleitung scheitern ohne Rückfrage.
- Mixed Content: Nicht vom Benutzer ausgelöste `http://`-Abrufe einer HTTPS-Seite (Bilder, CSS, Favicons) sind ohne Rückfrage blockiert, auch wenn sie im Cache lägen; die Seite lädt ohne sie. Frames und Meta-Refreshs von HTTPS auf HTTP zeigen stattdessen eine Fehlerseite ohne Fortfahren. `patches/netsurf-zehos-mixed-content.patch` macht dafür Verifizierbarkeit und Referer des Abrufs für den Fetcher lesbar, erzwingt in llcache einen Neuabruf und gibt Favicons die Seite als Referer.
- Formulare einer HTTPS-Seite an ein `http://`-Ziel gehen erst nach „Trotzdem senden“ auf einer Warnseite raus. POST-Daten bleiben dazu nur im Speicher des Fetchers (höchstens ein Formular, 5 Minuten); die Warnseite schickt nur ein Einmaltoken. Bei GET verlinkt die Warnseite die Ziel-URL; `html/form.c` markiert solche Abrufe per Patch. Die Warnseite lädt nichts nach (eigenes Favicon), damit die Ziel-URL nicht als Referer abfließt. Der Fetcher liefert sie als 303-Weiterleitung auf eine `data:`-URL aus; Neuladen und Zurück/Vor nutzen deshalb die `data:`-URL und zeigen wieder nur die Warnung (bei POST mit demselben, weiter nur einmal gültigen Token). Die Ziel-URL steht nur noch im Speicher (Fensterparameter, `urldb`, nicht gespeichert). Ruft NetSurf sie daraus ohne Formularmarke erneut ab, etwa mit „Zurück“ auf einer Fehlerseite, warnt der Fetcher wieder: Er merkt sich bis zu 256 gewarnte Ziele und verdrängt keines; ist die Liste voll, warnt er bei jedem solchen Abruf. Bestätigt werden nur Abrufe von der `data:`-Seite. Kommt auf diesem Weg ein POST-Ziel zurück, gilt die Warnung einem GET-Abruf ohne Felder, weil NetSurf den POST-Inhalt dabei verwirft. Die Adressleiste zeigt die `data:`-URL, bei POST samt Token (einmalig, 5 Minuten). Nach dem Klick steht das Ziel normal in der History; erneutes Laden des bestätigten Ziels fragt nicht noch einmal.
- HSTS: NetSurfs `urldb` wertet `Strict-Transport-Security` aus HTTPS-Antworten aus und hebt `http://`-Adressen solcher Hosts auf HTTPS, nur im Speicher bis zum Beenden. Eine Weiterleitung auf so einen Host zeigt deshalb keine Warnseite.
- Über TLS gilt eine Antwort ohne Längenangabe nur mit `close_notify` als vollständig, sonst als abgeschnitten. Der Link der Warnseiten wird HTML-maskiert; die Szenarien belegen das nur eingeschränkt, weil NetSurf `"`, `<` und `>` in URLs ohnehin prozentkodiert.
- Fehlerseiten nennen die Ursache: fehlende Netzwerkberechtigung, unbekannter Servername, abgelehnte oder abgebrochene Verbindung, Zeitüberschreitung, ungültige, unvollständige oder zu große Antwort.

## Grenzen

- **Tastatur:**
  - In der Adressleiste ist nur ASCII möglich; in Formularfeldern der Seite funktioniert Unicode.
  - Ziffernblocktasten fallen mit Pfeilen, Pos1, Ende, Bild auf/ab und Entf zusammen, weil die Tastencodes kein E0-Merkmal tragen.
  - Für Zurück gibt es kein Tastenkürzel, nur die Schaltfläche.
- **Inhalte:** nur HTTP ohne TLS, kein JavaScript, keine PNG-/JPEG-/SVG-Bilder; GIF und BMP sind enthalten.
- **Prüfstand:** Linux-Prüfhost unter WAMR (`host/run_tests.py`) und ZehOS-Loader (wasm3) in QEMU 10.2.2 mit TCG über `scripts/test-netsurf-desktop.py`: `NETSURF_GUEST PASS` am 15. November 2026, frei 442 488–443 192 KiB (rund 432 MiB) über drei Starts. Unter KVM besteht der Lauf mit `-cpu qemu64,+rdrand,+rdseed` (ohne RDRAND/RDSEED scheitert die Kontoanlage, siehe [#56](https://github.com/peterzw98/ProjektZehOS/issues/56)); VMware und echte Hardware ungeprüft.

## Geprüft (Linux-Prüfhost, WAMR-2.4.5, 15. November 2026)

| Ablauf | Ergebnis |
| --- | --- |
| Start ohne Argumente, Paketordner als `/` | Startseite `file:///pages/index.html`, Exit 0 nach `should_exit` |
| Klick auf Link | Formularseite geladen, Zurück-Schaltfläche aktiv |
| Klick ins Feld, Texte `Grüße @€`, Zeichentaste `g` ohne Text, Rücktaste | Feld zeigt `Grüße @` |
| Rad −5 auf langer Seite | Seite nach unten gescrollt |
| Kaputter Link | Seite „Not found“, Browser bedienbar |
| Maustaste auf Link gedrückt, `reset` | Seite bleibt, kein Klick |
| danach Klick auf anderen Link | Linkziel geladen, keine hängende Taste |
| Rollbalken ziehen, `reset`, Maus weiterbewegen | Seite rollt beim Ziehen, nach `reset` nicht mehr (Gegenprobe ohne `reset` rollt weiter) |
| Links auf Seite ziehen, rechte Taste dazu, `reset`, danach Linkklick | kein Klick durch `reset`, danach Link normal geöffnet |
| Rollbalken-Daumen ziehen, rechte Taste dazu, `reset`, Maus weiterbewegen | Seite rollt beim Ziehen, danach nicht mehr |
| Maustaste ohne vorherige Bewegung | Klick wirkt an der Ereignisposition |
