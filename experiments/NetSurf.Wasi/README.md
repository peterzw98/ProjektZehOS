# NetSurf als ZehOS-Paket (WASIp1)

NetSurf 3.11 mit dem Framebuffer-Frontend als `wasm32-wasip1`-Modul. Eine eigene libnsfb-Oberfläche (`frontend/zehos.c`) zeichnet über `zehos_v1.present` und liest Eingaben über `zehos_v1.poll_event`. Quellen und Werkzeuge werden nicht eingecheckt; `build.py` lädt sie mit festen SHA-256-Werten nach `.tools/netsurf/`.

## Bauen

Auf einem Linux-Host mit `make`, `cc`, `perl`, `gperf`, `patch` sowie den nativen Entwicklungspaketen für libpng und zlib (z. B. `libpng-dev`, `zlib1g-dev`). Die nativen Generatoren brauchen diese auch bei ausgeschalteten PNG-Inhalten:

```bash
python3 experiments/NetSurf.Wasi/build.py
```

Ergebnis ist `experiments/NetSurf.Wasi/bin/package/`:

| Pfad | Inhalt |
| --- | --- |
| `netsurf.wasm` | Einstieg `_start` |
| `res/` | Messages, CSS, interne Seiten, `Choices` mit Startseite `file:///pages/index.html` |
| `pages/` | Testseiten: Start (blau), Linkziel (grün), langer Text (orange), Eingabefeld (lila), kaputter Link |
| `licenses/` | NetSurf (GPL-2.0), NetSurf-Bibliotheken (MIT), utf8proc, zlib |

Bestandteile:

| Baustein | Stand |
| --- | --- |
| NetSurf | `netsurf-all-3.11.tar.gz` |
| wasi-sdk | 25.0 für Linux (dieselbe Version wie für Doom) |
| zlib | 1.3.1 |

Zwei Bibliotheken fehlen bewusst: libsvgtiny braucht setjmp/longjmp, und libdom verzichtet auf expat und nutzt hubbub.

Patches (`patches/`, mit `patch -p1` auf den Quellbaum):

- `libnsfb-wasi-dump.patch`: `nsfb_dump` ohne `dup()`, das WASI nicht kennt.
- `libnsfb-zehos-surface.patch`: Oberflächentyp `NSFB_SURFACE_ZEHOS` vor `RAM`, damit das Frontend ihn ohne Kommandozeile wählt.
- `netsurf-curl-include.patch`: `curl.h` nur mit `WITH_CURL` einbinden (Upstream-Fehler bei `NETSURF_USE_CURL=NO`).
- `netsurf-fbtk-text.patch`: Tastencodes ab `0x40000000` tragen Unicode-Zeichen aus `poll_event`-Texten. Die Adressleiste speichert ein Byte je Zeichen und nimmt deshalb nur druckbares ASCII an.

Ausgeschaltet sind (`Makefile.config`): curl/OpenSSL (kein Netz in diesem Schritt), Duktape (JavaScript, setjmp), PNG, JPEG, WebP und SVG. `mmap` und `signal` kommen aus der Emulation der wasi-libc, `iconv` aus der wasi-libc selbst.

## Schnittstelle zum Loader

Importe des Moduls:

- `zehos_v1`: `present`, `poll_event`, `ticks_ms`, `sleep_ms`, `should_exit`
- WASI Preview 1: `args_get`, `args_sizes_get`, `environ_get`, `environ_sizes_get`, `clock_time_get`, `fd_close`, `fd_fdstat_get`, `fd_fdstat_set_flags`, `fd_pread`, `fd_prestat_get`, `fd_prestat_dir_name`, `fd_read`, `fd_readdir`, `fd_seek`, `fd_tell`, `fd_write`, `path_create_directory`, `path_filestat_get`, `path_open`, `path_readlink`, `proc_exit`

Annahmen:

