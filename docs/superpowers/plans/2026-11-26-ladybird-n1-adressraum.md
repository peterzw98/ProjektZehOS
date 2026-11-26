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

(Senior)
