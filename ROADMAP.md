# Entwicklungsplan für ZehOS

ZehOS soll einen bedienbaren Desktop mit Fenstern, einer Shell im Terminal, einem einfachen Browser und spielbarem Doom bekommen. Der Weg führt über kleine, einzeln prüfbare Funktionen. Jede Etappe soll einen startbaren Stand hinterlassen, auf dem die nächste aufbaut.

Die empfohlene Reihenfolge lautet: **Grundlage prüfen → Textshell → Ereignisschleife und Eingabe → Grafik → Fenster → Terminal im Desktop → Multitasking und Scheduling → Nachrichten und Synchronisation → Benutzer und Dateirechte → erste Anwendungen → Doom → Netzwerk → einfacher Browser → Unterstützung moderner Webseiten.** Die bereits vorgezogene Dateisystemetappe 7 liefert die Grundlage für Benutzerverzeichnisse und Dateirechte. Die drei neuen Etappen heißen 6a bis 6c, damit bestehende Etappennummern und Verweise erhalten bleiben.

## Ausgangspunkt am 28. Oktober 2026

Im Repository steht bisher ein kleiner C#-Kernel auf Basis von Cosmos Gen3 3.0.94, .NET 10 und x64. `ProjektZeh/Kernel.cs` gibt eine Startmeldung aus, liest mit `Console.ReadLine()` eine Zeile und gibt sie wieder aus. Eine eigene Shell, ein Fenstersystem und Anwendungen sind noch zu entwickeln.

Build und Start über QEMU sowie ein VMware-Startskript sind dokumentiert. Das VMware-Skript erzeugt derzeit eine VM ohne Festplatte und mit deaktiviertem Sound. Für dauerhafte Dateien muss später ein virtuelles Datenlaufwerk dazukommen.

**Aktueller Stand am 12. November 2026:** Der normale Boot öffnet einen Desktop mit zwei unabhängigen Terminalfenstern. Fenster lassen sich aktivieren, verschieben und schließen; ein Starter öffnet weitere Terminals. Textshell-Recovery, Diagnose sowie die bereits vorhandenen Datei- und Energiekommandos bleiben erhalten. Die folgenden datierten Abschnitte dokumentieren die einzelnen Entwicklungsstände.

Cosmos ist die technische Grundlage: vorhandene Treiber und Systemdienste zuerst verwenden und nur bei einer konkreten Lücke erweitern. Die aktuelle Gen3-Dokumentation beschreibt Grafik, Eingabe, Speicherverwaltung, Scheduling, Dateisystem und Netzwerk. Ob die jeweils benötigte Funktion bereits mit **3.0.94** und der gewählten VM funktioniert, wird vor ihrer Verwendung geprüft. Die Online-Dokumentation kann einen neueren Entwicklungsstand zeigen.

Erledigte Aufgaben sind angekreuzt; offene Kontrollkästchen kennzeichnen geplante Arbeit. Die genannten Klassen und Schnittstellen sind Vorschläge für eigenen ZehOS-Code, keine zugesicherten Cosmos-APIs.

## Zielumfang

Der erste vollständige Zielstand ist erreicht, wenn ZehOS in der VM zum Desktop startet, ein Terminal Befehle annimmt, Dateien einen Neustart überstehen, Doom mit Tastatursteuerung läuft und ein Browser einfache verlinkte HTML-Seiten über HTTP laden und anzeigen kann. Lokale HTML-Dateien sind ein früheres Zwischenziel.

Vor den weiteren Desktopanwendungen kommen drei Lernziele hinzu: Hintergrundaufgaben mit nachvollziehbarem Scheduling, begrenzte Nachrichtenwarteschlangen mit Synchronisation sowie Benutzerkonten mit zentral geprüften Dateirechten. Jede Erweiterung erhält eine eigene Vorführung und Fehlerfallprüfungen. Threadkommunikation und Dateirechte im gemeinsamen Kernel sind dabei von isolierten Prozessen und hardwaregestütztem Speicherschutz zu unterscheiden.

Ein Browser für heutige komplexe Webseiten ist ein weiteres großes Entwicklungsziel. Dafür kommen unter anderem HTTPS, Zertifikate, umfangreiche HTML- und CSS-Unterstützung, JavaScript sowie Schutz vor fehlerhaften und fremden Inhalten hinzu. Diese Anforderungen sollen den ersten benutzbaren Desktop nicht blockieren.

## 0 Technische Grundlage und offene Fragen prüfen

**Zweck:** Früh herausfinden, welche Cosmos-Funktionen wir übernehmen können und wo größere Portierungsarbeit wartet.

- [ ] 0.1 Einen unveränderten Kernel mit der festgelegten Version bauen und in QEMU starten. ISO-Pfad, Startbefehl und einen sichtbaren erfolgreichen Boot festhalten.
- [ ] 0.2 QEMU als erste Referenzumgebung festlegen. Virtuelle Grafik, Eingabegeräte und später Netzwerk und Datenlaufwerk konkret dokumentieren. VMware nach wesentlichen Etappen zusätzlich prüfen.
- [ ] 0.3 In kleinen, getrennten Versuchen Grafikfläche, nicht blockierende Tastaturabfrage, Maus, monotone Zeitmessung, Dateisystem und Netzwerkkarte prüfen. Pro Versuch festhalten: funktioniert, fehlt oder benötigt eine bestimmte VM-Konfiguration. Noch keine vollständigen Dienste bauen.
- [x] 0.4 Den möglichen Doom-Port kurz untersuchen: Welche Teile sind reine Spiel- und Renderlogik, welche benötigen externe Bibliotheken oder Betriebssystemfunktionen? Ein kleines Buildexperiment soll die größten Abhängigkeiten sichtbar machen.
- [ ] 0.5 Ergebnisse in `docs/COSMOS_SUPPORT.md` sammeln. Falls eine benötigte Funktion in 3.0.94 fehlt, gezielt zwischen eigener Ergänzung und einem geprüften Versionsupdate entscheiden.

**Fertig, wenn:** Der Ausgangskernel reproduzierbar startet und die nächsten Schritte auf geprüften Schnittstellen beruhen. Ein unbekannter Doom-Port bleibt als offene technische Frage sichtbar.

**Teilstand am 13. November 2026 zu 0.4:** Der [Doom-Vorversuch](docs/DOOM_FEASIBILITY.md) dokumentiert feste Quellstände, Lizenzen und Plattformgrenzen von Managed Doom und doomgeneric. Managed Dooms Kern und Software-Renderer kompilieren ohne Silk-Desktopadapter unter .NET 10 mit Trim-/AOT-Analyse (null Warnungen/Fehler). Das ist ein Hostbuild, kein nativer Cosmos-Build und kein spielbarer Gastnachweis; 9.1 bleibt offen.

