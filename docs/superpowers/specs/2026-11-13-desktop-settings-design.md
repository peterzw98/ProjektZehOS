# Systemeinstellungen und moderner KDE-orientierter Desktop

Stand: 13. November 2026. Entwurf zur Freigabe, noch keine implementierten Funktionen.

## Auftrag und Ausgangspunkt

Der Desktop soll professioneller und wie eine zusammenhängende moderne Desktopumgebung wirken. Der Nutzer hat KDE als Hauptvorbild gewählt: klassische Taskleiste und anpassbares Erscheinungsbild. Gewünscht sind eine Einstellungsapp, besonders Anzeigeeinstellungen mit Full HD, Hell-/Dunkelmodus und auswählbares Hintergrundbild. Programme im Startmenü sollen sich per Doppelklick starten lassen.

Arbeitsbranch: `codex/desktop-settings`, direkt von `codex/desktop-modern` bei `358bf75` abgezweigt. Die getrennte FPS-Korrektur auf `codex/desktop-fps` gehört nicht zur gewählten Basis. Bei der Umsetzung deren gemeinsame Ereignisschleife vergleichen und die notwendige Korrektur gezielt erhalten, damit das neue Erscheinungsbild nicht den bekannten ruckelnden Mauszeiger übernimmt. Andere Etappen und Arbeitskopien bleiben außerhalb des Auftrags.

Vorhanden sind Fensterverwaltung, Größenänderung, Minimieren/Maximieren, Alt+Tab, Menüsuche, gemeinsame UI-Steuerelemente sowie Terminal, Dateien, Texteditor und Pong. `DisplayService` akzeptiert bisher nur 1024×768×32. UI-Farben sind feste Konstanten, die Schrift ist ein Bitmapfont. Die installierte Cosmos-Schnittstelle bietet `Canvas.AvailableModes` und `Canvas.GetFullScreen(Mode)`; die tatsächliche Modusänderung hängt vom Displaytreiber ab. Ein API-Aufruf allein beweist keinen Auflösungswechsel.

## Bewertete Ansätze

1. **Vorhandenen Desktop und gemeinsame UI weiterentwickeln — gewählte technische Richtung.** Vorhandene Apps und Fensterzustände bleiben nutzbar; Farben, Typografie, Abstände, Symbole und Bedienung werden zusammenhängend überarbeitet. Das entspricht der gewählten KDE-Richtung und begrenzt Kernelrisiken.
2. **Windows-11-orientierter Desktop mit zentrierten Startern.** Vom Nutzer zugunsten der klassischen KDE-Richtung verworfen. Vertraute Fensterbedienung bleibt dennoch sinnvoll.
3. **Vollständiger Austausch durch ein fremdes Desktopframework.** Nicht vorgesehen: Die vorhandene Cosmos-/NativeAOT-Anbindung lässt sich wiederverwenden; ein normales C#-Desktopframework ist kein nachgewiesener Ersatz im Kernel. Keine neuen Pakete oder Werkzeugupdates für diesen Auftrag.

## Sichtbares Ergebnis

- Ruhiger Desktop ohne ständig sichtbare Entwicklerhinweise oder Speicherstatistik. Diagnose bleibt über einen ausdrücklichen Diagnoseweg erreichbar.
- Durchgängiger heller beziehungsweise dunkler Stil für Taskleiste, Menü, Fensterrahmen, Einstellungen und gemeinsame App-Steuerelemente. Terminaltext und Editorinhalt bleiben gut lesbar.
- Klassische Taskleiste unten: Anwendungsmenü links, angeheftete Starter, stabile Liste laufender Fenster und rechts Uhr/Systembereich. Klare Symbole mit Text beziehungsweise Beschriftung; aktive und minimierte Fenster sind unterscheidbar.
- Überarbeitetes Startmenü mit Suche und gut treffbaren Programmeinträgen. Ein Klick markiert, Doppelklick auf denselben Eintrag startet genau einmal. Enter startet die Auswahl. Doppelklick auf verschiedene Einträge oder nach zu langer Pause startet nichts versehentlich. Vorhandener Startknopf bleibt als eindeutige Alternative.
- Fenster erhalten einheitliche Abstände, dezente Rahmen/Schatten und klare Schaltflächen für Minimieren, Maximieren und Schließen. Keine aufwendige Echtzeitunschärfe, die Full-HD-Zeichnen unnötig verteuert.

