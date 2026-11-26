# Ladybird N1-B5: Messung Adressräume (Entwurf, noch ohne Messwerte)

Paket N1-B5 (#634), Plan `2026-11-24-ladybird-n1-nachweise.md`. Messung von `agent-peter-engineer`, Urteil von einem Senior (Abschnitt „Urteil“ am Ende, noch leer). Dieser Branch baut auf #712 (N1-B4, Kopf `d1f3fc7c1`) auf, das auf #710 (user-cr3, Pin `7b4b1018`) beruht. Entwurf, nicht mergen.

## Stand der Voraussetzungen (aus #712 und #710 gelesen, nicht neu gelaufen)

- B1 (AddressSpaceMap): Hostteil, Branch `task/ladybird-n1b1-adressraum`; Abnahme dort, hier nicht nachgeprüft.
- B3 (#710): `Tests.UserPaging` KVM 7 Läufe, TCG 2 Läufe je 43/0/5 von 48; 6 Gegenproben rot; ein Fenster (Warten auf `Changing`) nur durch Lesereview gestützt.
- B4 (#712): KVM smp1 und smp4 je 10/10 (27 Fälle), TCG `-cpu max` smp4 3/3, **TCG smp1 1/3** (Fall `device`, `program_checks=0`, vermutlich Zeitabhängigkeit der Probe, Ursache nicht belegt). B4-Abnahme „3 TCG, 1 und 4 CPUs“ ist damit **nicht erfüllt**, solange das offen ist.
- `gc2` smp4: `overlapped=0` (nicht Pflicht, benannt).

## Lücke: #712 misst keine Zeit

Die Fälle von #712 (`isolate`, `kernelread`, `nx`, `edge`, `sparse`, `copy`, `churn`, `gc2`, `device`) enthalten keine Zeitmessung. Die Messung nach Plan („Zeit je Adressraumwechsel und je Seitenfehler“) braucht zwei neue Fälle in `CosmosAddressSpaceProbe.cs` und `probe.S`: Dafür gilt dieselbe Methode wie in A7 (`sysmed`).

## Messplan

1. **Adressraumwechsel (`aswitch`):** zwei Threads mit eigenem CR3, Wechsel erzwungen durch `Yield`; Zeit je Wechsel = (Zeit mit verschiedenem CR3 − Zeit mit gleichem CR3) je Hin und Zurück, `rdtsc` um 100 000 Wechsel, Median über Läufe; Gegenwert „gleicher CR3“ als Eigenkosten. KVM, 1 CPU (smp4 zusätzlich), 3 Läufe.
2. **Seitenfehler (`pfault`):** Programm berührt 10 000 verstreute Seiten (nicht belegt), `FaultHandler` belegt sie; Zeit je Fehler = `rdtsc`-Spanne im Ring 3 um den Zugriff, Differenzen nach dem Messfenster an den Kernel (wie `sysmed`), Median und p99, Leerlauf (Zugriff auf belegte Seite) als Abzug.
3. **Normalbau unverändert:** Desktop (`scripts/test-desktop*.py`, 1 und 4 CPUs), ClassiCube, Netz (Ping/DHCP-Skript) im Normalbau (ohne `-p:ZehOSUserModeProbe`) mit Cosmos-Pin `7b4b1018` gegen `origin/master` mit smp.13, je 3 Läufe KVM. #710 meldet Desktop 1 und 4 CPUs bereits bestanden (Probebranch `task/pin-user-cr3-probe`).
4. Zu klären vor dem Lauf: ClassiCube- und Netzskript (Namen im Repository), ob ClassiCube in der Matrix von engineer2 vorkommt.

## Regel (aus #634)

`trägt`: B1 und B4 erfüllt, Normalbau unverändert. `trägt nicht`: Fall scheitert an bekannter Ursache in Cosmos/NativeAOT ohne abgegrenzten Patch. Sonst `offen`. Einschätzung vor der Messung: B4 TCG smp1 1/3 verhindert „erfüllt“, bis der `device`-Fix läuft.

## Messwerte

(folgen nach der Matrix von engineer2)

## Nicht geprüft / Grenzen

(folgen)

## Urteil

(Senior)