## 1 Aus der Texteingabe eine kleine Shell machen

**Zweck:** Eine erste nutzbare Bedienoberfläche schaffen, die später im Desktop weiterverwendet wird.

- [x] 1.1 `Shell/ShellService.cs` anlegen. Der Kernel übergibt eine eingegebene Zeile an `ExecuteLine(...)`; die Befehlslogik wandert aus `Kernel.cs` heraus.
- [x] 1.2 Eine Befehlstabelle anlegen, beispielsweise über `ICommand` mit Name, Hilfetext und Ausführung. Zunächst `help`, `echo`, `clear` und `about` implementieren.
- [x] 1.3 Befehlsname und Argumente trennen. Leerzeilen ignorieren; unbekannte Befehle verständlich melden. Danach Argumente in Anführungszeichen unterstützen.
- [x] 1.4 Erwartete Eingabefehler als Fehlermeldung behandeln, sodass der nächste Befehl weiterhin ausgeführt werden kann.
- [x] 1.5 Ein eigenes `ITextOutput` für Ausgabe vorsehen. Anfangs schreibt es auf die Cosmos-Konsole, später in den Textpuffer eines Terminalfensters.

**Fertig, wenn:** `help`, `echo "Hallo ZehOS"`, Leerzeilen und unbekannte Befehle funktionieren und die Shell anschließend weiter bedienbar bleibt.

**Erledigt am 28. Oktober 2026:** 24 MSTest-Fälle bestehen, der native x64-Build ist erfolgreich und die vier Befehle sowie die Weiterarbeit nach Eingabefehlern wurden in QEMU geprüft. Die Befehlstabelle verwendet `ShellCommand` mit Beschreibung und Ausführung.

## 2 Diagnose und Bootablauf ordnen

**Zweck:** Grafik- und Treiberfehler später noch verstehen können, auch wenn auf dem Bildschirm nichts mehr sichtbar ist.

- [x] 2.1 Einen `SystemLog` mit Zeitangabe, Komponente und Meldung einführen. Serielle Ausgabe und einen begrenzten Puffer im Speicher verwenden.
- [x] 2.2 Initialisierung in benannte Schritte aufteilen: Eingabe, Anzeige, Dateien und später Netzwerk. Erfolg und Fehler jedes Schritts protokollieren.
- [x] 2.3 Shellbefehle wie `version`, `uptime` und `log` ergänzen. Nur Informationen anzeigen, die tatsächlich aus einer verfügbaren Schnittstelle gelesen werden.
- [x] 2.4 Einen Start in die Textshell als Wiederherstellungsmodus vorsehen. Wenn die grafische Initialisierung scheitert, soll die Diagnose erreichbar bleiben.

**Fertig, wenn:** Im seriellen Log erkennbar ist, bis wohin der Start kam, und ein Grafikfehler die Diagnose nicht unzugänglich macht.

**Stand am 10. November 2026:** `BootService` protokolliert Beginn, Erfolg, Fehlerstatus und Exceptions benannter Schritte. Der Kernel prüft die von Cosmos bereitgestellten Eingabe-, Konsolen- und Speicherdienste; Netzwerk bleibt ausdrücklich übersprungen. `version` liest die geladenen Assemblyversionen, `uptime` die monotone Uhr seit ZehOS-Konstruktion und `log` den Ringpuffer. Mit `-p:ZehOSRecovery=true` entsteht ein Textshell-Recovery-Build, der die normale Anzeigeinitialisierung umgeht. Fehler in dieser Initialisierung führen ebenfalls zur Wiederherstellung der Textkonsole. Bis Etappe 4 ist die normale Anzeige noch die Cosmos-Textkonsole; der Rückfall bei Grafikfehlern wird durch injizierte Fehler im Hosttest geprüft. Ohne funktionierenden Cosmos-Framebuffer bleibt nur die serielle Diagnose. 58 Hosttests bestehen; normaler Release-Build und Recovery-Build erzeugen mit null Warnungen ein ISO. Normaler Boot, die drei Diagnosebefehle sowie der Recovery-Boot mit bedienbarem `log` sind in QEMU geprüft.

Nach Integration der Dateisystemänderungen aus `master` bleiben Datei- und Diskbefehle erhalten; der protokollierte Dateischritt bindet die vorhandene Datenpartition ein. Die kombinierte Hosttestsuite umfasst 98 erfolgreiche Tests.

## 3 Nicht blockierende Eingabe und gemeinsame Ereignisschleife

**Zweck:** Maus, Fenster und später Spiele müssen weiter reagieren, während jemand Text eingibt.

- [x] 3.1 Eigene Ereignisse für gedrückte und losgelassene Tasten, Texteingabe, Mausbewegung und Maustasten definieren. Physische Tasten und eingegebene Zeichen getrennt behandeln.
- [x] 3.2 Cosmos-Eingabe über einen kleinen `InputService` anbinden. Eine begrenzte Ereigniswarteschlange und ein definiertes Verhalten bei Überlauf vorsehen.
- [x] 3.3 Eine zentrale Schleife einführen: Eingabe abholen → Ereignisse verteilen → Anwendungen kurz aktualisieren → bei Bedarf zeichnen → kurz warten oder CPU abgeben.
- [x] 3.4 Für die Shell einen `LineEditor` bauen: Zeichen einfügen, Backspace und Enter; anschließend Pfeiltasten und Befehlshistorie ergänzen. `Console.ReadLine()` aus dem interaktiven Hauptpfad entfernen.
- [x] 3.5 Zeitabstände mit einer monotonen Uhr berechnen. Lange Arbeit in kleine Schritte aufteilen; aufwendige Dateizugriffe und Netzwerkzugriffe später über geeignete Hintergrundarbeit entkoppeln.

**Fertig, wenn:** Eine regelmäßig aktualisierte Statusanzeige weiterläuft, während Text eingegeben wird, und Tastendrücke samt Loslassen zuverlässig ankommen.

**Umgesetzt am 10. November 2026:** `InputService`, begrenzte Ereignispuffer, getrennte Tasten-/Textereignisse und `LineEditor` mit horizontaler Ansicht und 32 Befehlen Verlauf sind angebunden. In QEMU laufen Zeit- und Tastenzustandsanzeige während der Eingabe weiter; gedrückte und losgelassene Tasten, Cursorbearbeitung und Verlauf wurden geprüft. Die Schleife begrenzt das Eingabebudget und verwendet die TSC-basierte `Stopwatch`-Zeitmessung. Datei- und Laufwerksbefehle bleiben synchron; deren spätere Entkopplung aus 3.5 steht weiterhin aus. Der kleine Rohdaten-Plug für Cosmos 3.0.94 und die Grenzen der Tastencodes sind in `docs/COSMOS_SUPPORT.md` beschrieben.

