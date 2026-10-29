# Entwicklungsplan für ZehOS

ZehOS soll einen bedienbaren Desktop mit Fenstern, einer Shell im Terminal, einem einfachen Browser und spielbarem Doom bekommen. Der Weg führt über kleine, einzeln prüfbare Funktionen. Jede Etappe soll einen startbaren Stand hinterlassen, auf dem die nächste aufbaut.

Die empfohlene Reihenfolge lautet: **Grundlage prüfen → Textshell → Ereignisschleife und Eingabe → Grafik → Fenster → Terminal im Desktop → Dateien → erste Anwendungen → Doom → Netzwerk → einfacher Browser → Unterstützung moderner Webseiten.**

## Ausgangspunkt am 28. Oktober 2026

Im Repository steht bisher ein kleiner C#-Kernel auf Basis von Cosmos Gen3 3.0.94, .NET 10 und x64. `ProjektZeh/Kernel.cs` gibt eine Startmeldung aus, liest mit `Console.ReadLine()` eine Zeile und gibt sie wieder aus. Eine eigene Shell, ein Fenstersystem und Anwendungen sind noch zu entwickeln.

Build und Start über QEMU sowie ein VMware-Startskript sind dokumentiert. Das VMware-Skript erzeugt derzeit eine VM ohne Festplatte und mit deaktiviertem Sound. Für dauerhafte Dateien muss später ein virtuelles Datenlaufwerk dazukommen.

Cosmos ist die technische Grundlage: vorhandene Treiber und Systemdienste zuerst verwenden und nur bei einer konkreten Lücke erweitern. Die aktuelle Gen3-Dokumentation beschreibt Grafik, Eingabe, Speicherverwaltung, Scheduling, Dateisystem und Netzwerk. Ob die jeweils benötigte Funktion bereits mit **3.0.94** und der gewählten VM funktioniert, wird vor ihrer Verwendung geprüft. Die Online-Dokumentation kann einen neueren Entwicklungsstand zeigen.

Erledigte Aufgaben sind angekreuzt; offene Kontrollkästchen kennzeichnen geplante Arbeit. Die genannten Klassen und Schnittstellen sind Vorschläge für eigenen ZehOS-Code, keine zugesicherten Cosmos-APIs.

## Zielumfang

Der erste vollständige Zielstand ist erreicht, wenn ZehOS in der VM zum Desktop startet, ein Terminal Befehle annimmt, Dateien einen Neustart überstehen, Doom mit Tastatursteuerung läuft und ein Browser einfache verlinkte HTML-Seiten über HTTP laden und anzeigen kann. Lokale HTML-Dateien sind ein früheres Zwischenziel.

Ein Browser für heutige komplexe Webseiten ist ein weiteres großes Entwicklungsziel. Dafür kommen unter anderem HTTPS, Zertifikate, umfangreiche HTML- und CSS-Unterstützung, JavaScript sowie Schutz vor fehlerhaften und fremden Inhalten hinzu. Diese Anforderungen sollen den ersten benutzbaren Desktop nicht blockieren.

## 0 Technische Grundlage und offene Fragen prüfen

**Zweck:** Früh herausfinden, welche Cosmos-Funktionen wir übernehmen können und wo größere Portierungsarbeit wartet.

- [ ] 0.1 Einen unveränderten Kernel mit der festgelegten Version bauen und in QEMU starten. ISO-Pfad, Startbefehl und einen sichtbaren erfolgreichen Boot festhalten.
- [ ] 0.2 QEMU als erste Referenzumgebung festlegen. Virtuelle Grafik, Eingabegeräte und später Netzwerk und Datenlaufwerk konkret dokumentieren. VMware nach wesentlichen Etappen zusätzlich prüfen.
- [ ] 0.3 In kleinen, getrennten Versuchen Grafikfläche, nicht blockierende Tastaturabfrage, Maus, monotone Zeitmessung, Dateisystem und Netzwerkkarte prüfen. Pro Versuch festhalten: funktioniert, fehlt oder benötigt eine bestimmte VM-Konfiguration. Noch keine vollständigen Dienste bauen.
- [ ] 0.4 Den möglichen Doom-Port kurz untersuchen: Welche Teile sind reine Spiel- und Renderlogik, welche benötigen externe Bibliotheken oder Betriebssystemfunktionen? Ein kleines Buildexperiment soll die größten Abhängigkeiten sichtbar machen.
- [ ] 0.5 Ergebnisse in `docs/COSMOS_SUPPORT.md` sammeln. Falls eine benötigte Funktion in 3.0.94 fehlt, gezielt zwischen eigener Ergänzung und einem geprüften Versionsupdate entscheiden.

