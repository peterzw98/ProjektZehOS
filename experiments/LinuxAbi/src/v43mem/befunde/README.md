# Befund B1: Kernel-Halt in LinuxUnixSocket.Close (Link.Gate == null)

Die Logs sind gekürzt: Timer- und Scheduler-Zeilen sind entfernt, LINUXRUN OUT ist dekodiert. Es sind Gastadressen, keine Daten des Hosts.

| Datei | Stand | Lauf |
|---|---|---|
| `halt-c95ab059-maps-smp1.txt` | c95ab059 (Pin 9ff7e86) | 512M smp1, Szenario maps (vorher kheap, mapsfrei im selben Boot), Halt in Stufe 2 (Socketpaare bei vollem Speicher) |
| `halt-c95ab059-sockloop-smp4.txt` | c95ab059 | 512M smp4, sockloop, 2. Wiederholung, Runde 31 |

Häufigkeit auf c95ab059: 2 Halts in etwa 13 Bootvorgängen mit Socketlast bei vollem Speicher.

## Stand d1d06dc0 (V43+V45, Pin 053fb42), Läufe laufen noch
| Datei | Lauf | Art |
|---|---|---|
| `halt-d1d06dc0-sockloop-smp1-oom.txt` | 512M smp1, sockloop:150, 1. Wiederholung, Runde 51 | **anderer Halt:** unbehandelte OutOfMemoryException, `[FAILFAST] Unhandled exception`, "System halting" |

Vor diesem Halt meldet die GC `Mark stack overflow` und `finalizer queue cannot grow`. Danach liefen zwei weitere Sammlungen, die kaum etwas freigaben (17 Objekte).
Der Mark-Stapel wird ohne Reserve geholt (`PageAllocator.AllocPages` ohne `aKeepFree`). Scheitert das, war auch die Kernel-Reserve aufgebraucht. Der Kernel-Heap hat
also mehr als die 64 MiB gebraucht: Das ist der offene V43-Restpunkt "Kernel-Heap über der Reserve". Der gemeldete Ausnahmeort 0xFFFFFFFF803FBA2B liegt in
`FrozenObjectHeapManager.FrozenObjectSegment..ctor` (nm dieses Baus). Welcher Kernelthread die Ausnahme ohne Fänger warf, ist nicht ermittelt.

## Stapel (beide Halts identisch; Symbole mit `nm -n` aus dem ELF des Baus, `ProjektZeh.elf` im ISO unter `boot/`)
```
#PF CR2=0x14, RDI=0   ip 0xFFFFFFFF803CA733  System.Threading.Monitor.Enter      (lock auf null)
[0] 0xFFFFFFFF80359E26  LinuxUnixSocket.Close          LinuxUnixSocket.cs:474  lock (l.Gate)
[1] 0xFFFFFFFF8034143C  LinuxSyscalls.ReleaseIfUnused
[2] 0xFFFFFFFF803469BE  LinuxSyscalls.DispatchFileGated (close)
[3] 0xFFFFFFFF8030DC4B  CosmosLinuxRun.OnSyscall
[4] 0xFFFFFFFF8023EF45  __managed__user_syscall
[5] 0xFFFFFFFF800241A2  _native_x64_user_syscall
```
Die Disassemblierung von Close zeigt: `_link` (Feld 0x10) ist nicht null, `Link.Gate` (Feld 0x08) ist null. `Gate` ist eine Get-only-Eigenschaft, die der Konstruktor
setzt (LinuxUnixSocket.cs:704), und eine fehlgeschlagene Allokation wirft. Null kann deshalb nur von einem beschädigten Kernel-Heap kommen. Im smp4-Lauf lief etwa 19 Runden vor
dem Halt die erste GC-Sammlung. Die Adressen gelten nur für den jeweiligen Bau.

## Nachfahren
```sh
CC=musl-gcc sh experiments/LinuxAbi/build.sh v43mem
python3 scripts/test-linux-mem.py <iso -p:ZehOSLinuxRun=true> <neuer-ordner> 4 512M sockloop:150
```

## GC-Warnungen und Pinstände (Quelltext)
- Beide Warnungen gibt es schon im Pin 9ff7e86 (c95ab059): `[GC] WARNING: Mark stack overflow` (GarbageCollector.Mark.cs:551, wenn
  `PageAllocator.AllocPages` für einen größeren Mark-Stapel null liefert) und `finalizer queue cannot grow` (GarbageCollector.Finalizer.cs:731).
- Zwischen 9ff7e86 und 053fb42 ändert sich unter `src/Cosmos.Kernel.Core/Memory` und `src/Cosmos.Kernel.Core/Runtime` nichts (`git diff --stat` leer);
  053fb42 bringt nur sigreturn-iretq (V45) und die Patchdatei zu V43. Der GC ist also gleich.
- In den vollständigen seriellen Logs der beiden Gate-Halts (c95ab059) steht **keine** der beiden Warnungen. Die Warnungen erschienen auf c95ab059 in vier anderen Läufen
  (maps, kheap), die nicht anhielten. Ein Mark-Stapel-Überlauf ist für die Gate-Halts also nicht belegt. Verlorene Marken bleiben möglich (die Überlaufbehandlung
  `ProcessMarkOverflow` ist nicht geprüft), werden aber nicht gemeldet.
- Auf d1d06dc0 hält sockloop:150 bei smp1 4 von 4 Läufen, immer in Runde 51 der ersten Wiederholung. Die Folge ist immer gleich: erste Sammlung erst bei erschöpftem Heap,
  dann Mark-Stapel-Überlauf, zwei Sammlungen ohne Ertrag, unbehandelte OutOfMemoryException, FAILFAST. Der Kernel-Heap sammelt also erst, wenn auch die Reserve
  verbraucht ist (Auslöser ist die Zahl der Refills, nicht der freie Speicher), und hält dann an, bevor der Gate-Pfad erreicht wird.