## 4 Eine eigene grafische Fläche zeichnen

**Zweck:** Die Zeichenfunktionen bereitstellen, aus denen später der Desktop entsteht.

- [x] 4.1 Die geprüfte Cosmos-Grafikfläche hinter `Graphics/DisplayService.cs` anbinden. Zunächst eine feste, in der Referenz-VM funktionierende Auflösung verwenden.
- [x] 4.2 Hintergrund, gefüllte Rechtecke, Linien, Bitmaptext und einen Pixelpuffer zeichnen. Den Puffer später auch für die Doom-Ausgabe verwenden können.
- [x] 4.3 In einen Bildpuffer zeichnen und das fertige Bild ausgeben. Vorhandene Pufferung von Cosmos verwenden, soweit sie ausreicht.
- [x] 4.4 Einen Mauszeiger zeichnen, seine Position auf den Bildschirm begrenzen und Klicks sichtbar machen.
- [x] 4.5 Zeichenbereiche beschneiden. Auch negative Koordinaten und teilweise außerhalb liegende Rechtecke dürfen keine Speicherzugriffe außerhalb des Puffers auslösen.

**Fertig, wenn:** Hintergrund, Text und beweglicher Mauszeiger stabil sichtbar sind und wiederholtes Neuzeichnen keine Spuren hinterlässt.

**Umgesetzt am 10. November 2026:** F4 öffnete zunächst die Grafikprobe; Esc/F4 kehrte zur Shell mit erhaltenem Eingabeentwurf zurück. `DisplayService` verwendet den bestehenden Cosmos-Puffer bei 1024×768×32, `PixelSurface` übernimmt beschränktes Zeichnen und Pixelkopien. Negative/extreme Koordinaten, abgeschnittene Bitmapzeichen und ungültige Quellpuffer werden auf dem Host geprüft. In QEMU wurden Mausbewegung ohne Zeigerspuren, Bildschirmgrenzen, Klicks und wiederholte Ansichtwechsel geprüft. Seit Etappe 5/6 verwendet der Desktop dieselbe Grafikgrundlage.

## 5 Fensterverwaltung entwickeln

**Zweck:** Mehrere grafische Anwendungen auf demselben Bildschirm bedienen können.

- [x] 5.1 Eine `Window`-Klasse mit Position, Größe, Titel, Inhaltsbereich und Zeichenfunktion anlegen. Zunächst ein festes Fenster darstellen.
- [x] 5.2 Eine Fensterliste im `WindowManager` führen und von hinten nach vorne zeichnen.
- [x] 5.3 Beim Klick das oberste getroffene Fenster aktivieren. Tastatureingaben ausschließlich an das aktive Fenster geben.
- [x] 5.4 Ziehen an der Titelleiste und Schließen implementieren. Mausereignisse während des Ziehens weiter an dieses Fenster liefern, auch wenn der Zeiger die Titelleiste verlässt.
- [x] 5.5 Inhalte am Fensterbereich beschneiden. Beim Verschieben und Schließen den freigelegten Hintergrund korrekt neu zeichnen.

**Fertig, wenn:** Zwei überlappende Fenster verschoben, aktiviert und geschlossen werden können und Eingaben im richtigen Fenster ankommen.

**Umgesetzt am 12. November 2026:** `Window` und `WindowManager` zeichnen im bestehenden `PixelSurface`-Puffer. Die Liste bestimmt Z-Ordnung und Fokus; Drag-Capture endet erst beim Loslassen der linken Taste oder bei Reset/Ansichtswechsel. Inhalte werden am Inhaltsrechteck beschnitten. Fenster bleiben in der Arbeitsfläche über der unteren Leiste. Hosttests und QEMU bestätigen Überlappung, Fokus, Verschieben bis zum Rand, Schließen, vollständiges Neuzeichnen und einen weiterhin bedienbaren leeren Desktop.

## 6 Desktop und Terminal als erste Anwendung

**Zweck:** Das erste Kernziel erreichen: eine grafische Oberfläche mit benutzbarer Shell.

- [x] 6.1 Ein kleines Anwendungsmodell einführen, etwa `IApplication` mit Start, Ereignisbehandlung, Aktualisierung, Zeichnen und Schließen.
- [x] 6.2 `Apps/Terminal/TerminalApp.cs` bauen. Ausgabezeilen in einem begrenzten Textpuffer speichern und im Fenster darstellen.
- [x] 6.3 `ShellService` und `LineEditor` aus den früheren Etappen anschließen. Ein Terminal bekommt seine eigene Sitzung mit Eingabe, Historie und später Arbeitsverzeichnis.
- [x] 6.4 Scrollen durch ältere Ausgabe, einen sichtbaren Cursor und einen einfachen Starter für neue Terminalfenster ergänzen.
- [x] 6.5 Aufräumen beim Schließen implementieren. Zunächst nur fest einkompilierte Anwendungen starten; ein Loader für fremde Programme ist eine spätere Aufgabe.

**Fertig, wenn:** ZehOS zum Desktop startet und zwei Terminalfenster unabhängig Befehle ausführen können.

**Umgesetzt am 12. November 2026:** Der Desktop startet mit zwei Terminals und öffnet über `+ Terminal` bis zu acht Fenster. Jede Sitzung erhält eigene Shell-/Dateidienste, Eingabe und Verlauf. Der Ringpuffer hält 256 umgebrochene Ausgabezeilen; PageUp/PageDown scrollen, normale Texteingabe kehrt zur neuesten Ausgabe zurück. Schließen leert den Textpuffer und entfernt die Anwendung aus der Fensterliste. Esc/F4 erhält offene Sitzungen und die separate Textshell-Eingabe. Recovery startet weiterhin textuell. 149 Hosttests mit Warnungen als Fehler, native Release-/Recovery-Builds und QEMU-Prüfungen bestehen. Getrennte Arbeitsverzeichnisse, Dateifehler, Speichern und erneutes Lesen nach Reboot sind auf einer Datenimage-Kopie geprüft. Neustart und Ausschalten bestehen auch mit verbundenem GDB. VMware und echte Hardware wurden nicht neu geprüft.

Die Anwendungen können zunächst gemeinsam im Kernel laufen und kooperativ Rechenzeit abgeben. Die anschließenden Etappen 6a bis 6c ergänzen Hintergrundaufgaben, Kommunikation und Dateirechte. Ein Fehler kann weiterhin das gesamte System betreffen. Prozessisolation und Speicherschutz bleiben Gegenstand von Etappe 12.4; vor dem Umgang mit beliebigen fremden Inhalten müssen sie erneut bewertet werden.

## 6a Multitasking und Scheduling

**Zweck:** Mehrere Hintergrundaufgaben ausführen und die Verteilung ihrer Rechenzeit nachvollziehbar machen.

