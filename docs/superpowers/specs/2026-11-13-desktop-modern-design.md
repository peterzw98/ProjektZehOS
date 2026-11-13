# ZehOS: moderne Desktopbasis und Anwendungen

Stand: 13. November 2026. Entwurf zur Freigabe, noch keine Implementierung.

## Ziel und gewählte Richtung

ZehOS soll einen alltagstauglicher wirkenden Desktop mit KDE/Cinnamon als Bedienvorbild erhalten. Diese Richtung hat der Nutzer gewählt. Zuerst wird die Desktopbasis überarbeitet, danach folgen grafischer Dateimanager und Rechner. Das vorhandene Terminal wird als reguläre Anwendung angeboten. ZehOS bleibt ein Cosmos-Kernel; Linux-Programme oder KDE selbst werden dadurch nicht ausführbar.

## Bestand und Wiederverwendung

Geprüft wurden ROADMAP.md, Desktop, WindowManager, Window, PixelSurface, DisplayService, InteractiveSession, IApplication, TerminalApp und FileService. Der aktuelle Desktop öffnet zwei Terminals, bietet einen Terminalstarter und unterstützt Fokus, Z-Reihenfolge, Ziehen und Schließen. Fenstergrößen sind unveränderlich. Anwendungen zeichnen in den gemeinsamen Bildpuffer und erhalten Eingaben für ihren Inhaltsbereich. Die Shell ist bereits von der Terminaldarstellung getrennt.

PixelSurface, Cosmos-Anzeige und -Schrift, IApplication, ShellService, LineEditor, Result-Typen und FileService werden weiterverwendet. Es wird zunächst kein zusätzliches GUI-Paket eingeführt und keine bestehende Werkzeugversion aktualisiert. Ein eigenständiger vollständiger GUI-Stack würde zunächst erst Cosmos-/NativeAOT-Anbindung benötigen; für diese Ausbaustufe reichen die vorhandenen Zeichen- und Eingabebausteine. Dies ist keine pauschale Kompatibilitätsaussage über externe Bibliotheken.

## Betrachtete Ansätze

1. Nur Farben und Hintergrund ändern: geringer Aufwand, löst aber fehlende Fensterfunktionen und Appbedienung nicht.
2. Vorhandene Fensterverwaltung zu einer konsistenten Desktopbasis ausbauen: empfohlen. Bestehende Shell und Grafik bleiben nutzbar, Änderungen können getrennt von Etappe 6 entstehen.
3. Externen Desktop beziehungsweise umfangreiches GUI-Framework portieren: eigener großer Kompatibilitätsauftrag; für das jetzige Ziel nicht erforderlich.

Die optische Richtung ist entschieden. Ansatz 2 und der nachfolgende Umfang sind der Vorschlag des Assistenten.

## Schritt A: zuerst die Desktopbasis

