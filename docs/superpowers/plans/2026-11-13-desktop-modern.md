# Moderne Desktopbasis – Umsetzungsplan

> Ausführung im aktuellen Chat durch den implementierenden Assistenten; unabhängiges Review des fertigen Stands vor PR.

## Ziel und Grenzen

Freigegebener Schritt A des Desktopentwurfs: KDE/Cinnamon-artige Leiste und Menü, Fensterzustände, Größenänderung, Tastaturwechsel und gemeinsame UI. Cosmos 3.0.94 und .NET 10 unverändert. Etappe 6 bleibt in ihren eigenen Arbeitskopien. Nach Nutzersteuerung wurden die inzwischen in master integrierten Apps und UI-Bausteine aus Etappe 8 übernommen; der Rechner bleibt separat.

## Aufgaben

- [x] Fenster: `Window` erhält veränderliche Geometrie, Mindestgröße, minimierten/maximierten Zustand und gespeicherte Wiederherstellungsgeometrie. `WindowManager` verwaltet stabile Fensterliste getrennt von Z-Ordnung, Aktivieren, Minimieren, Maximieren, Schließen und Mausaufnahme für alle Ränder. Tests zuerst für Wiederherstellung, verdeckte Fenster, Grenzwerte und Reset.
- [x] Desktop/UI: gemeinsame Schaltflächen, Textfeld und Liste mit Fokus; Menü mit tatsächlichen Apps und Suche, leere Startfläche, Terminalstarter und Fensterleiste. Alt+Tab zyklisch, Menü per Tastatur bedienbar. Escape schließt zuerst Menü, sonst Textshell. Tests zuerst für Menü, leeren Start, Limit, Leiste und Tastatur.
- [x] Terminal: begrenzten bestehenden Ausgabeinhalt bei Größenänderung neu umbrechen, Eingabe und Verlauf erhalten. Tests zuerst für Verkleinern/Vergrößern und begrenzte Ausgabe. Keine zusätzliche Shellimplementierung.
- [x] Integration und Prüfung: README/Roadmap anpassen, Hosttests Release mit `-warnaserror`, native Normal-/Recovery-Builds, QEMU mit echter Bedienung, VMware soweit verfügbar. Unabhängiges Review, berechtigte Befunde beheben, Commit/Push/PR ohne Merge.

## Prüfungsfokus

Mausaufnahme beim Verlassen des Fensters und beim Reset; minimierte Fenster erhalten Zustand, bekommen aber keine Eingaben; Alt+Tab bei drei Fenstern ohne Pendeln; schrumpfende Fenster schneiden keine gespeicherten Ausgaben ab; Menü und Leiste reichen Ereignisse nicht in darunterliegende Apps durch.

## Entscheidungen

Fensterfunktionen, Menü, Terminal und Integration sind umgesetzt und wie in [DESKTOP_REDESIGN.md](../../DESKTOP_REDESIGN.md) geprüft. Zwei unabhängige Reviewdurchgänge; beide wesentlichen Befunde mit Rot/Grün-Regression korrigiert. Veröffentlichungsstatus wird im Arbeitsstand geführt.

Die vorhandene Ereignisschleife bleibt Eigentümerin der UI. Keine direkten Änderungen an Scheduler, Benutzern oder Dateirechten. Terminalausgabe bleibt auf 256 gespeicherte Segmente begrenzt; Umbruch wird für die aktuelle Breite berechnet, ohne beim Zeichnen den gespeicherten Inhalt zu verändern.
