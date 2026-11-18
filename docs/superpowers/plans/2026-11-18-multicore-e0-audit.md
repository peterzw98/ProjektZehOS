# Etappe 0: Audit geteilter Zustände

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-18-multicore.md) lesen. Diese Etappe ändert keinen Code.

**Ziel:** Eine vollständige Liste aller Zustände, die mehr als ein Ausführungskontext liest oder schreibt, mit dem
heutigen Schutz und dem Urteil, ob er auf mehreren CPUs reicht.

**Warum:** Auf einer CPU fallen viele Rennen nie auf. Etappe 6 gibt Threads auf weitere CPUs frei; jeder Befund
„Schutz fehlt“ wird dort ein eigenes Paket.

**Entwurf:** [Abschnitte 5.4 und 5.5](../specs/2026-11-18-multicore-design.md).

**Ergebnis:** `docs/SMP_AUDIT.md`, eine Tabelle je Bereich. Drei Pakete, voneinander unabhängig; jedes schreibt seinen
eigenen Abschnitt in dieselbe Datei. Damit sie sich nicht in die Quere kommen, legt E0-A die Datei mit den drei leeren
Abschnittsüberschriften an und merged zuerst; E0-B und E0-C füllen danach nur ihren Abschnitt.

## Ausführungskontexte

Jede Zeile der Tabelle nennt, welche dieser Kontexte zugreifen:

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

## Tabellenformat

```markdown
| Zustand (Datei:Zeile) | Kontexte | Schutz heute | Reicht auf mehreren CPUs? | Nötige Änderung | Etappe |
| --- | --- | --- | --- | --- | --- |
| `Input/CosmosKeyboardPlug.cs:12` Ringpuffer `buffer` | IRQ schreibt, HS liest | Interruptmaske (`:34`, `CosmosInputService.cs:59`) | nein | `CosmosIrqLock` | E4-D |
```

„Schutz heute“ ist eines von: `lock (<Objekt>)`, `StorageLock`, `Interlocked`/`Volatile`, Interruptmaske, „nur ein
Kontext“, „unveränderlich nach Boot“, „keiner“. „Reicht?“ ist `ja`, `nein` oder `unklar` mit einem Satz Begründung.
Bei `nein` und `unklar` steht in „Nötige Änderung“ ein konkreter Vorschlag (welche Sperre, welches Feld).

## Methode (für alle drei Pakete gleich)

- [ ] **Schritt 1:** Kandidaten sammeln. In den Verzeichnissen des Pakets suchen nach: `static` Feldern ohne `readonly` und ohne `const`; `static readonly` Feldern veränderlicher Typen (Listen, Wörterbücher, Arrays, Puffer); `MaskInterrupts`; `lock (`; `Monitor.`; `Interlocked.`; `Volatile.`; `[ThreadStatic]`; `UnmanagedCallersOnly`; in C: Dateiebenen-Variablen ohne `const`, `static` in Funktionen.
- [ ] **Schritt 2:** Für jeden Kandidaten die Zugreifer bestimmen (Aufrufer bis zu einem der Kontexte oben verfolgen). Instanzfelder von Objekten, die mehrere Kontexte erreichen (etwa die Sitzung einer Wasm-App), gehören ebenfalls in die Tabelle.
- [ ] **Schritt 3:** Zeile schreiben. Kandidaten mit nur einem Kontext kommen in eine zweite, kurze Liste „Geprüft, nur ein Kontext“ mit Datei:Zeile, damit das Review die Vollständigkeit prüfen kann.
- [ ] **Schritt 4:** Verweise prüfen: `python scripts/check-doc-links.py`. Erwartet: „Alle relativen Verweise sind gueltig.“
- [ ] **Schritt 5:** Commit, Review, PR. Reine Doku: kein unabhängiges Review vor dem PR nötig, die Stichprobe unten macht der Reviewer am PR.