- [x] 6a.1 In einem getrennten Versuch die Thread-, Warte- und Scheduler-Schnittstellen von Cosmos 3.0.94 prüfen. Zwei Aufgaben im Gast ausführen und Ergebnisse sowie experimentelle APIs in `docs/COSMOS_SUPPORT.md` dokumentieren. Bei fehlender Unterstützung eine gezielte Ergänzung oder ein geprüftes Versionsupdate bewerten, bevor der Desktop umgestellt wird.
- [x] 6a.2 Hintergrundaufgaben mit Kennung und Zuständen wie bereit, laufend, wartend und beendet verwalten. Start, Statusanzeige und kooperativen Abbruch über die Shell ermöglichen; Ressourcen beim Beenden aufräumen.
- [x] 6a.3 Eine Round-Robin-Strategie für echte Kernelthreads auf der geprüften Cosmos-Anbindung implementieren. Kontextwechsel und Timermechanismus von Cosmos weiterverwenden; Zeitscheibe und Auswahlreihenfolge nachvollziehbar protokollieren. Eine reine Verteilung von `Update`-Aufrufen wäre nur kooperatives Anwendungsscheduling und darf nicht als präemptives Thread-Scheduling ausgegeben werden.
- [x] 6a.4 Gemeinsame Dienste gegen gleichzeitige Zugriffe absichern. Zeichnen und Eingabe im Desktop belassen; Hintergrundarbeit über kontrollierte Übergaben anbinden. Dateioperationen nur dann in Hintergrundaufgaben verschieben, wenn die verwendete Cosmos-Anbindung dafür geprüft ist.
- [x] 6a.5 Zwei Rechenaufgaben parallel zum bedienbaren Terminal vorführen. Fortschritt, Warten, Abbruch und wiederholten Start prüfen. Auswahlreihenfolge und Zustandswechsel auf dem Host testen; tatsächliche Verdrängung und Reaktionsfähigkeit im Gast nachweisen. Eine zweite Strategie erst später zum Vergleich ergänzen.

**Fertig, wenn:** Zwei Hintergrundaufgaben unter Round-Robin nachweislich Fortschritt machen, eine rechenintensive Aufgabe die andere nicht dauerhaft verdrängt und Terminal sowie Desktop bedienbar bleiben.

**Umgesetzt am 13. November 2026:** Die ZehOS-Round-Robin-Policy verwendet echte Cosmos-Threads, den bestehenden Timer und eine feste 20-ms-Zeitscheibe. Eine vorab begrenzte Runqueue und eine Auswahlspur vermeiden Allokationen im Tickpfad. `tasks` startet bis zu acht Rechen-/Warteaufgaben, zeigt Kennung/Zustand/Fortschritt und bricht kooperativ ab. Worker verwenden kontrollierte atomare/synchronisierte Zustände; Eingabe, Anzeige und Dateien bleiben im Vordergrund. Hosttests prüfen FIFO, Zeitscheibe, Zustände, Abbruch, Fehler und Grenzen. Native Probe-, Normal- und Recovery-Builds sind erfolgreich; die Verdrängungs-/Warteprobe besteht mit Round-Robin in QEMU und VMware. In QEMU bleibt das Terminal während zweier Rechenaufgaben bedienbar; nach Abbruch und erneutem Start läuft die neue Aufgabe auch in der bedienbaren Textshell weiter. Recovery und Energieaktionen sind geprüft. Details und Grenzen stehen in `docs/COSMOS_SUPPORT.md`.

## 6b Nachrichtenwarteschlangen und Synchronisation

**Zweck:** Aufgaben aus Etappe 6a Daten austauschen lassen und ihr Warten aufeinander korrekt koordinieren.

- [x] 6b.1 Eine begrenzte Nachrichtenwarteschlange mit definierten Ergebnissen bei voller, leerer und geschlossener Warteschlange bereitstellen. Nicht blockierendes Senden und Empfangen sowie wartende Varianten mit Abbruch und Zeitlimit vorsehen; die Desktop-Schleife darf dabei nicht blockieren.
- [x] 6b.2 Geprüfte Synchronisationsmittel verwenden, um gemeinsame Zustände zu schützen und wartende Aufgaben bei neuen Daten beziehungsweise freiem Platz aufzuwecken. Verlorene Wecksignale und aktives Dauerabfragen vermeiden; Interruptschutz nur für die dafür vorgesehenen kurzen Bereiche verwenden.
- [x] 6b.3 Eine Erzeuger-Verbraucher-Vorführung bauen: Eine Aufgabe erzeugt nummerierte Daten, die andere verarbeitet sie. Langsame Erzeugung und Verarbeitung machen die Fälle leerer beziehungsweise voller Puffer sichtbar.
- [x] 6b.4 Für mehrere gemeinsam benötigte Sperren eine feste Erwerbsreihenfolge festlegen und Deadlockvermeidung an einem kontrollierten Beispiel erklären und prüfen. Kein absichtliches dauerhaftes Blockieren des produktiven Desktops.
- [x] 6b.5 Nachrichtenreihenfolge, Kapazitätsgrenze, konkurrierende Zugriffe, Abbruch, Zeitlimit und Schließen bei wartenden Teilnehmern testen. Im Gast nachweisen, dass keine Daten verloren oder doppelt verarbeitet werden und Aufgaben nach dem Schließen sauber enden.

**Fertig, wenn:** Erzeuger und Verbraucher zuverlässig über den begrenzten Puffer kommunizieren, bei leerem oder vollem Puffer korrekt warten und durch Abbruch oder Schließen beendet werden können.

**Geprüft am 13. November 2026:** 216 Hosttests bestehen; der native Probe-Build liefert in QEMU `MESSAGES_PROBE PASS` einschließlich alleinigem Consumer-Abbruch bei vollem Puffer und vollständigem Neustart. Diese Code-/QEMU-Abnahme belegt keine VMware-Kompatibilität; die neue Nachrichtenprobe und Bedienabläufe sind dort noch offen. Einzelheiten stehen in `docs/ETAPPE6B_STATUS.md`.

Diese Etappe behandelt Thread- und Anwendungskommunikation im gemeinsamen Kernel. Sie bildet eine Grundlage für Interprozesskommunikation; isolierte Prozesse und deren Schutzgrenzen folgen erst in Etappe 12.4. Eigene Softwareinterrupts sind für diese erste Kommunikation nicht erforderlich.

## 6c Benutzerverwaltung und Dateirechte

**Zweck:** Mehrere Benutzer mit getrennten Sitzungen und kontrolliertem Dateizugriff unterstützen. Voraussetzung ist die vorgezogene Dateisystemgrundlage aus Etappe 7.

