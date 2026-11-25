# Ladybird L2-F: ZehOS-Laufzeit für memory64, geteilten Speicher und wasi-threads

Stand: 20. November 2026. ZehOS `origin/master` `bbf64f46`; WAMR 2.4.5 aus dem Tarball (SHA-256 `1ab09d51099f276ca4a1d6629f6b589aab2bd0caa01445e05031a4bed22c199b`, vor dem Entpacken gegen `ProjektZeh/ThirdParty/wamr/ORIGIN.txt` geprüft, gleich; entpackt nach `/home/laser/wamr-2.4.5-src`, nichts daraus gebaut oder ausgeführt); wasi-sdk 34.0 lokal in `/opt/wasi-sdk` (nicht 25.0, das Projekt pinnt 25.0). Paket L2-F des [Lesestudien-Plans](../plans/2026-11-19-ladybird-l2-lesestudie.md), Board-Block #456. Nur gelesen. Pfade `core/...` und `wamr-compiler/...` liegen im WAMR-Tarball, Pfade `ProjektZeh/...` im ZehOS-Stand. BELEGT heißt: Stelle gelesen. HYPOTHESE: Schluss ohne Bau oder Lauf.

## Frage

Trägt WAMR 2.4.5 im ZehOS-Zuschnitt (`ProjektZeh/ThirdParty/wamr`, AOT-Lader für x86-64) memory64, geteilten Speicher und `wasi-threads` im AOT-Modus, und welche ZehOS-Stellen müssten sich ändern?

**Kurzantwort:** Alle drei sind `nachrüstbar`, keines ist `vorhanden`. Der Quelltext von WAMR kennt sie im AOT-Modus; der Zuschnitt schaltet sie ab (`ProjektZeh/ProjektZeh.csproj:73`) und lässt `thread-mgr` und `lib-wasi-threads` weg (`ORIGIN.txt:5-8`). Für `wasi-threads` fehlt zusätzlich eine echte Plattformschicht (`zehos_platform.c:101-110` sind Leerfunktionen). Bei memory64 ist die Laufzeit nicht der Engpass: **wasi-sdk 34.0 hat kein `wasm64-wasip1`** (siehe unten); ein memory64-Gast braucht eine eigene Werkzeugkette.

## Befunde

### Vergleich Zuschnitt und Tarball (BELEGT)

- Die übernommenen Verzeichnisse `core/iwasm/{common,aot,interpreter,compilation,include}`, `core/shared/{utils,mem-alloc,platform/include}` sind Datei für Datei gleich dem Tarball (`diff -rq`, nur „Only in“-Zeilen: `aot_perf_map.*`, `aot_validator.*`, andere `aot/arch/*`, `common/arch`, `common/gc`, `iwasm_common.cmake`, `SConscript`). Das stimmt mit „Änderungen: keine“ (`ORIGIN.txt:9`) überein.
- `core/iwasm/common/wasm_shared_memory.c` (477 Zeilen) und `wasm_exec_env.c` (336) sind im Zuschnitt vorhanden und werden mitgebaut (`ProjektZeh/ProjektZeh.csproj:86`, ganzes Verzeichnis); sie sind heute durch `WASM_ENABLE_SHARED_MEMORY=0` leer (`ProjektZeh/ProjektZeh.csproj:73`).
- **Fehlt im Zuschnitt:** `core/iwasm/libraries/thread-mgr/` (`thread_manager.c` 1 569 Zeilen, `.h` 256), `core/iwasm/libraries/lib-wasi-threads/` (`lib_wasi_threads_wrapper.c` 177, `tid_allocator.c` 83, `.h` 45), und `core/shared/platform/common` samt allen echten Plattformen (`core/shared/platform/linux` usw.). Kein `GCCProject`/`GCCIncludePath` dafür (`ProjektZeh/ProjektZeh.csproj:83-96`).
- Build-Schalter heute: `-DWASM_ENABLE_THREAD_MGR=0 -DWASM_ENABLE_SHARED_MEMORY=0`, `WASM_ENABLE_MEMORY64` nicht gesetzt (Standard 0, `core/config.h:679-680`), `-DWASM_DISABLE_HW_BOUND_CHECK=1` (`ProjektZeh/ProjektZeh.csproj:73`).

