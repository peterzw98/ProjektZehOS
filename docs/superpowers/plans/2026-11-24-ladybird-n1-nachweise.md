# Ladybird N1: Nachweise

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-24-ladybird-nativ.md) und den [Entwurf](../specs/2026-11-24-native-pakete-design.md), Abschnitte 3 bis 6, lesen, danach nur dein Paket und „Gemeinsame Schnittstellen“. Ein Paket = ein Branch = ein PR. Arbeitsweise: superpowers:executing-plans.

**Ziel:** Vor dem eigentlichen Bau klären, ob Ring 3 und eigene Seitentabellen unter Cosmos/NativeAOT tragen, welche Systemaufrufe Ladybird wirklich nutzt und ob es mit musl baut. Neu geschnitten am 24. November 2026 (Auftrag Peter: kleine, parallele Pakete; der Plan selbst ist freigegeben).

**Urteil je Gruppe (A, B, C, D, E):** `trägt` / `trägt nicht` / `offen`, mit Befehl und Messwert, im letzten Paket der Gruppe. Bericht: `docs/superpowers/specs/2026-11-24-ladybird-n1-<thema>.md`. Bei `trägt nicht`: genaue Stelle und Grund. Code-PRs brauchen ein unabhängiges Review.

**Zeitlimit:** je Paket steht eine Größe in Arbeitstagen. Das Doppelte ist die Grenze; danach Stand pushen, `[BLOCKER]` mit Ursache. Gruppe A zusammen höchstens 20, Gruppe B höchstens 15 Arbeitstage, D zusammen 6 (Bauzeit zählt nicht).

**Cosmos-Probebranches:** Alle Cosmos-Pakete sind Entwurfs-PRs nach `cosmos` und werden **nicht gemergt**. Wer auf einem anderen Cosmos-Paket aufbaut, startet von dessen geprüftem Branch (normaler Merge, kein Force-Push). ZehOS-Branches, die einen Probestand brauchen (A6, A7, B4, B5), setzen `deploy/cosmos/COMMIT` und `VERSION` nur dort und bleiben Entwurf. Auf `master` gehen nur A5, B1, C1, C2, D1, D2, E1–E3 und die Berichte.

## Gemeinsame Schnittstellen (fest)

Experimentell (`COSMOS0001`). Gemeinsamer Rahmen von Stub und `syscall`-Eintritt: wie `ThreadContext` ohne FPU-Bereich.

```csharp
namespace Cosmos.Kernel.Core.CPU;

public struct UserFrame
{
    public ulong R15, R14, R13, R12, R11, R10, R9, R8, Rdi, Rsi, Rbp, Rbx, Rdx, Rcx, Rax;
    public ulong Interrupt, CpuFlags, Cr2, Temp;   // wie heute im Stub; beim syscall 0
    public ulong Rip, Cs, Rflags, Rsp, Ss;
}

public static unsafe class UserMode
{
    public static bool IsSupported { get; }          // false auf ARM64 und vor Initialize
    public static bool Initialize();                 // einmal, vor dem ersten Enter, für alle Online-CPUs
    // Läuft mit Interrupts an auf dem Kernelstack des Threads. Ergebnis nach Rax des Rahmens schreiben.
    public static delegate* unmanaged<UserFrame*, void> SyscallHandler;
    // Vektor, Fehlercode, Fehleradresse (CR2, sonst 0). 1 = Anweisung wiederholen, 0 = Enter kehrt zurück.
    public static delegate* unmanaged<UserFrame*, ulong, ulong, ulong, int> FaultHandler;
    public static ulong Enter(ulong rip, ulong rsp); // aus einem Kernelthread; liefert den Wert von LeaveCurrent
    public static void LeaveCurrent(ulong exitValue); // nur im Handler; merkt vor, Enter kehrt nach dem Handler zurück
    public static void SetFsBase(ulong address);     // je Thread gesichert und wiederhergestellt
    // Von jedem Thread: Enter des Zielthreads kehrt mit exitValue zurück, spätestens beim nächsten Interrupt im Ring 3.
    public static void RequestLeave(SchedulerThread thread, ulong exitValue);
}

namespace Cosmos.Kernel.Core.Memory;

public sealed class UserPageTable : IDisposable      // Dispose erst ohne gebundenen Thread; gibt Tabellen frei, nicht die Seiten
{
    public static bool ShareKernelHalf();            // einmal: alle 256 oberen PML4-Einträge anlegen
    public static UserPageTable? TryCreate();        // untere Hälfte leer, obere gemeinsam
    public bool TryMap(ulong user, ulong physical, bool writable, bool executable); // 4 KiB; beides zugleich → false
    public bool TryProtect(ulong user, bool writable, bool executable);
    public ulong Unmap(ulong user);                  // physische Adresse oder 0; Seite gehört danach dem Aufrufer
    public bool TryTranslate(ulong user, bool write, out ulong physical);
    public void BindToCurrentThread();               // N1-B3
    public static void UnbindCurrentThread();        // N1-B3: zurück auf die Kerneltabellen
    public static ulong AllocateFrame();             // genullt, physische Adresse; 0 bei Mangel
    public static void FreeFrame(ulong physical);
}
```