- [x] 6c.1 Benutzerkonten mit stabiler Kennung und den Rollen Administrator und Benutzer einführen. Anmelden, Abmelden und Anzeige der aktuellen Identität ermöglichen. Kennwörter nur mit einer geprüften gesalzenen Passwort-Hashfunktion speichern; Verfügbarkeit der nötigen Kryptografie und Zufallsquelle vorher prüfen.
- [x] 6c.2 Jeder Sitzung ihre Benutzeridentität und ihr Benutzerverzeichnis zuordnen. Benutzerwechsel und mehrere Terminalfenster dürfen Identität oder Arbeitsverzeichnis anderer Sitzungen nicht unbeabsichtigt verändern.
- [x] 6c.3 Eigentümer sowie Lese- und Schreibrechte für Dateien und Verzeichnisse festlegen. Benötigte Rechteinformationen auf dem FAT32-Datenlaufwerk separat speichern und nach einem Neustart wiederherstellen. Bestehende Dateien ausdrücklich zuordnen; fehlende oder beschädigte Rechteinformationen dürfen keinen unkontrollierten Zugriff freigeben.
- [x] 6c.4 Rechte zentral im Dateidienst nach vollständiger Pfadauflösung prüfen. Lesen, Auflisten, Verzeichniswechsel, Anlegen und Überschreiben sowie temporäre Dateien des sicheren Schreibpfads berücksichtigen. Administrative Laufwerks- und Benutzerverwaltung auf Administratoren beschränken; spätere Anwendungen verwenden dieselben geprüften Dienste.
- [x] 6c.5 Mit zwei Benutzern vorführen: eigene Datei bearbeiten, fremden Zugriff ohne Berechtigung verweigern und gezielt Leserechte vergeben. Pfadumgehungen, parallele Sitzungen, Neustart sowie fehlende oder beschädigte Konten- und Rechteinformationen prüfen. Einen ausdrücklich gestarteten administrativen Recovery-Weg dokumentieren.

Implementierungsstand 13.11.2026: Konten/ACLs, gesicherte Shellbefehle und zentrale Dateiprüfungen sind einschließlich der aktuellen Desktopanwendungen und Etappen 6a/6b umgesetzt. Hostregressionen und isolierte QEMU-Datei-/Kryptografieproben bestehen; interaktiver Recovery-Login, Logout und tatsächlicher Neustart mit erhaltener Kennung/Datei wurden geprüft. Die Abnahmekriterien sind auf Host und QEMU erfüllt; VMware und echte Hardware bleiben ausdrücklich ungeprüft. Einzelheiten und aktuelle Nachweise: [docs/ETAPPE6C_STATUS.md](docs/ETAPPE6C_STATUS.md).

**Fertig, wenn:** Zwei Benutzer getrennt arbeiten, erlaubte Dateioperationen funktionieren, unerlaubte Zugriffe zentral abgelehnt werden und Konten sowie Rechte einen Neustart überstehen.

Diese Rechte gelten für Anwendungen, die die kontrollierten ZehOS-Dienste verwenden. Solange Anwendungen im Kernel laufen, können sie die Regeln technisch umgehen. Eine Schutzgrenze gegen fremden Code erfordert zusätzlich Kernel-/Usermode, Prozessisolation und kontrollierte Systemaufrufe aus Etappe 12.4.

## 7 Dateien lesen und dauerhaft speichern

**Zweck:** Einstellungen, Texte, Webseiten und Spieldaten laden können.

- [x] 7.1 Separates virtuelles 64-MiB-Datenlaufwerk über SATA/AHCI an QEMU anschließen. MBR und FAT32 mit Cosmos auf der vollständig leeren Testplatte ausdrücklich über `disk init` einrichten.
- [x] 7.2 Laufwerk erkennen und über Cosmos unter `/data` einhängen. Vorhandene Daten beim Start ausschließlich einbinden, ohne Formatierung.
- [x] 7.3 Einen `FileService` für Pfadauflösung und begrenzte Textoperationen (65.536 Zeichen) einführen. Shellbefehle `ls`, `pwd`, `cd` und `cat` ergänzen.
- [x] 7.4 Schreiben ergänzen: Verzeichnisse und kleine Textdateien anlegen, schließen und mit `disk unmount` erforderliche Schreibpuffer leeren. Dafür `mkdir` und `write` bereitstellen.
- [x] 7.5 Fehlendes Laufwerk, ungültige Pfade, fehlende Dateien und vollen Datenträger behandeln. Ohne Datenlaufwerk soll der Desktop weiterhin starten.
- [x] 7.6 Datei speichern, VM sauber beenden, erneut starten und Inhalt vergleichen. Danach bei Bedarf auch das VMware-Startprofil um ein Datenlaufwerk erweitern.

**Fertig, wenn:** Eine im Gast geschriebene Datei nach einem Neustart unverändert gelesen werden kann.

Ein RAM-Dateisystem ist für frühe Experimente möglich, erfüllt aber keine dauerhafte Speicherung. Ressourcen können anfangs auch einkompiliert werden; das ist kein Ersatz für den Neustarttest.

Die Dateisystemetappe wurde vorgezogen und mit Cosmos 3.0.94 in QEMU geprüft. Dateien mit Unterordnern und Leerzeichen im Namen bleiben nach einem vollständigen VM-Neustart erhalten. Vorhandene Partitionen, nicht leere Images und abweichende Plattengrößen werden bei der Einrichtung abgelehnt. Auch auf einem vollen Datenträger bleiben bisherige Dateien bei einem Schreibfehler erhalten; temporäre Dateien werden entfernt. Doppelte Mounts unter `/data` werden abgelehnt. Schritt 7.5 ist am 13. November 2026 im Desktop mit fehlendem Laufwerk, Pfadfehlern und vollständig belegter isolierter FAT32-Testkopie in QEMU geprüft. Dateierhalt, Bereinigung, Weiterbedienung und Neustart sind nachgewiesen; VMware bleibt offen. Ablauf und Grenzen stehen in [docs/DESKTOP_STORAGE_TESTS.md](docs/DESKTOP_STORAGE_TESTS.md). Start und Einrichtung beschreibt die README.

## 8 Kleine Desktopanwendungen bauen

**Zweck:** Die gemeinsamen Bausteine an überschaubaren Anwendungen prüfen, bevor Browser und Doom zusätzliche Komplexität bringen.

- [x] 8.1 Wiederverwendbare Schaltflächen, Textfelder und Listen mit Fokus und einfacher Anordnung entwickeln.
- [x] 8.2 Einen Dateibetrachter bauen: Ordner auflisten und eine ausgewählte Textdatei anzeigen.
- [x] 8.3 Einen kleinen Texteditor ergänzen: Text bearbeiten, speichern und bei ungespeicherten Änderungen vor dem Schließen nachfragen.
- [x] 8.4 Ein kleines Spiel wie Pong bauen. Tastenzustände, zeitabhängige Bewegung, Pixelgrafik und Rückkehr zum Desktop damit prüfen.
- [x] 8.5 Bildzeiten und Speicherverbrauch beobachten. Große Bildpuffer wiederverwenden, statt sie in jedem Bild neu anzulegen. Ungenutzte Ressourcen beim Schließen freigeben.

