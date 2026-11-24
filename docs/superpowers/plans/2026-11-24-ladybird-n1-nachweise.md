# Ladybird N1: Nachweise

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-24-ladybird-nativ.md) und den [Entwurf](../specs/2026-11-24-native-pakete-design.md), Abschnitte 3 bis 6, lesen. Arbeitsweise: superpowers:executing-plans; Schritte haben Kästchen.

**Ziel:** Vor dem eigentlichen Bau klären, ob Ring 3 und eigene Seitentabellen unter Cosmos/NativeAOT tragen, welche Systemaufrufe Ladybird wirklich nutzt und ob es mit musl baut.

**Urteil je Paket:** `trägt` / `trägt nicht` / `offen`, mit Befehl und Messwert. Bericht: `docs/superpowers/specs/2026-11-24-ladybird-n1-<thema>.md`. Bei `trägt nicht`: genaue Stelle und Grund. Probecode im Kernel liegt hinter einem Bauschalter; was bleibt, wird in N2 neu bewertet. Code-PRs brauchen ein unabhängiges Review.

## Pakete

### N1-A: Ring 3 unter Cosmos

- **Branch:** `cosmos-patch/user-mode-probe` (Cosmos; PR als Entwurf, **nicht mergen**) und `task/ladybird-n1a-ring3` · **Rolle:** Senior (Opus) · **Voraussetzung:** #523 auf `master`
- **Frage:** Kann ein Thread im Ring 3 laufen, unterbrochen werden, Systemaufrufe machen und fehlerhaft enden, ohne dass Kernel, GC oder andere CPUs leiden?
- **Cosmos-Dateien:** anlegen `src/Cosmos.Kernel.Native.X64/CPU/UserMode.s`, `src/Cosmos.Kernel.Core.X64/Cpu/Gdt.cs`, `src/Cosmos.Kernel.Core/CPU/UserMode.cs`; ändern `Interrupts.s` (`IRQ_STUB`), `Smp.s`, `ThreadContext.X64.cs`, `PerCpuBlock.cs`, `Idt.cs`, `SchedulerManager.cs`. Jede CPU lädt eigene GDT und eigenes TSS.
- **GDT (fest):** Limines Kernelselektoren bleiben (`0x28` Code, `0x30` Daten); dazu `0x38` reserviert, `0x40` Programmdaten, `0x48` Programmcode, `0x50` TSS (16 Byte). `STAR[47:32]=0x28`, `STAR[63:48]=0x38`. `SFMASK` löscht IF, DF, TF, AC, NT. `EFER.SCE` und `LSTAR` je CPU. IST 1–3 für #DF, NMI, #MC. IOPB-Offset hinter dem TSS-Limit (alle Ports gesperrt). SMEP und UMIP an, wenn CPUID sie meldet. Neue Programmthreads starten mit FCW `0x37F` und MXCSR `0x1F80`.
- **Schnittstelle (experimentell, `COSMOS0001`):**

```csharp
namespace Cosmos.Kernel.Core.CPU;

public struct UserFrame   // gemeinsamer Rahmen von Stub und syscall-Eintritt: wie ThreadContext ohne FPU-Bereich
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
    public static void LeaveCurrent(ulong exitValue); // nur im Syscall- oder Fehlerhandler; merkt vor, Enter kehrt nach dem Handler zurück
    public static void SetFsBase(ulong address);     // je Thread gesichert und wiederhergestellt
    // Von jedem Thread: Enter des Zielthreads kehrt mit exitValue zurück, spätestens beim nächsten Interrupt im Ring 3.
    public static void RequestLeave(SchedulerThread thread, ulong exitValue);
}
```

  `LeaveCurrent` merkt nur vor: Der Handler läuft zu Ende und gibt seine Sperren normal frei, erst der Austrittspfad verwirft den Programmrahmen und lässt `Enter` zurückkehren. `RequestLeave` setzt einen Merker je Thread; der Austrittspfad nach Ring 3 prüft ihn. In N1-A gibt es noch keinen eigenen CR3: Probecode und Probestack liegen in einem sonst leeren PML4-Eintrag der unteren Hälfte, nur im Probebau. Die Kernelhälfte erhält nie ein User-Bit.