`LeaveCurrent` und `RequestLeave` setzen einen Merker je Thread. Ein Handler läuft zu Ende und gibt seine Sperren frei; erst der Austrittspfad nach Ring 3 prüft den Merker und lässt `Enter` zurückkehren.

**GDT:** Limines Kernelselektoren bleiben (`0x28` Code, `0x30` Daten); dazu `0x38` reserviert, `0x40` Programmdaten, `0x48` Programmcode, `0x50` TSS (16 Byte). `STAR[47:32]=0x28`, `STAR[63:48]=0x38`.

**Seriallog der Gastproben:** je Fall `USER_PROBE <name> ok=<0|1> …`, am Ende `USER_PROBE done failed=<n>`.

## Gruppe A: Ring 3 unter Cosmos

Frage: Kann ein Thread im Ring 3 laufen, unterbrochen werden, Systemaufrufe machen und fehlerhaft enden, ohne dass Kernel, GC oder andere CPUs leiden?

### N1-A1: GDT, TSS und IST je CPU

- **Branch:** `cosmos-patch/user-gdt` · **Größe:** 2 · **Rolle:** Senior (Opus) · **Voraussetzung:** #523 auf `master`
- **Dateien:** anlegen `src/Cosmos.Kernel.Core.X64/Cpu/Gdt.cs`, `src/Cosmos.Kernel.Native.X64/CPU/Gdt.s`; ändern `src/Cosmos.Kernel.Core.X64/Cpu/Idt.cs` (IST-Nummer je Gate). GDT und TSS werden in `Idt.RegisterAllInterrupts` (Boot-CPU) und `Idt.LoadOnCurrentCpu` (weitere CPUs) geladen. Keine gemeinsame Datei mit A2: **nicht** `Interrupts.s`, `X64InterruptController.cs`, `LocalApic.cs`, `ThreadContext.X64.cs`. A1 ist von R8 unabhängig, weil es den Tick nicht berührt.
- **Inhalt:** eigene GDT und TSS je CPU, IST 1–3 für #DF, NMI, #MC; IOPB-Offset hinter dem TSS-Limit; `RSP0` auf einen gültigen Kernelstack. Handler auf einem IST-Stack dürfen keinen Threadwechsel vormerken und keinen Stack des unterbrochenen Threads melden: Prüfen, ob ein Stop per NMI (`CpuStop`, Panikpfad) den Rahmen als Threadkontext speichert; dann NMI erst in A3 auf IST legen und das im PR nennen.
- **Abnahme:** Cosmos-Testkernel `Interrupts`, `Smp`, `Threading`, `GarbageCollector` wie ohne Patch, 1 und 4 CPUs; Selektoren und TSS jeder CPU im Seriallog; NMI-Probe (`scripts/test-isolation-probe.py`, NMI-Fall) unverändert.

### N1-A2: FPU-Zustand vollständig sichern