**Fertig, wenn:** Eine Textdatei bearbeitet und neu geöffnet werden kann und das Spiel auf Eingaben reagiert, während der Desktop weiter bedienbar bleibt.

**Umgesetzt am 13. November 2026:** Gemeinsame UI-Bausteine mit Fokus, Dateibetrachter, Mehrzeileneditor mit Schließrückfrage und Pong sind an den Desktop angeschlossen. Direkte Voraussetzungen sind ein wiederverwendeter Textbearbeitungskern, verschachteltes Clipping, abbrechbares Schließen und regelmäßige Spielbilder. 173 Hosttests, native Normal-/Recovery-Builds und QEMU-Prüfungen einschließlich Dateineustart, ENOSPC, Spiel/Fokus und Ressourcenbeobachtung bestehen. VMware und echte Hardware bleiben ausdrücklich offen. [Bedienung, Architektur und Nachweise](docs/DESKTOP_APPS.md). Etappen 6a–6c bleiben unverändert.

Der zusätzliche KDE/Cinnamon-orientierte Desktop-Umbau wird unabhängig von den abgeschlossenen Punkten 8.1–8.5 geprüft: Programmmenü mit Suche, Fensterleiste, Minimieren/Maximieren, Größenänderung und Alt+Tab. Er verwendet die vorhandenen Apps und UI-Bausteine aus Etappe 8. [Umfang und Integrationsnachweise](docs/DESKTOP_REDESIGN.md). Ein Rechner und weitergehende Dateioperationen bleiben spätere Erweiterungen; die laufenden Etappen 6a–6c werden durch diesen Umbau nicht abgehakt.

## 9 Doom schrittweise portieren

**Zweck:** Doom als Anwendung auf ZehOS ausführen. Für den ersten spielbaren Stand reichen Softwaregrafik, Tastatur, Zeitmessung und lesbare Spieldaten.

- [ ] 9.1 Aus dem frühen Portierungsversuch einen Weg auswählen. Ein C#-Port wie Managed Doom ist ein Kandidat; seine Desktop-, Grafik- und Audioanbindung muss durch ZehOS-Anbindungen ersetzt werden. Vor der Integration einen konkreten Quellstand festhalten.
- [ ] 9.2 Spielkern und Plattformcode trennen. Adapter für Spieldaten, Tastenzustände, Zeit und Bildausgabe schreiben. Als Ausgabe zunächst einen festen Pixelpuffer verwenden.
- [ ] 9.3 Eine passende, rechtmäßig verfügbare WAD-Datei laden. Erst Verzeichnis und Ressourcen lesen, dann Titelbild oder einen einzelnen gerenderten Spielbildschirm darstellen.
- [ ] 9.4 Den Spieltakt unabhängig von der Zeichenrate ausführen. Bewegung, Drehen, Schießen und Menü mit gedrückten und losgelassenen Tasten prüfen. Sound zunächst weglassen.
- [ ] 9.5 Start aus dem Desktop, Tastaturfokus und Rückkehr zum Desktop implementieren. Bei exklusiver Maussteuerung muss es eine eindeutige Taste zum Freigeben geben.
- [ ] 9.6 Wiederholt starten und beenden; anschließend mehrere Minuten spielen. Speicherentwicklung, Bildzeit und Reaktionsfähigkeit des Desktops prüfen.

**Fertig, wenn:** Ein Level mit Steuerung spielbar ist und Doom beendet werden kann, ohne ZehOS neu zu starten.

Als Alternative kommt `doomgeneric` infrage. Dafür sind zusätzlich C-Build, Verknüpfung mit dem Kernel, passende Aufrufkonventionen und benötigte C-Laufzeitfunktionen zu lösen. Die wenigen Grafik- und Eingabefunktionen allein ergeben noch keinen vollständigen Port. Ein gewöhnliches Windows- oder Linux-Doom-Binary kann ZehOS zu diesem Zeitpunkt nicht einfach ausführen.

Bei Weitergabe eines Ports die jeweiligen Quellcode-Lizenzen und die getrennten Rechte an den WAD-Spieldaten berücksichtigen. Sound wird erst nach dem spielbaren Stand als eigene Erweiterung mit geprüftem Audiotreiber, Pufferung und einer Audioanbindung geplant.

## 10 Netzwerk zunächst in der Shell prüfen

**Zweck:** Die Datenübertragung unabhängig von HTML und Fensterdarstellung zum Laufen bringen.

- [x] 10.1 Eine von der verwendeten Cosmos-Version unterstützte virtuelle Netzwerkkarte einstellen. Erkennung, Treiber und Verbindungszustand protokollieren.
- [x] 10.2 Zuerst mit fester IPv4-Konfiguration einen bekannten lokalen Testdienst erreichen. Danach DHCP und DNS ergänzen, soweit sie in der verwendeten Version verfügbar sind.
- [x] 10.3 Diagnosebefehle wie `netinfo`, `ping` und `nslookup` an die tatsächlich unterstützten Dienste anbinden.
- [x] 10.4 Eine TCP-Verbindung zu einem kleinen Testserver auf dem Entwicklungsrechner herstellen. Senden, Empfangen und Schließen einzeln prüfen.
- [x] 10.5 Einen begrenzten HTTP-Client für kontrollierte Testseiten bauen: Statuszeile und Header lesen, aufgeteilte Antworten zusammensetzen, Antwortgröße und Wartezeit begrenzen. Der Testserver liefert anfangs einfache unkomprimierte Antworten mit klar definierter Länge.
- [x] 10.6 Laden abbrechen können. Blockierende Verbindungsaufbauten und Lesezugriffe dürfen nicht in der Desktop-Schleife laufen. Dafür geprüfte Hintergrundarbeit mit Zeitlimits verwenden oder Zugriffe als nicht blockierende Zustandsmaschine aufbauen.

**Fertig, wenn:** `fetch` den Text einer Testseite ausgibt und Serverausfall oder Verbindungsabbruch den Desktop nicht einfrieren lassen.

### HTTPS als Voraussetzung für den späteren Browser

