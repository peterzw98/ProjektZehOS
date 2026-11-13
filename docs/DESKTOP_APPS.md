# Desktopanwendungen: Etappe 8

Stand: 13. November 2026. Etappen 8.1 bis 8.5 sind implementiert. Die direkten Voraussetzungen bauen auf dem vorhandenen Desktop, der Ereigniseingabe und dem Dateidienst einschließlich der Fehlerfallprüfung aus 7.5 auf. Die parallel bearbeiteten Etappen 6a bis 6c werden nicht erweitert.

## Bedienung

Der Starter öffnet **Dateien**, **Editor** und **Pong** neben den bisherigen Terminals. Das gemeinsame Limit bleibt acht Fenster. Die Anwendungen besitzen feste Fenstergrößen; Prozessisolation, Größenänderung und Hintergrund-Dateizugriffe gehören nicht zu dieser Etappe.

Gemeinsame Bedienelemente:

- Mit Tab beziehungsweise Shift+Tab den Fokus wechseln. Der Rahmen zeigt den aktiven Fokus; Mausaktivierung funktioniert ebenfalls.
- Eine Schaltfläche mit Enter oder Leertaste auslösen. Loslassen außerhalb der Schaltfläche bricht einen Mausklick ab.
- In Textfeldern bearbeiten Pfeile, Home/End, Backspace und Delete den Text. Strg+A markiert den gesamten Inhalt; Tippen ersetzt ihn. Mehrzeilige Felder nehmen mit Enter eine neue Zeile auf.
- Listen mit Pfeilen, Home/End und PageUp/PageDown auswählen. Enter öffnet den ausgewählten Eintrag. Eine Vorschau erhält durch Anklicken den Fokus und lässt sich mit PageUp/PageDown scrollen.
- Esc/F4 wechselt weiterhin zwischen Desktop und Textshell und erhält die Fenster. Ein Fokuswechsel löst gehaltene Spieltasten und laufende Mausklicks zurück.

**Dateien:** Das obere Feld enthält den Ordnerpfad; Enter lädt ihn. Unterordner sind mit `/` markiert. „Oeffnen“ zeigt den gewählten Text rechts an oder wechselt in einen Ordner. „Eltern“ wechselt zum übergeordneten Ordner, F5 lädt die Liste neu. „Bearbeiten“ öffnet die ausgewählte Textdatei im Editor. Ordner und Dateien werden getrennt und innerhalb der Gruppen ordinal sortiert. Fehlende Laufwerke oder Dateien erscheinen als Meldung; weitere Eingaben bleiben möglich.

**Editor:** Den Dateipfad oben eintragen und mit „Oeffnen“ oder F3 laden. Das große Textfeld unterstützt mehrere Zeilen und sichtbaren Umbruch. Mit „Speichern“ oder F2 wird über den bestehenden Dateidienst gespeichert; das Limit bleibt 65.536 Zeichen einschließlich der gespeicherten Zeilenenden. CRLF-Dateien behalten beim Speichern CRLF; intern werden Zeilenumbrüche vereinheitlicht. Der eingebettete Bitmapfont bleibt auf seinen bisherigen Zeichenvorrat beschränkt.

Ein Stern kennzeichnet ungespeicherte Änderungen. Beim Schließen stehen „Zurueck“, „Verwerfen“ und „Speichern“ zur Auswahl. Speichern schließt erst nach Erfolg; ein Schreibfehler lässt Dialog und Entwurf erhalten. „Neu“ und „Oeffnen“ ersetzen keinen ungespeicherten Entwurf: zuerst speichern oder das Fenster mit der Rückfrage schließen. Der neue Standardpfad ist `/data/neu.txt` und kann vor dem Speichern geändert werden. Die Funktionen bieten keine Undo-/Redo-, Auswahlbereichs- oder Zwischenablagenverwaltung.

**Pong:** W/S oder Pfeil auf/ab bewegen den linken Schläger; rechts spielt ein einfacher Computergegner. Leertaste pausiert beziehungsweise setzt fort. Bei inaktivem Fenster pausiert die Simulation und zeigt einen Hinweis. Das Reaktivieren setzt die gehaltenen Tasten nicht wieder ein. Der Ball bewegt sich zeitabhängig in kleinen begrenzten Schritten; ein langsames Bild oder die Rückkehr aus der Textshell führt nicht zu einem großen Sprung. Das Fenster lässt sich wie die anderen Anwendungen bewegen und schließen.

