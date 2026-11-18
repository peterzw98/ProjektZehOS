# Audit geteilter Zustände für mehrere CPUs

Stand: 18. November 2026, `master` @ `83ad625`; Nachtrag (Stichprobe yellow zu #295) @ `4742a87`: Zeilen in `CosmosRoundRobinScheduler.cs` nach #292 verschoben, neue Zeile `firstCpu`/`WaitingThreads`. Plan: [Etappe 0](superpowers/plans/2026-11-18-multicore-e0-audit.md) des
[Multicore-Plans](superpowers/plans/2026-11-18-multicore.md), [Entwurf](superpowers/specs/2026-11-18-multicore-design.md) Abschnitte 5.4 und 5.5. Das Dokument ändert keinen
Code; jeder Befund „Schutz fehlt“ wird in der genannten Etappe ein eigenes Paket.

Drei Abschnitte, je Paket einer: E0-A (Interrupt gegen Thread) legt die Datei an, E0-B und E0-C füllen danach nur ihren
Abschnitt.

## Ausführungskontexte

| Kürzel | Kontext | Heute | Ab Etappe 6/7 |
| --- | --- | --- | --- |
| IRQ | Interrupt-Handler (Tastatur, Maus, Tablet, Netz, Timer) | unterbricht jeden Thread | läuft auf CPU 0 |
| HS | Hauptschleife (`Runtime/InteractiveSession.cs`, Desktop, Textshell, Apps im Kernel) | ein Thread | eigener Thread, Compute-CPUs |
| WW | Wasm-Worker (`Runtime/Wasm/WasmRuntime.cs`, `Work`) | ein Thread, verdrängbar | läuft parallel zur HS |
| WN | Wasm-Netzwerk-Worker (`Runtime/Wasm/CosmosWasmNetwork.cs:88`) | ein Thread | parallel |
| BG | Hintergrundaufgaben (`Scheduling/BackgroundTaskService.cs`) | mehrere Threads | parallel |
| DW | Kennwort-Worker (`Scheduling/DedicatedWorker.cs`) | ein Thread | parallel |
| SCH | Scheduler-Hooks (im Interrupt- oder Aufruferkontext) | Interrupts maskiert | je CPU unter deren Queue-Sperre |
| EXC | CPU-Ausnahmepfad (`Runtime/CosmosFaultIsolation.cs`) | auf dem Stack des fehlerhaften Threads | je CPU |

„Schutz heute“ ist eines von: `lock (<Objekt>)`, `StorageLock`, `Interlocked`/`Volatile`, Interruptmaske, „nur ein
Kontext“, „unveränderlich nach Boot“, „keiner“. Grundsatz aus dem Plan: Eine Interruptmaske ist keine Sperre; sie hält nur
Interrupts der eigenen CPU ab.

## Interrupt gegen Thread

Paket E0-A. Gelesen: `ProjektZeh/Input/**`, `Security/CosmosVirtioEntropyDriver.cs`, `Runtime/CosmosVmwareClockPlug.cs`,
`Runtime/CosmosFaultIsolation.cs`, `Runtime/FaultIsolationPolicy.cs`, `Scheduling/Cosmos*.cs`, `Network/Cosmos*.cs`,
`Graphics/DisplayService.cs`.

| Zustand (Datei:Zeile) | Kontexte | Schutz heute | Reicht auf mehreren CPUs? | Nötige Änderung | Etappe |
| --- | --- | --- | --- | --- | --- |
| `Input/CosmosKeyboardPlug.cs:16` Ringpuffer `events` (`InputEventQueue`, `head`/`count`) | IRQ schreibt (`ReportScanCode` `:26`), HS liest und leert (`TryRead` `:31`), Boot leert (`Initialize` `:19`) | Interruptmaske im Leser (`:34`); der Schreiber (`:28`) läuft im IRQ | nein: liegt der Tastatur-IRQ auf CPU 0 und die HS auf einer anderen CPU, schützt die Maske der HS nichts; `head`/`count` können sich überschneiden | `TryEnqueue` und `TryRead` unter `CosmosIrqLock` | E4-D |
| `Input/CosmosKeyboardPlug.cs:17` Verlustzähler `dropped` | IRQ erhöht (`:28`), HS liest und setzt zurück (`:38-42`) | Interruptmaske im Leser | nein: Erhöhen und Zurücksetzen auf zwei CPUs verlieren Zählungen | gemeinsam mit `events` unter derselben `CosmosIrqLock` | E4-D |
| `Input/CosmosMousePlug.cs:13` `packets` (`RelativeMouseBuffer`: Ring, Summen `deltaX/deltaY`, `tabletX/Y`, `lost`, `hasRawMotion`) | IRQ (PS/2, `HandleRelative` `:17`), IRQ (USB-Tablet, `RecordTabletMotion` `:40`, `ApplyPending` `:21` und über `Sink?.ReportRelative` `CosmosUsbTabletDriver.cs:62` → Cosmos → `HandleRelative`), HS (`ApplyPending`, `TakeMotion` `:42`) | Interruptmaske in HS (`CosmosInputService.cs:59`) und Tablet-Handler (`CosmosUsbTabletDriver.cs:53`); PS/2-Schreiber im IRQ | nein: zwei Interruptquellen und die HS können auf verschiedenen CPUs laufen; Ring und Summen sind mehrere Felder | alle Zugriffe auf `packets` unter einer gemeinsamen `CosmosIrqLock` (dieselbe wie für die Cosmos-Mausfelder) | E4-D |
| Cosmos `MouseManager` `X`, `Y`, `ScrollDelta`, `Left/Right/MiddleButton` (über `CosmosMousePlug.cs:21-38`, `CosmosUsbTabletDriver.cs:60`) | HS (`ApplyPending`, Lesen `CosmosInputService.cs:61-69`), IRQ (Tablet: `ApplyPending`, `SetPosition`) | Interruptmaske | nein: die HS liest X, Y und Tasten als ein Paket (Kommentar `CosmosInputService.cs:53`); ein Tablet-IRQ auf CPU 0 kann dazwischen schreiben | dieselbe `CosmosIrqLock` wie `packets`; der Tablet-Handler nimmt sie statt der Maske | E4-D |
| `Input/CosmosUsbTabletDriver.cs:43` `TabletState.Sink` | Treiber-Worker setzt (`Probe` `:33`), IRQ liest (`:61-62`) | „unveränderlich nach Boot“ (vor dem Öffnen der Pipe gesetzt) | ja: wird vor `OpenInterruptPipe` (`:34`) geschrieben und danach nur gelesen | – | – |
| `Scheduling/CosmosRoundRobinScheduler.cs:10-13` `selectedThreads`, `traceNext`, `traceCount`, `preemptions` (Feld der einen Scheduler-Instanz) | SCH (`PickNext` `:45`, `OnTick` `:57`), HS/BG (`Snapshot` `:88`) | Interruptmaske (`:95`); die Hooks laufen maskiert | nein: die Instanz gilt für alle CPUs, die Queue aber je CPU (`InitializeCpu` `:16`); zwei CPUs schreiben `traceNext` und `preemptions` gleichzeitig | Spur und Zähler je CPU in `PerCpuState.SchedulerData` führen; `Snapshot` liest sie je CPU unter deren Queue-Sperre | E4-C |
| `Scheduling/CosmosRoundRobinScheduler.cs:20` `RoundRobinCore` je CPU (`state.SchedulerData`) | SCH | Interruptmaske; `GetRunQueueCount`/`GetRunQueueThread` (`:76-86`) maskieren selbst | unklar: richtig, solange Cosmos jede Queue nur unter ihrer eigenen Sperre anfasst; `OnThreadMigrate` (`:68`) berührt zwei Queues, `GetRunQueue*` können von einer fremden CPU kommen | in E4-C festlegen, welche Sperre Cosmos in den Hooks hält; `GetRunQueue*` nur unter der Queue-Sperre der betroffenen CPU, Migration in fester Reihenfolge der beiden Sperren | E4-C |
| `Scheduling/CosmosRoundRobinScheduler.cs:67` `SelectCpu` → immer `currentCpu` | SCH | „nur ein Kontext“ (eine CPU) | ja für Etappe 5 (alle Threads auf CPU 0); ab Etappe 6 ersetzt | Platzierung aus E4-B | E4-B/E6 |
| `Scheduling/CosmosRoundRobinScheduler.cs:17-18` `firstCpu` (statisch) und `WaitingThreads` (seit #292) | SCH setzt (`InitializeCpu` `:24`, `ShutdownCpu` `:28`), HS liest ohne Sperre (`CosmosMainLoopIdle.cs:13` → `RoundRobinCore.Waiting`, `Volatile`) | `Volatile` in `RoundRobinCore.Waiting`; `firstCpu` „nur ein Kontext“ (eine CPU) | nein: `firstCpu` ist die Queue der ersten CPU; auf mehreren CPUs meldet `WaitingThreads` nur deren bereite Threads, der Hinweis der Hauptschleife („bereite Threads warten“) wird falsch. `Waiting` selbst reicht (ein Schreiber je Queue) | Summe oder Maximum über alle CPUs (je CPU `Waiting` lesen) statt `firstCpu`; die Waiting-Pflicht aus #254 dort umsetzen | E4-B/E4-C |
| `Runtime/FaultIsolationPolicy.cs:52` `ThreadQuarantine.entries` | EXC schreibt (`Add` `:55`, Schreibstelle `:58`), SCH liest (`OnThreadReady`, `CosmosRoundRobinScheduler.cs:35`) | `Volatile` je Eintrag | ja: ein Eintrag je Thread-Id, ein Schreiber je Id, nur Wechsel von 0 auf 1 | – | – |
| `Runtime/CosmosFaultIsolation.cs:49-50,53` `contextVector`, `contextClaimed`, `contextRip` | EXC (`OnCpuContext` `:91-101`, `OnHalt` `:148-152`) | `Interlocked`/`Volatile`; „der erste Kontext gewinnt“ | nein: ein Satz für alle CPUs; zwei gleichzeitige Ausnahmen auf zwei CPUs überschreiben oder verbrauchen den Vektor der anderen | je CPU (Index aus der APIC-Id) statt global | E7 |
| `Runtime/CosmosFaultIsolation.cs:45-46` `workerThreadId`, `inNativeRun` | WW schreibt (`RegisterWorker` `:63`, `EnterNativeRun`/`LeaveNativeRun` `:76-78`), EXC liest und setzt zurück (`:157-163`) | `Volatile` | unklar: richtig für genau einen Wasm-Worker (heute so); bei mehreren Workern (12.7/12.9) reicht ein Feld nicht | heute nichts; mit mehreren Workern eine Tabelle je Thread-Id wie `ThreadQuarantine` | nicht in diesem Plan |
| `Runtime/CosmosFaultIsolation.cs:48,51-52` `isolatedThreadId`, `isolatedVector`, `isolatedRip` | EXC schreibt (`:164-166`), HS liest (`TakeIsolated` `:81`, `IsolatedVector`, `IsolatedRip`) | `Interlocked`/`Volatile` | unklar: drei Felder ohne gemeinsamen Schutz; die HS kann Vektor und RIP einer zweiten Isolation zur Id der ersten lesen (nur Protokoll) | Id zuletzt schreiben (heute so) und die HS liest Id zuerst; bei zwei CPUs je CPU wie `contextVector` | E7 |
| `Runtime/CosmosFaultIsolation.cs:47` `hostDepth` (Zeiger auf den C-Zähler) | WW setzt (`RegisterWorker`), EXC liest (`:155`) | `volatile` | ja, solange ein Worker; der Zähler selbst ist C-Zustand (E0-C) | – | – |
| `Runtime/CosmosFaultIsolation.cs:71,120,153` `SchedulerManager.GetCpuState(0)` | WW, EXC | – | nein: fest CPU 0; eine Ausnahme auf einer anderen CPU läse den Thread von CPU 0 und würde ihn in Quarantäne setzen | die CPU des Aufrufers verwenden (`GetCurrentCpu`/APIC-Id) | E5 (Worker bleiben auf CPU 0), spätestens E6 |
| `Security/CosmosVirtioEntropyDriver.cs:18` `current` | Treiber-Worker (`Probe` `:40`, `OnDetach` `:48`), DW/BG/WW (`TryFill` `:26`) | `Volatile`/`Interlocked` | ja: ein Verweis, atomar ersetzt; ein abgebautes Gerät meldet über `broken` Fehler | – | – |
| `Security/CosmosVirtioEntropyDriver.cs:59-60` `busy`, `broken` (Virtqueue, DMA-Puffer) | DW, BG, WW | `Interlocked.CompareExchange` auf `busy` als Spinsperre mit `Thread.Sleep(1)` | ja: kein IRQ (Polling), die Sperre ist CPU-unabhängig | – | – |
| `Runtime/CosmosVmwareClockPlug.cs:69-70,115-116` Cosmos `s_ticksPerMs`, `s_timerCalibrated`, `TscFrequency`, `IsTscCalibrated` | Boot (Kalibrierung vor Scheduler und Geräten) | „unveränderlich nach Boot“ | unklar: auf einer CPU ja; ob Cosmos für jede weitere CPU `CalibrateTimer` erneut ruft und dabei die globalen Werte überschreibt, ist im SMP-Stapel festzulegen | in Etappe 5 prüfen: einmal auf der BSP kalibrieren, APs übernehmen den Wert | E5 |

### Geprüft, nur ein Kontext

- `Input/CosmosInputService.cs:10-11` `capsLock`, `numLock` und `:9` `layout`: nur HS (`Poll`).
- `Input/InputService.cs`, `Input/TextBuffer.cs`, `Input/LineEditor.cs`: Instanzen der HS bzw. eines Terminals, kein statischer Zustand.
- `Input/GermanKeyboard.cs`, `Input/UsbTabletReport.cs`, `Input/InputEvent.cs`: ohne Zustand.
- `Graphics/DisplayService.cs:23-25` `canvas`, `surface`, `nextSample`: nur HS (Zeichnen und Statistik in der Ereignisschleife).
- `Runtime/CosmosVmwareClockPlug.cs:11` `CosmosVmwareClock`: ohne Zustand (CPUID).
- `Scheduling/CosmosStackProbe.cs`: ohne Zustand.
- `Network/CosmosNetworkService.cs:18` `Gate` und `:25` `ReadStatus`: kein Interrupt- oder Rückrufkontext; Zugriffe nur aus Threads (Shell, BG, WN), daher in „Hauptschleife gegen Worker“ (E0-B).
- `Network/CosmosNetworkProbe.cs`: Gastprobe, nur mit eigener Buildoption; ihre `Volatile`-Felder teilen nur zwei Probe-Threads.

### Rückrufe aus dem Interrupt

Cosmos ruft diese ZehOS-Methoden im Interrupt- oder Ausnahmekontext auf:

- `Input/CosmosKeyboardPlug.cs:10` `HandleScanCode` (Plug in `KeyboardManager`, PS/2-IRQ) → `CosmosKeyboardCapture.ReportScanCode` `:26`.
- `Input/CosmosMousePlug.cs:17` `HandleRelative` (Plug in `MouseManager`, PS/2-Maus-IRQ; auch aus `ReportRelative` des Tablets).
- `Input/CosmosUsbTabletDriver.cs:46` `TabletState.OnReport` (Interrupt-Pipe, `:34`; „kann im Interrupt laufen“, `:45`).
- `Scheduling/CosmosRoundRobinScheduler.cs` alle `IScheduler`-Hooks (`OnTick` `:57` im Timer-IRQ; `PickNext`, `OnThreadReady`, `OnThreadBlocked`, `OnThreadYield`, `OnThreadExit`, `OnPickFailed` im IRQ oder im Aufrufer mit maskierten Interrupts).
- `Runtime/CosmosFaultIsolation.cs:23` `HaltCpu` und `:28` `PrintCpuContext` (Plugs in `Cosmos.Kernel.Panic`, CPU-Ausnahme).

Nicht im Interrupt: die Kalibrier-Plugs in `CosmosVmwareClockPlug.cs` (Boot), `CosmosVirtioEntropyDriver` (Polling; `Probe`/`OnDetach` im Treiber-Worker).

### Sperren, die auch ein Thread hält

Keiner dieser Rückrufe nimmt heute eine Sperre im Sinne von `lock`, `Monitor`, `StorageLock` oder `AppStorageGate`. Sie
verlassen sich auf die Interruptmaske, die auf einer CPU genügt, weil der Thread auf derselben CPU nicht weiterläuft,
solange der Handler arbeitet. `OnHalt` liest nur `StorageLock.IsHeldByCurrentThread` und
`AppStorageGate.IsHeldByCurrentThread` (`CosmosFaultIsolation.cs:158`) und isoliert dann nicht, ohne die Sperre zu nehmen.
Die einzigen Spinsperren im Rückrufweg kommen aus Cosmos selbst (Scheduler-Queue ab Etappe 5). Damit gibt es heute keine
Verklemmung zwischen Interrupt und Thread; sobald Interrupt und Leser auf verschiedenen CPUs laufen (Etappe 6/7), müssen
die drei Eingabepuffer aber eine echte Sperre bekommen (`CosmosIrqLock`, E4-D), die der Thread mit maskierten
Interrupts nimmt, damit der Interrupt auf derselben CPU nicht auf sie wartet.

## Hauptschleife gegen Worker

Paket E0-B (C#), folgt.

## C-Code

Paket E0-C. Stand `master` @ `4742a87`. Gelesen: `ProjektZeh/Native/Wasm/host/**`, `Native/Wasm/libc/**`,
`Native/Wasm/wamr-platform/**`, `Native/AotProbe/**`; in `ThirdParty/wasm3/source/` und `ThirdParty/wamr/core/` die zur
Laufzeit geschriebenen Dateiebenen-Variablen. Kandidaten nicht nur per Suche, sondern vollständig aus den Objektdateien
eines Baus mit `-p:ZehOSAotProbe=true` (schließt `ZehOSWasmAot` ein): alle Symbole in `.data`/`.bss` (`nm`, Typ
`b`/`B`/`d`/`D`), also auch `static`-Variablen in Funktionen. Cosmos-eigene C-Objekte (uACPI, `kmain`, `stdlib` und
andere) gehören nicht zu diesem Paket.

Alle C-Zugriffe laufen heute auf dem Wasm-Worker (WW): Nur `m3_core`/`m3_env` (wasm3), `zehos_platform` (WAMR) und
`aot_probe` verwenden `malloc`/`free`/`errno` aus `libc.c`; die C-Objekte von Cosmos binden sie nicht. Lesend kommt der
CPU-Ausnahmepfad (EXC) hinzu (`zehos_host_depth`, siehe oben `CosmosFaultIsolation.cs:47`).

| Zustand (Datei:Zeile) | Kontexte | Schutz heute | Reicht auf mehreren CPUs? | Nötige Änderung | Etappe |
| --- | --- | --- | --- | --- | --- |
| `Native/Wasm/host/zehos_host_core.c:10` `zehos_host_depth` (Zähler offener Host- und Heapaufrufe) | WW erhöht und senkt: nicht atomar in den Hostrahmen (`zehos_host_wasm3.c:53/55`, `zehos_host_wamr.c:46/61`), atomar in `libc.c` (`:46` ff.) und `zehos_platform.c:51/53`; EXC liest (`CosmosFaultIsolation.cs`, `OnHalt`) | `volatile`; „nur ein Kontext“ schreibt | ja für genau einen Wasm-Worker: einziger Schreiber, die Ausnahme liest auf der CPU des fehlerhaften Threads; mit mehreren Workern zählt ein globaler Zähler die Aufrufe aller Worker zusammen, und `++`/`--` ohne Atomik verlieren Schritte | heute nichts; mit mehreren Workern ein Zähler je Worker (Feld im Laufkontext, Zeiger je Worker an `FaultIsolation`) | nicht in diesem Plan |
| `Native/Wasm/libc/libc.c:39-42` Speicherbudget `memory_head`, `memory_limit`, `memory_used`, `memory_peak`, `memory_active`, `memory_owner`, `memory_exceeded`, `memory_current_thread` | WW (`zehos_memory_begin` `:44`, `malloc` `:75`, `free` `:111`, `zehos_memory_end` `:62`); laut Kommentar `:116` auch die HS, wenn sie einen aufgegebenen Lauf aufräumt | keiner (nur `zehos_host_depth` als Kill-Schutz) | ja für einen Worker: heute ruft kein C#-Code `zehos_memory_begin`, das Budget ist also immer aus; räumt die HS künftig auf, läuft der Worker dann nicht mehr (Quarantäne); `begin` lehnt einen zweiten Lauf ab (`memory_active`, `:47`), ein zweites Budget gibt es nicht | heute nichts; mit mehreren Workern Budget je Lauf (Struktur im Laufkontext statt Dateiebene) und Liste unter Sperre, sobald HS und WW gleichzeitig `free` rufen können | nicht in diesem Plan |
| `Native/Wasm/libc/libc.c:25` `errno` | WW (wasm3 `m3_env`, WAMR-Plattform über `platform_internal.h:11`) | „nur ein Kontext“ | ja für einen Worker; mehrere Läufe überschreiben sich `errno` gegenseitig | mit mehreren Workern `errno` je Thread (Feld im Laufkontext) | nicht in diesem Plan |
| `Native/Wasm/host/zehos_host_wamr.c:145-151` `bindings` (`.symbol.signature`) und `:157` `signatures` | WW: `register_host` (`:176`) schreibt beide bei jedem AOT-Lauf neu | „nur ein Kontext“ | ja für einen Worker; zwei gleichzeitige AOT-Läufe schreiben dieselben Felder (gleiche Werte) und registrieren die Gruppen doppelt in der globalen WAMR-Liste | einmal beim ersten Start registrieren oder mit dem WAMR-Laufzeitzustand zusammen pro Prozess führen (siehe nächste Zeilen) | nicht in diesem Plan |
| `Native/Wasm/wamr-platform/zehos_platform.c:117` `stack_boundary` | WW setzt (`zehos_host_wamr.c:333`, zurück `:348`), WAMR liest (`os_thread_get_stack_boundary` `:121`, genutzt in `wasm_exec_env.c:292` und `wasm_runtime_common.c:246`) | „nur ein Kontext“ | ja für einen Worker; ein zweiter AOT-Lauf auf anderem Stack prüft gegen die Grenze des ersten und erkennt einen Stapelüberlauf zu früh oder gar nicht | Grenze je Thread (Ausführungsumgebung oder Tabelle je Thread-Id) | nicht in diesem Plan |
| `Native/Wasm/wamr-platform/zehos_platform.c:101-108` `os_mutex_*`, `os_cond_*` als leere Funktionen | WAMR intern (`externref_lock`, `wasm_runtime_common.c:6384`) | keiner (Sperren sind wirkungslos) | ja für einen Worker; mit mehreren Läufen schützen die WAMR-Sperren nichts | echte Sperren (Cosmos-Spinsperre oder `Monitor` über einen Rückruf), bevor WAMR in zwei Threads läuft | nicht in diesem Plan |
| WAMR-Laufzeitzustand: `wasm_runtime_common.c:601` `runtime_ref_count`, `wasm_memory.c:27,29,43,52,61,70` `memory_mode`, `pool_allocator`, `malloc_func`/`realloc_func`/`free_func`, `global_pool_size`, `:36-37` `enlarge_memory_error_cb`/`_user_data`, `wasm_native.c:22` `g_native_symbols_list`, `wasm_runtime_common.c:6384-6386` `externref_lock`/`externref_global_id`/`externref_map` | WW: `zehos_wasm_run_aot` startet und beendet WAMR je Lauf (`wasm_runtime_full_init` `zehos_host_wamr.c:234`, `wasm_runtime_destroy` `:358`); dabei setzt WAMR alle diese Felder neu bzw. gibt sie frei | „nur ein Kontext“; `externref_lock` geht über die leeren `os_mutex_*`, und für `runtime_ref_count` legt WAMR ohne `OS_THREAD_MUTEX_INITIALIZER` (in `platform_internal.h` nicht gesetzt) gar keine Sperre an (`wasm_runtime_common.c:592-600`) | ja für einen Worker; ein zweiter gleichzeitiger AOT-Lauf teilt sich diesen Zustand, und das `destroy` des ersten Laufs gibt Natives-Liste, Externref-Tabelle und Allokatorzustand des zweiten frei | WAMR einmal pro Kernel starten statt je Lauf, und echte Sperren (Zeile davor) | nicht in diesem Plan |
| `ThirdParty/wamr/core/iwasm/common/wasm_native.c:1426` `quick_aot_entries` | WW: `quick_aot_entry_init` (`:1477`, aus `wasm_native_init` `:599`) sortiert die Tabelle bei jedem `wasm_runtime_full_init` an Ort und Stelle (`qsort` `:1479`), Aufrufe suchen per `bsearch` (`:1524`) | „nur ein Kontext“ | ja für einen Worker; ein zweiter Lauf sortiert die Tabelle um, während der erste darin sucht | mit WAMR-Start je Kernel (vorige Zeile) nur noch einmal sortiert | nicht in diesem Plan |
| `ThirdParty/wasm3/source/m3_core.c:321` `static M3_THREAD_LOCAL u8* base` in `m3_NativeStackLimit` (clang setzt `M3_HAS_THREAD_LOCAL`) | WW | im ZehOS-Bau keiner nötig: `m3_HostStackBase` liefert auf `x86_64-elf` (`m3_host_none`) `NULL`, der Compiler entfernt die Variable | ja heute; nein, sobald `m3_HostStackBase` für Cosmos echt umgesetzt wird: dann entsteht ein FS-basierter Zugriff auf threadlokalen Speicher, den Cosmos je Thread vermutlich nicht einrichtet, und ein je Thread gespeicherter Stackanfang, der für einen Ersatzworker auf neuem Stack falsch wäre | eine echte `m3_HostStackBase` nur zusammen mit `M3_HAS_THREAD_LOCAL=0` oder mit dem Stackanfang als Parameter je Lauf | E5+ |
| `Native/AotProbe/aot_probe.c:108` `host_symbols` | HS beim Boot (`Kernel.cs:463`, nur mit `-p:ZehOSAotProbe=true`): `wasm_runtime_register_natives` sortiert das Feld an Ort und Stelle (`wasm_native.c:283`) | „nur ein Kontext“ (vor der Ereignisschleife, Probebau) | ja: läuft einmal vor allen anderen Threads | – | – |

### Geprüft, nur ein Kontext oder unveränderlich

- `ThirdParty/wasm3/source/`: im ZehOS-Bau keine beschreibbare Dateiebenen- oder `static`-Variable (alle `m3_*`-Objekte
  ohne `.data`/`.bss`-Symbol); der ganze Zustand liegt in Umgebung, Laufzeit und Modul, die `zehos_host_wasm3.c` je Lauf
  anlegt. Bedingungen: `m3_core.c:194-198` `g_guard*` nur mit `d_m3GuardedMemory`, `stack_start` nur mit
  `d_m3LogNativeStack` (beide abgeschaltet); `m3_core.c:321` steht als eigene Zeile in der Tabelle.
- `Native/Wasm/host/zehos_host_wasm3.c` `bindings`, `zehos_host_core.c` `environment_entry`, `program_name`, `save_name`:
  nur lesbar (`.rodata`). Der Hostzustand eines Laufs (`zehos_host_context`, offene Dateien) liegt auf dem Stack des
  Workers (`zehos_host_wamr.c:224`) bzw. im Laufkontext.
- WAMR `wasm_runtime_common.c:5003-5011` (und die Varianten `:5568-5576`, `:5854-5862`) `invokeNative_*`: `volatile`
  initialisierte Funktionszeiger, nie geschrieben.
- WAMR `aot_reloc_x86_64.c:35` `target_sym_map`: nur gelesen (`:86`, `aot_loader.c:3096`).
- WAMR `wasm_c_api.c:434` `singleton_engine`: nur über die C-API (`wasm_engine_new`), die ZehOS nicht aufruft.
- WAMR `bh_log.c:12` `log_verbose_level`, `:62-63` `last_time_ms`, `total_time_ms`: nur über `wasm_runtime_set_log_level`
  bzw. `bh_print_time` (nur im Compiler `wamrc`), beides in ZehOS nicht aufgerufen.
- WAMR `wasm_shared_memory.c:24` `g_shared_memory_lock`, `:47` `wait_map`: Code nur mit `WASM_ENABLE_SHARED_MEMORY`,
  das ZehOS abschaltet (`ProjektZeh.csproj`, `ZehOSWasmAot`).
- `Native/AotProbe/fixture/*.c` (`static` und `volatile` dort): Wasm-Gastcode der Probe, liegt im linearen Speicher
  der jeweiligen Instanz, nicht im Kernel.

### Mehrere gleichzeitige Wasm-Läufe (12.7, 12.9)

Für mehrere CPUs mit weiterhin genau einem Wasm-Worker (Etappen 5 bis 7) braucht der C-Code keine Änderung: Jeder
beschreibbare Zustand hat diesen Worker als einzigen Schreiber, und der einzige fremde Leser ist der Ausnahmepfad des
Workers selbst. Mehrere gleichzeitige Läufe verhindern dagegen:

1. `zehos_host_depth` als ein globaler Zähler für alle Worker, in den Hostrahmen ohne Atomik.
2. Das Speicherbudget in `libc.c` als ein globales Budget mit einer Blockliste ohne Sperre; ein zweites `begin` scheitert.
3. `errno` als eine globale Variable.
4. WAMR: Start und Ende der ganzen Laufzeit je Lauf, mit globalem Allokator-, Natives- und Externref-Zustand, und
   wirkungslose `os_mutex_*`; dazu `stack_boundary` global statt je Thread und `quick_aot_entries`, das bei jedem Start
   umsortiert wird.

wasm3 selbst hält keinen globalen Zustand; mehrere wasm3-Läufe scheitern nur an den Punkten 1 bis 3. Diese Zeilen tragen
deshalb „nicht in diesem Plan“, damit Etappe 6 sie nicht für nötig hält.