- **Bild:** festes Bild 632×392, 0x00RRGGBB, immer vollständig je `present`.
- **Laufzeit:** Die Hauptschleife wartet höchstens 16 ms je `sleep_ms` und fragt dazwischen `should_exit` ab. Danach endet `_start` regulär.
- **Dateien:** `stat()`/`access()` (Ressourcen, `file:`-URLs) brauchen `path_filestat_get`, `realpath()` braucht `path_readlink` (für Nicht-Links `EINVAL`). `fd_readdir` ist nur für Verzeichnislisten nötig.
- **Tasten:** normalisierte ZehOS-Tastencodes (Set-1-Nummern ohne E0-Merkmal), zum Beispiel Pfeile 72/75/77/80 und Entf 83.
- **Text:** Zeichen kommen ausschließlich aus `poll_event`-Texten (Typ 3, Unicode). Zeichentasten ohne Strg erzeugen kein Tastenereignis, damit nichts doppelt getippt wird. Strg-Kürzel nutzen die US-Zuordnung der Taste.
- **Maus:** Maustasten-Code 1/2/4 = links/rechts/Mitte. Das Rad liefert je Rastung Maustaste 4 (aufwärts) oder 5 (abwärts) an fbtk. `reset` lässt alle gehaltenen Tasten und Maustasten los; Loslassen ohne vorheriges Drücken wird verworfen. Eine so losgelassene Maustaste wirkt an der letzten Zeigerposition und kann dort einen Klick abschließen.

## Ohne VM prüfen

`host/zehos_host_linux.c` stellt `zehos_v1` als WAMR-Native-Bibliothek bereit. Es schreibt jedes Bild als PPM, liefert Ereignisse aus einer Datei und meldet `should_exit` nach einer festen Zeit. `iwasm` muss mit `-rdynamic` gebaut sein, damit die Bibliothek die WAMR-Funktionen findet.

```bash
cd .tools/netsurf-host
cmake -S wamr/product-mini/platforms/linux -B build-iwasm -DCMAKE_BUILD_TYPE=Release \
  -DWAMR_BUILD_LIBC_WASI=1 -DWAMR_BUILD_LIB_PTHREAD=0 -DCMAKE_EXE_LINKER_FLAGS=-rdynamic
cmake --build build-iwasm -j
gcc -O2 -shared -fPIC -Iwamr/core/iwasm/include ../../experiments/NetSurf.Wasi/host/zehos_host_linux.c -o libzehos_host.so
ZEHOS_PPM=frame.ppm ZEHOS_EXIT_MS=8000 ./build-iwasm/iwasm --native-lib=$PWD/libzehos_host.so \
  --map-dir=/::../../experiments/NetSurf.Wasi/bin/package --stack-size=1048576 \
  ../../experiments/NetSurf.Wasi/bin/package/netsurf.wasm
```

`wamr` ist WAMR-2.4.5 (Commit `25bd7eb63e828e4bd242cc9b38d260b4b31c6605`). Ereignisdateien (`ZEHOS_EVENTS`) enthalten je Zeile `<ms> <typ> <code> <x> <y>` nach dem `poll_event`-ABI.

## Grenzen

- **Tastatur:**
  - In der Adressleiste ist nur ASCII möglich; in Formularfeldern der Seite funktioniert Unicode.
  - Ziffernblocktasten fallen mit Pfeilen, Pos1, Ende, Bild auf/ab und Entf zusammen, weil die Tastencodes kein E0-Merkmal tragen.
  - Für Zurück gibt es kein Tastenkürzel, nur die Schaltfläche.
- **Inhalte:** kein Netz, kein JavaScript, keine PNG-/JPEG-/SVG-Bilder; GIF und BMP sind enthalten.
- **Prüfstand:** Bisher nur auf dem Linux-Prüfhost unter WAMR geprüft, nicht im ZehOS-Loader (wasm3), nicht in QEMU oder VMware.

## Geprüft (Linux-Prüfhost, WAMR-2.4.5, 15. November 2026)

| Ablauf | Ergebnis |
| --- | --- |
| Start ohne Argumente, Paketordner als `/` | Startseite `file:///pages/index.html`, Exit 0 nach `should_exit` |
| Klick auf Link | Formularseite geladen, Zurück-Schaltfläche aktiv |
| Klick ins Feld, Texte `Grüße @€`, Zeichentaste `g` ohne Text, Rücktaste | Feld zeigt `Grüße @` |
| Rad −5 auf langer Seite | Seite nach unten gescrollt |
| Kaputter Link | Seite „Not found“, Browser bedienbar |
| Maustaste auf Link gedrückt, `reset`, danach neuer Klick | keine hängende Taste; das Loslassen durch `reset` schließt den Klick auf den ersten Link ab |