## Direkte technische Voraussetzungen und Wiederverwendung

`TextBuffer` enthält die gemeinsame begrenzte Textbearbeitung. `LineEditor` behält seine bisherige Befehlsübernahme, Verlaufssuche und Entwurfswiederherstellung und verwendet den Baustein für Einfügen, Löschen und Cursorbewegung. Vorhandene Regressionstests bleiben erhalten.

`UI/` stellt Schaltfläche, ein-/mehrzeiliges Textfeld, Liste, Fokussteuerung und eine einfache Dreispalten-Anordnung bereit. Gezeichnet wird direkt über den bestehenden `PixelSurface` und den Cosmos-Bitmapfont. Verschachtelte Zeichenbereiche werden geschnitten und anschließend wiederhergestellt. Diese vorhandenen Projektbausteine decken die benötigte Plattformanbindung ab; es wird kein zusätzliches UI-Paket eingeführt.

`FileService.ListDirectory` ergänzt Metadaten für den Betrachter und verwendet den bisherigen Ergebnis-/Fehlerweg. Dateioperationen selbst bleiben synchron und im Vordergrund; die Oberfläche behauptet keine geprüfte parallele Cosmos-Dateianbindung. Ein neuer Dateidienst pro Dateianwendung hält ihr Arbeitsverzeichnis unabhängig von den Terminals.

`IApplication.RequestClose` erlaubt eine Rückfrage; ein bestätigter Schließwunsch wird in der bestehenden Ereignisschleife abgeschlossen. Der Fensterverwalter entfernt Zeiger-Capture, aktive Referenz und Anwendung. Editor und Betrachter leeren ihre Text-/Listenzustände; Datei-Handles werden weiterhin innerhalb der einzelnen Operation geschlossen. Pong besitzt keinen eigenen großen Bildpuffer. Eine laufende aktive Spielinstanz fordert regelmäßig ein Bild an; es wird kein eigener Thread oder Scheduler eingeführt.

## Bildzeit und Speicher

In der Leiste steht das Maximum der **Zeichen- und Displaydauer** aus dem letzten Messfenster von ungefähr einer Sekunde in Millisekunden. Dies ist weder eine vollständige Ereignisschleifenzeit noch eine garantierte Bildrate. `GC:…K` zeigt den Heapstand der **letzten GC** in KiB. Vor der ersten Sammlung kann dort null stehen. Oben steht der kumulative Allokationszähler; dieser sinkt beim Schließen definitionsgemäß nicht und ist kein Maß für aktuell lebende Anwendungsobjekte.