### memory64 — Urteil `nachrüstbar` (Laufzeit); Werkzeugkette `fehlt`

| Befund | Beleg |
| --- | --- |
| AOT-Lader und -Laufzeit tragen memory64 hinter `WASM_ENABLE_MEMORY64`: Flag-Prüfung beim Laden, 64-Bit-Zugriffsgrenzen, `memory.grow` 64-Bit, Datensegment-Offsets als `i64`, `malloc`/`free` des Gastes als `i64` | `core/iwasm/common/wasm_loader_common.c:60-105` (Fehler „invalid limits flags“ ohne den Schalter), `core/iwasm/aot/aot_loader.c:3957,4069`, `core/iwasm/aot/aot_runtime.c:999-1006,1126,1287-1312,2824-2830,2899-2927`, `core/iwasm/common/wasm_memory.c:1029,1095,1141`, `core/iwasm/common/wasm_runtime_common.c:4741-4809,5901-6009`. BELEGT |
| Standardgrenze: 65 536 Seiten (wasm32) bzw. `UINT32_MAX` Seiten (memory64) | `core/iwasm/interpreter/wasm.h:110-111`. BELEGT |
| WAMR 2.4.5 erzwingt bei memory64 die Softwareprüfung (`WAMR_DISABLE_HW_BOUND_CHECK 1`); nur Interpreter und AOT unterstützt | `build-scripts/config_common.cmake:345-351`, `doc/build_wamr.md:79-82`. BELEGT; ZehOS fährt ohnehin ohne Hardwareprüfung (`ProjektZeh.csproj:73`), das passt |
| `wamrc` ist immer mit memory64 gebaut | `wamr-compiler/CMakeLists.txt:55` (`-DWASM_ENABLE_MEMORY64=1`); der AOT-Kompiler ist also nicht der Engpass. Ob das fertige `wamrc` von `Native/AotProbe/fixture` ein memory64-Modul übersetzt, ist nicht gebaut (HYPOTHESE) |
| memory64 gilt für einen Speicher; bei mehreren Speichern wird angenommen, alle seien gleich | `core/iwasm/aot/aot_loader.c:3957-3965` (TODO-Kommentar). BELEGT; für Ladybird unerheblich (ein Speicher) |
| **wasi-sdk:** Lokal 34.0 hat unter `/opt/wasi-sdk/share/wasi-sysroot/lib` nur `wasm32-wasip1`, `wasm32-wasip1-threads`, `wasm32-wasip2`, `wasm32-wasip3`, kein `wasm64-*`. Die Doku von wasi-sdk (README `main`, Zeilen 269-272): „64-bit linear memories (a 'wasm64' target) are not supported at this time … no plans to add support for `wasm64-wasi{,-threads,p1,p1-threads}`“. wasi-sdk 25.0 nennt wasm64 in seiner README überhaupt nicht. | BELEGT (lokal und `raw.githubusercontent.com/WebAssembly/wasi-sdk/main/README.md`). `wasm64-wasip1` als Ziel **gibt es nicht**; clang kann `wasm64` übersetzen, aber ohne wasi-libc für wasm64 wäre eine eigene Libc nötig (HYPOTHESE zum Aufwand, hier nicht in Personwochen) |
| ZehOS-Seite: Hostfunktionen setzen 32-Bit-Gastzeiger voraus (`guest_offsets` wandelt Hostzeiger in Offsets, Kennung `i`; Wrapper geben `uint32_t` zurück), die Seitenobergrenze ist 32-Bit-`uint32_t` (`ZEHOS_WASM_MAXIMUM_PAGES = 65536`), das Paketprofil heißt `zehos-wasm32-v1` | `ProjektZeh/Native/Wasm/host/zehos_host_wamr.c:17,85-98,137-139,279`, `ProjektZeh/Packages/PackageManifest.cs:18,53`. BELEGT |

