# Übergabe: Cosmos 3.0.94 → 3.0.97 und Rückbau der Umwege

Stand: 16. November 2026, Basis `master` 499321e (ZehOS 0.5.3). Erstellt von agent-peter-master als Übergabe für einen anderen Agenten. Das Update hat Peter (Benutzer) am 16. November 2026 ausdrücklich freigegeben; die Zuteilung im Kanal macht agent-coordinator.

## Befund

- Cosmos 3.0.94 führt beim Exception-Unwind keine `finally`-/`fault`-Funclets aus. Der Dispatcher kennt nur `RhpCallCatchFunclet` und `RhpCallFilterFunclet`; in `Cosmos.Kernel.Native.X64/build/CPU/ExceptionHandling.s` und `Cosmos.Kernel.Core.dll` des Pakets 3.0.94 gibt es kein `RhpCallFinallyFunclet`. Außerdem hält 3.0.94 den Dispatcherzustand global (`__cosmos_exinfo_stack_head`, „single-threaded kernel“), obwohl ZehOS mehrere Threads verwendet.
- Upstream (`CosmosOS/Cosmos`, Branch `gen3`) ist das behoben und in den Releases ab **v3.0.95** enthalten (geprüft: `ExceptionHandling.s` in v3.0.97 enthält `RhpCallFinallyFunclet`, in v3.0.94 nicht):
  - PR #3319 `feature/finallyFunclet` (11.10.2026): c2912ed7f „Call a finally/fault funclet during exception unwinding“, 36844171c „Run the finally funclets the exception leaves, in the catching frame too“.
  - PR #3330 `fix/440-per-thread-exception-dispatch` (18.10.2026): e7d31f2d2 „Keep the exception dispatcher's state per thread“.
- Neueste Version: **3.0.97** (1.11.2026). Zwischen v3.0.94 und v3.0.97 liegen 129 Commits.
- Ein eigener IL-Umbau im Patcher (try/finally → catch + rethrow) ist damit nicht nötig. Der richtige Weg ist das Update.

## Weitere relevante Änderungen bis 3.0.97 (Vorauswahl)

Diese Liste ist eine Vorauswahl aus den Commit-Titeln, keine vollständige Prüfung. **Selbst die vollständige Änderungsliste `v3.0.94...v3.0.97` durchgehen** (`gh api repos/CosmosOS/Cosmos/compare/v3.0.94...v3.0.97`) und für jede in `docs/COSMOS_SUPPORT.md`, `AGENTS.md` und im Code dokumentierte Grenze bzw. jeden Umweg prüfen, ob er jetzt entfallen kann oder ob die Änderung ZehOS bricht.

| Upstream-Änderung | Bezug in ZehOS |
| --- | --- |
| 1.11. „Give the C code a standard memset, memmove and memcmp“ | Eigener Ersatz in `ProjektZeh/Native/Wasm/libc/` (`string.h`, `libc.c`) wegen des wortweisen `__cosmos_memcmp`; prüfen, ob er entfallen kann |
| 1.11. „Replace LAI with uACPI“ | Energieaktionen: `scripts/test-power-commands.ps1` zwingend, auch VMware (Ausschalten scheiterte dort bisher) |
| 1.11. „Panic when a thread parks with interrupts masked“ | Risiko: `Scheduling/CosmosRoundRobinScheduler.cs` und `Input/CosmosKeyboardPlug.cs` arbeiten mit Interruptmasken |
| 1.11. „Lock the network stack instead of masking interrupts“, 27.10. TCP-Connect-Request-Fix, 26.10. DHCP je Adapter, 22.10. DNS-Antwort-Parser | Netzwerk-Umwege in `Network/` (ein Worker, blockierender Connect, Fristen); `docs/NETWORK_TESTS.md` |
| 29.10. „PIT: tick every 10 ms, and never under the period asked for“ | Zeitmessung/Kalibrierung, `Runtime/CosmosVmware*Plug.cs`, Taskleistenuhr, VMware-Zeitkalibrierung |
| 23.10. Finalizer auf eigenem Thread (Standard) | Threadanzahl, Taskmanager-Anzeige, Scheduler-Policy |
| 27.10. SMP Phase 0 (atomare Interlocked/Scheduler-Zähler) | eigener Round-Robin-Scheduler (Etappe 6a) |
| 8.10. `KeyboardTextReader.Read` über `ConsoleSession.WaitForKey` | Tastatur-Plug und Rohpfad prüfen |
| 29.10. ISO-9660-Treiber, CD-ROM hinter AHCI, `isoFiles/` | Installer/ISO-Inhalt, ggf. neue Möglichkeiten |
| 11.10. EHCI/OHCI-Hostcontroller | USB-Tablet-Treiber des Projekts prüfen |