**Fertig, wenn:** Der Ausgangskernel reproduzierbar startet und die nächsten Schritte auf geprüften Schnittstellen beruhen. Ein unbekannter Doom-Port bleibt als offene technische Frage sichtbar.

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

- [ ] 2.1 Einen `SystemLog` mit Zeitangabe, Komponente und Meldung einführen. Serielle Ausgabe und einen begrenzten Puffer im Speicher verwenden.
- [ ] 2.2 Initialisierung in benannte Schritte aufteilen: Eingabe, Anzeige, Dateien und später Netzwerk. Erfolg und Fehler jedes Schritts protokollieren.
- [ ] 2.3 Shellbefehle wie `version`, `uptime` und `log` ergänzen. Nur Informationen anzeigen, die tatsächlich aus einer verfügbaren Schnittstelle gelesen werden.
- [ ] 2.4 Einen Start in die Textshell als Wiederherstellungsmodus vorsehen. Wenn die grafische Initialisierung scheitert, soll die Diagnose erreichbar bleiben.

**Fertig, wenn:** Im seriellen Log erkennbar ist, bis wohin der Start kam, und ein Grafikfehler die Diagnose nicht unzugänglich macht.

## 3 Nicht blockierende Eingabe und gemeinsame Ereignisschleife

**Zweck:** Maus, Fenster und später Spiele müssen weiter reagieren, während jemand Text eingibt.

- [ ] 3.1 Eigene Ereignisse für gedrückte und losgelassene Tasten, Texteingabe, Mausbewegung und Maustasten definieren. Physische Tasten und eingegebene Zeichen getrennt behandeln.
- [ ] 3.2 Cosmos-Eingabe über einen kleinen `InputService` anbinden. Eine begrenzte Ereigniswarteschlange und ein definiertes Verhalten bei Überlauf vorsehen.
- [ ] 3.3 Eine zentrale Schleife einführen: Eingabe abholen → Ereignisse verteilen → Anwendungen kurz aktualisieren → bei Bedarf zeichnen → kurz warten oder CPU abgeben.
- [ ] 3.4 Für die Shell einen `LineEditor` bauen: Zeichen einfügen, Backspace und Enter; anschließend Pfeiltasten und Befehlshistorie ergänzen. `Console.ReadLine()` aus dem interaktiven Hauptpfad entfernen.
- [ ] 3.5 Zeitabstände mit einer monotonen Uhr berechnen. Lange Arbeit in kleine Schritte aufteilen; aufwendige Dateizugriffe und Netzwerkzugriffe später über geeignete Hintergrundarbeit entkoppeln.

**Fertig, wenn:** Eine regelmäßig aktualisierte Statusanzeige weiterläuft, während Text eingegeben wird, und Tastendrücke samt Loslassen zuverlässig ankommen.

## 4 Eine eigene grafische Fläche zeichnen

**Zweck:** Die Zeichenfunktionen bereitstellen, aus denen später der Desktop entsteht.

- [ ] 4.1 Die geprüfte Cosmos-Grafikfläche hinter `Graphics/DisplayService.cs` anbinden. Zunächst eine feste, in der Referenz-VM funktionierende Auflösung verwenden.
- [ ] 4.2 Hintergrund, gefüllte Rechtecke, Linien, Bitmaptext und einen Pixelpuffer zeichnen. Den Puffer später auch für die Doom-Ausgabe verwenden können.
- [ ] 4.3 In einen Bildpuffer zeichnen und das fertige Bild ausgeben. Vorhandene Pufferung von Cosmos verwenden, soweit sie ausreicht.
- [ ] 4.4 Einen Mauszeiger zeichnen, seine Position auf den Bildschirm begrenzen und Klicks sichtbar machen.
- [ ] 4.5 Zeichenbereiche beschneiden. Auch negative Koordinaten und teilweise außerhalb liegende Rechtecke dürfen keine Speicherzugriffe außerhalb des Puffers auslösen.

**Fertig, wenn:** Hintergrund, Text und beweglicher Mauszeiger stabil sichtbar sind und wiederholtes Neuzeichnen keine Spuren hinterlässt.

## 5 Fensterverwaltung entwickeln

**Zweck:** Mehrere grafische Anwendungen auf demselben Bildschirm bedienen können.

- [ ] 5.1 Eine `Window`-Klasse mit Position, Größe, Titel, Inhaltsbereich und Zeichenfunktion anlegen. Zunächst ein festes Fenster darstellen.
- [ ] 5.2 Eine Fensterliste im `WindowManager` führen und von hinten nach vorne zeichnen.
- [ ] 5.3 Beim Klick das oberste getroffene Fenster aktivieren. Tastatureingaben ausschließlich an das aktive Fenster geben.
- [ ] 5.4 Ziehen an der Titelleiste und Schließen implementieren. Mausereignisse während des Ziehens weiter an dieses Fenster liefern, auch wenn der Zeiger die Titelleiste verlässt.
- [ ] 5.5 Inhalte am Fensterbereich beschneiden. Beim Verschieben und Schließen den freigelegten Hintergrund korrekt neu zeichnen.

