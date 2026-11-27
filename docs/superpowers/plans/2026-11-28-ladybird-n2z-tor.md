# Ladybird N2-Z: Torpakete nach dem Vorzeigestand

Stand 28. November 2026. Auftrag Peter (Terminal, 28.11.): nach dem Vorzeigestand mit dem Gesamtplan weitermachen. Grundlage: [Übersicht](2026-11-24-ladybird-nativ.md) (Tor N2-Z), [N2-Umriss](2026-11-24-ladybird-n2-prozesse.md), [Zeitplanentwurf](2026-12-04-ladybird-n2-n6-zeitplan.md) auf `docs/lb-plan-nach-demo` (wartet auf Entscheidung Peter).

## Rahmen

- Update Peter (28.11.): Ladybird wird am 4. Dezember nicht vorgestellt, gezeigt wird `master`. Es gibt keinen Freeze: reviewte `task/`-Branches gehen wie gewohnt in `story/ladybird` (Hosttests vorher grün). Vor Freitag läuft ein Check von `master` (Normal- und Recovery-Bau, Desktop, Wasm-Pakete).
- Reihenfolge: die Torpakete T1 bis T4 zuerst, die Entwürfe T5 (N4) und N3 parallel, damit niemand leerläuft.
- Merge nach `master`, Veröffentlichung und Paketfreigaben nur mit Freigabe eines Projektbenutzers.
- Gastläufe auf dem MSI: höchstens ein zeitmessender Gast gleichzeitig; Laufplatten nach jedem Lauf löschen.

## Tor N2-Z (aus der Übersicht)

1. S1-Programme aus N1-E haben auf ZehOS dieselbe Ausgabe wie auf Linux.
2. Ein Fehler im Ring 3 beendet nur den Prozess.
3. 1000 Prozessstarts ohne Speicherverlust über 1 MiB.
4. Desktop und Wasm-Pakete unverändert.

Stand: (1) 23 von 24 (`badptr` scheitert), (2) durch V24/V45-Gastfälle belegt, Torlauf fehlt, (3) verfehlt ohne GC-Lauf (~962 kB je Start, ungesammelter GC-Heap, V47), (4) nicht geprüft auf dem Story-Stand.

## Pakete

| Paket | Inhalt | Branch | Zuständig | Fertig, wenn |
| --- | --- | --- | --- | --- |
| T1 Speicher | V47 (GC vor `ENOMEM`, GC bei Prozessende unter Schwelle) in `story/ladybird`; Messung frei vor/nach 1000 Starts jeweils nach erzwungener GC | `task/lb-v47-prozessleck` | engineer2 | Verlust nach GC unter 1 MiB, KVM 1 und 4 CPUs, Review ohne P1/P2 |
| T2 S1 vollständig | Ursache `badptr` klären und beheben | `task/lb-n2z-badptr` | engineer | 24/24 gegen `expected/`, KVM 1 und 4 CPUs, TCG 1 CPU |
| T3 Fehlerisolation | Torlauf: Seitenfehler, `ud2`, Division durch null, `SIGSEGV` ohne Handler im Ring 3; danach bleibt die Shell bedienbar und ein weiterer Start gelingt | kein Code, Lauf auf dem Story-Kopf | senior2 | alle Fälle zweimal, Shell danach bedienbar |
| T4 Unverändert | Normal- und Recovery-Bau ohne `ZehOSLinuxRun` aus dem Story-Kopf; Boot, Shell, Desktop, Wasm-Pakete (Rechner, Doom), `scripts/test-power-commands.ps1` | kein Code | senior2 nach T3 | alle Abläufe wie auf `master` |
| T5 N4-Schnitt | Entwurf: Threads und Prozesse eines Linux-Laufs auf mehrere CPUs verteilen (heute eine CPU je Lauf); Sperren, TLB-Abschuss, Futex über CPUs; Pakete mit Schnittstellen | Doku `docs/lb-n4-smp-entwurf` | senior | Entwurf mit Paketen und Torbezug N4-Z, Review |
| T6 Integration | Integrationsbericht auf `ff57d65b` nachziehen, Reihenfolge Cosmos-Pin → ZehOS, Prüfmatrix | Doku `docs/lb-master-integration` | senior25 | Bericht aktuell; Merge erst nach Freigabe |

T1, T2, T5 und T6 laufen parallel. T3 und T4 nutzen den Gast nacheinander.

## Danach

- N3: P1 bis P4 sind auf `master` gemergt (#668, #687, #688, #694). Der nächste Schnitt (Index, sysroot, Laufzeitlader) folgt nach N2-Z.
- V47 und V49 gehen sofort nach grünen Hosttests in `story/ladybird`; danach Tor N2-Z förmlich prüfen.