```text
┌ Einstellungen ──────────────────────────────── ─ □ × ┐
│ Anzeige          │ Anzeige                           │
│ Personalisierung │ Auflösung     [1920 × 1080    v]   │
│                  │ Aktuell       1920 × 1080          │
│                  │               [Übernehmen]         │
│                  │                                    │
│                  │ Änderungen bestätigen oder         │
│                  │ automatisch zurückkehren            │
└──────────────────────────────────────────────────────┘
┌ Menü │ Dateien │ Einstellungen │ laufende Fenster │ Uhr ┐
└────────────────────────────────────────────────────────┘
```

Die Skizze beschreibt die Anordnung, nicht die endgültige Pixelgestaltung. Eine kleinere, besser proportionierte Schrift ist gegenüber der bisherigen groben 16×32-Darstellung zu prüfen; bestehende Schriftressourcen und Cosmos-Funktionen zuerst bewerten. Keine unscharfe Verkleinerung als Ersatz für lesbare Schrift.

## Einstellungsapp

Ein über Menü und Taskleiste erreichbares Fenster mit Seitenleiste. Zunächst zwei vollständig bedienbare Bereiche, keine leeren Kategorien oder wirkungslosen Schalter:

- **Anzeige:** tatsächliche Auflösung und unterstützte Modi, ausdrücklich 1920×1080 bei erfolgreichem Treibernachweis. Änderungen mit Übernehmen, Bestätigungsfrist und automatischer Rückkehr zur letzten funktionierenden Auflösung. Fenstergeometrie, Maximieren, Mausgrenzen und Textshell müssen zur neuen Größe passen. App- und Terminalzustände bleiben erhalten. Die Anzeige einer angeforderten Auflösung darf nie als tatsächlich aktive Auflösung ausgegeben werden.
- **Personalisierung:** Hell-/Dunkelmodus, Akzentfarbe aus einer kleinen Palette, Hintergrundauswahl mit Vorschau. Mitgelieferte Hintergrundbilder und ein eigenes Bild über Dateipfad. Für eigene Bilder zunächst ein klar benanntes, tatsächlich unterstütztes Bitmapformat mit Größen-/Speichergrenzen; die konkrete Decoderwahl trifft die Umsetzung nach Wiederverwendungsprüfung. Nicht unterstützte oder beschädigte Dateien erzeugen einen verständlichen Fehler und erhalten den bisherigen Hintergrund. Bilder werden beim Wechsel geladen und vorbereitet, nicht pro Bild neu dekodiert.

Eigene technische Festlegung: Standard ist ein ruhiges dunkles KDE-orientiertes Erscheinungsbild; der helle Modus ist gleichwertig nutzbar. Die Einstellungen wirken sofort, außer einer Auflösung, die der Treiber nur beim Booten setzen kann. In diesem Fall muss die Oberfläche den Neustartbedarf wahrheitsgemäß erklären. Full HD bleibt ein Abnahmekriterium: Wenn der vorhandene Treiber keinen Laufzeitwechsel unterstützt, zuerst den Full-HD-Bootweg nachweisen und dessen sichere Auswahl lösen; keine bloß dekorative Auswahlliste ausliefern.

## Speichern und Fehlerverhalten