- **ZehOS-Dateien (anlegen):** `ProjektZeh/Runtime/CosmosUserModeProbe.cs`, `ProjektZeh/Native/UserProbe/probe.S`, `scripts/test-user-mode-probe.py`. Schalter `-p:ZehOSUserModeProbe=true`, nie im Normalbau.
- **Schritte:**
  - [ ] GDT, TSS, IST auf jeder CPU; ohne Ring 3 bestehen die Cosmos-Testkernel `Interrupts`, `Smp`, `Threading`, `GarbageCollector`.
  - [ ] Stub: Herkunft über `CS.RPL`, `swapgs`, `fxsave64`/`fxrstor64` statt der XMM-Einzelsicherung; NMI und #MC prüfen `IA32_GS_BASE`. Bei Vektoren mit Fehlercode schreibt der Stub heute als Erstes nach `gs:[…]`; die Herkunft ist deshalb **vor** diesem Zugriff aus `[rsp+16]` (CS über Fehlercode und RIP) zu prüfen. Beim Threadwechsel `TSS.RSP0` und FS-Basis setzen; im Bericht belegen, dass Kernelcode FS nicht nutzt (Hinweis in `CpuOps.s`), sonst FS bei jedem Eintritt sichern. `syscall`-Eintritt wechselt den Stack über `PerCpuBlock`; vor **jedem** Rückweg nach Ring 3 (`sysretq` und `iretq`) RIP und RSP auf die untere kanonische Hälfte prüfen und RFLAGS auf erlaubte Bits beschränken (IF=1, IOPL=0, NT=0); sonst kehrt `Enter` mit Fehler zurück, weil auch `iretq` dann im Ring 0 scheitert.
  - [ ] Probeprogramm ruft `0x7A01` (zählen) und `0x7A02` (Ende) auf. Seriallog je Fall `USER_PROBE <name> ok=<0|1> …`, am Ende `USER_PROBE done failed=<n>`; das Skript wertet nur diese Zeilen und endet bei fehlender Schlusszeile mit Exit 1.
  - [ ] Fälle: `loop` 1 000 000 Aufrufe; `spin` 10 s ohne Aufruf, andere Threads laufen weiter; `fpu` zwei Programmthreads mit verschiedenen x87-, MXCSR- und XMM-Werten bleiben 10 s unverändert; `gc` zweiter Thread ruft 1000-mal `GC.Collect()`; `fault` #DE, #UD, #GP (`cli`, `hlt`, `in`) und #PF auf Kerneladressen (lesen, schreiben, ausführen) beenden je nur den Probelauf; `badret` Handler setzt eine nicht kanonische und eine Kernel-Rücksprungadresse; `killspin` `RequestLeave` auf eine Rechenschleife, 1000-mal, bei 4 CPUs von einer anderen CPU; `nmi` NMI während `loop`; `leave` 1000-mal `Enter` und `LeaveCurrent`; `gegenprobe` #PF im Kernelthread hält weiter an (eigener Lauf).
  - [ ] Messen: Systemaufruf hin und zurück (`rdtsc` im Ring 3 um jeden von 100 000 Aufrufen, Median, KVM); Doom-Bildrate mit `scripts/test-wasm-doom-desktop.py --scenario full` unter der Standardlaufzeit, 5 Läufe je Stand, Median, sowie `scripts/test-power-commands.ps1` mit gepatchtem Cosmos im Normalbau, jeweils gegen denselben Stand ohne Patch.
- **Abnahme `trägt`:** alle Fälle in den festen Läufen; Systemaufruf im Median ≤ 5 µs (KVM); Doom-Bildrate höchstens 5 % unter vorher; freier Speicher nach `leave` ±1 MiB.
- **`trägt nicht`:** Ein Fall scheitert bis zum Zeitlimit mit bekannter Ursache in Cosmos oder NativeAOT, die keinen abgegrenzten Patch erlaubt.

### N1-B: Eigene Adressräume

