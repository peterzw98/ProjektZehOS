# Firefox als ZehOS-Paket: Machbarkeitsstand

Stand: 15. November 2026. Grundlage: [Plan](superpowers/plans/2026-11-15-firefox-machbarkeit.md), Roadmap-Etappe 12a. Dieser Bericht ist ein **Zwischenstand ohne Laufnachweis**: Firefox hat in keiner Umgebung ein Bild erzeugt. Er dient der Wahl zwischen Firefox und NetSurf; es wird nur eine der beiden Lösungen umgesetzt.

## Ausgangspunkt

[HeyPuter/firefox-wasm](https://github.com/HeyPuter/firefox-wasm) (MPL-2.0) baut Gecko mit Emscripten 6.0.1 als Modul für Webbrowser als Host. Laut Projekt erfolgt die Darstellung in Software in ein Canvas, das Netz läuft über einen WISP-Websocket-Tunnel, und pthreads laufen über Web Worker. Ein Wasm-Build für WASI oder einen Betriebssystemkern als Host ist nicht bekannt.

## Gemessen

Release v0.0.1 (`gecko.js-v0.0.1.tar.gz`, SHA-256 `bac28056…8a73`), Auswertung mit `experiments/Firefox.WamrProbe/imports.py` und einem einfachen Exportparser:

| Größe | Wert | ZehOS heute |
| --- | --- | --- |
| `gecko.wasm` | 150 200 916 Byte, 194 780 Funktionen | Modul ≤ 64 MiB, Paket ≤ 128 MiB |
| Lineare Memory | importiert, geteilt, min. 8192 Seiten (512 MiB), max. 65 536 (4 GiB), kein memory64 | ≤ 256 MiB, nicht geteilt; VM 512 MiB |
| Importe | 473: 468 `env`, 5 WASI. Darunter 282 `emscripten_*`, 38 `_wasmfs_*`, 29 `wasmhost_*`, 25 `invoke_*`, 4 `wisp_*`, 3 `hostimg_*` | WASIp1-Teilmenge und 6 `zehos_v1` |
| Exporte | 125, kein `_start`. Start über `_emscripten_proxy_main`; vorhanden sind unter anderem `xul_cmd_ptr`, `_emscripten_check_mailbox`, `setThrew` | Paket-Apps starten über `_start` |

WAMR-2.4.5 (`iwasm`), Entwicklungsrechner unter Linux:

- **Ohne WASI-libc:** Laden und Validieren gelingen. Die Instanziierung führt die Startsektion des Moduls aus und bricht beim ersten nicht verknüpften Import `_emscripten_init_main_thread_js` ab. Bis dahin: **2,4 s, 624 MiB RSS**.
- **Mit WASI-libc:** `iwasm` lehnt das Modul ab, weil es die Memory importiert statt exportiert.

Damit ist belegt: WAMR validiert das unveränderte Modul bei eingeschaltetem SIMD und geteilter Memory. Nicht belegt ist, dass Gecko darunter startet.

Nachvollziehen (Pfade unter `.tools/firefox-probe/`; der Build hängt sonst nur von cmake und einem C-Compiler ab):

```bash
python3 experiments/Firefox.WamrProbe/fetch.py
cmake -S wamr/product-mini/platforms/linux -B build-nowasi -DCMAKE_BUILD_TYPE=Release \
  -DWAMR_BUILD_INTERP=1 -DWAMR_BUILD_FAST_INTERP=1 -DWAMR_BUILD_AOT=1 -DWAMR_BUILD_SIMD=1 \
  -DWAMR_BUILD_SHARED_MEMORY=1 -DWAMR_BUILD_BULK_MEMORY=1 \
  -DWAMR_BUILD_LIB_PTHREAD=0 -DWAMR_BUILD_LIBC_WASI=0 -DWAMR_BUILD_LIBC_BUILTIN=0
cmake --build build-nowasi -j
./build-nowasi/iwasm gecko.wasm
```

Der Build mit WASI-libc verwendet zusätzlich `-DWAMR_BUILD_LIB_PTHREAD=1 -DWAMR_BUILD_LIBC_WASI=1` und den Aufruf `iwasm --max-threads=32`. Zeit und RSS stammen aus einem Python-Aufruf mit `time.time()` und `resource.getrusage(RUSAGE_CHILDREN).ru_maxrss`.

## Hypothesen

Diese Punkte stammen aus Quellenlektüre oder Projektangaben und sind nicht durch einen Lauf bestätigt.

- **Buildkonfiguration laut Projekt:** SIMD (`-msimd128`), pthreads mit rund 20 Workern, SpiderMonkey ohne JIT im Portable Baseline Interpreter. Ein Single-Thread-Versuch scheiterte laut Projekt bei `NS_InitXPCOM`.
- **Einstufung der Importe:** GPT-Agenten des Koordinators haben alle 473 Importe anhand des Glue-Codes `gecko.js` eingestuft. Ergebnis: **90 nötig, 44 Stub, 339 ungenutzt**. Ungenutzt sind vor allem WebGL, Audio, Medien, Dialoge und OPFS, jeweils unter der Bedingung, dass diese Funktionen nicht verwendet werden. Die Liste stimmt mit der gemessenen Importliste überein.
- **`wasmhost_*`:** Die 29 Importe dienen einem experimentellen JIT von JavaScript nach Wasm. Im Fork (Commit `2e1e835a`, `js/src/wasm/WasmJitRuntime.cpp`) schaltet die Umgebungsvariable `GECKO_NOWASMJIT` ihn ab. Dann genügen Stubs mit echten Fehlerwerten, und die Zahl der nötigen Importe sinkt auf **61**.
- **Ereignisschleife:** Es gibt keinen Export für Malen, Eingabe, Timer oder Beenden. Eingaben und Bildanforderungen laufen über einen gemeinsamen Befehlsblock (`xul_cmd_ptr`) mit `memory.atomic.wait/notify`; das Bild wird als BGRA kopiert. Der Host braucht daher keine vollständige JS-Ereignisschleife, sondern Timer, Atomics-Warten/Wecken, Threadinstanzen auf der geteilten Memory und die Proxywarteschlangen (`_emscripten_check_mailbox`, `emscripten_proxy_execute_queue` etwa alle 50 ms).
- **setjmp/longjmp:** Die 25 `invoke_*` mit `_emscripten_throw_longjmp` und `setThrew` bilden setjmp/longjmp über JS-Ausnahmen nach. C++-Exceptions werden nicht importiert.

## Offen

| Frage | Warum entscheidend |
| --- | --- |
| Erstes Bild einer lokalen Testseite, Zeit und Spitzen-Memory | Kernnachweis. Braucht einen eigenen Einbetter, weil 468 `env`-Importe und die Emscripten-pthreads von keinem vorhandenen WAMR-Werkzeug bereitgestellt werden. |
| setjmp/longjmp über `invoke_*` | WAMR kann über Wasm-Rahmen nicht zurückspringen. Möglich sind Host-`setjmp` bei AOT oder ein Neubau mit `-sSUPPORT_LONGJMP=wasm`; Wasm-Exceptions gibt es nur im klassischen WAMR-Interpreter. |
| Geschwindigkeit im Interpreter und mit AOT | JavaScript läuft ohne JIT. Voraussichtlich ist AOT nötig, also nativer Code im Paket; darüber muss gesondert entschieden werden. |
| Celeron J3455 (Koordinatorrechner, VM etwa 2 GiB) | Laut Koordinator baut WAMR dort in 16 s; die Ladeprobe steht noch aus. |
| Cosmos 3.0.94: Start unter KVM, Allokation von 0,5–1,5 GiB, rund 20 Threads mit Warten/Wecken, ausführbarer Speicher für AOT | Ungeprüft; ohne diese Punkte ist kein Gastlauf möglich. |

## Aufwand bei positiver Entscheidung (Schätzung)

1. Einbetter und Emscripten-Shim auf dem Host bis zum ersten Bild: mehrere Tage bis wenige Wochen. Bei einem Gecko-Neubau kommen Bauzeit und laut firefox-wasm rund 31 GiB RAM ohne LTO hinzu; der Entwicklungsrechner hat 64 GB.
2. Loader von wasm3 auf WAMR umstellen und eine Cosmos-Plattformschicht schreiben (Speicher, Threads, Futex, Zeit, gegebenenfalls ausführbarer Speicher). Paket-, Modul- und Memorylimits anheben und mehrere App-Threads zulassen: Wochen.
3. Neue allgemeine Hostfunktionen für TCP und DNS, Maus, Rad und Text sowie ein beschreibbares Profil (gesondert zu beauftragen): Wochen.
4. Paket, Signatur und Abnahme auf beiden Zielrechnern.

Insgesamt sind es voraussichtlich **Monate**. Das Ergebnis ist ein Browser ohne JIT. Auf dem Celeron sind auch im besten Fall (KVM und AOT) nur einfache Seiten brauchbar zu erwarten. Sicherheitsupdates erfordern Neubauten des Gecko-Forks.

## Einschätzung

Technisch ist ein lokaler Firefox nicht ausgeschlossen: Das Modul ist gültig, und WAMR validiert es. Die Machbarkeit hängt aber an drei ungeprüften Punkten: setjmp/longjmp im Einbetter, Cosmos-Unterstützung für große Speicher und viele Threads sowie die Geschwindigkeit ohne JIT auf dem Celeron. Ein Teil der neuen Hostfunktionen wäre auch für NetSurf nötig: TCP und DNS, Maus und Text sowie gegebenenfalls höhere Limits.

## Grenzen dieses Berichts

- Kein Gecko-Start, kein Bild, keine Geschwindigkeitsmessung.
- Messungen nur auf dem Entwicklungsrechner unter Linux, nicht in ZehOS, nicht in QEMU oder VMware und nicht auf echter Hardware.
- Importeinstufung, Ereignisschleife und `wasmhost_*` sind Quellenhypothesen.