- **Branch:** `cosmos-patch/fxsave` · **Größe:** 2 · **Rolle:** Senior (Opus) · **Voraussetzung:** #523 auf `master` und der Cosmos-Patch R8 aus #423 im Branch `cosmos`
- **Dateien:** `src/Cosmos.Kernel.Native.X64/CPU/Interrupts.s` (nur FPU-Teil), `src/Cosmos.Kernel.Core/Scheduler/ThreadContext.X64.cs`, `src/Cosmos.Kernel.Core.X64/Cpu/X64InterruptController.cs` (`XmmSaveAreaSizeBytes`, heute 256; benutzt im Reschedule- und im Stop-Pfad, Z. 156 und 201), `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs` (Tick, Z. 594); die Stub-Versätze 256 und 400. Nur prüfen, bei Bedarf ändern: `SchedulerThread.cs` (`ThreadContext.Size`, `GetContext`), `GarbageCollector.Mark.cs`, `CpuStop` und Panikpfad (Patch `panic-stop-cpus`), `TryAbandonCurrentFromInterrupt` (`ausnahme-hook`, `abandoned-thread-reap`) und der R8-Hook in `OnTimerInterrupt`. Test in `tests/Kernels/Cosmos.Kernel.Tests.Threading`.
- **Inhalt:** Die Bereichsgröße steht danach an genau einer Stelle je Sprache; `git grep` auf 256 und 400 im PR belegt, dass kein alter Wert übrig ist. `fxsave64`/`fxrstor64` (512 Byte, 16-Byte-ausgerichtet) statt `movdqu` XMM0–15; neue Threads beginnen mit FCW `0x37F` und MXCSR `0x1F80`.
- **Abnahme:** neuer Test: zwei Threads mit verschiedenen x87-, MXCSR- und XMM-Werten bleiben 10 s unverändert (ohne Patch rot); Testkernel wie A1; GC-Tests grün; Kerneltest von R8, aufgegebene Threads (`scripts/test-isolation-probe.py`) und Panik mit 4 CPUs unverändert.

### N1-A3: Interruptstub mit Herkunft Ring 3

- **Branch:** `cosmos-patch/user-stub` auf A2 · **Größe:** 2 · **Rolle:** Senior (Opus) · **Voraussetzung:** N1-A2
- **Dateien:** `src/Cosmos.Kernel.Native.X64/CPU/Interrupts.s` (Ein- und Austritt), `src/Cosmos.Kernel.Core/CPU/PerCpuBlock.cs` (auf 192 Byte).
- **Inhalt:** Bei Herkunft Ring 3 (`CS.RPL`) `swapgs` am Ein- und Austritt. Vektoren mit Fehlercode prüfen CS bei `[rsp+16]` **vor** dem ersten `gs:`-Zugriff. NMI und #MC lesen `IA32_GS_BASE`, statt der Herkunft zu trauen. Ohne Ring 3 bleibt das Verhalten gleich.
- **Abnahme:** Testkernel wie A1; Disassembly eines Stubs mit und ohne Fehlercode im PR. Der Ring-3-Teil ist erst in A4 ausführbar; das steht so im PR.

### N1-A4: Eintritt, Systemaufruf und Rückweg

- **Branch:** `cosmos-patch/user-mode-probe` auf A3, mit A1 zusammengeführt · **Größe:** 3 · **Rolle:** Senior (Opus) · **Voraussetzung:** N1-A1, N1-A3 (damit auch R8)
- **Dateien:** anlegen `src/Cosmos.Kernel.Native.X64/CPU/UserMode.s`, `src/Cosmos.Kernel.Core/CPU/UserMode.cs`, `src/Cosmos.Kernel.Core.X64/Cpu/UserModeX64.cs`, `tests/Kernels/Cosmos.Kernel.Tests.UserMode/`; ändern `src/Cosmos.Kernel.Core/Scheduler/SchedulerManager.cs`, `SchedulerThread.cs` (RSP0, FS-Basis, Merker je Thread).
- **Inhalt:** Schnittstelle `UserMode`. `EFER.SCE`, `LSTAR`, `SFMASK` (IF, DF, TF, AC, NT) je CPU; SMEP und UMIP, wenn CPUID sie meldet. Vor **jedem** Rückweg (`sysretq` und `iretq`): RIP und RSP in der unteren kanonischen Hälfte, RFLAGS auf erlaubte Bits (IF=1, IOPL=0, NT=0), sonst kehrt `Enter` mit Fehler zurück. FS: belegen, dass Kernelcode FS nicht nutzt (Hinweis in `CpuOps.s`), sonst bei jedem Eintritt sichern. Fehler aus Ring 3 gehen an `FaultHandler`, Fehler aus Ring 0 unverändert an den vorhandenen Hook. Der R8-Hook im Tick lenkt nie um, wenn der Rahmen aus Ring 3 stammt (`CS.RPL`); `RequestLeave` nutzt einen eigenen Merker.
- **Abnahme:** Testkernel `UserMode`, 1 und 4 CPUs: ein Systemaufruf, ein #UD, ein #PF mit Fehlercode, `RequestLeave` auf eine Schleife, nicht kanonischer Rückweg; Kerneltest von R8 grün. Testseiten liegen in einem sonst leeren PML4-Eintrag der unteren Hälfte; die Kernelhälfte erhält nie ein User-Bit.