- [x] 10.7 TLS-Wege getrennt recherchieren: Paketstand, Lizenz, Abhängigkeiten, .NET 10, NativeAOT und die festgelegte Cosmos-Version prüfen. Cosmos.Network.Http 3.0.0 und die bereits vorhandene BouncyCastle-Version 2.7.0 wurden untersucht; die öffentliche HTTP-API allein bietet keine ausreichende Kontrolle über Transportfristen.
- [x] 10.8 Einen begrenzten TLS-Transport über vorhandene TLS-Protokolllogik integrieren. HTTP und HTTPS verwenden dieselbe Antwortbegrenzung und Hintergrundzuständigkeit. Ein HTTPS-Fehler darf keinen Rückfall auf unverschlüsseltes HTTP auslösen.
- [x] 10.9 Sichere Zufallsquelle, UTC-Uhr und einen festgelegten Vertrauensspeicher anbinden. Zertifikatskette, Verwendungszweck, Gültigkeitszeit und Zielname prüfen; fehlende Voraussetzungen sperren HTTPS. Keine Bedienoption zum Überspringen der Prüfung anbieten.
- [x] 10.10 HTTPS im nativen QEMU-Gast mit kontrolliertem TLS-Dienst prüfen: Erfolg, falscher Name, fremde CA, abgelaufenes und noch nicht gültiges Zertifikat, begrenzter Empfang, Fehler und Abbruch. Testwurzeln ausschließlich in ausdrücklich gebaute Proben einbetten. Den VMware-Prüfstatus getrennt ausweisen.

**Umgesetzt am 14. November 2026:** Shellnetzwerk mit E1000e, IPv4/DHCP/DNS/ICMP und begrenztem HTTP/HTTPS ist mit 323 Hosttests, nativen Release-/Recovery-Builds und QEMU-Proben geprüft. Netzwerkaufträge laufen im Hintergrund und lassen sich sitzungsbezogen abbrechen. Cosmos-interne Connect-/Send-/DHCP-Warteabschnitte reagieren erst nach ihrer Rückkehr; dies ist keine harte Gesamtfrist über diese Aufrufe hinweg. UTC bleibt eine Voraussetzung des Hypervisors; automatische Vertrauensspeicherpflege und Zertifikatssperrprüfung sind offen. VMware und echte Hardware bleiben ungeprüft. [Umsetzung und Nachweise](docs/NETWORK_TESTS.md).