- **Branch:** `task/ladybird-n1b-adressraum`, Cosmos auf dem Branch von N1-A · **Rolle:** Senior (Opus) · **Voraussetzung:** N1-A `trägt`; der Hostteil (`AddressSpaceMap`) sofort als eigener PR `task/ladybird-n1b-hostteil`
- **Cosmos-Dateien:** anlegen `src/Cosmos.Kernel.Core.X64/Cpu/PageTables.cs`, `src/Cosmos.Kernel.Core/Memory/UserPageTable.cs`; ändern `DeviceMapper.cs`, `SchedulerThread.cs`, `SchedulerManager.cs` (CR3 beim Wechsel, nur wenn verschieden).
- **Schnittstelle:**

```csharp
namespace Cosmos.Kernel.Core.Memory;

public sealed class UserPageTable : IDisposable      // Dispose: erst wenn kein Thread mehr gebunden ist; gibt Tabellen frei, nicht die Seiten
{
    public static bool ShareKernelHalf();            // einmal: alle 256 oberen PML4-Einträge anlegen
    public static UserPageTable? TryCreate();        // untere Hälfte leer, obere gemeinsam
    public bool TryMap(ulong user, ulong physical, bool writable, bool executable); // 4 KiB; beides zugleich → false
    public bool TryProtect(ulong user, bool writable, bool executable);
    public ulong Unmap(ulong user);                  // physische Adresse oder 0; Seite gehört danach dem Aufrufer
    public bool TryTranslate(ulong user, bool write, out ulong physical);
    public void BindToCurrentThread();
    public static void UnbindCurrentThread();        // zurück auf die Kerneltabellen
    public static ulong AllocateFrame();             // genullt, physische Adresse; 0 bei Mangel
    public static void FreeFrame(ulong physical);
}
```

- **ZehOS-Dateien (anlegen):** `ProjektZeh/Processes/AddressSpaceMap.cs` (plattformunabhängig), `ProjektZeh.Tests/Processes/AddressSpaceMapTests.cs`, `ProjektZeh/Runtime/CosmosAddressSpaceProbe.cs`; `ProjektZeh.Tests.csproj` und `scripts/test-user-mode-probe.py` ergänzen.

```csharp
namespace ProjektZeh.Processes;

internal enum MapAccess { None, Read, ReadWrite, ReadExecute }
internal enum Placement { Hint, FixedReplace, FixedNoReplace }   // wie mmap ohne Flag, MAP_FIXED, MAP_FIXED_NOREPLACE
internal sealed class AddressSpaceMap(ulong start, ulong end)   // seitenausgerichtet, [start, end)
{
    public ResultObject<ulong> Reserve(ulong hint, ulong length, MapAccess access, Placement placement);
    public ResultErr Release(ulong address, ulong length);       // teilt Bereiche; Lücken sind erlaubt
    public ResultErr Protect(ulong address, ulong length, MapAccess access); // Lücke → Fehler, nichts geändert
    public bool TryFind(ulong address, out MapAccess access);
}
```

- **Schritte:**
  - [ ] Hosttests zuerst: überlappende Reservierung mit `FixedReplace` (ersetzt, wie Linux) und `FixedNoReplace` (Fehler, nichts geändert), Teilen am Anfang, in der Mitte und am Ende, Überlauf `address+length`, nicht ausgerichtete Werte, Länge 0, nächster freier Platz, 32 GiB und 4 TiB + 32 GiB gleichzeitig.
  - [ ] Cosmos-Teil und Seitenbelegung im `FaultHandler`.
  - [ ] Fälle: `isolate` zwei Programme an derselben Adresse mit verschiedenem Inhalt, 10 s im Wechsel, auf derselben und auf verschiedenen CPUs; `kernelread` Kernelhälfte und alte Identitätsabbildung sind nicht lesbar; `sparse` 32 GiB + 4 TiB reserviert, 1000 verstreute Seiten benutzt; `churn` 1000 Adressräume anlegen und freigeben; `copy` Kopierweg über HHDM mit 10 000 zufälligen Adressen und Längen (feste Saat) ohne Kernelfehler; `device` Netz, AHCI und Framebuffer, während ein Programm läuft; `nx` Ausführen einer Datenseite scheitert; `edge` `syscall` als letzte Anweisung vor `0x7FFF_FFFF_F000` (oberste Seite bleibt gesperrt); `gc` wie N1-A mit eigenem CR3.
  - [ ] Zeit je Adressraumwechsel und je Seitenfehler (KVM, Median).