- Ruhiger Hintergrund, dunkle neutrale Flächen, klarer Akzent, konsistente Abstände und gut unterscheidbare aktive Fenster. Farben und einfache gezeichnete Symbole bleiben Implementierungsentscheidungen.
- Untere Leiste: links Programmmenü und Starter, mittig offene Fenster, rechts eine knappe Statusanzeige. Nur tatsächlich verfügbare Funktionen anzeigen, keine scheinbaren Netzwerk-/Audiofunktionen.
- Programmmenü listet installierte, fest einkompilierte Apps. Anfangs Terminal; weitere Apps erscheinen erst mit ihrer Implementierung. Eine Registrierung gemeinsamer Appinformationen dient Menü und Startern, kein externer Programmlader.
- Normaler Start mit freiem Desktop statt zwei automatisch geöffneten Terminals. Ein Klick im Menü startet ein Terminal mit eigener Shell und eigenem Arbeitsverzeichnis.
- Fenster schließen, minimieren, wiederherstellen, maximieren und an den Rändern in der Größe ändern. Maximieren lässt die Leiste sichtbar. Wiederherstellen bringt die vorherige Größe und Position zurück.
- Fensterliste aktiviert Fenster; ein Klick auf das aktive Fenster minimiert es, ein Klick auf ein minimiertes stellt es wieder her. Minimierte Apps behalten ihren Zustand und werden nicht geschlossen.
- Mindestgrößen je App, Arbeitsflächenbegrenzung und eindeutige Mausaufnahme verhindern verlorene Fenster und hängenbleibendes Ziehen. Fokuswechsel und Reset lösen gedrückte Zustände.
- Tastaturwechsel mit Alt+Tab; Tab navigiert gemeinsame Bedienelemente, Enter/Leertaste aktiviert passende Elemente. Tastenkombinationen werden zentral verarbeitet und nicht als Text in Apps eingeschleust.
- Wiederverwendbare Schaltfläche, Texteingabe und auswählbare, scrollbare Liste mit sichtbarem Fokus sowie kleinem Layoutmechanismus. Keine vollständige universelle UI-Bibliothek.
- Das Terminal passt sichtbare Zeilen und Textdarstellung an Größenänderungen an. Bestehender Inhalt, Verlauf und Eingabe bleiben erhalten; die begrenzte Ausgabe bleibt begrenzt. Die konkrete Umbruchstrategie wird vor ihrer Implementierung gegen TerminalBuffer geprüft.
- F4 und Esc sowie textueller Recovery-Start bleiben zunächst wie bisher erhalten. Esc schließt zuerst ein geöffnetes Desktopmenü; sonst bleibt der bestehende Rückweg zur Textshell bestehen. Fensterzustände überleben den Wechsel.

Skizze der Anordnung:

    +------------------------------------------------------------+
    | ZehOS                                                      |
    |             +-- Terminal ------------------ _ [] x --+     |
    |             | zehos>                                 |     |
    |             |                                        |     |
    |             +----------------------------------------+     |
    |                                                            |
    +------------------------------------------------------------+
    | Anwendungen | Terminal | [offene Fenster]       | Status    |
    +------------------------------------------------------------+

## Schritt B: Anwendungen nach stabiler Desktopbasis

Dateimanager: Seitenbereich mit erreichbaren Orten, Pfadfeld, Zurück/Vorwärts/Übergeordnet, Dateiliste mit unterscheidbaren Ordnern und Dateien, Auswahl und Scrollen. Ordner öffnen; Textdateien in einem begrenzten Textbetrachter öffnen. Fehlendes Laufwerk, fehlende Leserechte und fehlerhafte Dateien als verständliche Meldung im Fenster behandeln. Ein eigener Dateidienst pro App verhindert Änderungen am Arbeitsverzeichnis anderer Fenster.

Ein erster lesender Dateimanager deckt Roadmap 8.2 ab; ein vollständiger Explorer ist umfangreicher. Ordner anlegen, Umbenennen, Kopieren/Verschieben und Löschen sind gewünschte spätere Ausbauschritte, aber nicht implizit bereits implementiert. Sie brauchen passende zentrale Dienstoperationen, Konfliktbehandlung und bei Löschaktionen eine Bestätigung. Für diese Operationen nach dem lesenden Stand einen eigenen abgegrenzten Umsetzungsschritt festlegen.

Rechner: eigene App mit Ziffern, Dezimaltrennzeichen, Grundrechenarten, Vorzeichen, Löschen und Ergebnis. Maus und Tastatur, verständlicher Fehler bei Division durch null und Überlauf. Keine wissenschaftlichen Funktionen in der ersten Version.

Texteditor und Pong bleiben sichtbare offene Teile der bisherigen Etappe 8. Der Rechner ersetzt Pong nicht. Die ganze Etappe 8 gilt erst nach den ursprünglichen beziehungsweise ausdrücklich aktualisierten Abnahmekriterien als abgeschlossen.

## Abhängigkeiten von der laufenden Etappe 6

