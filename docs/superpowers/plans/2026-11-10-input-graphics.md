# Eingabe und Grafik Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Roadmap-Etappen 3 und 4 auf Cosmos 3.0.94 in QEMU umsetzen.

**Architecture:** Ein InputService trennt physische Tasten von Text und liefert begrenzte Ereignisse an eine kooperative Schleife. Der LineEditor verwendet die vorhandene Shell. F4 oeffnet eine Grafikprobe; Escape kehrt zur Shell zurueck. DisplayService verwendet den vorhandenen Cosmos-Bildpuffer; PixelSurface zeichnet mit Clipping direkt darin.

**Tech Stack:** C#, .NET 10, Cosmos 3.0.94, vorhandenes MSTest, QEMU q35/PS2.

**Spec:** ROADMAP.md, Etappen 3 und 4; Nutzerauftrag vom 10.11.2026. Etappe 2 wird parallel bearbeitet.

## Global Constraints

- Keine neuen Pakete und kein Cosmos-Update.
- Vorhandene Datei- und Shellbefehle erhalten.
- Keine Fensterverwaltung oder Desktop-Terminal vorwegnehmen.
- Gemeinsame Dateien unmittelbar vor Anpassungen erneut lesen; fremde Aenderungen erhalten.
- Zeit ueber das in Cosmos an den TSC gebundene Stopwatch messen, nicht Environment.TickCount64 (zaehlt in 3.0.94 Aufrufe).
- Cosmos 3.0.94 verwirft Tastenfreigaben: kleiner Plug am Rohdaten-Eingang, Uebersetzung ausserhalb des Interrupts mit dem vorhandenen Tastaturlayout.
- Langsame Dateibefehle bleiben vorerst synchron; Hintergrundarbeit folgt bei den entsprechenden spaeteren Roadmap-Etappen.

## Review Focus

- Ueberlauf darf keine dauerhaft gedrueckten Tasten hinterlassen: Reset-Ereignis und Zaehler.
- Lange Eingaben und Verlauf: begrenzen, Entwurf samt Cursor wiederherstellen.
- Negative/extreme Zeichenkoordinaten: weder Ueberlauf noch lange Schleifen ausserhalb des Bildes.
- Grafikwechsel: keine gleichzeitigen Konsolenschreibzugriffe, Eingabeentwurf erhalten.
- Fehlendes Datenlaufwerk und unveraenderte Dateibefehle: im Gast pruefen.

## Task 1: Nicht blockierende Eingabe

**Files:** ProjektZeh/Input/*, ProjektZeh/Shell/ConsoleLineEditor.cs, ProjektZeh/Kernel.cs, ProjektZeh.Tests/InputTests.cs, ProjektZeh.Tests/LineEditorTests.cs, ProjektZeh.Tests/ProjektZeh.Tests.csproj.

**Interfaces:** InputService.ReportKey/ReportPointer/TryRead, InputEvent mit Typ und physischem Tastencode, LineEditor.Handle(InputEvent, out string?).

- [x] Tests fuer FIFO/Ueberlauf/Reset, Down/Up/Text-Trennung, Maus und Zeileneditor schreiben und rot ausfuehren.
- [x] Begrenzte Queues, Rohdaten-Plug, Layoutanbindung und LineEditor implementieren.
- [x] Shell in eine fortlaufende Schleife mit begrenztem Eingabebudget und kurzer Wartezeit einbinden; Status auch waehrend Eingabe aktualisieren.
- [x] Hosttests und nativen Build pruefen.

## Task 2: Gepufferte Grafikprobe

**Files:** ProjektZeh/Graphics/*, ProjektZeh.Tests/PixelSurfaceTests.cs, Kernel-Integration.

**Interfaces:** PixelSurface auf bestehendem int[]; FillRectangle, DrawLine, DrawText, DrawPixels; DisplayService.Render/Input-Verarbeitung.

- [x] Kleine, handgepruefte Pixeltests fuer Clipping, Linien, Bitmaptext und Pixelkopien schreiben und rot ausfuehren.
- [x] Zeichnen und Clipping implementieren; einen Cosmos-Puffer wiederverwenden.
- [x] F4/Escape, Bildschirmgrenzen, sichtbare Mausklicks und laufende Statusanzeige integrieren.
- [x] Alle Hosttests und nativen Build pruefen.

## Task 3: Gastpruefung und Dokumentation

- [x] QEMU mit isolierter Kopie des Datenimages starten; Shell, Editieren/Verlauf, Down/Up, Grafikwechsel, Maus und Neustartpfade pruefen.
- [x] QEMU ohne Datenplatte pruefen.
- [x] Diff gesondert reviewen; README, COSMOS_SUPPORT und nur die geprueften Roadmap-Punkte aktualisieren.

## Ergebnis

104 Hosttests mit Warnungen als Fehler bestanden; nativer Release-Build: 0 Warnungen, 0 Fehler. QEMU bestaetigt Eingabe, Tastenfreigaben, Grafik, Maus und erhaltene Datei nach Neustart sowie Bedienung ohne Datenplatte. Der unabhängige Review fand einen Ziffernblockfehler; zwei rote Regressionen reproduzierten ihn, `IsTextKey` verhindert nun die doppelte Verarbeitung als Navigation und Text. Die gesamte Testsuite und der Gasttest bestanden danach. Grenzen der Cosmos-Tastencodes und synchrone Dateioperationen sind in COSMOS_SUPPORT dokumentiert.

## Nachtrag: master integrieren

Auf Nutzerwunsch `origin/master` bei `d81d24a` abgerufen und integriert. Diagnose, Recovery und Energieaktionen aus Etappe 2 bleiben erhalten. Konflikte in Kernel und Roadmap zusammengefuehrt; die vorhandene Stopwatch wird nun auch fuer SystemLog und uptime verwendet, da Environment.TickCount64 in Cosmos 3.0.94 keine echte verstrichene Zeit liefert. Nach dem Merge bestehen 131 Hosttests. Normaler nativer Build und expliziter Recovery-Build mit Warnungen als Fehler bestanden. QEMU bestaetigt Diagnosebefehle, steigende uptime, Grafikwechsel und Dateilesen, shutdown sowie einen bedienbaren Recovery-Start ohne Datenplatte.