### N1-A5: Probeprogramm und Auswertung

- **Branch:** `task/ladybird-n1a5-probe` · **Größe:** 2 · **Rolle:** Engineer · **Voraussetzung:** keine
- **Dateien (anlegen):** `ProjektZeh/Native/UserProbe/probe.S`, `ProjektZeh/Native/UserProbe/build.sh`, `scripts/test-user-mode-probe.py`, `scripts/tests/test_user_mode_probe.py`
- **Inhalt:** positionsunabhängiges Programm, ein Einstieg je Fall; `0x7A01` zählt, `0x7A02` endet. Das Skript hat `--iso`, `--accel`, `--smp`, `--runs`, `--expect a,b,c`: Es startet QEMU wie die vorhandenen Probeskripte, wertet nur `USER_PROBE`-Zeilen; fehlender Fall, `ok=0` oder fehlende Schlusszeile ergeben Exit 1.
- **Abnahme:** Skripttests mit Beispielprotokollen grün (alles gut, ein Fehlschlag, fehlende Schlusszeile, fehlender Fall, Zeitlimit); `probe.S` assembliert, Disassembly im PR; Einstiege liegen fest in `probe.S` (Kopftabelle mit Namen und Versatz). Noch nicht in den Kernelbau eingebunden.

### N1-A6: Gastfälle

- **Branch:** `task/ladybird-n1a6-faelle` · **Größe:** 3 · **Rolle:** Senior (Opus) · **Voraussetzung:** N1-A4, N1-A5
- **Dateien:** anlegen `ProjektZeh/Runtime/CosmosUserModeProbe.cs`; ändern `ProjektZeh/ProjektZeh.csproj` (Schalter `-p:ZehOSUserModeProbe=true`, nie im Normalbau), `ProjektZeh/Kernel.cs` (Aufruf hinter dem Schalter), `deploy/cosmos/COMMIT`, `VERSION`.
- **Fälle:** `loop` 1 000 000 Aufrufe; `spin` 10 s ohne Aufruf, andere Threads laufen weiter; `fpu` wie A2 im Ring 3; `gc` zweiter Thread ruft 1000-mal `GC.Collect()`; `fault` #DE, #UD, #GP (`cli`, `hlt`, `in`) und #PF auf Kerneladressen (lesen, schreiben, ausführen) beenden je nur den Probelauf; `badret` Handler setzt eine nicht kanonische und eine Kernel-Rücksprungadresse; `killspin` `RequestLeave` 1000-mal, bei 4 CPUs von einer anderen CPU; `nmi` während `loop`; `leave` 1000-mal `Enter` und `LeaveCurrent`, freier Speicher ±1 MiB; `gegenprobe` #PF im Kernelthread hält weiter an (eigener Lauf).
- **Abnahme:** alle Fälle je 10 Läufe KVM und 3 TCG, 1 und 4 CPUs.

### N1-A7: Messung und Urteil Ring 3

- **Branch:** `docs/ladybird-n1a7-urteil` · **Größe:** 2 · **Rolle:** Engineer; Urteil Senior · **Voraussetzung:** N1-A6
- **Dateien:** Bericht `…-n1-ring3.md`
- **Inhalt:** Systemaufruf hin und zurück (`rdtsc` im Ring 3, 100 000 Aufrufe, Median, KVM; Ausgabe des Falls `loop`); Doom-Bildrate mit `scripts/test-wasm-doom-desktop.py --scenario full` unter der Standardlaufzeit, 5 Läufe je Stand, Median, mit und ohne Cosmos-Patches; `scripts/test-power-commands.ps1`.
- **Abnahme / Regel:** `trägt`, wenn A6 erfüllt ist, der Median ≤ 5 µs beträgt und Doom höchstens 5 % verliert. `trägt nicht`, wenn ein Fall mit bekannter Ursache in Cosmos oder NativeAOT scheitert, die keinen abgegrenzten Patch erlaubt. Sonst `offen`.

## Gruppe B: Eigene Adressräume

### N1-B1: AddressSpaceMap (Hostteil)

- **Branch:** `task/ladybird-n1b1-adressraum` · **Größe:** 2 · **Rolle:** Engineer · **Voraussetzung:** keine
- **Dateien:** anlegen `ProjektZeh/Processes/AddressSpaceMap.cs`, `ProjektZeh.Tests/Processes/AddressSpaceMapTests.cs`; ändern `ProjektZeh.Tests/ProjektZeh.Tests.csproj` (eine Zeile). Einziges N1-Paket mit dieser Datei.

