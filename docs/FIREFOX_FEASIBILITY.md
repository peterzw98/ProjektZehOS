# Firefox als ZehOS-Paket: Machbarkeitsstand

Stand: 15. November 2026. Grundlage: [Plan](superpowers/plans/2026-11-15-firefox-machbarkeit.md), Roadmap-Etappe 12a. Dieser Bericht ist ein **Zwischenstand ohne Laufnachweis**: Firefox hat in keiner Umgebung ein Bild erzeugt. Er dient der Wahl zwischen Firefox und NetSurf; es wird nur eine der beiden Lösungen umgesetzt.

## Ausgangspunkt

[HeyPuter/firefox-wasm](https://github.com/HeyPuter/firefox-wasm) (MPL-2.0) baut Gecko mit Emscripten 6.0.1 als Modul für Webbrowser als Host: Softwaredarstellung in ein Canvas, Netz über einen WISP-Websocket-Tunnel, pthreads über Web Worker. Ein Wasm-Build für WASI oder ein Betriebssystemkern als Host ist nicht bekannt. Eingaben laden `experiments/Firefox.WamrProbe/fetch.py` (feste SHA-256/Commits) nach `.tools/firefox-probe/`.

## Gemessen

Release v0.0.1 (`gecko.js-v0.0.1.tar.gz`, SHA-256 `bac28056…8a73`), Auswertung mit `experiments/Firefox.WamrProbe/imports.py`:

| Größe | Wert | ZehOS heute |
| --- | --- | --- |
| `gecko.wasm` | 150 200 916 Byte, 194 780 Funktionen | Modul ≤ 64 MiB, Paket ≤ 128 MiB |
| Lineare Memory | importiert, geteilt, min. 8192 Seiten (512 MiB), max. 65 536 (4 GiB), kein memory64 | ≤ 256 MiB, nicht geteilt; VM 512 MiB |
| Importe | 473: 468 `env`, 5 WASI | WASIp1-Teilmenge und 6 `zehos_v1` |
| Befehlssatz | SIMD (`-msimd128`), Atomics | wasm3: kein SIMD, keine Threads |

WAMR-2.4.5 (`iwasm`, Fast-Interpreter, SIMD, geteilte Memory, ohne WASI-libc), Entwicklungsrechner unter Linux: Laden, Validieren und Instanziieren gelingen in **2,4 s bei 624 MiB RSS**. Der Lauf endet erwartungsgemäß beim ersten JS-Hostimport `_emscripten_init_main_thread_js`. Mit WASI-libc lehnt `iwasm` das Modul ab, weil es die Memory importiert statt exportiert; ein eigener Einbetter ist deshalb Pflicht.

Damit ist belegt: WAMR akzeptiert das unveränderte Modul einschließlich SIMD und geteilter Memory. Nicht belegt ist, dass Gecko darunter startet.

## Hypothesen (Quellenlektüre, nicht durch Lauf bestätigt)

- Einstufung aller 473 Importe durch GPT-Agenten des Koordinators anhand des Glue-Codes `gecko.js`: **90 nötig, 44 Stub, 339 ungenutzt**. Ungenutzt sind 260 WebGL-Importe sowie Audio, Medien, Dialoge und OPFS, jeweils unter der Bedingung, dass diese Funktionen nicht verwendet werden. Die Liste stimmt mit der gemessenen Importliste überein.
- Nötig sind vor allem: rund 30 `invoke_*` mit `_emscripten_throw_longjmp`, 29 `wasmhost_*` (Wasm in Wasm), Hauptthread- und Mailboxfunktionen der Emscripten-pthreads, Uhren und Zeitumrechnung, Zufall sowie vorgeladene Dateien und Dateiprovider von wasmfs.
- Ohne Threads startet Gecko nicht; laut Projekt scheiterte ein Single-Thread-Versuch bei `NS_InitXPCOM`. Der Build verwendet rund 20 Worker.

## Offen

| Frage | Warum entscheidend |
| --- | --- |
| Erstes Bild einer lokalen Testseite, Zeit und Spitzen-Memory | Kernnachweis; braucht einen Einbetter mit rund 134 Hostfunktionen und eine nachgebildete JS-Ereignisschleife (`emscripten_unwind_to_js_event_loop`) |
| setjmp/longjmp über `invoke_*` | Emscripten springt über JS-Ausnahmen. WAMR kann das nicht direkt; möglich sind Host-`setjmp` bei AOT oder ein Neubau mit `-sSUPPORT_LONGJMP=wasm` (Wasm-Exceptions nur im klassischen WAMR-Interpreter) |
| Sind `wasmhost_*` für Start und `about:blank` nötig? | Falls nein, entfallen 29 schwierige Importe |
| Geschwindigkeit im Interpreter und mit AOT | JavaScript läuft ohne JIT; voraussichtlich ist AOT nötig, also nativer Code im Paket (eigene Entscheidung) |
| Celeron J3455 (Koordinator, VM etwa 2 GiB) | WAMR baut dort in 16 s; die Ladeprobe steht noch aus |
| Cosmos 3.0.94: Start unter KVM, Allokation von 0,5–1,5 GiB, rund 20 Threads mit Warten/Wecken, ausführbarer Speicher für AOT | ungeprüft; ohne diese Punkte ist kein Gastlauf möglich |

## Aufwand bei positiver Entscheidung (Schätzung)

1. Einbetter und Emscripten-Shim auf dem Host bis zum ersten Bild: mehrere Tage bis wenige Wochen. Bei einem Gecko-Neubau kommen Bauzeit und 31 GiB RAM ohne LTO hinzu (auf dem Entwicklungsrechner vorhanden).
2. Loader von wasm3 auf WAMR umstellen und eine Cosmos-Plattformschicht schreiben (Speicher, Threads, Futex, Zeit, gegebenenfalls ausführbarer Speicher). Paket-, Modul- und Memorylimits anheben, mehrere App-Threads zulassen: Wochen.
3. Neue allgemeine Hostfunktionen: TCP und DNS, Maus, Rad und Text, ein beschreibbares Profil (gesondert zu beauftragen): Wochen.
4. Paket, Signatur und Abnahme auf beiden Zielrechnern.

Insgesamt sind es voraussichtlich **Monate**. Das Ergebnis ist ein Browser ohne JIT. Auf dem Celeron sind auch im besten Fall (KVM und AOT) nur einfache Seiten brauchbar zu erwarten. Sicherheitsupdates erfordern Neubauten des Gecko-Forks.

## Einschätzung

Technisch ist ein lokaler Firefox nicht ausgeschlossen: Das Modul ist gültig, und WAMR lädt es. Die Machbarkeit hängt aber an drei ungeprüften Punkten: der Nachbildung von setjmp/longjmp und Ereignisschleife, der Cosmos-Unterstützung für große Speicher und viele Threads sowie der Geschwindigkeit ohne JIT auf dem Celeron. Ein Teil der neuen Hostfunktionen wäre auch für NetSurf nötig: TCP und DNS, Maus und Text sowie höhere Limits.

## Grenzen dieses Berichts

- Kein Gecko-Start, kein Bild, keine Geschwindigkeitsmessung.
- Messungen nur auf dem Entwicklungsrechner unter Linux, nicht in ZehOS, nicht in QEMU oder VMware und nicht auf echter Hardware.
- Die Importeinstufung ist eine Quellenhypothese.