Die Aufgaben 6a–6c befinden sich laut Auftrag und Arbeitsstand in anderen Arbeitskopien in Arbeit. Keine davon wird hier abgeschlossen, umgeschrieben oder als bereits geprüft angenommen.

Desktopdarstellung, Fensteraktionen, gemeinsame UI, Terminaldarstellung und Rechner können weitgehend unabhängig entstehen. Die UI bleibt zunächst auf ihrer bestehenden Ereignisschleife; sie wird nicht ohne geprüfte Vorgaben auf mehrere Threads verteilt.

Der Dateimanager verwendet ausschließlich den zentralen Dateidienst. Die fertige 6c-Schnittstelle muss vor seiner produktiven Integration übernommen werden: aktuelle Identität, Benutzerverzeichnis und zentrale Rechteprüfung. Keine direkte Umgehung über System.IO im Appcode. Vorhandene Fehlerresultate werden verwendet. Der unabhängige Zwischenstand beansprucht keine Benutzerisolation.

Hintergrundoperationen und Fortschrittsanzeige benötigen die tatsächlich geprüften Dienste aus 6a/6b. Lange Dateioperationen bleiben bis dahin eine bekannte Einschränkung; blockierende Warteschlangenaufrufe gehören nicht auf den UI-Pfad. Neue Aufgaben erstellen Ergebnisdaten und verändern keine Fenster-/UI-Zustände nebenläufig.

Berührungspunkte sind vor allem Kernel-Verdrahtung, InteractiveSession, Shell-/Dateidienst-Erzeugung, Tests und gemeinsame Dokumentation. Umsetzung auf eigener Arbeitskopie und codex/-Branch vom passenden aktuellen master; vor Integration die fertige Etappe 6 erneut lesen und Konflikte fachlich lösen. Keine fremden Änderungen übernehmen oder zurücksetzen.

## Prüfbare Abnahme

A: Normaler Boot zeigt den neuen Desktop. Menü startet mehrere unabhängige Terminals. Fensterfokus, Minimieren/Wiederherstellen, Maximieren/Wiederherstellen, Größenänderung, Fensterliste und Alt+Tab funktionieren. Kein Fenster verdeckt dauerhaft die Leiste. Menü und UI haben sichtbaren Fokus. F4/Esc/Recovery funktionieren und erhalten Zustand. Wiederholtes Öffnen und Schließen hält die Ressourcen begrenzt.

B: Dateimanager navigiert Ordner, zeigt eine Textdatei und behandelt fehlendes Laufwerk sowie abgelehnten Zugriff ohne Absturz. Zwei Dateimanager und Terminals verändern die Arbeitsverzeichnisse anderer Fenster nicht. Rechner ist mit Maus und Tastatur verwendbar und behandelt Fehler. Nach Übernahme von 6c zusätzlich dieselben Zugriffe mit zwei Identitäten und verschiedenen Rechten prüfen.

Hosttests mit Warnungen als Fehler prüfen Fensterzustände und Geometrie, Fokus/Reset/Capture, Tastaturnavigation, Appzustand, Rechner und Dateimanagerfehlerfälle. Native Normal- und Recovery-Builds sowie QEMU prüfen die echten Abläufe. VMware separat prüfen und fehlende Prüfungen ausdrücklich offenhalten; echte Hardware nur nach tatsächlichem Test als geprüft melden. Dateischreibtests ausschließlich mit isoliertem Datenimage.

README bei Bedienänderungen aktualisieren. Roadmap um den vorgeschalteten Desktop-Umbau und Rechner ergänzen, bestehende offene Punkte erhalten. Keine Etappe ohne erfüllte Abnahme abhaken. Vor PR unabhängiges Review, danach Commit/Push/PR gemäß Projektregeln; kein Merge.

## Aktueller Status

Nur Bestandsaufnahme und Entwurf. Kein Produktcode geändert, keine neuen Tests oder Gaststarts ausgeführt. Vor der Umsetzung ist die Freigabe dieses Entwurfs noch offen.