**Fertig, wenn:** Zwei überlappende Fenster verschoben, aktiviert und geschlossen werden können und Eingaben im richtigen Fenster ankommen.

## 6 Desktop und Terminal als erste Anwendung

**Zweck:** Das erste Kernziel erreichen: eine grafische Oberfläche mit benutzbarer Shell.

- [ ] 6.1 Ein kleines Anwendungsmodell einführen, etwa `IApplication` mit Start, Ereignisbehandlung, Aktualisierung, Zeichnen und Schließen.
- [ ] 6.2 `Apps/Terminal/TerminalApp.cs` bauen. Ausgabezeilen in einem begrenzten Textpuffer speichern und im Fenster darstellen.
- [ ] 6.3 `ShellService` und `LineEditor` aus den früheren Etappen anschließen. Ein Terminal bekommt seine eigene Sitzung mit Eingabe, Historie und später Arbeitsverzeichnis.
- [ ] 6.4 Scrollen durch ältere Ausgabe, einen sichtbaren Cursor und einen einfachen Starter für neue Terminalfenster ergänzen.
- [ ] 6.5 Aufräumen beim Schließen implementieren. Zunächst nur fest einkompilierte Anwendungen starten; ein Loader für fremde Programme ist eine spätere Aufgabe.

**Fertig, wenn:** ZehOS zum Desktop startet und zwei Terminalfenster unabhängig Befehle ausführen können.

Die Anwendungen können zunächst gemeinsam im Kernel laufen und kooperativ Rechenzeit abgeben. Ein Fehler kann dann das gesamte System betreffen. Prozessisolation und Speicherschutz bekommen eine spätere eigene Etappe; vor dem Umgang mit beliebigen fremden Inhalten müssen sie erneut bewertet werden.

## 7 Dateien lesen und dauerhaft speichern

**Zweck:** Einstellungen, Texte, Webseiten und Spieldaten laden können.

- [ ] 7.1 Ein separates virtuelles Datenlaufwerk erstellen und mit einem in Etappe 0 geprüften Controller an QEMU anschließen. Ein vorbereitetes FAT-Dateisystem verwenden; Schreibversuche zunächst auf einer Kopie durchführen.
- [ ] 7.2 Laufwerk erkennen und über Cosmos einhängen. Zunächst Dateien nur lesen. Den tatsächlich verfügbaren Mountpfad dokumentieren, beispielsweise `/mnt`.
- [ ] 7.3 Einen `FileService` für Pfadauflösung und begrenzte Lese- und Schreiboperationen einführen. Shellbefehle `ls`, `pwd`, `cd` und `cat` ergänzen.
- [ ] 7.4 Schreiben ergänzen: Verzeichnisse und kleine Textdateien anlegen, schließen und erforderliche Schreibpuffer leeren. Dafür `mkdir` und einen einfachen Schreibbefehl bereitstellen.
- [ ] 7.5 Fehlendes Laufwerk, ungültige Pfade, fehlende Dateien und vollen Datenträger behandeln. Ohne Datenlaufwerk soll der Desktop weiterhin starten.
- [ ] 7.6 Datei speichern, VM sauber beenden, erneut starten und Inhalt vergleichen. Danach bei Bedarf auch das VMware-Startprofil um ein Datenlaufwerk erweitern.

**Fertig, wenn:** Eine im Gast geschriebene Datei nach einem Neustart unverändert gelesen werden kann.

Ein RAM-Dateisystem ist für frühe Experimente möglich, erfüllt aber keine dauerhafte Speicherung. Ressourcen können anfangs auch einkompiliert werden; das ist kein Ersatz für den Neustarttest.

## 8 Kleine Desktopanwendungen bauen

**Zweck:** Die gemeinsamen Bausteine an überschaubaren Anwendungen prüfen, bevor Browser und Doom zusätzliche Komplexität bringen.

- [ ] 8.1 Wiederverwendbare Schaltflächen, Textfelder und Listen mit Fokus und einfacher Anordnung entwickeln.
- [ ] 8.2 Einen Dateibetrachter bauen: Ordner auflisten und eine ausgewählte Textdatei anzeigen.
- [ ] 8.3 Einen kleinen Texteditor ergänzen: Text bearbeiten, speichern und bei ungespeicherten Änderungen vor dem Schließen nachfragen.
- [ ] 8.4 Ein kleines Spiel wie Pong bauen. Tastenzustände, zeitabhängige Bewegung, Pixelgrafik und Rückkehr zum Desktop damit prüfen.
- [ ] 8.5 Bildzeiten und Speicherverbrauch beobachten. Große Bildpuffer wiederverwenden, statt sie in jedem Bild neu anzulegen. Ungenutzte Ressourcen beim Schließen freigeben.