Außerdem selbst nachsehen: Thread-Stacks beendeter Threads (bisher nicht freigegeben), Break-Ereignisse der Tastatur, absolute Zeigerposition (`MouseManager.HandleAbsolute`), Patcher-/Plug-API-Änderungen (`[Plug]`, Driver Kit, `VirtioAccess`).

## Auftrag

1. **Update als eigene technische Änderung** (eigener Branch, z. B. `task/cosmos-3.0.97`): `ProjektZeh/ProjektZeh.csproj` (`Cosmos.Sdk/…`, `Cosmos.Kernel`, `Cosmos.Kernel.System`), `.github/workflows/ci.yml` und `release.yml` (`Cosmos.Tools`, `Cosmos.Patcher`), lokale `cosmos`-CLI und Patcher, README/`docs/COSMOS_SUPPORT.md`. SDK, Kernelpakete, CLI und Patcher müssen zusammenpassen.
2. **Nachweis `finally`:** `Messaging/MessageGuestProbe.cs` meldet bisher `first-finally=0 second-finally=0`. Nach dem Update im Gast `1/1` belegen, zusätzlich einfaches `try/finally`, `using`, `lock` und Ausnahme in einem zweiten Thread. Erst danach Umwege zurückbauen.
3. **Volle Prüfung** nach AGENTS.md: Hosttests mit `-warnaserror`, nativer Normal- und Recovery-Build, QEMU (Boot, Shell, Eingabe, Desktop, Dateien mit isolierter Imagekopie, Netzwerk/HTTPS, `pkg install doom`/`netsurf`/`rechner`, Wasm-Apps, Installer/Update), `test-power-commands.ps1`, VMware. Ergebnisse in `docs/COSMOS_SUPPORT.md` mit Datum.
4. **Rückbau der Umwege** (gern als zweiter PR auf dem Update): Stellen, die wegen der Unwind-Grenze ohne `finally`/`using` arbeiten oder Ressourcen doppelt freigeben, auf den normalen Weg umstellen. Verhalten und Fehlerwege dabei nicht ändern. Bekannte Stellen auf `master` 499321e:
   - `Graphics/Desktop.cs:558`
   - `Network/CosmosNetworkService.cs:165`
   - `Packages/PackageSource.cs:28`
   - `Packages/PackageStore.cs:141`, `:202`, `:276`, `:324`, `:508`
   - `Scheduling/BackgroundTaskService.cs:81`
   - `Messaging/MessageDemoService.cs:61`
   - `Security/CosmosVirtioEntropyDriver.cs:80`
   - `Security/PasswordHasher.cs:62`
   - `Shell/PackageShellCommands.cs:192`
   - `Update/CosmosHttpUpdateSource.cs:86`
   - Suche: `git grep -nE 'finally|Unwind|3\.0\.94' -- ProjektZeh`
5. **Regeln anpassen:** `AGENTS.md` (Abschnitt „Technischer Rahmen“, `finally`-Regel und Versionsangabe), `docs/COSMOS_SUPPORT.md`, Hinweise in offenen Plänen. Die Regel erst entfernen, wenn Schritt 2 im Gast belegt ist.
6. ZehOS-Version beim PR erhöhen (Basiswechsel, Vorschlag Minor). Unabhängiges Review vor dem PR.

## Grenzen dieser Übergabe

Nur Quellen, Paketinhalt und Commit-Titel gelesen. Weder gebaut noch im Gast geprüft. Ob 3.0.97 ohne weitere Anpassungen mit ZehOS baut, ist offen.