- **Abnahme `trägt`:** Hosttests grün; alle Fälle in den festen Läufen; `churn` ±1 MiB; Desktop, ClassiCube und Netz im Normalbau mit demselben Cosmos unverändert.
- **`trägt nicht`:** wie N1-A.

### N1-C: Aufrufliste von Ladybird

- **Branch:** `task/ladybird-n1c-aufrufe` · **Rolle:** Engineer (Linux) · **Voraussetzung:** #448 und #450, derselbe Rechner
- **Dateien (anlegen):** `experiments/Ladybird.Probe/syscalls.py`, `experiments/Ladybird.Probe/test_syscalls.py`, Bericht `…-n1-aufrufe.md`
- **Schnittstelle:** `syscalls.py --trace DIR --out FILE.json` wertet Dateien aus `strace -ff -f -yy -v -o DIR/t` aus: `{"processes": [{"name": "...", "syscalls": {"<name>": {"count": n, "errors": {"ENOSYS": n}, "flags": ["..."]}}}], "paths": ["..."], "sockets": ["..."]}`.
- **Schritte:**
  - [ ] Tests mit festen Beispielzeilen: `openat`, `clone3`, `futex`, `<unfinished ...>` mit `resumed`, Fehlerwert, Signalzeile.
  - [ ] Die drei L1-Seiten headless unter `strace`, dazu `js` allein mit `1+1`.
  - [ ] Bericht: Tabelle Aufruf × Prozess × Satz; alles, was in S1–S3 fehlt, als S4; gelesene Pfade unter `/proc`, `/sys`, `/etc`; Threads und größter reservierter Adressraum je Prozess.
  - [ ] Für jeden S4-Aufruf `strace -f -e inject=<name>:error=ENOSYS` auf `static.html`: Pflicht oder verzichtbar.
- **Abnahme:** Tests grün; jeder beobachtete Aufruf ist genau einem Satz zugeordnet. `trägt`, wenn höchstens 40 Aufrufe S4-Pflicht sind und darunter keiner von `io_uring_*`, `bpf`, `userfaultfd`, `ptrace` und kein GPU-Gerät ist; sonst `trägt nicht`.

### N1-D: Ladybird mit musl

- **Branch:** `task/ladybird-n1d-musl` · **Rolle:** Engineer (Linux) · **Voraussetzung:** #448 (glibc-Bau als Vergleich)
- **Dateien (anlegen):** `experiments/Ladybird.Probe/musl/{build.sh,patches/,README.md}`, Bericht `…-n1-musl.md`
- **Probe:** derselbe Ladybird-Stand in einem Alpine-Wurzelverzeichnis (WSL oder Container) mit denselben Bauoptionen; zuerst `js`, dann `WebContent`, dann alles. Geschrieben wird nur unter `~/ladybird-probe/`. Zeitlimit 5 Arbeitstage Bearbeitung (Bauzeit zählt nicht); nach `js` und nach `WebContent` je ein Zwischenurteil.
- **Abnahme:** je Ziel Urteil, Patchzeilen (Ladybird und vcpkg-Ports getrennt), Größen, `js` mit `1+1` und Array-Sort (10 000), `static.html` headless. `trägt`, wenn alles baut, läuft und höchstens 1000 Patchzeilen nötig sind. Sonst `glibc` mit der ersten nicht lösbaren Stelle; scheitert auch #448, `offen`.

### N1-E: Vergleichsprogramme

