# Audit geteilter Zustände für mehrere CPUs

Stand: 18. November 2026, `master` @ `83ad625`. Plan: [Etappe 0](superpowers/plans/2026-11-18-multicore-e0-audit.md) des
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
| `Input/CosmosMousePlug.cs:13` `packets` (`RelativeMouseBuffer`: Ring, Summen `deltaX/deltaY`, `tabletX/Y`, `lost`, `hasRawMotion`) | IRQ (PS/2, `HandleRelative` `:17`), IRQ (USB-Tablet, `RecordTabletMotion` `:40` und `ApplyPending` `:21`), HS (`ApplyPending`, `TakeMotion` `:42`) | Interruptmaske in HS (`CosmosInputService.cs:59`) und Tablet-Handler (`CosmosUsbTabletDriver.cs:53`); PS/2-Schreiber im IRQ | nein: zwei Interruptquellen und die HS können auf verschiedenen CPUs laufen; Ring und Summen sind mehrere Felder | alle Zugriffe auf `packets` unter einer gemeinsamen `CosmosIrqLock` (dieselbe wie für die Cosmos-Mausfelder) | E4-D |
| Cosmos `MouseManager` `X`, `Y`, `ScrollDelta`, `Left/Right/MiddleButton` (über `CosmosMousePlug.cs:21-38`, `CosmosUsbTabletDriver.cs:60`) | HS (`ApplyPending`, Lesen `CosmosInputService.cs:61-69`), IRQ (Tablet: `ApplyPending`, `SetPosition`) | Interruptmaske | nein: die HS liest X, Y und Tasten als ein Paket (Kommentar `CosmosInputService.cs:53`); ein Tablet-IRQ auf CPU 0 kann dazwischen schreiben | dieselbe `CosmosIrqLock` wie `packets`; der Tablet-Handler nimmt sie statt der Maske | E4-D |
| `Input/CosmosUsbTabletDriver.cs:43` `TabletState.Sink` | Treiber-Worker setzt (`Probe` `:33`), IRQ liest (`:61-62`) | „unveränderlich nach Boot“ (vor dem Öffnen der Pipe gesetzt) | ja: wird vor `OpenInterruptPipe` (`:34`) geschrieben und danach nur gelesen | – | – |
| `Scheduling/CosmosRoundRobinScheduler.cs:10-13` `selectedThreads`, `traceNext`, `traceCount`, `preemptions` (Feld der einen Scheduler-Instanz) | SCH (`PickNext` `:33`, `OnTick` `:45`), HS/BG (`Snapshot` `:76`) | Interruptmaske (`:83`); die Hooks laufen maskiert | nein: die Instanz gilt für alle CPUs, die Queue aber je CPU (`InitializeCpu` `:16`); zwei CPUs schreiben `traceNext` und `preemptions` gleichzeitig | Spur und Zähler je CPU in `PerCpuState.SchedulerData` führen; `Snapshot` liest sie je CPU unter deren Queue-Sperre | E4-C |
| `Scheduling/CosmosRoundRobinScheduler.cs:16` `RoundRobinCore` je CPU (`state.SchedulerData`) | SCH | Interruptmaske; `GetRunQueueCount`/`GetRunQueueThread` (`:64-74`) maskieren selbst | unklar: richtig, solange Cosmos jede Queue nur unter ihrer eigenen Sperre anfasst; `OnThreadMigrate` (`:56`) berührt zwei Queues, `GetRunQueue*` können von einer fremden CPU kommen | in E4-C festlegen, welche Sperre Cosmos in den Hooks hält; `GetRunQueue*` nur unter der Queue-Sperre der betroffenen CPU, Migration in fester Reihenfolge der beiden Sperren | E4-C |
| `Scheduling/CosmosRoundRobinScheduler.cs:55` `SelectCpu` → immer `currentCpu` | SCH | „nur ein Kontext“ (eine CPU) | ja für Etappe 5 (alle Threads auf CPU 0); ab Etappe 6 ersetzt | Platzierung aus E4-B | E4-B/E6 |
| `Runtime/FaultIsolationPolicy.cs:52` `ThreadQuarantine.entries` | EXC schreibt (`Add` `:58`), SCH liest (`OnThreadReady`, `CosmosRoundRobinScheduler.cs:23`) | `Volatile` je Eintrag | ja: ein Eintrag je Thread-Id, ein Schreiber je Id, nur Wechsel von 0 auf 1 | – | – |
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
- `Runtime/CosmosVmwareClockPlug.cs:4` `CosmosVmwareClock`: ohne Zustand (CPUID).
- `Scheduling/CosmosStackProbe.cs`: ohne Zustand.
- `Network/CosmosNetworkService.cs:18` `Gate` und `:25` `ReadStatus`: kein Interrupt- oder Rückrufkontext; Zugriffe nur aus Threads (Shell, BG, WN), daher in „Hauptschleife gegen Worker“ (E0-B).
- `Network/CosmosNetworkProbe.cs`: Gastprobe, nur mit eigener Buildoption; ihre `Volatile`-Felder teilen nur zwei Probe-Threads.

### Rückrufe aus dem Interrupt

Cosmos ruft diese ZehOS-Methoden im Interrupt- oder Ausnahmekontext auf:

- `Input/CosmosKeyboardPlug.cs:10` `HandleScanCode` (Plug in `KeyboardManager`, PS/2-IRQ) → `CosmosKeyboardCapture.ReportScanCode` `:26`.
- `Input/CosmosMousePlug.cs:17` `HandleRelative` (Plug in `MouseManager`, PS/2-Maus-IRQ; auch aus `ReportRelative` des Tablets).
- `Input/CosmosUsbTabletDriver.cs:46` `TabletState.OnReport` (Interrupt-Pipe, `:34`; „kann im Interrupt laufen“, `:45`).
- `Scheduling/CosmosRoundRobinScheduler.cs` alle `IScheduler`-Hooks (`OnTick` `:45` im Timer-IRQ; `PickNext`, `OnThreadReady`, `OnThreadBlocked`, `OnThreadYield`, `OnThreadExit`, `OnPickFailed` im IRQ oder im Aufrufer mit maskierten Interrupts).
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

Paket E0-C, folgt.
