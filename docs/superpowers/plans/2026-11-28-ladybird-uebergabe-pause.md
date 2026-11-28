# Ladybird: Übergabe während der Pause (28. November bis 1. Dezember 2026)

Peter (Terminal, 28.11.): Das MSI-Team pausiert bis Dienstag. senior25 (Windows-PC) und Laser arbeiten weiter. Alle Stände sind gepusht; dieses Dokument nennt Köpfe, offene Blöcke und Grenzen.

## Köpfe

| Branch | Kopf | Inhalt |
| --- | --- | --- |
| `story/ladybird` | dieser Commit | N2-Z T1 bis T4, N3 A/B1–B3/C/E/G, N4 S0–S5, IRQ-Signalzustellung (Pin `0fb91b8df`), [Stand 28.11.](../../LADYBIRD_FEASIBILITY.md) |
| `story/ladybird-cosmos` | `2ecd10951` | S0, S1 TLB-Abschuss, S3 Kick, user-return-work mit Folgecommit |
| `master` | `c042ab90` | Vorführstand für Freitag, Check grün (MSI-KVM und Windows-TCG) |

## Offene Blöcke, nicht gemergt

| Branch | Kopf | Stand | Fehlt |
| --- | --- | --- | --- |
| `task/lb-n4-irq-signal` | `369822544` | Pin `2ecd10951`, Anstoß der übrigen Threads nach Enter-Ende (PassOnSignals), LINUX_ABI Maske je Thread; `test-linux-all` KVM `linuxsmp=0` 32/32, `linuxsmp=1` 16/16; Gegenprobe ohne Clear rot | Review des PassOnSignals-Commits, Hosttests, TCG |
| `task/lb-n2z-fat-lookup` | `863d2e1fb` | N2-Z Stufe 1: GetAttr statt FileInfo, ganze Auflösung unter einer StorageLock, Mounts einmal; Hosttests 2381/2381, nativ 0/0, `test-linux-all` kvm:1 16/16, `test-package2-all` kvm:1 3/3 | Review, kvm:4, Messung Lookups/KB je Start (Vorher: 17,2 Lookups, 205 KB je Start) |
| `docs/lb-n2z-messbau` | `36b07769` | Messbau für N2-Z T1: GC-Patch, Zähler, `fatprobe-cosmos.patch`, Skripte (Basis `502942a2`) | nur Werkzeug, nicht mergen |
| `task/lb-n2z-pool` | `a0d604468` | Puffer-Pool | verworfen: Verlust größer als ohne Pool, nicht mergen |

## Nächste Schritte für senior25

1. Review `task/lb-n4-irq-signal` (Delta seit `4b595fc00`) und `task/lb-n2z-fat-lookup` (Delta zu `story/ladybird`), nur lesend, Befunde im Kanal.
2. Hosttests beider Branches auf diesem PC (`dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`).
3. Gastläufe unter QEMU-TCG auf diesem PC, soweit möglich (`test-linux-all`, `test-package2-all`); KVM-Zahlen kommen vom MSI nach der Pause.
4. Merge in `story/ladybird` bleibt bei lead1 nach der Pause; bis dahin Ergebnisse auf den Branches festhalten.

Python-Skripttests: Unter Windows scheitern 17 Tests, die POSIX-Werkzeuge brauchen (`os.killpg`, `SIGHUP`, Symlinks, FIFOs, Gastwerkzeuge). Maßgeblich ist der Lauf unter Linux (CI oder WSL).

## Wartet auf Peter

- N3-D: Freigabe der Toolchain- und Containerversionen. Danach N3-F.
- PR #752 (Laser, #582): Freigabe durch Peter oder Lars; unabhängig von `story/ladybird`.
- Zeitplanentwurf N2–N6 (`ddf9c2d6` auf `docs/lb-plan-nach-demo`).

## Tor N2-Z

Punkte 1, 2 und 4 erfüllt. Punkt 3 (1000 Starts < 1 MiB, nach 100 Aufwärmstarts, Basis `502942a2`): 4 CPUs 852–916 KiB, 1 CPU 1108–1236 KiB. Rest ist Allokation je Start (Kindpfad 624 KB, FAT-Open 205 KB), kein Objekt- oder Seitenleck; Seiten zerfallen nicht. Stufe 1 FAT soll den Rest senken; Stufe 2 (Cosmos-Patch clusterweiser Lookup) nur nach Messung.

## Grenzen

- `zehos.linuxsmp` bleibt Vorgabe 0 bis Tor N4-Z. Ladybird mit `linuxsmp=1`: 13/13 plus 6/6 Dauerlauf, Bild identisch.
- Nicht geprüft: VMware (auf dem Windows-PC nicht installiert), echte Hardware.
- Auf dem PC gebaute ISOs, Laufplatten und Container nach jedem Lauf löschen (Peter).
