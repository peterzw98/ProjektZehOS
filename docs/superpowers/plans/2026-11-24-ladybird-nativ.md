# Plan: Ladybird über native Pakete (Übersicht)

> **Für ausführende Agenten:** Diese Übersicht ganz lesen, danach nur die Datei deiner Phase. Jedes Paket ist ein eigener Branch und PR. Arbeitsweise: superpowers:executing-plans oder superpowers:subagent-driven-development.

**Ziel:** Programme laufen nativ im Ring 3, kommen als signierte Pakete mit Abhängigkeiten und lassen sich mit normalen Linux-Werkzeugen bauen; Ladybird läuft so, und der Interpreter entfällt.

**Architektur:** Cosmos liefert die Mechanik (Ring 3, CR3 und TLS-Basis je Thread), ZehOS Prozesse und eine Auswahl der Linux-x86-64-Systemaufrufe in C#, `pkg` v2 den Paketdienst. Entwurf: [Native Pakete](../specs/2026-11-24-native-pakete-design.md).

**Technik:** C#/.NET 10, Cosmos 3.0.97-zehos (Quelle), Assembler, musl, Clang, Rust `x86_64-unknown-linux-musl`, MSTest, QEMU.

## Phasen

| Phase | Ergebnis | Roadmap | Datei | beginnt nach |
| --- | --- | --- | --- | --- |
| L1 | Ladybird unverändert auf Linux gebaut und gemessen (#448–#450) | 12.1 | [L1](2026-11-19-ladybird-l1-messung.md), Lead-Chat | freigegeben |
| **N1** | Nachweise: Ring 3, Seitentabellen, Aufrufliste, musl | 12.4 (Weg B, Studie) | [N1](2026-11-24-ladybird-n1-nachweise.md) | je Paket, siehe dort |
| N2 | Statische Linux-Programme im Terminal | 12.4, 12.5 | [N2](2026-11-24-ladybird-n2-prozesse.md) | Tor N1-Z |
| N3 | `pkg` v2, sysroot, gemeinsame Bibliotheken | 12.5, 12.6 | [N3](2026-11-24-ladybird-n3-paketdienst.md) | Tor N1-Z, letzter Teil nach N2 |
| N4 | Threads, Prozesse, IPC, Netz | 12.7, 12.8, 12.9 | [N4](2026-11-24-ladybird-n4-posix.md) | Tor N2-Z |
| N5 | Apps nativ, wasm3 und WAMR entfernt | Ablösung 13.5 | [N5](2026-11-24-ladybird-n5-umstieg.md) | Tor N3-Z; Netz-Apps nach N4 |
| N6 | Ladybird im Fenster | 12.3 | [N6](2026-11-24-ladybird-n6-ladybird.md) | Tore N3-Z und N4-Z |
| SDL | SDL-Anbindung für native Programme (#690, Auftrag Peter 25.11.) | – | Issue #690, Schnitt danach | Tore N3-Z und N4-Z |

In Pakete geschnitten sind N1 (19 Pakete und das Tor) und die hostprüfbare [Vorarbeit P zum Paketdienst](2026-11-24-ladybird-p-paketdienst-vorarbeit.md) (P1 bis P4), die nicht auf das Tor wartet. N2 bis N6 sind Umrisse und werden am jeweiligen Tor geschnitten, weil ihre Schnittstellen von den Nachweisen abhängen.

## Nebeneinander

```
L1-A, L1-B → L1-C ─→ N1-C ─────────────┐
L1-A ─→ N1-D ──────────────────────────┤
N1-E (sofort) ─────────────────────────┼─→ N1-Z ─┬─→ N2 ─→ N4 ─┬─→ N6
#523 (Multicore auf master) → N1-A → N1-B ┘      └─→ N3 ───────┴─→ N5
P1 → P2 → P4, P1 → P3 (sofort, Host) ──────→ N3
#423 (AOT als Standard), Lead-Chat: Voraussetzung für N1-Z
V1 bis V10 (#731, Story-Branches) vorgezogen aus N2 und N4, neben N1
```

N2 und N3 haben getrennte Dateien (`Processes/` gegen `Packages/`, `pkgs/`, Werkzeuge) bis auf Index, sysroot und Anzeige in N3. N5 und N6 haben getrennte Dateien.

## Story-Branches und Vorstellung

Seit 26. November 2026 (Auftrag Peter) läuft alle Ladybird-Arbeit auf `story/ladybird` (ZehOS, Basis `master`, Pin für Gastbauten auf `story/ladybird-cosmos`) und `story/ladybird-cosmos` (Cosmos-Patches, Basis `cosmos`). Pakete arbeiten auf `task/lb-<paket>` oder ihrem Plan-Branch und werden per Merge in die Story übernommen; kein Ladybird-PR gegen `master` oder `cosmos`. Das ändert die Regeln oben: „Jedes Paket ist ein eigener PR“ gilt gegen die Story, „Cosmos-Änderungen nur über `cosmos`-PRs“ erst bei der Übernahme nach `cosmos`. Issues, deren Ergebnis nur auf der Story liegt, sind trotzdem geschlossen (#450, #630, #632, #634, #635, #698). Wann und wie die Story nach `master` und `cosmos` geht, entscheidet das Tor N1-Z.

Für die Vorstellung am Freitag, 4. Dezember 2026 wird N2 vor dem Tor vorgezogen: [Plan Vorstellung](2026-11-26-ladybird-vorstellung.md) mit V0 bis V6, auf den Branches inzwischen bis V10; Übersicht im Sammelblock #731. V10 (Threads, `clone`, `futex`) ist Inhalt von N4. Die Tore N2-Z und N4-Z gelten unverändert; die V-Pakete ersetzen sie nicht.

Seit dem Schnitt am 24. November dazugekommen:

| Issue | Inhalt | Bezug |
| --- | --- | --- |
| #698 | N1-B3a: APIC und EfiRtc über HHDM, nötig für CR3 je Thread | vor N1-B3 (#632), geschlossen |
| #716 | Cosmos: `Thread.Yield` wechselt erst am Timertakt; Patch `yield-now` in `story/ladybird-cosmos` | Kosten je Adressraumwechsel (N1-B5) erst danach messbar; Latenz für N2 bis N4 |
| #732 | verschachteltes NMI unter TCG (Rest aus #700) | vor der Übernahme von A1–A4 nach `cosmos` |
| #733 | VMware, echte Hardware, Last ohne Obergrenze für A und B | vor N2-Z |
| #690 | SDL-Anbindung | nach N3-Z und N4-Z |
| #419 | 12.8, nur noch Restabnahme P4-Z | siehe „Überholt“ |

## Tore

| Tor | GO nur, wenn alles erfüllt ist | sonst |
| --- | --- | --- |
| N1-Z | siehe [N1-Z](2026-11-24-ladybird-n1-nachweise.md#n1-z-tor) | Tabelle in N1-Z |
| N2-Z | S1-Programme aus N1-E haben auf ZehOS dieselbe Ausgabe wie auf Linux; ein Fehler im Ring 3 beendet nur den Prozess; 1000 Prozessstarts ohne Speicherverlust über 1 MiB; Desktop und Wasm-Pakete unverändert | N4 nicht beginnen, Ursache zuerst |
| N3-Z | Kette aus drei Paketen mit gemeinsamer Bibliothek installiert, aktualisiert, entfernt; Abbruch in jedem Schritt lässt den alten Bestand startbar; ein unverändertes CMake- und ein Cargo-Projekt bauen gegen den sysroot | N5 und N6 nicht beginnen |
| N4-Z | alle Programme aus N1-E gleich wie auf Linux; zwei Prozesse tauschen Deskriptor und geteilten Speicher; Absturz des einen lässt den anderen laufen; ein `pthread`-Programm nutzt zwei CPUs | N6 nicht beginnen |
| N5-Z | Rechner, Doom, ClassiCube, NetSurf nativ abgenommen, Doom und ClassiCube nicht langsamer als unter AOT; Sicherheitsprüfung; **ausdrückliche Freigabe von Peter zum Entfernen** | Laufzeiten bleiben |
| N6-Z | statische Seite im ZehOS-Fenster, bedienbar, Speicher unter der Grenze | Ladybird bleibt unveröffentlicht |

Jedes Tor entscheidet ein Projektbenutzer. Ergebnisse stehen in `docs/LADYBIRD_FEASIBILITY.md`. Gastläufe in jedem Tor wie in „Feste Werte“.

## Feste Werte

| Name | Wert |
| --- | --- |
| Ladybird | `6fba5b78a1043fe93b3451204e04dcc0e3bd983a`, `ENABLE_JS_JIT=OFF`; übrige Werte in der [alten Übersicht](2026-11-17-ladybird.md#feste-werte) |
| ABI | Linux x86-64; Referenz ist der in N1-E notierte Linux-Kernel |
| libc | musl; Ausweg glibc nach N1-D |
| Testshell | toybox (0BSD) |
| Läufe im Gast | je Fall 10 vollständige Läufe unter QEMU/KVM und 3 unter TCG, jeweils mit 1 und mit 4 CPUs; KVM auf MSI oder in WSL mit KVM; VMware offen |
| No-Go Speicher | PSS auf `static.html` über 2 GiB (wie L1) |
| Zeitlimit | je Paket das Doppelte der genannten Größe; Gruppe A 20, B 15, D 6 Arbeitstage; danach Urteil mit dem erreichten Stand |

Quellen und Bauordner liegen unter `~/ladybird-probe/` und werden nie committed. Cosmos-Änderungen nur über `cosmos`-PRs nach `docs/regeln/COSMOS_PATCHES.md`. Keine Issues, PRs oder Kommentare bei Upstream-Projekten.

## Überholt

Der Wasm-Weg samt M1 (#611–#615, PR #616) und die Wasm-Pakete #417, #418, #420 (geschlossen; von #419 bleibt nur die Restabnahme P4-Z, [Entwurf, Abschnitt 8](../specs/2026-11-24-native-pakete-design.md#8-übergang)). Die Berichte L2-A bis L2-F bleiben als Quelle gültig.

## Offen

Plan freigegeben (Peter, 24. November 2026); N1 und die Vorarbeit P stehen als kleine Pakete auf dem Board, N2 bis N6 als Sammelblöcke (#643 bis #647); Nummern und Reihenfolge in [N1](2026-11-24-ladybird-n1-nachweise.md#reihenfolge). **Stand 26. November 2026:** P1 bis P4, L1 und N1-A, B, C, E sind erledigt (A, B, C `trägt`, Entwurf [Ergebnis N1](../../LADYBIRD_FEASIBILITY.md#ergebnis-n1)); offen sind D (#622, #636) und #423. Nach der Tor-Regel wartet N1-Z damit auf #423. An N1-Z entscheidet Peter über N2, an N5-Z über das Entfernen der Laufzeiten. In N3 ist zu entscheiden, wo Paketdateien über 100 MiB liegen.