```csharp
namespace ProjektZeh.Processes;

internal enum MapAccess { None, Read, ReadWrite, ReadExecute }
internal enum Placement { Hint, FixedReplace, FixedNoReplace }   // wie mmap ohne Flag, MAP_FIXED, MAP_FIXED_NOREPLACE
internal sealed class AddressSpaceMap(ulong start, ulong end)    // seitenausgerichtet, [start, end)
{
    public ResultObject<ulong> Reserve(ulong hint, ulong length, MapAccess access, Placement placement);
    public ResultErr Release(ulong address, ulong length);       // teilt Bereiche; Lücken sind erlaubt
    public ResultErr Protect(ulong address, ulong length, MapAccess access); // Lücke → Fehler, nichts geändert
    public bool TryFind(ulong address, out MapAccess access);
}
```

- **Abnahme:** Hosttests zuerst: Überlappung mit `FixedReplace` (ersetzt, wie Linux) und `FixedNoReplace` (Fehler, nichts geändert), Teilen am Anfang, in der Mitte und am Ende, Überlauf `address+length`, nicht ausgerichtete Werte, Länge 0, nächster freier Platz, 32 GiB und 4 TiB + 32 GiB gleichzeitig; 10 000 zufällige Operationen (feste Saat) gegen ein einfaches Seitenmodell. Ohne Warnungen; kein Kernelbau nötig.

### N1-B2: Seitentabellen ohne Umschalten

- **Branch:** `cosmos-patch/user-page-tables` · **Größe:** 3 · **Rolle:** Senior (Opus) · **Voraussetzung:** #523 auf `master`
- **Dateien:** anlegen `src/Cosmos.Kernel.Core.X64/Cpu/PageTables.cs`, `src/Cosmos.Kernel.Core/Memory/UserPageTable.cs`, `tests/Kernels/Cosmos.Kernel.Tests.UserPaging/`; ändern `src/Cosmos.Kernel.Core.X64/Cpu/DeviceMapper.cs` (nur soweit die geteilte Hälfte es verlangt).
- **Inhalt:** `UserPageTable` ohne `BindToCurrentThread`/`UnbindCurrentThread`. Keine gemeinsamen Dateien mit A1–A4.
- **Abnahme:** Testkernel: anlegen, abbilden, schützen, übersetzen, freigeben; schreib- und ausführbar zugleich wird abgelehnt; 1000 Tabellen ±1 MiB; nach `ShareKernelHalf` legt `DeviceMapper` keine PML4-Einträge mehr an. Desktop, Netz und AHCI mit dem Stand unverändert.

### N1-B3: CR3 je Thread

- **Branch:** `cosmos-patch/user-cr3` auf A4, mit B2 zusammengeführt · **Größe:** 2 · **Rolle:** Senior (Opus) · **Voraussetzung:** N1-A4, N1-B2
- **Dateien:** `src/Cosmos.Kernel.Core/Scheduler/SchedulerThread.cs`, `SchedulerManager.cs`, `src/Cosmos.Kernel.Core/Memory/UserPageTable.cs`; Fälle in `tests/Kernels/Cosmos.Kernel.Tests.UserPaging/`.
- **Inhalt:** CR3 beim Threadwechsel, nur wenn verschieden; Kernelthreads laufen mit den Kerneltabellen. Ein sterbender Thread löst die Bindung, bevor die Tabelle freigegeben wird.
- **Abnahme:** zwei Threads mit verschiedenen Inhalten an derselben Adresse, 1 und 4 CPUs; Freigabe nach Threadende; GC während eines fremden CR3; Testkernel wie A1.

### N1-B4: Gastfälle Adressraum

- **Branch:** `task/ladybird-n1b4-faelle` auf A6 · **Größe:** 3 · **Rolle:** Senior (Opus) · **Voraussetzung:** N1-A6, N1-B1, N1-B3
- **Dateien:** anlegen `ProjektZeh/Runtime/CosmosAddressSpaceProbe.cs`; ändern `ProjektZeh/Runtime/CosmosUserModeProbe.cs` (Aufruf), `ProjektZeh/Native/UserProbe/probe.S` (neue Einstiege am Ende), `deploy/cosmos/COMMIT`, `VERSION`. `test-user-mode-probe.py` bleibt gleich (`--expect`).
- **Fälle:** `isolate` zwei Programme an derselben Adresse, 10 s im Wechsel, auf derselben und auf verschiedenen CPUs; `kernelread` Kernelhälfte und alte Identitätsabbildung nicht lesbar; `sparse` 32 GiB + 4 TiB reserviert, 1000 verstreute Seiten, Belegung im `FaultHandler`; `churn` 1000 Adressräume ±1 MiB; `copy` Kopierweg über HHDM mit 10 000 zufälligen Adressen und Längen (feste Saat) ohne Kernelfehler; `device` Netz, AHCI und Framebuffer, während ein Programm läuft; `nx` Ausführen einer Datenseite scheitert; `edge` `syscall` als letzte Anweisung vor `0x7FFF_FFFF_F000` (oberste Seite gesperrt); `gc2` `GC.Collect()` mit eigenem CR3.
- **Abnahme:** alle Fälle je 10 Läufe KVM und 3 TCG, 1 und 4 CPUs.

