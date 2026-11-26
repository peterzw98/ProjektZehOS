# Ladybird: Woche bis zur Vorstellung (Freitag, 4. Dezember 2026)

> Auftrag Peter, 26.11.2026: bis Donnerstag 3.12. arbeiten, Freitag vorstellen; was dann nicht fertig ist, fehlt in der Vorstellung. Alles läuft auf den Story-Branches, master und `cosmos` bleiben unberührt (Ausnahme: nicht-Ladybird-Arbeit wie #714).

**Ziel:** (1) Entscheidungsvorlage N1-Z mit Messzahlen, (2) Vorziehen von N2: ein statisches `x86_64-linux-musl`-Programm startet im ZehOS-Terminal und endet sauber, zuletzt Ladybirds `js` mit `1+1`.

**Branches:** `story/ladybird-cosmos` (Cosmos-Patches, Basis `cosmos`) und `story/ladybird` (ZehOS, Basis master; der Pin zeigt für Gastbauten auf `story/ladybird-cosmos`). Jedes Paket arbeitet auf `task/lb-<paket>` von `story/ladybird` aus und wird vom Lead per Merge (kein Squash, kein Force-Push) in die Story übernommen. Kein Ladybird-PR gegen master oder `cosmos`.

**Grundlagen:** [N2-Umriss](2026-11-24-ladybird-n2-prozesse.md), Aufrufsatz S1 und Startstack in [Entwurf](../specs/2026-11-24-native-pakete-design.md) Zeile 74, Testprogramme `experiments/LinuxAbi` (N1-E), Ring-3-Einstieg `UserMode.Enter/SyscallHandler/FaultHandler` (N1-A), Seitentabellen je Thread (N1-B).

## Pakete

| Paket | Inhalt | Dateien | Wer | wartet auf |
| --- | --- | --- | --- | --- |
| V0 Story | restliche N1-Branches in `story/ladybird`, probe.S umnummerieren, Gastlauf aller Fälle | `ProjektZeh/Native/UserProbe/`, `ProjektZeh/Runtime/CosmosUserModeProbe.cs` | senior | – |
| V1 Lader | ELF64 statisch und statisch-PIE parsen, Segmente in einen Benutzeradressraum legen, Startstack (argv, envp, auxv wie S1); Parser mit Hosttests | `ProjektZeh/Processes/Elf*.cs`, `ProjektZeh/Processes/UserImage.cs` | senior | V0 |
| V2 Aufrufe | Linux-Aufrufe S1 als plattformunabhängiger Verteiler mit Hosttests; Speicherzugriff nur über eine Schnittstelle `IUserMemory` (prüft jeden Zeiger und jede Länge überlaufsicher) | `ProjektZeh/Processes/Syscalls/`, `docs/LINUX_ABI.md` | engineer, ab #717 auch senior2 | – |
| V3 Prozess und Terminal | Prozess (Deskriptoren 0–2 auf das Terminal, Ende, Abbruch bei Fehler), Brücke `SyscallHandler` → V2, Befehl `run <datei>` nur im Testbau (`-p:ZehOSLinuxRun=true`) | `ProjektZeh/Processes/`, `ProjektZeh/Shell/` | engineer2 nach #714 | V1-Schnittstelle |
| V4 `js` | `js` aus Ladybird statisch mit musl (Teil von #622), Größe, nötige Patches | `experiments/Ladybird.Probe/musl/` | senior25 | Ladybird-Bau |
| V5 Messung | #450, #635, #636 auf diesem Rechner | Berichte unter `docs/superpowers/specs/` | senior25 | Bau, V4 |
| V6 Vorlage | Abschnitt „Ergebnis N1“ in `docs/LADYBIRD_FEASIBILITY.md`, Folien-Stichpunkte für Freitag | Doku | Lead | alles |

**Gemeinsame Schnittstellen (fest):**

- `IUserMemory`: `bool TryRead(ulong addr, Span<byte> dst)`, `bool TryWrite(ulong addr, ReadOnlySpan<byte> src)`, `bool TryReadCString(ulong addr, int max, out string s)`.
- `LinuxSyscalls.Dispatch(ref SyscallArgs a, ILinuxProcess p) -> long` (negativer Wert = `-errno`); `SyscallArgs` mit `Number, A0..A5`.
- `ILinuxProcess`: `IUserMemory Memory`, Deskriptortabelle, `Brk`, `MapAnonymous(ulong len, int prot) -> ulong`, `Exit(int code)`, `ITextOutput Terminal`.
- `UserImage ElfLoader.Load(ReadOnlySpan<byte> elf, string[] argv, string[] envp)` → `Entry`, `StackPointer`, belegte Bereiche.

**Abnahme Demo (Stufen, jede für sich vorzeigbar):** (a) N1-E-`hello` gibt seinen Text im Terminal aus und endet mit 0; (b) die übrigen N1-E-Programme gegen `expected/`; (c) `js -c '1+1'` gibt `2` aus. Prüfung im Gast (QEMU/KVM), Ergebnis mit Befehl und Ausgabe in die Vorlage.

**Ablauf:** Zwischenstand je Paket täglich 18 Uhr an den Lead; Mittwoch 2.12. Abend Stand einfrieren, Donnerstag nur noch Prüfen und Vorlage.