Die am 14. November 2026 gelesene [Cosmos-Netzwerkdokumentation](https://cosmosos.github.io/articles/user/network.html) beschreibt inzwischen ein zusätzliches HTTP-/TLS-Paket. Das beweist keine Unterstützung durch den eingefrorenen Kernelstand 3.0.94. Recherche, konkrete Anbindung und aktuelle Prüfnachweise stehen in [NETWORK_TESTS.md](docs/NETWORK_TESTS.md); HTTPS bleibt eine ausdrücklich zu prüfende Voraussetzung des späteren Browsers. Etappe 12.2 umfasst den weiteren Betrieb von Uhr und Vertrauensspeicher sowie die dort benötigte Robustheit.

## 11 Einen einfachen Browser entwickeln

**Zweck:** Zuerst ein kleines, verlässliches Dokument darstellen und den Umfang anschließend erweitern.

- [ ] 11.1 Einen lokalen Dokumentbetrachter als Fensteranwendung bauen. Eine HTML-Datei aus dem Dateisystem laden; Netzwerk ist für diesen Schritt noch nicht nötig.
- [ ] 11.2 Eine kleine, festgelegte HTML-Teilmenge parsen: Überschriften, Absätze, Zeilenumbrüche, einfache Listen und Links. Nicht unterstützte Elemente kontrolliert überspringen; Skripte nicht ausführen.
- [ ] 11.3 Ein Dokumentmodell und einen einfachen Layoutschritt implementieren: Text umbrechen, Elementhöhen bestimmen und vertikale Positionen berechnen.
- [ ] 11.4 Dokument zeichnen, scrollen und Links anklickbar machen. Relative Dateipfade korrekt zur geöffneten Datei auflösen.
- [ ] 11.5 Adresszeile sowie Zurück und Vorwärts ergänzen. Dokumentquelle und Darstellung trennen, damit später dieselbe Ansicht lokale Dateien und HTTP-Antworten nutzt.
- [ ] 11.6 Den HTTP-Client aus Etappe 10 anschließen. Relative URLs, Zielwechsel durch Weiterleitungen und verständliche Fehlermeldungen ergänzen; Weiterleitungen und Datenmengen begrenzen. Nicht unterstützte HTTPS-Ziele ausdrücklich als solche anzeigen.
- [ ] 11.7 Kleine eigene Testseiten verwenden: langer Text, mehrere Links, fehlende Datei und fehlerhaftes HTML. Erst danach Bilder und einfache Gestaltungsregeln ergänzen.

**Fertig, wenn:** Mehrere lokale Seiten über Links besucht werden können. Die nächste Ausbaustufe ist erreicht, wenn dieselben einfachen Seiten vom Testserver geladen werden und Laden sowie Abbrechen den Desktop bedienbar lassen.

Der lokale Browserteil kann bereits nach Etappe 8 begonnen werden. Für Seiten aus dem Netz wird zusätzlich Etappe 10 benötigt. Die vorgeschlagene Reihenfolge gibt Doom zunächst Vorrang, weil dafür kein Netzwerkstack und keine Webstandards nötig sind.

## 12 Moderne Webseiten und ein robusteres System

**Zweck:** Den ersten Desktop zu einem umfangreicheren Betriebssystem ausbauen. Diese Arbeiten bestehen jeweils wieder aus eigenen Projekten.

- [ ] 12.1 Vor einem modernen Browser den Architekturweg prüfen: eine bestehende Engine portieren oder den eigenen kleinen Browser gezielt erweitern. An einem Buildversuch Laufzeit-, Speicher-, Thread-, Grafik- und Systemaufruf-Abhängigkeiten feststellen.
- [ ] 12.2 HTTPS über eine geeignete TLS-Implementierung ermöglichen. Dafür Zufallsquelle, verlässliche Uhrzeit, Zertifikatsspeicher und Prüfung von Zertifikatskette sowie Hostnamen bereitstellen. Zertifikatsprüfung gehört zum Zielumfang.
- [ ] 12.3 HTML, CSS, Schrift- und Bildformate schrittweise erweitern. Falls JavaScript zum Ziel gehört, eine geeignete Laufzeit mit kontrollierten Ressourcen integrieren und benötigte Browser-APIs ergänzen.
- [ ] 12.4 Speicher- und Prozessisolation, kontrollierte Systemaufrufe sowie Fehlerbehandlung für Anwendungen untersuchen und umsetzen. Die dafür nötigen Cosmos-Erweiterungen getrennt bewerten.
- [ ] 12.5 Danach externe Programme, einen Programmlader und breitere Hardwareunterstützung planen. Die Benutzer- und Dateirechte aus Etappe 6c an Prozessisolation und kontrollierte Systemaufrufe anbinden, damit fremde Programme die Prüfungen nicht umgehen können. Ein stabiler virtueller Referenzrechner bleibt für die Entwicklung erhalten.

**Fertig, wenn:** Für einen konkret festgelegten Satz von Webseiten die benötigten Funktionen und Schutzmechanismen funktionieren. „Alle heutigen Webseiten“ ist kein sinnvoller einzelner Meilenstein.

## Vorgeschlagene Struktur des eigenen Codes

Die Ordner erst anlegen, wenn die jeweilige Funktion entwickelt wird. `Kernel.cs` verbindet die Bausteine und hält den Bootablauf übersichtlich.

```text
ProjektZeh/
  Kernel.cs
  SystemServices/       Bootablauf, Diagnose und Zeit
  Shell/                Befehle, Parser und Sitzungen
  Input/                Ereignisse, Tastenzustände und Zeileneditor
  Graphics/             Anzeige, Zeichenfunktionen und Bildpuffer
  Desktop/              Ereignisschleife, Fenster und Fokus
  UI/                   Schaltflächen, Textfelder und Listen
  Storage/              Dateien, Pfade und Laufwerksanbindung
  Network/              Netzwerkdiagnose und HTTP
  Apps/
    Terminal/
    FileManager/
    TextEditor/
    Pong/
    Doom/
    Browser/
```

Eine Shell interpretiert Befehle; ein Terminal stellt ihre Ein- und Ausgabe dar. Diese Trennung erlaubt dieselbe Befehlslogik in der Textkonsole und in mehreren Fenstern. Entsprechend lädt der Netzwerkcode Daten, während der Browser sie parst, anordnet und zeichnet.

## Arbeitsweise für jede Etappe

Eine Aufgabe soll jeweils ein sichtbares Ergebnis liefern, beispielsweise „`help` zeigt vier Befehle“, „ein Fenster lässt sich verschieben“ oder „eine Datei übersteht einen Neustart“. Größere Kontrollkästchen vor der Umsetzung weiter in solche Änderungen zerlegen.

Nach jeder Änderung bauen und den betroffenen Ablauf in QEMU prüfen. Zum Abschluss einer Etappe zusätzlich die bisherigen Kernfunktionen kurz prüfen: Boot, Shell, Eingabe und später Fenster sowie Dateien. Für neue technische Risiken gezielt Fehlerfälle ergänzen, etwa einen Server ohne Antwort oder ein fehlendes Laufwerk.

Reine Logik wie Befehlsparser, Pfadauflösung, Textumbruch und HTML-Parser lässt sich zusätzlich in einem normalen .NET-Testprojekt auf dem Entwicklungsrechner testen. Treiber, Timing, Darstellung und Dateipersistenz brauchen weiterhin Prüfungen im Gast. Erfolgreiche Hosttests ersetzen keinen Kernelstart.

## Erste Änderungen und nächster Schritt

Die Textshell sowie Diagnose, Eingabe, Grafik und Desktop aus Etappen 2 bis 6 sind umgesetzt. Die noch offenen Grundlagenprüfungen, die Entkopplung langer Dateioperationen aus 3.5 und die zusätzliche VMware-Dateiprüfung bleiben sichtbar:

Als Nächstes folgen in dieser Reihenfolge Etappe 6a (Multitasking und Scheduling), 6b (Nachrichten und Synchronisation) und 6c (Benutzer und Dateirechte). Die unabhängig vorgezogene Etappe 8 ist implementiert und in QEMU geprüft; nach Abschluss der vorgesehenen Vorstufen folgen Doom aus Etappe 9 und die weiteren Netzwerk-/Browseretappen. VMware-Nachprüfungen bleiben offen.

- [x] `ShellService.ExecuteLine(...)` anlegen und die aktuelle Echo-Ausgabe dort als `echo` umsetzen.
- [x] `help`, `clear` und `about` über eine Befehlstabelle ergänzen.
- [x] Leerzeilen, unbekannte Befehle und Argumente in Anführungszeichen sauber behandeln.
- [x] Shellausgabe hinter `ITextOutput` legen, damit später ein Terminalfenster angeschlossen werden kann.
- [x] Serielle Diagnose und einen begrenzten Logpuffer hinzufügen.
- [x] Benannte Initialisierungsschritte mit Erfolg- und Fehlereinträgen sowie Textshell-Wiederherstellung ergänzen.
- [x] Nicht blockierende Eingabe und eine gepufferte Grafikprobe mit F4/Esc bereitstellen.
- [x] Fensterverwaltung, Desktop und unabhängige Terminalfenster ergänzen.
- [ ] Offene Schnittstellenprüfungen aus Etappe 0 weiter dokumentieren.

## Technische Quellen

- [Cosmos Gen3](https://github.com/CosmosOS/Cosmos): Grundlage und Übersicht der bereitgestellten Systemfunktionen.
- [Cosmos Scheduling-Erweiterungen](https://cosmosos.github.io/articles/user/scheduler-plugging.html): experimentelle Schnittstelle für eigene Schedulingstrategien und Round-Robin-Beispiel; Verfügbarkeit in 3.0.94 vor der Umsetzung prüfen.
- [Cosmos Grafik](https://cosmosos.github.io/articles/user/graphics.html): Zeichenflächen, Bildausgabe und Abgrenzung zum selbst zu entwickelnden Fenstersystem.
- [Cosmos Dateisystem](https://cosmosos.github.io/articles/user/filesystem.html): virtuelle Laufwerke, Mountpunkte und Dateioperationen.
- [Cosmos Netzwerk](https://cosmosos.github.io/articles/user/network.html): Netzwerkfunktionen und aktuelle Einschränkungen.
- [Managed Doom](https://github.com/sinshu/managed-doom): möglicher C#-Spielkern; die vorhandenen Desktopbibliotheken benötigen eine passende ZehOS-Anbindung.
- [doomgeneric](https://github.com/ozkl/doomgeneric): möglicher alternativer Portierungsweg in C.

Die Reihenfolge und die Architekturvorschläge sind auf den derzeitigen ZehOS-Code zugeschnitten. Die verlinkten Projekte dienen als technische Grundlage; sie bestätigen keine bereits funktionierende Integration in ZehOS.

## Installation und Updates – Stand am 14. November 2026

Installer-ISO mit grafischem Einrichtungsassistenten im Desktop-Stil, UEFI-Installation auf eine FAT32-EFI-Partition, signierte Kernelupdates über einen HTTP-Spiegel oder offline sowie Release-Workflow und Spiegelwerkzeuge setzen den [Entwurf](docs/superpowers/specs/2026-11-13-installer-updates-design.md) um. Installation, Start ohne ISO, Updates, Warmstart und Rückfallstart sind in QEMU/OVMF geprüft; VMware, echte Hardware und ein tatsächlicher GitHub-Release-Lauf sind offen. Nachweise: [docs/INSTALLER_UPDATES_TESTS.md](docs/INSTALLER_UPDATES_TESTS.md).