### N1-B5: Messung und Urteil Adressräume

- **Branch:** `docs/ladybird-n1b5-urteil` · **Größe:** 2 · **Rolle:** Engineer; Urteil Senior · **Voraussetzung:** N1-B4
- **Dateien:** Bericht `…-n1-adressraum.md`
- **Inhalt:** Zeit je Adressraumwechsel und je Seitenfehler (KVM, Median); Desktop, ClassiCube und Netz im Normalbau mit demselben Cosmos.
- **Abnahme / Regel:** `trägt`, wenn B1 und B4 erfüllt sind und der Normalbau unverändert läuft; `trägt nicht` und `offen` wie A7.

## Gruppe C: Aufrufliste von Ladybird

### N1-C1: Auswertung von strace

- **Branch:** `task/ladybird-n1c1-auswertung` · **Größe:** 1 · **Rolle:** Engineer · **Voraussetzung:** keine
- **Dateien (anlegen):** `experiments/Ladybird.Probe/syscalls.py`, `experiments/Ladybird.Probe/test_syscalls.py`. Nicht `README.md` (gehört L1-A).
- **Schnittstelle:** `syscalls.py --trace DIR --out FILE.json` wertet Dateien aus `strace -ff -f -yy -v -o DIR/t` aus: `{"processes": [{"name": "...", "syscalls": {"<name>": {"count": n, "errors": {"ENOSYS": n}, "flags": ["..."]}}}], "paths": ["..."], "sockets": ["..."]}`.
- **Abnahme:** `python3 -m unittest` mit festen Beispielzeilen: `openat`, `clone3`, `futex`, `<unfinished ...>` mit `resumed`, Fehlerwert, Signalzeile, abgeschnittene letzte Zeile.

### N1-C2: Messung und Urteil Aufrufliste

- **Branch:** `docs/ladybird-n1c2-aufrufe` · **Größe:** 2 · **Rolle:** Engineer (Linux) · **Voraussetzung:** N1-C1, #448, #450, derselbe Rechner
- **Dateien:** Bericht `…-n1-aufrufe.md`
- **Inhalt:** drei L1-Seiten headless unter `strace`, dazu `js` mit `1+1`. Tabelle Aufruf × Prozess × Satz; was in S1–S3 fehlt, ist S4; gelesene Pfade unter `/proc`, `/sys`, `/etc`; Threads und größter reservierter Adressraum je Prozess. Je S4-Aufruf `strace -f -e inject=<name>:error=ENOSYS` auf `static.html`: Pflicht oder verzichtbar.
- **Abnahme / Regel:** jeder Aufruf genau einem Satz zugeordnet. `trägt`, wenn höchstens 40 Aufrufe S4-Pflicht sind und darunter keiner von `io_uring_*`, `bpf`, `userfaultfd`, `ptrace` und kein GPU-Gerät; sonst `trägt nicht`.

## Gruppe D: Ladybird mit musl

### N1-D1: LibJS mit musl

- **Branch:** `task/ladybird-n1d1-js` · **Größe:** 3 · **Rolle:** Engineer (Linux) · **Voraussetzung:** #448 (glibc-Bau als Vergleich)
- **Dateien (anlegen):** `experiments/Ladybird.Probe/musl/build.sh`, `experiments/Ladybird.Probe/musl/README.md`, `experiments/Ladybird.Probe/musl/patches/js/`
- **Schnittstelle:** `build.sh --target js|WebContent|all [--workdir DIR]`, Alpine-Wurzelverzeichnis (WSL oder Container), dieselben Bauoptionen wie L1; geschrieben wird nur unter `~/ladybird-probe/`.
- **Abnahme:** Zwischenurteil für `js`: baut, `1+1` und Array-Sort (10 000) richtig; Patchzeilen (Ladybird und vcpkg getrennt), Größe, erste nicht lösbare Stelle. Das Zwischenurteil steht im PR und in der README.

