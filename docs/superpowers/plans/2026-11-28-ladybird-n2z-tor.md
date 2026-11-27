# Ladybird N2-Z: Torpakete nach dem Vorzeigestand

Stand 28. November 2026. Auftrag Peter (Terminal, 28.11.): nach dem Vorzeigestand mit dem Gesamtplan weitermachen. Grundlage: [Übersicht](2026-11-24-ladybird-nativ.md) (Tor N2-Z), [N2-Umriss](2026-11-24-ladybird-n2-prozesse.md), [Zeitplanentwurf](https://github.com/peterzw98/ProjektZehOS/blob/ddf9c2d642515a832a85277c11918b9be3d654bb/docs/superpowers/plans/2026-12-04-ladybird-n2-n6-zeitplan.md) auf `docs/lb-plan-nach-demo` (wartet auf Entscheidung Peter).

## Rahmen

- `story/ladybird` bleibt bis nach der Vorstellung am 4. Dezember auf dem Vorzeigestand (Kandidat `ff57d65b`, Rückfall `d0a5b7b7`); bis dahin nur Doku-Commits. Codepakete liegen auf eigenen `task/`-Branches ab `ff57d65b` und werden erst nach dem 4. Dezember gemergt.
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
| T1 Speicher | V47 (GC vor `ENOMEM`, GC bei Prozessende unter Schwelle) auf `ff57d65b` nachziehen; Messung frei vor/nach 1000 Starts jeweils nach erzwungener GC | `task/lb-v47-prozessleck` | engineer2 | Verlust nach GC unter 1 MiB, KVM 1 und 4 CPUs, Review ohne P1/P2 |
| T2 S1 vollständig | Ursache `badptr` klären und beheben | `task/lb-n2z-badptr` | engineer | 24/24 gegen `expected/`, KVM 1 und 4 CPUs, TCG 1 CPU |
| T3 Fehlerisolation | Torlauf: Seitenfehler, `ud2`, Division durch null, `SIGSEGV` ohne Handler im Ring 3; danach bleibt die Shell bedienbar und ein weiterer Start gelingt | kein Code, Lauf auf `ff57d65b` | senior2 | alle Fälle zweimal, Shell danach bedienbar |
| T4 Unverändert | Normal- und Recovery-Bau ohne `ZehOSLinuxRun` aus `ff57d65b`; Boot, Shell, Desktop, Wasm-Pakete (Rechner, Doom), `scripts/test-power-commands.ps1` | kein Code | senior2 nach T3 | alle Abläufe wie auf `master` |
| T5 N4-Schnitt | Entwurf: Threads und Prozesse eines Linux-Laufs auf mehrere CPUs verteilen (heute eine CPU je Lauf); Sperren, TLB-Abschuss, Futex über CPUs; Pakete mit Schnittstellen | Doku `docs/lb-n4-smp-entwurf` | senior | Entwurf mit Paketen und Torbezug N4-Z, Review |
| T6 Integration | Integrationsbericht auf `ff57d65b` nachziehen, Reihenfolge Cosmos-Pin → ZehOS, Prüfmatrix | Doku `docs/lb-master-integration` | senior25 | Bericht aktuell; Merge erst nach Freigabe |

T1, T2, T5 und T6 laufen parallel. T3 und T4 nutzen den Gast nacheinander.

## Danach

- N3: P1 bis P4 sind auf `master` gemergt (#668, #687, #688, #694). Der nächste Schnitt (Index, sysroot, Laufzeitlader) folgt nach N2-Z.
- Nach dem 4. Dezember: Merge von V47, V49 und den Torpaketen in `story/ladybird`, dann Tor N2-Z förmlich prüfen.
