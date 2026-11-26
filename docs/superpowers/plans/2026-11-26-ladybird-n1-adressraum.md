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

Rechner MSI (KVM), Bau `task/ladybird-n1b5-zeitfaelle` @`12904e126` (aswitch neu gemessen @`c5a7948d0`, siehe dort) (auf #712 `d1f3fc7c1`, Cosmos `3.0.97-zehos.0.g7b4b101`), `-warnaserror` 0 Warnungen/0 Fehler für Probe- und Normalbau. Je ein Gast gleichzeitig, Rohdaten `~/zehos-b4-out/n1b5-*` auf MSI. Einheit: TSC-Ticks (`rdtsc`); Wechsel/Seitenfehler sind Mediane je Lauf.

**B4 (#712-Fälle + Zeitfälle), `test-user-mode-probe.py --accel kvm --runs 3`:** smp1 3/3 PASS, smp4 3/3 PASS (isolate, kernelread, nx, edge, sparse, copy, churn, gc2, device, aswitch, pfault, `failed=0`). TCG smp1 3/3 steht in #712 Kommentar 7182091025 (nicht neu gelaufen).

**aswitch (korrigiert, Review-P2 #715):** Die erste Messung (426 Ticks, `net_med` 0) war **kein** Wechselmaß: `Thread.Yield` setzt nur ein Flag für den nächsten Interrupt-Austritt und kehrt sofort zurück, die 426 Ticks waren ein Syscall-Rundlauf. Der Fall ist umgebaut (Kopf `c5a7948d0`): Der Peer erhöht je Aufruf einen Kernelzähler und merkt sich sein CR3; eine Paarrunde endet erst, wenn der Zähler gestiegen ist (Peer lief nachweislich), und je Runde wird das CR3 des Peers gegen das des Messers geprüft. `kernel_pair` zählt genauso (zwei Kernelthreads, gemeinsamer Zähler). 1000 Paarrunden (eine Runde dauert einen Timertakt), alone 20 000 Runden. KVM, je 3 Läufe, alle PASS:

| smp | Lauf | alone | pair (Median) | Runden CR3 verschieden / gleich | kernel_pair (Median) |
| --- | --- | --- | --- | --- | --- |
| 1 | 1 | 416 | 50 081 502 | 1000 / 0 | 24 975 358 |
| 1 | 2 | 436 | 50 119 150 | 1000 / 0 | 25 058 224 |
| 1 | 3 | 436 | 50 119 830 | 1000 / 0 | 25 057 588 |
| 4 | 1 | 436 | 49 962 490 | 1000 / 0 | 49 962 952 |
| 4 | 2 | 436 | 49 967 220 | 1000 / 0 | 49 967 746 |
| 4 | 3 | 436 | 49 958 396 | 1000 / 0 | 49 959 006 |

Lesart: Der Wechsel zum Peer ist jetzt belegt (CR3 in allen 3000 Paarrunden je smp verschieden, Peer-Läufe >= Runden), aber eine Runde kostet 25-50 Mio Ticks (10-20 ms bei 2,5 GHz) = ein bis zwei Timertakte, auch für zwei reine Kernelthreads. **Der Wechsel geschieht erst am Takt, nicht beim Yield; die Rundenzeit ist die Zeitscheibe, nicht der CR3-Aufwand.** Der Aufwand eines Adressraumwechsels ist damit **nicht gemessen** (die Differenz pair−alone ist nutzlos). Ein Maß bräuchte einen sofortigen Wechsel (Cosmos-Änderung oder ein anderer Auslöser, etwa ein Interrupt aus dem Kernel); das ist ein Folgeauftrag. smp4: pair und kernel_pair zeigen beide ~50 Mio, das min (8,6 Mio) deutet auf einzelne Runden mit Wechsel auf eine andere CPU; die Fälle sind nicht an eine CPU gebunden bewiesen. Rohdaten `~/zehos-b4-out/n1b5v2-*`.

**pfault** (4000 verstreute Seiten, `FaultHandler` belegt): Median erster Zugriff 2118/2054/2056 (smp1) und 2044/2042/2098 (smp4) Ticks, schon abgebildet 14; Abzug `net_med` je Fehler 2104/2040/2042 (smp1), 2030/2028/2084 (smp4). p99 erster Zugriff 2880-3406; max 0,37-0,41 Mio Ticks (Seitentabellenzuteilung/Ausreißer).

**Normalbau unverändert** (ohne Probe, gleicher Cosmos-Pin `7b4b1018`/`g7b4b101`, KVM):

| Prüfung | Ergebnis |
| --- | --- |
| `test-desktop-guest.py` | 3 Läufe PASS (Standard), 3/3 `ZEHOS_QEMU_SMP=1`, 3/3 `=4`; je ~37 s |
| `test-network.py` (Probe-ISO mit Fixture-CA, Netzprobe) | smp1 3/3, smp4 3/3 `NETWORK_PROBE PASS` |
| `test-classicube-desktop.py` (Normalbau mit Testschlüssel, Paket `~/zehos-676-pk/cc`) | smp1 1/1 PASS, smp4 1/1 PASS (freier Speicher ~440-447 MiB) |

## Nachtrag: Normalbau gegen master (smp.13) und B1

Stand: `origin/master` 53cba9cd5 (Cosmos 3.0.97-zehos.6-smp.13), **ohne** den Probe-Code dieses Branches; Rechner MSI, KVM, ein Gast zugleich. Je ISO `-warnaserror`: main, wasm (Testschlüssel/-CA), net (Netzwerkprobe) 0 Warnungen, 0 Fehler.

| Fall | 1 CPU | 4 CPUs |
| --- | --- | --- |
| Desktop (`test-desktop-guest.py`) | 3/3 PASS | 3/3 PASS |
| Netz (`test-network.py`) | 3/3 PASS | 3/3 PASS |
| ClassiCube (`test-classicube-desktop.py`, Paket Seed 1) | 3/3 PASS | 3/3 PASS |

ClassiCube: freier Speicher in den drei Starts je Lauf 440,5-446,9 MiB, ohne Ausreißer. Der Normalbau ist damit nicht schlechter als in den früheren Läufen mit dem Branch-Stand. Bildschirmfotos wurden nicht angesehen (das Skript meldet PASS aus dem seriellen Protokoll).

B1 (#620, gemergt mit #666, Folge #677): auf master 53cba9cd5 `dotnet test ProjektZeh.Tests --configuration Release -warnaserror --filter AddressSpaceMapTests`: 24 bestanden, 0 fehlgeschlagen. Das ist nur der Hostteil (Buchführung); die Kernelprobe `CosmosAddressSpaceProbe` wurde hier nicht gefahren.

## Nicht geprüft / Grenzen

- Kein Vergleich gegen `origin/master` (smp.13) im Normalbau gemessen; nur der Pin dieses Stands. Die Matrix von engineer2 (#708, 90/90) lief auf anderem Kopf.
- ClassiCube nur 1 Lauf je smp (nicht 3); Bildschirmfotos nicht visuell geprüft; Paket nicht mit `--world-seed 1` neu gebaut (Skript lief trotzdem durch).
- TCG, VMware, echte Hardware nicht gemessen. `gc2` smp4 `overlapped=0` unverändert (aus #712). P3 aus dem Review offen: `rdtsc` ohne `lfence` im Ring-3-Arm.
- aswitch: Wechselkosten nicht gemessen (siehe oben, Yield wechselt erst am Timertakt); Medianwerte ohne Streuungsangabe über Läufe außer der Tabelle.
- Ein Fenster in B3 (Warten auf `Changing`) bleibt nur durch Lesereview gestützt.

## Urteil

**`offen`** (agent-peter-senior, 26.11.2026, Neufassung auf Kopf `9ae3f80cb`; nur gelesen, kein Gastlauf). Ersetzt das Urteil auf `e94c4c9ec`. Offen bleibt es **nur wegen B4** (formale Abnahme); die Wechselkosten tragen das Urteil nicht.

**Regel #634:** `trägt`, wenn B1 und B4 erfüllt sind und der Normalbau unverändert läuft; `trägt nicht` bei bekannter Ursache in Cosmos/NativeAOT ohne abgegrenzten Patch; sonst `offen`. Anders als A7 (Median ≤ 5 µs) nennt die Regel **keine Schwelle für die Wechselzeit**. Die Zeitmessung ist Inhalt des Pakets, aber keine Bedingung für `trägt`.

| Bedingung | Stand | Beleg |
| --- | --- | --- |
| B1 | erfüllt | #666 und #677 auf master gemergt (mit Review); `AddressSpaceMapTests` auf master `53cba9cd5` 24/24 (Nachtrag) |
| Normalbau unverändert | erfüllt | gegen master `53cba9cd5` (smp.13): Desktop, Netz, ClassiCube je 1 und 4 CPUs je 3/3 = 18/18, Bau 0/0 `-warnaserror`; mit Pin `7b4b1018` Desktop 9/9, Netz 6/6, ClassiCube 2/2 |
| B4 | **noch nicht abgenommen** | siehe unten |
| Zeit je Seitenfehler | gemessen | pfault ~2030-2100 Ticks je Fehler (KVM Median, smp1 und smp4, je 3 Läufe) |
| Zeit je Adressraumwechsel | **nicht messbar** | Yield wechselt erst am Timertakt (#716); Rundenzeit = Zeitscheibe (25-50 Mio Ticks), auch für zwei Kernelthreads. CR3-Wechsel je Runde ist belegt (3000/3000 je smp), sein Aufwand nicht |

**Warum B4 noch nicht erfüllt ist:** Die Abnahme von B4 (#633) lautet „alle Fälle je 10 Läufe KVM und 3 TCG, 1 und 4 CPUs“. Auf `04d19c72` waren alle Fälle KVM 10/10 je smp und TCG smp4 3/3, aber TCG smp1 nur 1/3 (`device`). Der Fix `d1f3fc7c1` ändert ausschließlich den Abschnitt `device` in `CosmosAddressSpaceProbe.cs` (alle Hunks ab Zeile 687, Abschnitt beginnt Zeile 682); die übrigen 26 Fälle bleiben damit von der Serie auf `04d19c72` abgedeckt. Für `device` auf dem neuen Code liegen vor: KVM smp1 3+3, KVM smp4 3+3 (#712 Kommentar 7182091025 und dieser Bericht, `12904e126`), TCG smp1 3/3, **TCG smp4 0**. Es fehlen also für `device`: KVM je smp 4 weitere Läufe (auf 10), TCG smp4 3 Läufe. Außerdem hat #712 als Code-PR **noch kein unabhängiges Review** (Plan, „Urteil je Gruppe“: Code-PRs brauchen ein unabhängiges Review).

Kein `trägt nicht`: Es gibt keinen gescheiterten Fall mit bekannter Ursache ohne abgegrenzten Patch. #716 ist ein Messhindernis, kein Fehler des Adressraums (der Wechsel findet statt, nur nicht sofort).

**Um auf `trägt` zu kommen (Routine, Engineer):**

1. `device` auf `d1f3fc7c1`: KVM smp1 und smp4 je 4 weitere Läufe, TCG `-cpu max` smp4 3 Läufe (`test-user-mode-probe.py`, alle 27 Fälle erwartet), Zahlen in #712.
2. Unabhängiges Review von #712 ohne P1/P2.

Dann `trägt`, ohne neue Messung hier. Die Wechselkosten werden nachgetragen, sobald #716 einen sofortigen Wechsel liefert (Folgeauftrag, nicht Bedingung).

**Hinweise zum Bericht:** Die ersten beiden Punkte unter „Nicht geprüft / Grenzen“ (kein Vergleich gegen master, ClassiCube nur 1 Lauf) sind durch den Nachtrag überholt. pfault: Die Seiten liegen hintereinander in einem Bereich, nicht „verstreut“ (`range.Value + i * 4096`); gemessen sind Fehler bei meist vorhandener Seitentabelle (eine neue Tabelle je 512 Seiten passt zu max ~0,4 Mio). P3 `rdtsc` ohne `lfence` bleibt; bei ~2000 Ticks je Fehler ohne Einfluss auf die Größenordnung.