Personalisierung und bestätigte Anzeigeauswahl werden auf einem eingebundenen Datenlaufwerk über vorhandene Dateidienste gespeichert. Ohne Laufwerk funktionieren Änderungen für die Sitzung; die App zeigt deutlich, dass sie nicht dauerhaft gespeichert sind. Fehler beim Speichern werden angezeigt und nicht als Erfolg behandelt. Keine automatische Plattenformatierung und kein ungefragtes Erstellen oder Ersetzen von Nutzerdatenimages.

Beim Laden gelten validierte Werte und sichere Standardwerte. Eine fehlende, beschädigte oder künftig unbekannte Einstellung darf den Desktopstart nicht verhindern. Eine fehlende Hintergrunddatei führt zu einem mitgelieferten Hintergrund. Bestehende Ersetzungs-/Wiederherstellungslogik der Dateidienste bleibt erhalten; daraus folgt keine Stromausfallgarantie.

## Betroffene Bausteine und Grenzen

`Graphics/Desktop`, `Window`, `WindowManager`, `DisplayService`, gemeinsame UI, die neue Einstellungsapp, Anzeige-/Mausabmessungen in `Runtime` und `Input`, die bestehende Dateianbindung, Hosttests sowie README und Cosmos-Nachweise. Neue plattformunabhängige Quellen werden im Hosttestprojekt eingebunden. Recovery, F4/Esc, unabhängige Terminals, Schließrückfragen und bestehende Apps bleiben erhalten.

Kein Port von KDE oder Windows selbst, keine allgemeine Pluginarchitektur, kein neuer Scheduler, keine Konten-/Rechteverwaltung und keine wirkungslosen Netzwerk-/Audiomenüs. Mehrere Monitore, freie UI-Skalierung und Hardwarebeschleunigung werden durch diesen Auftrag nicht zugesagt. Die Oberfläche soll bis Full HD sauber angeordnet sein; reale Hardware und VMware werden nur im Umfang tatsächlich ausgeführter Prüfungen als unterstützt bezeichnet.

## Abnahme und Nachweise

1. Hosttests mit Warnungen als Fehler: Modusvalidierung, Bestätigung/Timeout/Rücknahme, Geometrie und Mausgrenzen, Themewechsel, Einstellungen speichern/laden und Fehlerfälle, Hintergrundvalidierung sowie Doppelklickzeit/-ziel und Tastaturstart.
2. Nativer Normal- und Recovery-Build mit unveränderten Werkzeugversionen. Vorhandene Builds oder frühere Testergebnisse zählen nicht als neue Nachweise.
3. QEMU: sichtbarer 1920×1080-Desktop; Einstellungen öffnen; hell/dunkel und Hintergrund wechseln; verfügbare Auflösung übernehmen/bestätigen/zurücknehmen; Startmenü per Doppelklick und Enter; Fensterbedienung, Terminal, Editor-Schließrückfrage, Pong, F4/Esc und Recovery prüfen.
4. Persistenz und Bilddateien im Gast nur mit isolierter Kopie des Datenimages prüfen. Neustart, beschädigte Konfiguration, fehlendes Laufwerk und ungültiges Bild dürfen die Bedienbarkeit nicht verlieren lassen.
5. Bildrate bei Full HD und Mausbewegung messen und mit dem vorhandenen FPS-Stand vergleichen. Keine garantierten 60 FPS allein aus einer Fristberechnung ableiten.
6. VMware: betroffenen Anzeige-/Eingabepfad prüfen. Fehlende interaktive oder Hardwareprüfungen ausdrücklich offen ausweisen.
7. Unabhängiges Review, nötige Korrekturen und erneute betroffene Prüfungen; Commit, Push und PR gegen die passende Desktopbasis. Kein Merge ohne eigenen Auftrag.

## Freigabestand

KDE-Richtung und Doppelklickstart sind vom Nutzer festgelegt. Die übrige konkrete Ausgestaltung ist dieser Vorschlag. Implementierung nach Freigabe dieses Entwurfs oder ausdrücklichem Auftrag, ohne weitere Entwurfsfragen direkt umzusetzen.