### N1-D2: Ganzes Ladybird mit musl und Urteil

- **Branch:** `task/ladybird-n1d2-alles` · **Größe:** 3 · **Rolle:** Engineer (Linux) · **Voraussetzung:** N1-D1
- **Dateien:** anlegen `experiments/Ladybird.Probe/musl/patches/all/`, Bericht `…-n1-musl.md`; ändern `experiments/Ladybird.Probe/musl/{build.sh,README.md}`
- **Abnahme / Regel:** `trägt`, wenn alles baut, `static.html` headless läuft und höchstens 1000 Patchzeilen nötig sind. Sonst `glibc` mit der ersten nicht lösbaren Stelle; scheitert auch #448, `offen`.

## Gruppe E: Vergleichsprogramme

Jedes Programm schreibt nur deterministischen Text. `expected/<satz>/<name>.txt` enthält die Ausgabe, `<name>.status` `exit <n>` oder `signal <n>`; erzeugt nur auf Linux mit `--update`. Binärdateien werden nicht committed.

### N1-E1: Vergleicher und Satz S1

- **Branch:** `task/ladybird-n1e1-s1` · **Größe:** 3 · **Rolle:** Engineer · **Voraussetzung:** keine
- **Dateien (anlegen):** `experiments/LinuxAbi/{build.sh,README.md,run-linux.py,test_run_linux.py}`, `experiments/LinuxAbi/src/s1/*.c`, `experiments/LinuxAbi/expected/s1/`
- **Schnittstelle:** `build.sh` baut jede `src/<satz>/*.c` statisch für `x86_64-linux-musl` nach `out/<satz>/` und ruft danach `src/<satz>/build.sh`, falls vorhanden. `run-linux.py --bin DIR --expected DIR [--update] --json FILE` vergleicht Ausgabe und Status und notiert die Aufrufe aus `strace -f -c`; Exit 1 bei Abweichung. E2 und E3 ändern diese Dateien nicht.
- **Inhalt S1:** `args_env_auxv`, `files`, `dirs`, `mmap_anon`, `mprotect_fault`, `brk`, `clock`, `random`, `long_double`, `tls`, `badptr` (`EFAULT`), `unknown_syscall` (`ENOSYS`).
- **Abnahme:** Tests des Vergleichers grün (gleich, andere Ausgabe, falscher Status, fehlende Erwartung); alles zweimal gleich auf Linux (Kernelversion in der README); Aufrufe liegen in S1, sonst Ergänzung im Entwurf als eigener Commit.

### N1-E2: Satz S3

- **Branch:** `task/ladybird-n1e2-s3` · **Größe:** 3 · **Rolle:** Engineer · **Voraussetzung:** N1-E1
- **Dateien (anlegen):** `experiments/LinuxAbi/src/s3/*.c`, `experiments/LinuxAbi/expected/s3/`
- **Inhalt:** `threads`, `futex`, `fork_cow`, `exec`, `pipe_epoll`, `unix_fdpass`, `memfd_seal`, `tcp_echo` (nur localhost), `sigsegv_status`.
- **Abnahme:** 20-mal hintereinander gleich auf Linux; Aufrufe liegen in S1 bis S3.

### N1-E3: toybox und Rust

- **Branch:** `task/ladybird-n1e3-werkzeuge` · **Größe:** 2 · **Rolle:** Engineer · **Voraussetzung:** N1-E1
- **Dateien (anlegen):** `experiments/LinuxAbi/src/tools/build.sh`, `experiments/LinuxAbi/src/tools/rust/`, `experiments/LinuxAbi/expected/tools/`
- **Inhalt:** toybox (0BSD, Stand und SHA-256 fest, Quelle nicht im Repository): `cat`, `ls`, `wc` (S1), `sh` (S3); Rust mit `std`: Datei und Zeit (S1), Thread (S3).
- **Abnahme:** zweimal gleich auf Linux; Aufrufe je Programm und Satz in der README des Ordners. Gruppe E `trägt`, wenn E1 bis E3 erfüllt sind.

## Tor

### N1-Z: Tor

Teilurteile für #624: [D – musl](../specs/2026-11-24-ladybird-n1-musl.md)
und [E – Vergleichsprogramme](../specs/2026-11-24-ladybird-n1-vergleichsprogramme.md).

