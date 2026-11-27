# Befund B1: Kernel-Halt in LinuxUnixSocket.Close (Link.Gate == null)

Die Logs sind gekürzt: Timer- und Scheduler-Zeilen sind entfernt, LINUXRUN OUT ist dekodiert. Es sind Gastadressen, keine Daten des Hosts.

| Datei | Stand | Lauf |
|---|---|---|
| `halt-c95ab059-maps-smp1.txt` | c95ab059 (Pin 9ff7e86) | 512M smp1, Szenario maps (vorher kheap, mapsfrei im selben Boot), Halt in Stufe 2 (Socketpaare bei vollem Speicher) |
| `halt-c95ab059-sockloop-smp4.txt` | c95ab059 | 512M smp4, sockloop, 2. Wiederholung, Runde 31 |

Häufigkeit auf c95ab059: 2 Halts in etwa 13 Bootvorgängen mit Socketlast bei vollem Speicher.

## Stand d1d06dc0 (V43+V45, Pin 053fb42): Gesamttabelle (512M, KVM, je Zeile ein Boot)
| Läufe | Szenario | Halts | Art |
|---|---|---|---|
| s smp1 ×4 | sockloop:150 | 4/4 | Kernel-OOM: unbehandelte OutOfMemoryException, FAILFAST, Runde 51 (Wdh. 1); davor `Mark stack overflow` |
| s smp4 ×3 | sockloop:150 | 3/3 | wie oben |
| s smp4-4 | sockloop:150 | 1/1 | **Nullzugriff CR2=0x0 in GCHandleManager.AllocateHandler**, Runde 45, vorher keine einzige Sammlung (siehe unten) |
| t smp1 ×5 / smp4 ×5 | sockloop:40 | 0/5 / **1/5** | t-smp4-2: **Gate-Halt** (Wdh. 2, Runde 30), 1 Sammlung vorher, keine Warnung |
| f smp1 ×2 / smp4 ×2 | kheap, mapsfrei, maps, sockloop:40 | 0/2 / **1/2** | f-smp4-1: **Gate-Halt** in sockloop (Wdh. 2, Runde 32), 5 Sammlungen vorher, keine Warnung. smp1: mapsfrei kam auf 10^6 Abbildungen, munmap hing (B3), sockloop lief nicht |
| p smp1 ×2 / smp4 ×2 | pipeloop:40 | 0/4 | Gegenprobe ohne Sockets: kein Halt |
| a smp1 ×2, **altes ISO c95ab059** | sockloop:150 | 2/2 | Kernel-OOM wie auf d1d06dc0: keine Regression durch V45 oder den Pin |

Weitere FAILs ohne Halt kommen nur von `aussen.speicher_gesamt` (Kernel-Heap gibt erst bei der nächsten Sammlung zurück).

**Gate-Halt insgesamt: 4** (c95ab059: 2, d1d06dc0: 2, alle mit Socketlast bei vollem Speicher, keiner mit GC-Warnung davor). Die d1d06dc0-Halts fielen beide auf smp4.
Gegen das d1d06dc0-Abbild symbolisiert (nm, dieser Bau): derselbe Pfad wie auf c95ab059. Monitor.Enter (ip 0xFFFFFFFF803CA793, RDI=0, CR2=0x14) ← LinuxUnixSocket.Close
(0xFFFFFFFF80359E86) ← LinuxSyscalls.ReleaseIfUnused ← DispatchFileGated ← CosmosLinuxRun.OnSyscall. Die Disassemblierung vor der Rücksprungadresse zeigt
`mov 0x8(%r15),%rdi`: Es ist `Link.Gate`, wie auf c95ab059.

| Datei | Lauf |
|---|---|
| `halt-d1d06dc0-sockloop-smp1-oom.txt` | s-smp1-1, Kernel-OOM (stellvertretend für die 9 OOM-Halts) |
| `halt-d1d06dc0-sockloop150-smp4-gchandle.txt` | s-smp4-4, Nullzugriff in GCHandleManager |
| `halt-d1d06dc0-sockloop40-smp4.txt` | t-smp4-2, Gate-Halt |
| `halt-d1d06dc0-maps-folge-smp4.txt` | f-smp4-1, Gate-Halt |

### s-smp4-4: Nullzugriff statt OOM-Ausnahme
Stapel: GCHandleManager.AllocateHandler (ip 0xFFFFFFFF80239F72, CR2=0x0) ← GarbageCollector.AllocateHandler ← RuntimeImports.RhHandleAllocDependent ←
ConditionalWeakTable.Container.CreateEntryNoResize ← ConditionalWeakTable.GetOrAdd ← Monitor.PulseAll ← LinuxUnixSocket.OnLastClose ← LinuxUnixSocket.Close ←
ReleaseIfUnused. Quelltext Pin 053fb42: `GCHandleSegmentStore.AllocateHandle` (GCHandleManager.cs:68) liefert null, wenn kein neues Segment kommt.
`AllocateHandler` schreibt dann ohne Prüfung `handle->Object = obj` (GCHandleManager.cs:280). Erschöpfter Speicher wird so zum Kernel-Halt statt zu einer
OutOfMemoryException. Nebenbei sichtbar: Monitor legt für jedes gesperrte Objekt (etwa `Link.Gate`) einen Eintrag in einer ConditionalWeakTable mit
abhängigem GC-Handle an. Ob das mit Gate==null zusammenhängt, ist nicht geprüft (nur ein Hinweis).

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