### Geteilter Speicher — Urteil `nachrüstbar`

| Befund | Beleg |
| --- | --- |
| Ohne `WASM_ENABLE_SHARED_MEMORY` lehnt der Lader ein Modul mit Shared-Flag ab („invalid limits flags“); mit ihm: `shared memory must have maximum` | `core/iwasm/common/wasm_loader_common.c:60-90`; Standard 0: `core/config.h:218-219`; Schalter im Bau: `build-scripts/config_common.cmake:332-335`. BELEGT |
| Der AOT-Pfad kennt Shared Memory: Flag, Referenzzählung, Sperre | `core/iwasm/aot/aot_runtime.c:942-950,986-987,1161-1165,3113-3115`; `core/iwasm/common/wasm_memory.c:1039-1428` (`SHARED_MEMORY_LOCK`). BELEGT |
| Geteilter Speicher wird **voll vorab belegt** (`full_size_mmaped`), `memory.grow` verschiebt nichts | `core/iwasm/common/wasm_memory.c:1553-1558`. BELEGT. Folge: Das `maximum` des Moduls ist der Verbrauch; mit den 256 MiB Grenze (`WasmRuntime.cs:29`) darf das Modul nicht mehr als das deklarieren (HYPOTHESE zur Auswirkung) |
| `memory.atomic.wait`/`notify` brauchen `os_mutex_*` und `os_cond_reltimedwait`/`os_cond_signal` mit echtem Warten und Wecken | `core/iwasm/common/wasm_shared_memory.c:61,168,228,346-410`. ZehOS liefert Leerfunktionen, die sofort `BHT_OK` melden (`ProjektZeh/Native/Wasm/wamr-platform/zehos_platform.c:101-108`), `korp_mutex` ist ein `int` (`platform_internal.h:25-30`). BELEGT. Mit einem einzigen Thread ginge ein Modul mit Shared-Flag, solange es nie wartet (HYPOTHESE); ein `wait` kehrte sofort zurück und verfälschte das Ergebnis |
| Der Kompilierschalter: `wamrc --enable-multi-thread` erzeugt Atomics im AOT-Code; das Paketwerkzeug muss ihn setzen | `wamr-compiler/main.c:161,520`; Aufruf im Repo `Native/AotProbe/fixture/compile-aot.sh` (nicht gelesen, HYPOTHESE) |
| `os_atomic_cmpxchg` wird über `atomic_compare_exchange_strong` bereitgestellt | `core/shared/platform/include/platform_api_extension.h:135`; ob die freistehende C-Laufzeit von ZehOS `<stdatomic.h>` trägt, ist nicht geprüft (HYPOTHESE) |

### wasi-threads — Urteil `nachrüstbar`