- **Branch:** `docs/ladybird-n1z-tor` · **Größe:** 1 · **Rolle:** Lead · **Voraussetzung:** N1-A7, N1-B5, N1-C2, N1-D2, N1-E2, N1-E3, #450, Stand von #423
- **Dateien:** `docs/LADYBIRD_FEASIBILITY.md` („Ergebnis N1“), Übersicht, `ROADMAP.md`; bei GO werden N2 und N3 in Pakete geschnitten.
- **Regel (fest):**

| Ergebnis | N2 und N3 | Ladybird (N6) |
| --- | --- | --- |
| A, B, C, E `trägt`; D `trägt` oder `glibc`; PSS ≤ 2 GiB; #423 abgenommen | GO | bleibt geplant |
| wie oben, aber C `trägt nicht` oder PSS > 2 GiB | Peter kann GO geben, das Ziel Paketdienst hängt nicht an Ladybird | NO-GO |
| A oder B `trägt nicht` | NO-GO | NO-GO |
| A, B oder E `offen`, oder #423 nicht abgenommen | warten, mit dem fehlenden Punkt | warten |
| C oder D `offen`, sonst wie Zeile 1 | Peter kann GO geben | warten |

  Der Wasm-Weg lebt durch ein NO-GO nicht von selbst wieder auf. Die Entscheidung trifft Peter oder Lars.

## Reihenfolge

| Wann | Pakete (nebeneinander) | Hinweis |
| --- | --- | --- |
| jetzt | N1-A5 (#628), N1-B1 (#620), N1-C1 (#621), N1-E1 (#623), P1 (#639) | #449 läuft daneben; #448 nach Rechner |
| nach N1-E1 | N1-E2 (#637), N1-E3 (#638) |  |
| nach P1 | P2 (#640), P3 (#641) | P4 nach P2 |
| nach P2 | P4 (#642) |  |
| nach #448 | N1-D1 (#622) | allein auf dem Ladybird-Rechner |
| nach #448, #450, N1-C1 | N1-C2 (#635) | derselbe Rechner |
| nach N1-D1 | N1-D2 (#636) |  |
| nach #523 | N1-A1 (#619), N1-B2 (#631) | ohne gemeinsame Dateien |
| nach #523 und R8 (#423) | N1-A2 (#625) | neben A1 und B2; R8 ändert Tick und Rahmenzugriff |
| nach N1-A2 | N1-A3 (#626) | `Interrupts.s` |
| nach N1-A1, N1-A3 | N1-A4 (#627) |  |
| nach N1-A4, N1-A5 | N1-A6 (#629) |  |
| nach N1-A2 | N1-B3a (#698) | APIC und EfiRtc über HHDM; nachträglich aus N1-B3 gelöst |
| nach N1-A4, N1-B2, N1-B3a | N1-B3 (#632) | neben N1-A6 |
| nach N1-A6 | N1-A7 (#630) |  |
| nach N1-A6, N1-B1, N1-B3 | N1-B4 (#633) | neben N1-A7 |
| nach N1-B4 | N1-B5 (#634) | Zeit je Adressraumwechsel erst nach #716 messbar |
| nach allem und #423 | N1-Z (#624) | Entscheidung Peter oder Lars |

Die Vorarbeiten P1 bis P4 stehen in [Vorarbeit Paketdienst](2026-11-24-ladybird-p-paketdienst-vorarbeit.md).

**Was nacheinander laufen muss:** A2 → A3 → A4 → A6 → B4 → B5 ist der kritische Weg (2+2+3+3+3+2 = 15 Arbeitstage); A7 läuft neben B4. R8 (#423) ändert `OnTimerInterrupt` und den Zugriff auf den gesicherten Rahmen; A2, A3, A4 und B3 bauen deshalb auf dem Cosmos-Stand nach R8 auf. `X64InterruptController.cs` und `LocalApic.cs` ändert in N1 nur A2 (A4 nur bei Bedarf danach). `Interrupts.s` ändern nur A2 und A3, `SchedulerManager.cs` und `SchedulerThread.cs` nur A4 und danach B3, `probe.S` und `CosmosUserModeProbe.cs` nur A5/A6 und danach B4, `COMMIT` und `VERSION` nur A6 und danach B4. Sonst überschneiden sich keine Dateien. Voll parallel besetzt dauert N1 nach #523 und R8 also mindestens 15 Arbeitstage plus Reviews; mehr Leute verkürzen den kritischen Weg nicht.
