# Desktop-Umbau und Integration von Etappe 8

Stand: 13. November 2026. Eigener Branch `codex/desktop-modern`, integrierter master: `1578eec` (PR 16).

## Ergebnis und Abgrenzung

Die parallel entstandenen Dateibetrachter-, Texteditor- und Pong-Anwendungen aus master bleiben erhalten. Das KDE/Cinnamon-orientierte Redesign ergänzt freien Desktopstart, Programmmenü mit Suche, Terminalstarter, stabile Fensterleiste, Alt+Tab/Alt+Shift+Tab, Minimieren, Maximieren/Wiederherstellen und Größenänderung an Rändern und Ecken. Menü und Apps verwenden gemeinsam `UiPanel`, `UiTextBox`, `UiList`, `UiButton` und `UiPainter`; die separate UI-Implementierung des ersten Redesignstands wurde entfernt.

Schließrückfragen und asynchron angeforderte Fensterabschlüsse des Texteditors bleiben über `RequestClose`/`IsCloseRequested` erhalten. `IApplication.Resize` besitzt eine leere Standardimplementierung; Terminal, Editor und Dateibetrachter passen Inhalt beziehungsweise Layout sofort bei Geometrieänderungen an. Pong erhält eine Mindestgröße, die das feste Spielfeld vollständig aufnimmt. Das Fensterlimit zählt minimierte Fenster mit. Die Fensterleiste behält ihre Reihenfolge unabhängig von der Z-Ordnung.

Der Terminalpuffer merkt sich die Zusammengehörigkeit umgebrochener Segmente. Nur bei einer Breitenänderung werden die begrenzten gespeicherten Inhalte neu umgebrochen. Bei mehr als 256 Zeilen wird weiterhin älteste Ausgabe verworfen; späteres Vergrößern stellt bereits verworfene Ausgabe nicht wieder her.

Die bestehenden Cosmos-Schriften und der gemeinsame Bildpuffer werden wiederverwendet. Menü und Leiste berücksichtigen die reale 16×32-Schrift des Gasts; schmale Fenstereinträge zeigen kurze Kennungen. Frame-/GC-Messung und Spielanimation aus master bleiben vorhanden. F4/Esc und Recovery bleiben erreichbar; Esc schließt zuerst ein offenes Menü.

Keine Paket-/SDK-/Cosmos-Aktualisierung. Keine Änderungen an Scheduler, Nachrichtenwarteschlangen, Konten oder Dateirechten. Neue Apps verwenden weiterhin die zentralen Dateidienste. Rechner und zusätzliche Explorer-Operationen sind nicht Teil dieses Integrationsschritts.

## Prüfung

Die zusammengeführten Hosttests einschließlich ursprünglicher Etappe-8-Tests und neuer Fenster-/Menü-/Terminalregressionen werden mit `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` ausgeführt. Die Storage-Testvorbereitung öffnet ihre zwei Terminals nun ausdrücklich; der produktive Desktop startet leer.

Am abschließenden Stand ausgeführt:

- 186 Hosttests in Release mit Warnungen als Fehler bestanden. Die beiden Reviewbefunde (Alt während Maus-/Tastenfreigabe sowie Pong unter geöffnetem Menü) wurden jeweils zuerst durch fehlgeschlagene Regressionstests reproduziert, danach korrigiert. Ein erneutes unabhängiges Review meldete keine weiteren relevanten Befunde.
- Native normale und Recovery-Release-Builds mit .NET SDK 10.0.403, Cosmos 3.0.94 und Warnungen als Fehler: jeweils 0 Warnungen/0 Fehler. Der normale Stand wurde zusätzlich zuvor mit `cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release` gebaut; finale Gaststarts verwenden getrennt gesicherte Normal-/Recovery-ISOs des korrigierten Stands.
- QEMU 10.2.2, q35, CPU max, 512 MiB, Standard-VGA, ohne Datenlaufwerk: leerer Start, Menü mit allen vier Apps und echter 16×32-Schrift, Suche und Start, Editorentwurf, Schließrückfrage und Abbrechen ohne Entwurfsverlust, Minimieren/Wiederherstellen, Maximieren und Rückkehr zur ursprünglichen Geometrie, Größenänderung und anschließende Terminaleingabe geprüft. Pong zeichnet Bewegung zwischen zwei Bildern; danach Terminalbefehl, Esc zur Textshell, Befehlseingabe und F4 zurück bei erhaltenen Fenstern. Datei-App ohne Laufwerk zeigt kontrollierten Fehler.
- Recovery-QEMU: ausdrücklich angeforderter Textshellstart, `echo recovery-ok`, F4 zum freien Desktop, Menü, Terminalstart und `echo recovery-terminal-ok`; anschließend zur Textshell und Shutdown. Nur eigene plattenlose Test-VMs gestartet/beendet. Keine vorhandenen Datenimages verwendet oder verändert.
- VMware Player mit separater plattenloser UEFI-VM: finaler normaler Kernel startet, seriell `Desktop ist bereit` bei 982 ms. Interaktive Anzeige/Eingabe bleiben offen: `vmrun captureScreen` wurde mit fehlendem Gastlogin abgewiesen; die im Hintergrund gestartete Test-VM bot kein erfassbares UI-Fenster. Kein Login-/Tools- oder VM-Sicherheitswechsel vorgenommen. Der serielle Bootnachweis belegt keine vollständige VMware-Bedienkompatibilität.

Offen: interaktive VMware-Prüfung, echte Hardware und erneute Dateipersistenz-/Vollplatten-Gasttests des Redesigns. Die ursprünglichen Nachweise aus Etappe 8 bleiben als historische Ergebnisse erhalten. Dieses Redesign erweitert keine Dateioperationen und beansprucht keinen neuen Gastnachweis für Stromausfallsicherheit oder Benutzerisolation.