**Abnahme je Paket:** Jeder Kandidat aus Schritt 1 steht in der Tabelle oder in der Liste „nur ein Kontext“. Der
Reviewer zieht 20 Kandidaten per Suche und findet alle 20.

## Paket E0-A: Interrupt gegen Thread

**Rolle:** Engineer senior (hardwarenah). **Branch:** `task/mc-e0-a-audit-irq`.

**Dateien lesen:** `ProjektZeh/Input/**`, `ProjektZeh/Security/CosmosVirtioEntropyDriver.cs`,
`ProjektZeh/Runtime/CosmosVmwareClockPlug.cs`, `ProjektZeh/Runtime/CosmosFaultIsolation.cs`,
`ProjektZeh/Runtime/FaultIsolationPolicy.cs`, `ProjektZeh/Scheduling/Cosmos*.cs`, `ProjektZeh/Network/Cosmos*.cs`
(nur soweit Interrupt- oder Rückrufkontext), `ProjektZeh/Graphics/DisplayService.cs`.

**Anlegen:** `docs/SMP_AUDIT.md` mit Einleitung (Stand, Commit, Verweis auf diesen Plan), der Kontexttabelle von oben
und den drei Überschriften `## Interrupt gegen Thread`, `## Hauptschleife gegen Worker`, `## C-Code`.

**Zusätzlich beantworten** (je ein Absatz am Ende des Abschnitts): Welche ZehOS-Rückrufe ruft Cosmos aus einem
Interrupt auf (Liste mit Datei:Zeile)? Welche davon nehmen eine Sperre, die auch ein Thread hält?

## Paket E0-B: Hauptschleife gegen Worker (C#)

**Rolle:** Engineer. **Branch:** `task/mc-e0-b-audit-worker`. **Nach:** E0-A gemergt.

**Dateien lesen:** `ProjektZeh/Runtime/Wasm/**`, `ProjektZeh/Apps/Wasm/**`, `ProjektZeh/Runtime/InteractiveSession.cs`,
`ProjektZeh/Runtime/InstallerSession.cs`, `ProjektZeh/Scheduling/**`, `ProjektZeh/Messaging/**`,
`ProjektZeh/Network/**`, `ProjektZeh/Packages/**`, `ProjektZeh/Update/**`, `ProjektZeh/Storage/StorageLock.cs` und
`AppStorageGate`, `ProjektZeh/SystemServices/**`, `ProjektZeh/Security/**` (ohne Treiber), `ProjektZeh/Kernel.cs`.

**Zusätzlich beantworten:** Für jede Wasm-Hostfunktion (`WasmRuntime.HostCallbacks.cs`): Welche Daten der
Hauptschleife fasst sie an (Fensterpuffer, Eingabepuffer, Netz, Dateien, Uhr), und unter welcher Sperre? Als eigene
Tabelle „Hostfunktion | Daten | Sperre | Reicht?“.

## Paket E0-C: C-Code

**Rolle:** Engineer. **Branch:** `task/mc-e0-c-audit-c`. **Nach:** E0-A gemergt.

**Dateien lesen:** `ProjektZeh/Native/Wasm/host/**`, `ProjektZeh/Native/Wasm/libc/**`,
`ProjektZeh/Native/Wasm/wamr-platform/**`, `ProjektZeh/Native/AotProbe/**`. In `ProjektZeh/ThirdParty/wasm3/source/` und
`ProjektZeh/ThirdParty/wamr/core/` nur Dateiebenen-Variablen, die zur Laufzeit geschrieben werden.

**Zusätzlich beantworten:** Heute läuft höchstens ein Wasm-Lauf zur gleichen Zeit (`WasmRuntime`, ein Worker). Welche
globalen C-Zustände verhindern mehrere gleichzeitige Läufe (für 12.7 und 12.9)? Diese Zeilen bekommen in „Etappe“ den
Eintrag „nicht in diesem Plan“, damit Etappe 6 sie nicht für nötig hält.