| Befund | Beleg |
| --- | --- |
| Die Bibliothek besteht aus 305 Zeilen und exportiert genau `thread-spawn` `(i)i` (32-Bit-Argument) und ruft im Gast `wasi_thread_start` | `core/iwasm/libraries/lib-wasi-threads/lib_wasi_threads_wrapper.c:18,73,148-149`. BELEGT; **memory64 und wasi-threads kombinieren geht nicht ohne Eingriff**: `start_arg` ist `uint32` (`:73`), der Gast-Zeiger wäre 64 Bit. Ein Ladybird-Port, der beides braucht, bräuchte eine Änderung am Import (HYPOTHESE zur Tragweite) |
| Sie hängt am Thread-Manager (`wasm_cluster_create_thread`, `wasm_cluster_dup_c_api_imports`) und an `WASM_ENABLE_HEAP_AUX_STACK_ALLOCATION` (Pflicht mit `LIB_WASI_THREADS`) | `lib_wasi_threads_wrapper.c:112-118`, `core/config.h:185-193`. BELEGT |
| Der Thread-Manager nutzt 117 Stellen `os_thread_*`/`os_cond_*`/`os_mutex_*`/`os_sem_*`; der AOT-Lauf haken in ihn ein | `core/iwasm/libraries/thread-mgr/thread_manager.c` (`grep -c`), `core/iwasm/aot/aot_runtime.c:16-17,1777,2863,2953`, `core/iwasm/common/wasm_exec_env.c:763,904` (`os_cond_wait`). BELEGT |
| Pflichtfunktionen der Plattformschicht, die ZehOS nicht hat: `os_thread_create`, `os_thread_join`, `os_thread_detach`, `os_thread_exit`, `os_thread_env_init/destroy/inited`, `os_recursive_mutex_init`, `os_cond_wait`, `os_cond_broadcast`, optional `os_rwlock_*`, `os_sem_*` | `core/shared/platform/include/platform_api_extension.h:50-94,147-159,178,209,241,251-376`. ZehOS liefert nur die Leerfunktionen und `os_self_thread() = 1` (`zehos_platform.c:101-110`) und ein einziges globales `stack_boundary` (`:116-121`). BELEGT |
| ZehOS-Laufzeit ist auf einen Wasm-Lauf auf einem Worker ausgelegt: Warteschlange `WasmJobQueue`, ein Worker-Thread, 8 MiB Stack (`ThreadStackBytes`); Hostfunktionen erreichen ihren Lauf über die Nutzerdaten der Ausführungsumgebung, `zehos_wasm_host_*` sind nicht als nebenläufig geprüft | `ProjektZeh/Runtime/Wasm/WasmRuntime.cs:27,32-36,176-280`; `zehos_host_wamr.c:19-20`. BELEGT |
| Cosmos hat Kernelthreads (Round-Robin), läuft aber auf einer CPU; ein beendeter Thread gibt seinen Stack nicht frei (Kommentar zu 3.0.94) | `ProjektZeh/Runtime/Wasm/WasmRuntime.cs:22`; L2-D, [PR #586](https://github.com/peterzw98/ProjektZehOS/pull/586) (offen, `docs/superpowers/specs/2026-11-19-ladybird-l2-threads.md`) nennt `docs/superpowers/specs/2026-11-18-multicore-design.md:21-23`. Zusätzliche Threads brächten also Zeitscheiben, keine Parallelität. 9 bis 11 Threads zu je 8 MiB (nach L2-D) wären 72 bis 88 MiB Stack, wenn Stacks verloren gehen (HYPOTHESE) |
| `wasi-sdk`: `wasm32-wasip1-threads` gibt es in 34.0 (`/opt/wasi-sdk/share/wasi-sysroot/lib/wasm32-wasip1-threads` mit `libc.a`, `libpthread.a`; README `main` Zeilen 180 und 257: „experimental support for spawning threads“). wasi-sdk 25.0 nennt das Ziel noch `wasm32-wasi-threads` (README `wasi-sdk-25`, Zeilen 216-219, „experimental“). | BELEGT. Das Ziel in `experiments/NetSurf.Wasi/build.py:28-30` (25.0) heißt daher anders als im Plan; beim Wechsel auf 34.0 ist das Updatefrage und braucht Nachfrage (Regel „Updates“). |

## ZehOS-Änderungen

Zeilenzahlen sind die volle Länge der betroffenen Datei (Regel 5, grob). Bezug: 12.6 = Paketgrenzen (`ROADMAP.md:335`), 12.7 = Threads für Pakete (`ROADMAP.md:336`; Plan `docs/superpowers/plans/2026-11-17-roadmap-12.md:25`, P3, dort 6 bis 10 Wochen).

| Datei | Zeilen | Änderung | Funktion | Roadmap |
| --- | --- | --- | --- | --- |
| `ProjektZeh/ProjektZeh.csproj` | 283 | Schalter `WASM_ENABLE_MEMORY64`, `SHARED_MEMORY`, `THREAD_MGR`, `LIB_WASI_THREADS`, `HEAP_AUX_STACK_ALLOCATION`; neue `GCCProject`/`GCCIncludePath` für `libraries/thread-mgr` und `lib-wasi-threads` (Zeilen 73, 83-96) | alle | 12.7 |
| `ProjektZeh/Native/Wasm/wamr-platform/zehos_platform.c` | 256 | echte `os_mutex_*`, `os_cond_*` (mit Warten), `os_thread_create/join/exit`, Thread-Umgebung, Stackgrenze je Thread statt der einen Globalen (`:116-121`) | Shared Memory, Threads | 12.7 |
| `ProjektZeh/Native/Wasm/wamr-platform/platform_internal.h` | 56 | `korp_*`-Typen als echte Strukturen (heute `int`, `:25-30`), `os_atomic_*` | Shared Memory, Threads | 12.7 |
| `ProjektZeh/Native/Wasm/host/zehos_host_wamr.c` | 362 | Instanziierung mit Thread-Manager; Nutzerdaten je Thread (heute je Lauf, `:19-20`); 64-Bit-Seitenobergrenze und 64-Bit-Gastzeiger in `guest_offsets` (`:17,85-98,279`) | Threads, memory64 | 12.6, 12.7 |
| `ProjektZeh/Native/Wasm/host/zehos_host_core.c` | 1 097 | Hostfunktionen (`zehos_v1`, WASI) threadsicher; für memory64 Signaturen mit 64-Bit-Zeigern (Tabelle der Imports) | Threads, memory64 | 12.7 |
| `ProjektZeh/Runtime/Wasm/WasmRuntime.cs` | 733 | Worker-Modell (ein Thread, `WasmJobQueue`, 8 MiB) auf mehrere Threads je App mit Obergrenze und gemeinsamem Beenden; `MemoryLimitBytes` und `WasmStackBytes` (`:28-29`) wählbar | Threads, Grenzen | 12.6, 12.7 |
| `ProjektZeh/Packages/PackageManifest.cs` | 150 | Laufzeitprofil `zehos-wasm32-v1` (`:18`) braucht ein Profil mit Threads (und ggf. `wasm64`), Grenzen im Manifest wählbar (`:19-22`: 16 KiB Manifest, 128 Dateien, 128 MiB Inhalt) | alle | 12.6 |
| `ProjektZeh/Apps/Wasm/WasmGameApp.cs` | 347 | Modulgrenze 64 MiB (`:32`) systemweit statt fest | Grenzen | 12.6 |
| `ProjektZeh/Runtime/Wasm/WasmEngineSelection.cs` | 121 | `MaximumAotBytes` 64 MiB (`:37`); AOT-ABI-Kennung um Threads erweitern | Grenzen | 12.6 |
| `ProjektZeh/Runtime/CosmosAotProbe.cs` | 344 | Modulgrenze (`:102`), Probe um Thread-Fall | Grenzen | 12.6 |
| `core/iwasm/libraries/thread-mgr/`, `lib-wasi-threads/` (aus dem Tarball in `ProjektZeh/ThirdParty/wamr/` aufnehmen, unverändert; `ORIGIN.txt` anpassen) | 1 825 + 305 | neue Quelldateien, kein ZehOS-Code | Threads | 12.7 |

Nicht gezählt: die Werkzeugkette für memory64 (eigene Libc für `wasm64`, siehe oben) und `wamrc` mit `--enable-multi-thread`/memory64 im Paketbau (`docs/PAKETE_ERSTELLEN.md`, `Native/AotProbe/fixture`).

## Aufwand

Regel 5: 1 Personwoche je angefangene 1 500 Zeilen, mindestens 1.

| Funktion | Zeilen | Rechnung | Personwochen (Spanne) |
| --- | --- | --- | --- |
| memory64 | 283 + 362 + 1 097 + 733 + 150 = 2 625 | 2 625 / 1 500 = 1,75, also 2 | 2 bis 4 (ohne Werkzeugkette) |
| Geteilter Speicher | 283 + 256 + 56 = 595 | unter 1 500, also 1 | 1 bis 2 |
| wasi-threads | 283 + 256 + 56 + 362 + 1 097 + 733 + 2 130 (importiert) = 4 917 | 4 917 / 1 500 = 3,3, also 4 | 4 bis 8 |
| **Zusammen** (Dateien nur einmal) | 3 749 eigene + 2 130 importierte = 5 879 | 5 879 / 1 500 = 3,9, also 4 | **4 bis 10** |

Die obere Grenze ist die Summe der Einzelspannen ohne Überlappung, gekürzt um die gemeinsamen Dateien. Der Plan Roadmap 12 schätzt P3 auf 6 bis 10 Wochen; die Spanne liegt damit im selben Bereich (HYPOTHESE: Fehlersuche bei Cosmos-Threads und Stackverlust ist nicht in der Zeilenregel). Nicht abgedeckt: Cosmos-seitige Schwächen (ein beendeter Thread gibt den Stack nicht frei, `WasmRuntime.cs:22`) und die Messung des tatsächlichen Speicher- und Threadbedarfs von Ladybird (L1).

## Gesamturteil

**memory64 `nachrüstbar`, geteilter Speicher `nachrüstbar`, wasi-threads `nachrüstbar`; keines davon `fehlt`.** Nach der L2-Z-Regel schließt L2-F den Weg „Wasm“ nicht aus; er hängt an 12.6/12.7 mit 4 bis 10 Personwochen.

Was die anderen Berichte daraus machen:

- **L2-D (Threads, PR #586, offen):** unverändert 9 bis 11 Threads in WebContent, Umbau auf einen Thread 5 bis 8 Personwochen. Beide Wege (Threads tragen oder Umbau) sind nach diesem Bericht denkbar; der Thread-Weg kostet hier 4 bis 8, der Umbau dort 5 bis 8 Personwochen, und die Wartung des Umbaus ist ein Fork-Patch.
- **L2-A (GC-Heap, PR #589, offen):** Variante 3 („unverändert unter memory64“) braucht mehr als memory64: Adressraum 2 × 32 GiB + 4 TiB nur reserviert, ein Wasm-Speicher belegt aber bei `memory.grow`. Dazu passt: Die 256-MiB-Grenze (`WasmRuntime.cs:29`) und die Vorabbelegung von `os_mmap` (`zehos_platform.c:71-84`, `malloc` plus `memset`) tragen das nicht; wasm64 ohne Werkzeugkette macht Variante 3 zusätzlich unerreichbar. Variante 1 (Region ≤ 1 GiB, wasm32) braucht memory64 nicht.
- **L2-C (Prozesse, PR #588, offen):** `zusammenlegbar` unter der Bedingung „mehr als ein Thread“ (dort Bedingung 2); diese Bedingung erfüllt 12.7 erst nach Umsetzung.
- **L2-B (LibJS, PR #587, offen):** `schreibbar`, unabhängig von diesem Bericht.

**Empfehlung für L2-Z:** memory64 für Ladybird nicht einplanen (Werkzeugkette fehlt, wasm32 mit GC-Region ≤ 1 GiB nach L2-A Variante 1 genügt); wasi-threads nur dann, wenn L2-D den Thread-Weg wählt.

## Nicht geprüft

- Kein Bau und kein Lauf von WAMR, `wamrc` oder wasi-sdk (Auftrag: Tarball nur lesen). Ob wamrc 2.4.5 mit den ZehOS-Schaltern (`--stack-bounds-checks`, Red-Zone-Patch, `Native/AotProbe/fixture`) ein memory64- oder Threads-Modul übersetzt, ist offen.
- wasi-sdk 25.0 wurde nur über seine README geprüft (Threads: Ziel `wasm32-wasi-threads`); die Ziele von 25.0 lokal nicht, dort liegt nur 34.0.
- `compile-aot.sh`, `docs/PAKETE_ERSTELLEN.md` und `docs/superpowers/plans/2026-11-17-ladybird.md` gelesen nur, soweit oben zitiert.
- Ob die freistehende C-Laufzeit von ZehOS `<stdatomic.h>` und `os_thread_*` tragen kann; echte Cosmos-Threads mit AOT-Code (Interrupts, Red Zone, `docs/superpowers/plans/2026-11-17-aot-laufzeit-handoff.md`) sind nicht gemessen.
- Berichte L2-A bis L2-D wurden aus den offenen PR-Köpfen gelesen, nicht geändert; L2-E liegt nicht vor.