Die Diagnose nutzt `Stopwatch`, `GC.GetGCMemoryInfo` und `GC.GetTotalAllocatedBytes(false)`. `GC.GetTotalMemory(false)` wurde verworfen: Der native Link in Cosmos 3.0.94 scheitert dabei am fehlenden `RhWaitForPendingFinalizers`. Die GC-Daten entsprechen dem [Quellstand des verwendeten Pakets](https://github.com/CosmosOS/Cosmos/blob/5d0b9535251472db7ecfe012f0435f0a04ffca16/src/Cosmos.Kernel.Core/Runtime/GC.cs), insbesondere dem Stand der letzten Sammlung.

Die bestehenden Cosmos-/`PixelSurface`-Bildpuffer werden wiederverwendet. Textumbruch und Textkopien werden bei Änderungen beziehungsweise geänderter Breite erneuert, nicht für jedes unveränderte Bild. Ein Hosttest prüft nach Aufwärmen 100 unveränderte Editor-/Pong-Zeichnungen mit null zusätzlichen Allokationsbytes. Ein weiterer Test hält den Fensterverwalter am Leben und belegt, dass eine geschlossene Anwendung trotzdem eingesammelt werden kann. Diese Hostnachweise ersetzen keine Aussage über das vollständige Cosmos-GC-Verhalten.

## Tatsächlich ausgeführte Prüfungen

Referenz: .NET-SDK 10.0.112, Cosmos 3.0.94, x64, QEMU 10.2.2 mit `q35`, CPU `max`, 512 MiB RAM, Standard-VGA, PS/2-Eingabe. Testplatten sind ausschließlich isolierte Kopien der eigenen 64-MiB-Testplatte aus 7.5. Keine Paket-/SDK-Aktualisierung und kein Zugriff auf das normale Datenimage.

- **173 Hosttests** in Release mit Warnungen als Fehler: Shell-/Eingaberegressionen, Fokus/Capture, Auswahl und Clipping, Mehrzeilentext, Dateimetadaten, Speichern/erneutes Öffnen, CRLF-Erhalt, Schließen mit ungespeicherten Änderungen einschließlich Fehlerfall, Spieltakt/Begrenzung/Pause und Ressourcenprüfungen.
- Normaler nativer Release- und getrennter Recovery-Build erfolgreich; Recovery auch mit Warnungen als Fehler. Der bestehende Zugriff auf die geprüfte Core-Interruptmaske hat eine lokale `NAOT0007`-Ausnahme erhalten, damit diese bereits dokumentierte Anbindung nicht als neue unzulässige Schichtabhängigkeit behandelt wird. Der `using`-Schutz bleibt erhalten.
- QEMU: Im Editor mehrzeiligen Text bearbeitet und gespeichert, im Dateibetrachter angezeigt und von dort wieder im Editor geöffnet. Rückfrage abgebrochen, Entwurf erhalten, danach „Speichern“ im Schließdialog erfolgreich ausgeführt. Nach Reboot denselben Inhalt mit `cat` gelesen. Die abschließende Umbruchkorrektur wurde zusätzlich mit Pfeil auf, Einfügen und Datei-Readback am finalen ISO geprüft.
- QEMU: Pong mit W/S gesteuert, Terminal aktiviert und einen Befehl ausgeführt, Spiel wieder aktiviert. Über zwei Minuten aktives Spiel mit Bewegung und Punkten; danach sechs weitere Instanzen geöffnet/geschlossen und Terminal weiter bedient. In den Spiel-Screenshots etwa 8 ms Rendermaximum; kumulativ allokiert 3.310 bis 3.453 KiB während dieses Laufs. Der letzte-GC-Wert blieb dabei null. Das ist eine Beobachtung des Referenzprofils, kein allgemeiner Leistungsvergleich und kein Nachweis eines speicherleckfreien gesamten Kernels.
- QEMU: Voller Datenträger meldet ENOSPC beim Speichern; „Speichern“ im Schließdialog bleibt bei Fehler geöffnet und erhält den Entwurf. Ohne Datenplatte starten Betrachter und Editor mit kontrollierten Fehlermeldungen. Esc/F4 und anschließende Textshell-Eingabe bleiben bedienbar.
- Recovery ohne Datenplatte: sichtbarer Start zur Textshell mit eigener Kennzeichnung, Befehlseingabe, F4 zum Desktop, Pong-Start und Rückkehr zur Shell geprüft.

**Offen:** VMware und echte Hardware konnten in dieser Umgebung nicht geprüft werden; `vmrun`/VMware sind nicht installiert. Das bestehende VMware-Profil besitzt weiterhin kein Datenlaufwerk. Es wird weder VMware-Kompatibilität noch Sicherheit bei Stromausfall behauptet. Die Nebenläufigkeit und Rechteverwaltung aus 6a–6c bleiben eigene Arbeiten.

Wiederholen: Hosttests wie in `AGENTS.md`; normal und mit `-p:ZehOSRecovery=true` bauen und getrennte ISO-Dateien verwenden. Für Datei-/Fehlerfallprüfungen die [isolierten Testkopien aus 7.5](DESKTOP_STORAGE_TESTS.md) verwenden. Die Bedienfolge oben einschließlich Rückfrage, Neustart, vollem Laufwerk, Fokuswechsel und wiederholtem Spielstart prüfen. Mindestens zwei Minuten spielen und die Diagnosewerte zusammen mit dem verwendeten VM-Profil festhalten.