- **Branch:** `task/ladybird-n1e-vergleich` · **Rolle:** Engineer · **Voraussetzung:** keine
- **Dateien (anlegen):** `experiments/LinuxAbi/{build.sh,README.md}`, `experiments/LinuxAbi/expected/`, `experiments/LinuxAbi/src/*.c`, `experiments/LinuxAbi/rust/`, `experiments/LinuxAbi/run-linux.py`, `experiments/LinuxAbi/test_run_linux.py`
- **Inhalt:** statische `x86_64-linux-musl`-Programme, eines je Verhalten, eigene Quellen. S1: `args_env_auxv`, `files`, `dirs`, `mmap_anon`, `mprotect_fault`, `brk`, `clock`, `random`, `long_double`, `tls`, `badptr` (`EFAULT`), `unknown_syscall` (`ENOSYS`). S3: `threads`, `futex`, `fork_cow`, `exec`, `pipe_epoll`, `unix_fdpass`, `memfd_seal`, `tcp_echo`, `sigsegv_status`. Dazu toybox (0BSD): `cat`, `ls`, `wc` in S1, `sh` in S3; Rust mit `std`: Datei und Zeit in S1, Thread in S3.
- **Schnittstelle:** Jedes Programm schreibt nur deterministischen Text. `expected/<name>.txt` enthält die Ausgabe, `expected/<name>.status` `exit <n>` oder `signal <n>`. `run-linux.py --bin DIR --expected DIR [--update] --json FILE` vergleicht beides und notiert je Programm die Aufrufe aus `strace -f -c`; Exit 1 bei Abweichung. `expected/` entsteht nur auf Linux mit `--update`; die Kernelversion steht in der README.
- **Abnahme:** Tests des Vergleichers grün (gleich, abweichende Ausgabe, falscher Status, fehlende Erwartung); alle Programme auf Linux zweimal gleich; die Aufrufe der S1-Programme liegen vollständig in S1, sonst wird S1 im Entwurf ergänzt. Binärdateien werden nicht committed. `trägt`, wenn alles davon erfüllt ist.

### N1-Z: Tor

- **Branch:** `docs/ladybird-n1z-tor` · **Rolle:** Lead · **Voraussetzung:** N1-A bis N1-E, #450, Stand von #423
- **Dateien:** `docs/LADYBIRD_FEASIBILITY.md` („Ergebnis N1“), Übersicht, `ROADMAP.md`; bei GO werden N2 und N3 in Pakete geschnitten.
- **Regel (fest):**

| Ergebnis | N2 und N3 | Ladybird (N6) |
| --- | --- | --- |
| N1-A, N1-B, N1-C, N1-E `trägt`; N1-D `trägt` oder `glibc`; PSS ≤ 2 GiB; #423 abgenommen | GO | bleibt geplant |
| wie oben, aber N1-C `trägt nicht` oder PSS > 2 GiB | Peter kann GO geben, das Ziel Paketdienst hängt nicht an Ladybird | NO-GO |
| N1-A oder N1-B `trägt nicht` | NO-GO | NO-GO |
| N1-A, N1-B oder N1-E `offen`, oder #423 nicht abgenommen | warten, mit dem fehlenden Punkt | warten |
| N1-C oder N1-D `offen`, sonst wie Zeile 1 | Peter kann GO geben | warten |

  Der Wasm-Weg lebt durch ein NO-GO nicht von selbst wieder auf. Die Entscheidung trifft Peter oder Lars.

## Nebeneinander

| Paket | wartet auf | läuft neben | Rechner |
| --- | --- | --- | --- |
| N1-A (#619) | #523 | C, D, E | QEMU, KVM |
| N1-B (#620) | N1-A; Hostteil sofort | C, D, E | QEMU, KVM |
| N1-C (#621) | #448, #450 | A, B, D, E | Linux ≥ 32 GiB RAM |
| N1-D (#622) | #448 | A, B, E; nicht neben anderen Ladybird-Bauten | Linux ≥ 32 GiB RAM |
| N1-E (#623) | – | alle | Linux |
| N1-Z (#624) | alle | – | – |

Plan freigegeben (Peter, 24. November 2026). Ohne weitere Entscheidung startbar: N1-E und der Hostteil von N1-B.