**Fertig, wenn:** Eine Textdatei bearbeitet und neu geöffnet werden kann und das Spiel auf Eingaben reagiert, während der Desktop weiter bedienbar bleibt.

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

- [ ] 10.1 Eine von der verwendeten Cosmos-Version unterstützte virtuelle Netzwerkkarte einstellen. Erkennung, Treiber und Verbindungszustand protokollieren.
- [ ] 10.2 Zuerst mit fester IPv4-Konfiguration einen bekannten lokalen Testdienst erreichen. Danach DHCP und DNS ergänzen, soweit sie in der verwendeten Version verfügbar sind.
- [ ] 10.3 Diagnosebefehle wie `netinfo`, `ping` und `nslookup` an die tatsächlich unterstützten Dienste anbinden.
- [ ] 10.4 Eine TCP-Verbindung zu einem kleinen Testserver auf dem Entwicklungsrechner herstellen. Senden, Empfangen und Schließen einzeln prüfen.
- [ ] 10.5 Einen begrenzten HTTP-Client für kontrollierte Testseiten bauen: Statuszeile und Header lesen, aufgeteilte Antworten zusammensetzen, Antwortgröße und Wartezeit begrenzen. Der Testserver liefert anfangs einfache unkomprimierte Antworten mit klar definierter Länge.
- [ ] 10.6 Laden abbrechen können. Blockierende Verbindungsaufbauten und Lesezugriffe dürfen nicht in der Desktop-Schleife laufen. Dafür geprüfte Hintergrundarbeit mit Zeitlimits verwenden oder Zugriffe als nicht blockierende Zustandsmaschine aufbauen.

**Fertig, wenn:** `fetch` den Text einer Testseite ausgibt und Serverausfall oder Verbindungsabbruch den Desktop nicht einfrieren lassen.

Die aktuelle Cosmos-Netzwerkdokumentation nennt fehlendes TLS und damit fehlendes HTTPS als Einschränkung. HTTPS ist deshalb eine eigene technische Etappe und keine automatisch verfügbare Folge eines funktionierenden TCP-Clients. [Cosmos Netzwerk](https://cosmosos.github.io/articles/user/network.html)

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
- [ ] 12.5 Danach externe Programme, einen Programmlader, Benutzerrechte und breitere Hardwareunterstützung planen. Ein stabiler virtueller Referenzrechner bleibt für die Entwicklung erhalten.

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

Die ersten vier Änderungen sind mit der Textshell umgesetzt. Als Nächstes folgt die Diagnose aus Etappe 2:

- [x] `ShellService.ExecuteLine(...)` anlegen und die aktuelle Echo-Ausgabe dort als `echo` umsetzen.
- [x] `help`, `clear` und `about` über eine Befehlstabelle ergänzen.
- [x] Leerzeilen, unbekannte Befehle und Argumente in Anführungszeichen sauber behandeln.
- [x] Shellausgabe hinter `ITextOutput` legen, damit später ein Terminalfenster angeschlossen werden kann.
- [ ] Serielle Diagnose und einen begrenzten Logpuffer hinzufügen; danach die nicht blockierende Eingabe vorbereiten.

## Technische Quellen

- [Cosmos Gen3](https://github.com/CosmosOS/Cosmos): Grundlage und Übersicht der bereitgestellten Systemfunktionen.
- [Cosmos Grafik](https://cosmosos.github.io/articles/user/graphics.html): Zeichenflächen, Bildausgabe und Abgrenzung zum selbst zu entwickelnden Fenstersystem.
- [Cosmos Dateisystem](https://cosmosos.github.io/articles/user/filesystem.html): virtuelle Laufwerke, Mountpunkte und Dateioperationen.
- [Cosmos Netzwerk](https://cosmosos.github.io/articles/user/network.html): Netzwerkfunktionen und aktuelle Einschränkungen.
- [Managed Doom](https://github.com/sinshu/managed-doom): möglicher C#-Spielkern; die vorhandenen Desktopbibliotheken benötigen eine passende ZehOS-Anbindung.
- [doomgeneric](https://github.com/ozkl/doomgeneric): möglicher alternativer Portierungsweg in C.

Die Reihenfolge und die Architekturvorschläge sind auf den derzeitigen ZehOS-Code zugeschnitten. Die verlinkten Projekte dienen als technische Grundlage; sie bestätigen keine bereits funktionierende Integration in ZehOS.
