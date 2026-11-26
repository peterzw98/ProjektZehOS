# Ladybird N1-B5: Messung Adressräume (Entwurf, noch ohne Messwerte)

Paket N1-B5 (#634), Plan `2026-11-24-ladybird-n1-nachweise.md`. Messung von `agent-peter-engineer`, Urteil von einem Senior (Abschnitt „Urteil“ am Ende, noch leer). Dieser Branch baut auf #712 (N1-B4, Kopf `d1f3fc7c1`) auf, das auf #710 (user-cr3, Pin `7b4b1018`) beruht. Entwurf, nicht mergen.

## Stand der Voraussetzungen (aus #712 und #710 gelesen, nicht neu gelaufen)

- B1 (AddressSpaceMap): Hostteil, Branch `task/ladybird-n1b1-adressraum`; Abnahme dort, hier nicht nachgeprüft.
- B3 (#710): `Tests.UserPaging` KVM 7 Läufe, TCG 2 Läufe je 43/0/5 von 48; 6 Gegenproben rot; ein Fenster (Warten auf `Changing`) nur durch Lesereview gestützt.
- B4 (#712): KVM smp1 und smp4 je 10/10 (27 Fälle), TCG `-cpu max` smp4 3/3, **TCG smp1 1/3 vor dem Device-Fix; mit `d1f3fc7c1` 3/3** (#712 Kommentar 7182091025). B4-Abnahme „3 TCG, 1 und 4 CPUs“ ist damit nach dem Fix laut #712 erfüllt (TCG nicht hier neu gelaufen).
- `gc2` smp4: `overlapped=0` (nicht Pflicht, benannt).

## Lücke: #712 misst keine Zeit

Die Fälle von #712 (`isolate`, `kernelread`, `nx`, `edge`, `sparse`, `copy`, `churn`, `gc2`, `device`) enthalten keine Zeitmessung. Die Messung nach Plan („Zeit je Adressraumwechsel und je Seitenfehler“) braucht zwei neue Fälle in `CosmosAddressSpaceProbe.cs` und `probe.S`: Dafür gilt dieselbe Methode wie in A7 (`sysmed`).

## Messplan

1. **Adressraumwechsel (`aswitch`):** zwei Threads mit eigenem CR3, Wechsel erzwungen durch `Yield`; Zeit je Wechsel = (Zeit mit verschiedenem CR3 − Zeit mit gleichem CR3) je Hin und Zurück, `rdtsc` um 100 000 Wechsel, Median über Läufe; Gegenwert „gleicher CR3“ als Eigenkosten. KVM, 1 CPU (smp4 zusätzlich), 3 Läufe.
2. **Seitenfehler (`pfault`):** Programm berührt 10 000 verstreute Seiten (nicht belegt), `FaultHandler` belegt sie; Zeit je Fehler = `rdtsc`-Spanne im Ring 3 um den Zugriff, Differenzen nach dem Messfenster an den Kernel (wie `sysmed`), Median und p99, Leerlauf (Zugriff auf belegte Seite) als Abzug.
3. **Normalbau unverändert:** Desktop (`scripts/test-desktop*.py`, 1 und 4 CPUs), ClassiCube, Netz (Ping/DHCP-Skript) im Normalbau (ohne `-p:ZehOSUserModeProbe`) mit Cosmos-Pin `7b4b1018` gegen `origin/master` mit smp.13, je 3 Läufe KVM. #710 meldet Desktop 1 und 4 CPUs bereits bestanden (Probebranch `task/pin-user-cr3-probe`).
4. Zu klären vor dem Lauf: ClassiCube- und Netzskript (Namen im Repository), ob ClassiCube in der Matrix von engineer2 vorkommt.

## Regel (aus #634)

`trägt`: B1 und B4 erfüllt, Normalbau unverändert. `trägt nicht`: Fall scheitert an bekannter Ursache in Cosmos/NativeAOT ohne abgegrenzten Patch. Sonst `offen`. Einschätzung vor der Messung (TCG smp1 1/3) ist durch den Device-Fix überholt; das Urteil schreibt der Senior.

## Messwerte

Rechner MSI (KVM), Bau `task/ladybird-n1b5-zeitfaelle` @`12904e126` (auf #712 `d1f3fc7c1`, Cosmos `3.0.97-zehos.0.g7b4b101`), `-warnaserror` 0 Warnungen/0 Fehler für Probe- und Normalbau. Je ein Gast gleichzeitig, Rohdaten `~/zehos-b4-out/n1b5-*` auf MSI. Einheit: TSC-Ticks (`rdtsc`); Wechsel/Seitenfehler sind Mediane je Lauf.

**B4 (#712-Fälle + Zeitfälle), `test-user-mode-probe.py --accel kvm --runs 3`:** smp1 3/3 PASS, smp4 3/3 PASS (isolate, kernelread, nx, edge, sparse, copy, churn, gc2, device, aswitch, pfault, `failed=0`). TCG smp1 3/3 steht in #712 Kommentar 7182091025 (nicht neu gelaufen).

**aswitch** (Wechsel inkl. Yield; 20 000 Runden; Gegenstelle nur Yield, gleiche Arbeit in beiden Armen): Median alone (ein Adressraum) / pair (zwei Adressräume), Differenz `net_med`:

| smp | Lauf | alone | pair | net_med | kernel_pair (Kernelthreads, ohne Ring 3) |
| --- | --- | --- | --- | --- | --- |
| 1 | 1 | 436 | 436 | 0 | 54 |
| 1 | 2 | 426 | 426 | 0 | 54 |
| 1 | 3 | 426 | 426 | 0 | 54 |
| 4 | 1 | 426 | 426 | 0 | 58 |
| 4 | 2 | 426 | 424 | -2 | 56 |
| 4 | 3 | 426 | 428 | +2 | 56 |

Lesart: Der zusätzliche Aufwand des CR3-Wechsels liegt unter der Auflösung der Messung (|net_med| <= 2 Ticks von ~426); eine Yield-Runde mit Ring-3-Rückkehr kostet ~426 Ticks, ein reiner Kernelthread-Wechsel ~54-58. Kein reines CR3-Maß. **Grenze smp4:** Dauer `ms` ~60 statt 12-41 s bei smp1; mit mehreren CPUs ist nicht belegt, dass beide Programme auf derselben CPU wechseln (Yield wechselt ggf. nicht). Aussagekräftig für den Wechsel ist smp1.

**pfault** (4000 verstreute Seiten, `FaultHandler` belegt): Median erster Zugriff 2118/2054/2056 (smp1) und 2044/2042/2098 (smp4) Ticks, schon abgebildet 14; Abzug `net_med` je Fehler 2104/2040/2042 (smp1), 2030/2028/2084 (smp4). p99 erster Zugriff 2880-3406; max 0,37-0,41 Mio Ticks (Seitentabellenzuteilung/Ausreißer).

**Normalbau unverändert** (ohne Probe, gleicher Cosmos-Pin `7b4b1018`/`g7b4b101`, KVM):

| Prüfung | Ergebnis |
| --- | --- |
| `test-desktop-guest.py` | 3 Läufe PASS (Standard), 3/3 `ZEHOS_QEMU_SMP=1`, 3/3 `=4`; je ~37 s |
| `test-network.py` (Probe-ISO mit Fixture-CA, Netzprobe) | smp1 3/3, smp4 3/3 `NETWORK_PROBE PASS` |
| `test-classicube-desktop.py` (Normalbau mit Testschlüssel, Paket `~/zehos-676-pk/cc`) | smp1 1/1 PASS, smp4 1/1 PASS (freier Speicher ~440-447 MiB) |

## Nicht geprüft / Grenzen

- Kein Vergleich gegen `origin/master` (smp.13) im Normalbau gemessen; nur der Pin dieses Stands. Die Matrix von engineer2 (#708, 90/90) lief auf anderem Kopf.
- ClassiCube nur 1 Lauf je smp (nicht 3); Bildschirmfotos nicht visuell geprüft; Paket nicht mit `--world-seed 1` neu gebaut (Skript lief trotzdem durch).
- TCG, VMware, echte Hardware nicht gemessen. `gc2` smp4 `overlapped=0` unverändert (aus #712). P3 aus dem Review offen: `rdtsc` ohne `lfence` im Ring-3-Arm.
- aswitch smp4 siehe oben (kein belegter Same-CPU-Wechsel); Medianwerte ohne Streuungsangabe über Läufe außer der Tabelle.
- Ein Fenster in B3 (Warten auf `Changing`) bleibt nur durch Lesereview gestützt.

## Urteil

**`offen`** (agent-peter-senior, 26.11.2026; nur gelesen, kein Gastlauf; Cosmos-Quelle `7b4b1018`, Fälle `12904e126`).

**aswitch misst keinen Adressraumwechsel.** `Thread.Yield` wechselt in diesem Cosmos nicht: `RhYield` (`src/Cosmos.Kernel.Core/Runtime/Thread.cs:97-105`) ruft `SchedulerManager.YieldThread`, und das setzt nur `_needReschedule` (`SchedulerManager.cs:1702-1707`, „Returns at once: nothing switches outside an interrupt exit“). Verbraucht wird die Marke nur beim Austritt eines Interrupts (`ReschedulePendingFromIrq`, `SchedulerManager.cs:2530`, aufgerufen aus `X64InterruptController.cs:176` nach einer Hardware-IRQ, dazu der Timer-Tick). Der Syscall-Weg (`_native_x64_user_syscall`, `src/Cosmos.Kernel.Native.X64/CPU/UserMode.s:109-171`) kehrt nach `__managed__user_syscall` direkt mit `sysretq` zurück und prüft die Marke nicht. Eine Runde `SYS_YIELD` ist deshalb in beiden Armen dieselbe Arbeit (Syscall, Store, `sysretq`, zwei `rdtsc`); gewechselt wird nur beim nächsten Tick oder einer Geräte-IRQ, also in wenigen der 20 000 Runden, und diese landen oberhalb des Medians (p99/max). `net_med = 0` ist damit das erwartete Ergebnis ohne jeden CR3-Wechsel, kein Beleg für „CR3 unter der Auflösung“. Dasselbe gilt für die Gegenprobe `kernel_pair`: 54-58 Ticks sind ein Funktionsaufruf mit Store, kein Kontextwechsel (der allein speichert und lädt 512 Byte FPU-Zustand und nimmt die Queue-Sperre). Ein Zähler der CR3-Ladungen fehlt im Fall; er würde je Runde nahe 0 zeigen. Die Lesart im Abschnitt „aswitch“ und die Zeile „~54-58 je Kernelthread-Wechsel“ sind zu streichen.

**pfault trägt als Messung:** Der Store auf eine reservierte, nicht abgebildete Seite löst einen echten #PF aus, `mapped=4000` und `first == pages` belegen je Seite einen Fehler mit Belegung; ~2040-2100 Ticks je Fehler (KVM, Median, abzüglich 14 Ticks Leerlauf) sind plausibel. Kleinigkeit: Die Seiten liegen hintereinander in einem Bereich, nicht „verstreut“ (Code `PageFault`, `range.Value` + `i * 4096`); die Messung zeigt also Fehler bei schon vorhandener Seitentabelle (bis auf je eine neue Tabelle alle 512 Seiten, das passt zu max ~0,4 Mio).

**Regel #634:** `trägt` braucht B1 und B4 erfüllt und den Normalbau unverändert, und der Inhalt des Pakets ist die Zeit je Adressraumwechsel. Davon fehlt: (1) Zeit je Wechsel (siehe oben), (2) B1 hier nicht nachgeprüft, (3) Normalbau nicht gegen `origin/master` verglichen, ClassiCube 1 statt 3 Läufe. Eine bekannte Ursache in Cosmos/NativeAOT ohne abgegrenzten Patch liegt nicht vor, also nicht `trägt nicht`.

**Um auf `trägt` zu kommen:**

1. aswitch mit erzwungenem Wechsel je Runde: Der Handler wartet blockierend (zwei Ereignisse/Semaphoren im Ping-Pong, die Gegenstelle signalisiert und wartet); `Park` löst den Wechsel sofort über den Reschedule-Interrupt aus. Zwei Arme mit gleicher Arbeit: Paar in **einem** Adressraum (beide Threads an dieselbe Tabelle gebunden, `SwitchFor` lädt dann nichts) gegen Paar in **zwei** Adressräumen; die Differenz ist CR3 plus TLB. Beleg im Fall: Zähler, dass sich die Seiten abwechseln (jede Runde lief die andere Seite), und ein CR3-Wert je Seite (`ReadCr3` im Handler, zwei verschiedene im zweiten Arm, einer im ersten). smp1 genügt; smp4 nur mit Beleg derselben CPU.
2. Normalbau gegen `origin/master` (gleiche Skripte, je 3 Läufe KVM), ClassiCube 3 Läufe je smp.
3. B1-Abnahme verlinken (Kopf, Zahlen).
