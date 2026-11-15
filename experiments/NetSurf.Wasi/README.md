# NetSurf als ZehOS-Paket (WASIp1)

NetSurf 3.11 mit dem Framebuffer-Frontend als `wasm32-wasip1`-Modul. Eine eigene libnsfb-OberflÃ¤che (`frontend/zehos.c`) zeichnet Ã¼ber `zehos_v1.present` und liest Eingaben Ã¼ber `zehos_v1.poll_event`. Quellen und Werkzeuge werden nicht eingecheckt; `build.py` lÃ¤dt sie mit festen SHA-256-Werten nach `.tools/netsurf/`.

## Bauen

Auf einem Linux-Host mit `make`, `cc`, `perl`, `gperf`, `patch` sowie den nativen Entwicklungspaketen fÃ¼r libpng und zlib (z. B. `libpng-dev`, `zlib1g-dev`). Die nativen Generatoren brauchen diese auch bei ausgeschalteten PNG-Inhalten:

```bash
python3 experiments/NetSurf.Wasi/build.py
```

Ergebnis ist `experiments/NetSurf.Wasi/bin/package/`:

| Pfad | Inhalt |
| --- | --- |
| `netsurf.wasm` | Einstieg `_start` |
| `res/` | Messages, CSS, interne Seiten, `Choices` mit Startseite `file:///pages/index.html` |
| `pages/` | Testseiten: Start (blau), Linkziel (grÃ¼n), langer Text (orange), Eingabefeld (lila), kaputter Link |
| `licenses/` | NetSurf (GPL-2.0), NetSurf-Bibliotheken (MIT), utf8proc, zlib |

Bestandteile:

| Baustein | Stand |
| --- | --- |
| NetSurf | `netsurf-all-3.11.tar.gz` |
| wasi-sdk | 25.0 fÃ¼r Linux (dieselbe Version wie fÃ¼r Doom) |
| zlib | 1.3.1 |

Zwei Bibliotheken fehlen bewusst: libsvgtiny braucht setjmp/longjmp, und libdom verzichtet auf expat und nutzt hubbub.

Patches (`patches/`, mit `patch -p1` auf den Quellbaum):

- `libnsfb-wasi-dump.patch`: `nsfb_dump` ohne `dup()`, das WASI nicht kennt.
- `libnsfb-zehos-surface.patch`: OberflÃ¤chentyp `NSFB_SURFACE_ZEHOS` vor `RAM`, damit das Frontend ihn ohne Kommandozeile wÃ¤hlt.
- `netsurf-curl-include.patch`: `curl.h` nur mit `WITH_CURL` einbinden (Upstream-Fehler bei `NETSURF_USE_CURL=NO`).
- `netsurf-pointer-cancel.patch`: Steuercode `FBTK_CONTROL_CANCEL_POINTER` beendet eine gehaltene Maustaste oder ein Ziehen ohne Klick (Fokusverlust).
- `netsurf-fbtk-text.patch`: Tastencodes ab `0x40000000` tragen Unicode-Zeichen aus `poll_event`-Texten. Die Adressleiste speichert ein Byte je Zeichen und nimmt deshalb nur druckbares ASCII an.

Ausgeschaltet sind (`Makefile.config`): curl/OpenSSL (kein Netz in diesem Schritt), Duktape (JavaScript, setjmp), PNG, JPEG, WebP und SVG. `mmap` und `signal` kommen aus der Emulation der wasi-libc, `iconv` aus der wasi-libc selbst.

## Lizenztexte

`licenses/` enthält die Texte, die weder NetSurf noch wasi-sdk 25 mitliefern; sie gehören in jedes Paket:

| Datei | Quelle | Bezug |
| --- | --- | --- |
| `MPL-2.0.txt` | mozilla.org/MPL/2.0 | `public_suffix_list.dat` in libnspsl |
| `wasi-libc-*` | wasi-libc `574b88da481569b65a237cb80daf9a2d5aeaf82d` (Submodul von wasi-sdk-25) | statisch gelinkte libc einschließlich musl-Teilen und Emulationsbibliotheken |
| `compiler-rt-LICENSE.TXT` | llvm-project `ab4b5a2db582958af1ee308a790cfdb42bd24720`, `compiler-rt/` (Submodul von wasi-sdk-25) | statisch gelinkte Laufzeitfunktionen des Compilers |

## Schnittstelle zum Loader

Importe des Moduls:

- `zehos_v1`: `present`, `poll_event`, `ticks_ms`, `sleep_ms`, `should_exit`
- WASI Preview 1: `args_get`, `args_sizes_get`, `environ_get`, `environ_sizes_get`, `clock_time_get`, `fd_close`, `fd_fdstat_get`, `fd_fdstat_set_flags`, `fd_pread`, `fd_prestat_get`, `fd_prestat_dir_name`, `fd_read`, `fd_readdir`, `fd_seek`, `fd_tell`, `fd_write`, `path_create_directory`, `path_filestat_get`, `path_open`, `path_readlink`, `proc_exit`

Annahmen:

- **Bild:** festes Bild 632Ã—392, 0x00RRGGBB, immer vollstÃ¤ndig je `present`.
- **Laufzeit:** Die Hauptschleife wartet hÃ¶chstens 16 ms je `sleep_ms` und fragt dazwischen `should_exit` ab. Danach endet `_start` regulÃ¤r.
- **Dateien:** `stat()`/`access()` (Ressourcen, `file:`-URLs) brauchen `path_filestat_get`, `realpath()` braucht `path_readlink` (fÃ¼r Nicht-Links `EINVAL`). `fd_readdir` ist nur fÃ¼r Verzeichnislisten nÃ¶tig.
- **Tasten:** normalisierte ZehOS-Tastencodes (Set-1-Nummern ohne E0-Merkmal), zum Beispiel Pfeile 72/75/77/80 und Entf 83.
- **Text:** Zeichen kommen ausschlieÃŸlich aus `poll_event`-Texten (Typ 3, Unicode). Zeichentasten ohne Strg erzeugen kein Tastenereignis, damit nichts doppelt getippt wird. Strg-KÃ¼rzel nutzen die US-Zuordnung der Taste.
- **Maus:** Maustasten-Code 1/2/4 = links/rechts/Mitte. Das Rad liefert je Rastung Maustaste 4 (aufwÃ¤rts) oder 5 (abwÃ¤rts) an fbtk. Maustasten und Rad wirken an der Position ihres eigenen Ereignisses (vorab Bewegung dorthin). `reset` lÃ¤sst gehaltene Tasten los und bricht gehaltene Maustasten ohne Klick ab; Loslassen ohne vorheriges DrÃ¼cken wird verworfen.

## Ohne VM prÃ¼fen

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

Automatische Szenarien (Klick, Reset, kaputter Link, Rad) prüfen das letzte Bild anhand der Markerfarben der Testseiten:

```bash
python3 experiments/NetSurf.Wasi/host/run_tests.py --iwasm .tools/netsurf-host/build-iwasm/iwasm \
  --host .tools/netsurf-host/libzehos_host.so
```

`wamr` ist WAMR-2.4.5 (Commit `25bd7eb63e828e4bd242cc9b38d260b4b31c6605`). Ereignisdateien (`ZEHOS_EVENTS`) enthalten je Zeile `<ms> <typ> <code> <x> <y>` nach dem `poll_event`-ABI.

## Grenzen

- **Tastatur:**
  - In der Adressleiste ist nur ASCII mÃ¶glich; in Formularfeldern der Seite funktioniert Unicode.
  - Ziffernblocktasten fallen mit Pfeilen, Pos1, Ende, Bild auf/ab und Entf zusammen, weil die Tastencodes kein E0-Merkmal tragen.
  - FÃ¼r ZurÃ¼ck gibt es kein TastenkÃ¼rzel, nur die SchaltflÃ¤che.
- **Inhalte:** kein Netz, kein JavaScript, keine PNG-/JPEG-/SVG-Bilder; GIF und BMP sind enthalten.
- **PrÃ¼fstand:** Bisher nur auf dem Linux-PrÃ¼fhost unter WAMR geprÃ¼ft, nicht im ZehOS-Loader (wasm3), nicht in QEMU oder VMware.

## GeprÃ¼ft (Linux-PrÃ¼fhost, WAMR-2.4.5, 15. November 2026)

| Ablauf | Ergebnis |
| --- | --- |
| Start ohne Argumente, Paketordner als `/` | Startseite `file:///pages/index.html`, Exit 0 nach `should_exit` |
| Klick auf Link | Formularseite geladen, ZurÃ¼ck-SchaltflÃ¤che aktiv |
| Klick ins Feld, Texte `GrÃ¼ÃŸe @â‚¬`, Zeichentaste `g` ohne Text, RÃ¼cktaste | Feld zeigt `GrÃ¼ÃŸe @` |
| Rad âˆ’5 auf langer Seite | Seite nach unten gescrollt |
| Kaputter Link | Seite â€žNot foundâ€œ, Browser bedienbar |
| Maustaste auf Link gedrÃ¼ckt, `reset` | Seite bleibt, kein Klick |
| danach Klick auf anderen Link | Linkziel geladen, keine hÃ¤ngende Taste |
| Maustaste ohne vorherige Bewegung | Klick wirkt an der Ereignisposition |
