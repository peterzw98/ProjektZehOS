# Fensterverwaltung und Desktop

Auftrag: vom aktuellen `master` aus den nächsten Roadmap-Schritt bauen; der Nutzer
hat den Auftrag ausdrücklich um den Desktop erweitert. Grundlage sind Etappen 5
und 6 der Roadmap. Basis: `f0dc4b1`, Branch `codex/fensterverwaltung`.

## Umfang und Entscheidungen

- Normaler Boot zum Desktop bei 1024 × 768 × 32; Recovery startet in der Textshell.
- Zwei überlappende Terminalfenster beim Start, weitere über einen sichtbaren
  Terminal-Starter. Höchstens acht gleichzeitig geöffnete Fenster.
- Fenster haben Titel, Inhalt, Fokus, Z-Reihenfolge, Ziehen an der Titelleiste und
  Schließen. Sie bleiben innerhalb der Arbeitsfläche über der unteren Leiste.
- Nur das aktive Fenster bekommt Tastatureingaben. Fokuswechsel, Eingabe-Reset
  und Wechsel zur Textshell lösen festgehaltene Eingabezustände und Ziehen auf.
- Alle Fenster zeichnen im vorhandenen Cosmos-Puffer mit Beschneidung des Inhalts;
  jedes neue Bild zeichnet den Hintergrund neu. Keine weiteren Bildpuffer je Fenster.
- Kleines `IApplication` mit Start, Handle, Update, Draw und Close. Zunächst nur
  fest einkompilierte Terminals; kein Loader oder Prozessmodell.
- Jedes Terminal erhält einen eigenen `ShellService`, `FileService`, `LineEditor`
  und einen Ringpuffer mit 256 umgebrochenen Ausgabezeilen. Eingabe und Verlauf
  bleiben je Fenster getrennt. PageUp/PageDown scrollen die Ausgabe, ein Cursor
  zeigt den Eingabepunkt. Bestehende Shell-, Datei-, Diagnose- und Powerbefehle bleiben.
- F4 wechselt Desktop/Textshell, Esc führt vom Desktop zur Textshell zurück.
  Geöffnete Fenster sowie Texteingaben bleiben beim Wechsel erhalten.
- Schließen entfernt das Fenster, beendet die Anwendung und gibt ihren Textpuffer frei.

## Ansätze

Ein eigener Compositor mit Puffern pro Fenster würde zusätzlichen Speicher und
Kopierlogik benötigen. Die vorhandene `PixelSurface` und vollständiges Neuzeichnen
bei Änderungen reichen für diese Etappe und werden weiterverwendet. Ein bloßes
Fensterdemo erfüllte den erweiterten Desktopauftrag nicht; die vorhandene Shell
wird deshalb direkt über `ITextOutput` an Terminalfenster angeschlossen.

## Grenzen und Nachweis

Keine neuen Pakete und kein Cosmos-Update. Dateioperationen bleiben synchron;
Prozessisolation, Größenänderung, Minimieren, Netzwerk und weitere Apps folgen
späteren Etappen. Hosttests prüfen Z-Ordnung, Fokus, Capture/Reset, Ränder,
Clipping, Puffergrenzen, Scrollen und unabhängige Sitzungen. Native Builds und
QEMU prüfen Boot, Bedienung, Fensterbewegung, Shellwechsel und Recovery.
