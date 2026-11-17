# ProjektZeh

[![Build and tests](https://github.com/peterzw98/ProjektZehOS/actions/workflows/ci.yml/badge.svg)](https://github.com/peterzw98/ProjektZehOS/actions/workflows/ci.yml)

ProjektZeh ist ein Cosmos-Gen3-Kernel für x64 mit einem grafischen Desktop und einer kleinen Shell in unabhängigen Terminalfenstern.

## Shell

Nach der Anmeldung lässt sich über das Startmenü oder die angeheftete Terminalschaltfläche ein Konsolenfenster mit dem Prompt `zehos>` öffnen. Im Wiederherstellungsmodus erscheint derselbe Prompt direkt in der Textshell. Die Befehle sind:

| Befehl | Funktion |
| --- | --- |
| `help` | Verfügbare Befehle mit Verwendung anzeigen |
| `echo [Text ...]` | Argumente als Text ausgeben |
| `clear` | Konsole leeren |
| `reboot` | Datenlaufwerk aushängen und ZehOS neu starten |
| `shutdown` | Datenlaufwerk aushängen und den Rechner ausschalten |
| `about` | Informationen über ZehOS anzeigen |
| `pwd` | Aktuelles Arbeitsverzeichnis anzeigen |
| `cd <Pfad>` | Arbeitsverzeichnis wechseln |
| `ls [Pfad]` | Dateien und Unterverzeichnisse auflisten |
| `cat <Datei>` | Textdatei lesen |
| `mkdir <Pfad>` | Verzeichnis anlegen |
| `write <Datei> <Text>` | Textdatei anlegen oder ihren Inhalt ersetzen |
| `disk list` | Erkannte Laufwerke mit Größe und Partitionszahl anzeigen |
| `disk init <Name> [--yes-large] [--yes-overwrite]` | Leere Platte (64 MiB bis 128 GiB; über 4 GiB nur Stichprobenprüfung und nur mit `--yes-large`) mit MBR und FAT32 (Label `ZEHOSDATA`) einrichten; ist schon `/data` eingebunden oder trägt `sata0` eine Partition, verlangt der Befehl zusätzlich `--yes-overwrite` |
| `disk use <Name> --yes` | Auf die `ZEHOSDATA`-Partition des Laufwerks umschalten; ein eingebundenes `/data` wird vorher ausgehängt. Der Boot bindet eine einzelne `ZEHOSDATA`-Platte ohne Bestand auf `sata0` nur dann selbst ein, wenn sie schon eine Kontendatei trägt |
| `disk mount <Name> [--yes]` | Einzige FAT-Partition des Laufwerks unter `/data` einbinden; `sata0` (Bestand) ohne, jede andere Platte nur mit `--yes` |
| `disk unmount` | `/data` mit Schreiben der Dateisystempuffer aushängen |
| `version` | Versionen der geladenen ZehOS- und Cosmos-Assemblies anzeigen |
| `uptime` | Millisekunden seit ZehOS-Start anzeigen; ohne initialisierten Timer als nicht verfügbar melden |
| `log` | Die letzten maximal 128 Systemdiagnosen in zeitlicher Reihenfolge anzeigen |
| `frametimes [on\|off]` | Bildzeit laufender Paket-Apps in Anteile zerlegen (App-Wanduhrzeit einschließlich Verdrängung durch den Desktop, `sleep_ms`, Bildkopie, Skalierung, Desktop, Anzeige); bei `on` je 60-s-Messfenster eine Zeile `Anteile …` im seriellen Protokoll, ohne Argument der aktuelle Stand |

Befehlsnamen sind unabhängig von Groß- und Kleinschreibung. Doppelte Anführungszeichen fassen Argumente zusammen, beispielsweise `echo "Hallo ZehOS"`. Backslashes bleiben normale Zeichen; Escape-Sequenzen werden nicht ausgewertet. Pipes (`|`) und abschließende Ausgabeumleitung (`>`/`>>`) funktionieren außerhalb doppelter Anführungszeichen. Leerzeilen werden ignoriert. Bei unbekannten Befehlen, falschen Argumenten oder nicht geschlossenen Anführungszeichen erscheint eine Fehlermeldung und danach der nächste Prompt.

Die Befehlsauswertung in `ProjektZeh/Shell` ist von der Ausgabe über `ITextOutput` getrennt und wird von Textshell und Terminalfenstern gemeinsam verwendet. Die Texteingabe verwendet einen nicht blockierenden `LineEditor`: Links/Rechts, Pos1/Ende, Backspace und Entf bearbeiten die Zeile; Hoch/Runter blättern durch die letzten 32 Befehle und zurück zum aktuellen Entwurf. Lange Zeilen scrollen horizontal und sind auf 66.560 Zeichen begrenzt. Standard ist die deutsche QWERTZ-Belegung mit Umlauten, ß und AltGr (etwa @, €, { }, [ ], Backslash und |). Shift und CapsLock werden getrennt ausgewertet; AltGr funktioniert auch mit CapsLock.

In der Textshell läuft oben eine Statusanzeige für Zeit, gedrückte Tasten und verlorene Eingabeereignisse weiter, auch während eine Zeile bearbeitet wird. Der Desktop zeigt Fensterzahl und Laufzeit in der unteren Leiste. Physische Tastendrücke und das Loslassen werden getrennt von Texteingaben verarbeitet. Bei einem Überlauf der begrenzten Eingabepuffer werden die Tastenzustände zurückgesetzt und ein Ziehvorgang beendet.

## Benutzer und Dateirechte (Etappe 6c, noch in Prüfung)

Dateizugriffe erfordern eine Anmeldung und gültige Konten-/Rechtemetadaten auf `/data`. Fehlende oder beschädigte Metadaten sperren den Zugriff; bestehende Dateien werden nicht automatisch übernommen. `whoami` zeigt die Identität, `login <Name>` fragt das Kennwort verdeckt ab, `logout` meldet nur die aktuelle Sitzung ab. Kennwörter enthalten 12 bis 128 Zeichen; die Berechnung läuft im Hintergrund. Kennwörter erscheinen weder in der Ausgabe noch im Verlauf. Global laufen höchstens zwei Kennwortberechnungen gleichzeitig, auch wenn ihre Terminalfenster geschlossen wurden. Weitere Eingaben werden bis zum Abschluss abgewiesen. `shutdown` und `reboot` warten nicht blockierend, sondern melden während laufender Kennwortberechnungen einen Fehler; danach kann die Energieaktion erneut gestartet werden. Die Hashes verwenden BouncyCastle 2.7.0 mit PBKDF2-SHA256, zufälligem 16-Byte-Salt und 600000 Iterationen. Fällt RDSEED aus, wird RDRAND verwendet. Fehlen beide oder versagen sie, springt ein virtio-rng-Gerät ein (QEMU: `-object rng-random,id=rng0,filename=/dev/urandom -device virtio-rng-pci,rng=rng0`). Ohne jede dieser Quellen wird die Kennwortanlage mit „Ohne RDRAND/RDSEED/virtio-rng keine Kontoanlage oder Kennwortaenderung.“ abgewiesen.

Die Rechtedatei enthält einen Eintrag je Datei und Ordner und wird bei jeder Änderung vollständig neu geschrieben. Sie ist auf 1 Mi Zeichen begrenzt, je nach Pfadlänge einige tausend bis rund 10000 Einträge. Ist sie voll, scheitert nur das Anlegen oder Verschieben der neuen Datei mit „Rechtedatenbank voll“; vorhandene Dateien und angemeldete Sitzungen bleiben unberührt. Bei sehr vielen Einträgen wird jede Dateierstellung durch das Neuschreiben spürbar langsamer.

Beim Start fragt ein Anmeldefenster des Desktops Benutzername und Kennwort ab; die Textshell zeigt denselben Ablauf zeilenweise mit verdecktem Kennwort. Beide melden die gemeinsame Sitzung von Textshell, Desktop, Dateien und Texteditor an. Neu geöffnete Terminals starten als dieser Benutzer und können danach mit `login`/`logout` unabhängig wechseln. **Abmelden** im Ein/Aus-Untermenü des Startmenüs oder `logout` in der Textshell sperrt den Desktop wieder und verwirft eine offene Kennwortabfrage der Textshell. Bei jedem Wechsel dieser Sitzung schließt der Desktop alle Fenster und meldet damit auch deren Terminals ab; ungespeicherte Entwürfe gehen ohne Rückfrage verloren, auch wenn Konten nach einem Speicherfehler gesperrt werden. Eine bereits laufende Kennwortprüfung einer Anmeldung wird nach einem zwischenzeitlichen Abmelden verworfen. Der Zeilenverlauf der Textshell beginnt bei jedem Wechsel neu. Eine Anmeldung in einem Terminal verändert die Desktopidentität nicht. Jede Dateianwendung erhält ein eigenes Arbeitsverzeichnis und prüft jede Operation über denselben Dateidienst; nach Logout werden weitere Zugriffe verweigert. Im Terminal werden bei einem Identitätswechsel auch Ausgabe, Entwurf, Verlauf, Aliase, Pager und die interne Zwischenablage verworfen. Bereits angezeigte Inhalte und ungespeicherte Entwürfe anderer Dateianwendungen bleiben bis zum Schließen des Fensters erhalten. Neue Editor-Dateien liegen zunächst im Benutzerverzeichnis.

Administratoren legen Konten mit `user add <Name> user` beziehungsweise `user add <Name> admin` an; `user list` zeigt die Konten. `grant <Pfad> <Name> r` vergibt Leserechte, `rw` zusätzlich Schreibrechte, `none` entfernt eine Freigabe. Für das Lesen einer Datei in einem fremden Home muss der Administrator auch auf diesem Verzeichnis Leserechte vergeben. Anlegen und Ersetzen benötigen Schreibrechte auf dem Elternverzeichnis, weil der sichere Schreibpfad dort temporäre Dateien und Sicherungen anlegt; `rw` auf der Datei allein genügt nicht. Eigentümer dürfen Rechte auf ihren Einträgen selbst vergeben. `security adopt <Pfad> <Name>` ordnet als Administrator einen bestehenden Eintrag ausdrücklich zu. `user del <Name>` löscht ein fremdes Konto, meldet dessen Sitzungen ab und überträgt seine Einträge samt Home an den ausführenden Administrator. Das Home eines gelöschten Kontos bleibt bestehen; derselbe Name kann deshalb nicht erneut angelegt werden. `passwd` ändert das eigene Kennwort verdeckt, `passwd <Name>` als Administrator ein fremdes; das bisherige Kennwort wird dabei nicht abgefragt.

Befehlsrechte: Administratoren dürfen jeden Befehl ausführen. Für alle anderen sind `user`, `security`, `disk`, `update`, `chperm`, `reboot` und `shutdown` anfangs gesperrt, alle übrigen Befehle frei. `chperm <Befehl> admin` beschränkt einen Befehl auf Administratoren, `chperm <Befehl> *` gibt ihn frei; `perms` zeigt die Tabelle. `login`, `logout` und `chperm` lassen sich nicht umstellen. Freigegebene Befehle (`*`) stehen auch einem abgemeldeten Terminal zur Verfügung; Dateizugriffe verlangen weiterhin eine Anmeldung. Die Tabelle liegt in der reservierten Datei `/data/.zehos-commands.db`; ist sie beschädigt, bleiben Konten gesperrt wie bei beschädigten Kontodaten. Solange keine gültigen Konten geladen sind, gilt die Tabelle nicht, damit Einrichtung, Recovery und Energieaktionen erreichbar bleiben; die Befehle prüfen ihre eigenen Voraussetzungen weiterhin. Laufwerksverwaltung erfordert Administratorrechte. Nach dem Aushängen ist die Kontendatei nicht mehr erreichbar; zum automatischen Einbinden regulär neu starten oder den expliziten Recovery-Build verwenden.

Fehlt auf einem eingebundenen FAT32-Laufwerk die Kontodatei `/data/.zehos-security.db` (nicht beschädigt, sondern tatsächlich nicht vorhanden), bietet das Anmeldefenster beziehungsweise die Textshell an, den ersten Administrator anzulegen (Name, Kennwort, Wiederholung; intern `security setup <Name>`). Das verwendet denselben Weg wie die Recovery; vorhandene Nutzdateien bleiben bis `security adopt` gesperrt. Wer die Kontodatei außerhalb von ZehOS löscht, kann deshalb im normalen Build einen neuen Administrator anlegen; zuvor war dafür der Sicherheits-Recovery-Build nötig. Das gilt nur beim Boot und nur mit tatsächlich eingebundenem `/data`: wechselt `/data` erst in der laufenden Sitzung (`disk mount`, `disk use`) auf ein Laufwerk ohne Kontodatei, wird gesperrt statt eingerichtet; dann entweder neu starten (das Konto entsteht beim Boot) oder im Sicherheits-Recovery-Build `security recover <Adminname>` verwenden. Nach `disk unmount` sind die Konten bis zum nächsten Neustart gesperrt (die Anmeldung fehlt dann auch für `disk mount`). Ohne eingebundenes Laufwerk oder bei beschädigten Metadaten zeigt das Anmeldefenster „Konten gesperrt“.

Die Einrichtung einer leeren Testplatte (`disk init`) und die Wiederherstellung beschädigter Konten erfolgen nur in einem ausdrücklich gebauten Sicherheits-Recovery-System:

```powershell
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSSecurityRecovery=true
```

Dieses ISO startet in der Textshell. Auf einer vollständig leeren **isolierten 64-MiB-Testplatte** zuerst `disk init sata0`, dann `disk mount sata0` ausführen (danach den Gast neu starten, damit die Ersteinrichtung beim Boot den ersten Administrator anlegt, oder im Sicherheits-Recovery-Build `security recover <Adminname>` verwenden; in der laufenden Sitzung wird sie nicht angeboten); ein vorhandenes FAT32-Laufwerk nicht initialisieren. `security recover <Adminname>` fragt ein neues Kennwort ab und ersetzt Konten/Rechte ausdrücklich. Eine vorhandene Metadatendatei wird unverändert als `.zehos-security-recovery-<Kennung>.db` erhalten. Vorhandene Nutzdateien bleiben bis zur ausdrücklichen Zuordnung gesperrt. Anschließend normal ohne Recovery-Eigenschaft neu bauen und starten. Metadaten, temporäre Dateien und Sicherungen mit reserviertem `.zehos-`-Präfix sind für normale Dateioperationen gesperrt.

Die Regeln gelten für die kontrollierten ZehOS-Dienste. Anwendungen laufen weiterhin gemeinsam im Kernel; diese Rechte bieten keine Isolation gegenüber fremdem Kernelcode. Aktuelle Nachweise und offene Gastprüfungen stehen in `docs/ETAPPE6C_STATUS.md`.

## Desktop und Fenster

ZehOS startet zu einem freien Desktop mit **1920 × 1080 Pixeln und 32 Bit Farbtiefe**. Das Limine-Bootmenü wartet drei Sekunden; der zweite Eintrag startet mit **1024 × 768** als sichere Rückfallwahl für Anzeigen ohne Full HD. Unterstützt werden 1024 × 720 bis 1920 × 1080.

Die Taskleiste unten zeigt mittig nur Symbole: Start, angeheftete Starter für Dateien, Terminal und Einstellungen sowie die Fenster des aktuellen virtuellen Desktops. Rechts folgen die Desktopumschaltung als vier Punkte (aktueller Desktop als Akzentpille, belegte hell), das Netzwerksymbol und die Uhr. Die Uhr zeigt die Ortszeit Europe/Berlin (MEZ/MESZ nach EU-Sommerzeitregel) und setzt voraus, dass die Hardwareuhr auf UTC steht. Aktive Fenster tragen einen langen Akzentbalken, geöffnete einen kurzen, minimierte erscheinen gedämpft; Fenstertitel stehen nur in der Titelleiste. **Start** oder **Alt+F1** öffnet das Startmenü mittig im Stil von Windows 11: Suchfeld oben, darunter alle Apps einschließlich installierter Pakete als Kacheln, unten Benutzername und Ein/Aus-Symbol. Ein Klick markiert, ein Doppelklick auf dieselbe Kachel innerhalb von 450 ms startet sie genau einmal; Enter startet die Auswahl ebenfalls, Pfeiltasten bewegen die Auswahl im Raster. Rechtsklick oder **Umschalt+F10** auf eine Kachel öffnet ein Kontextmenü mit **Auf Desktop legen** bzw. **Vom Desktop entfernen**. Tab/Shift+Tab wechseln den Fokus zwischen Suche, Kacheln und Ein/Aus. Das Ein/Aus-Symbol öffnet ein Untermenü mit **Abmelden**, **Neu starten** und **Herunterfahren**; Pfeiltasten wählen, Enter führt aus, Esc schließt zuerst nur das Untermenü. Neu starten und Herunterfahren verwenden dieselben Befehle wie die Shell. Den Taskmanager öffnet seine Kachel. Gestaltung und Bedienung orientieren sich an KDE; ZehOS verwendet weiterhin Cosmos und führt keine Linux-Programme aus.

**Einstellungen** hat zwei Bereiche. *Anzeige* zeigt die tatsächlich aktive Auflösung. Meldet der Treiber mehrere Modi (in den installierten Cosmos-Paketen nur VMware SVGA), wird eine Auswahl mit 15 Sekunden Bestätigungsfrist und automatischer Rückkehr angeboten. `scripts/start-qemu.ps1` startet QEMU deshalb mit `-vga vmware`; dann lässt sich die Auflösung zwischen 1024×768 und 1920×1080 im Betrieb ändern. Der Gast startet dabei mit 1024×768 und stellt eine bestätigte Auflösung nach der Anmeldung wieder her. Mit `-StandardVga` verwendet das Skript die Standardgrafik; deren Limine-Bildpuffer meldet genau einen Modus, dann verweist die Seite auf das Bootmenü. *Personalisierung* wechselt sofort zwischen hell und dunkel, sechs Akzentfarben und vier eingebauten Hintergründen. Ein eigenes Bild ist ein unkomprimiertes 24/32-Bit-BMP bis 4 Megapixel und 16 MiB; ungültige oder fehlende Dateien lassen den bisherigen Hintergrund unverändert. Die Werte werden als `zehos-desktop.conf` im Heimatverzeichnis der Desktopsitzung gespeichert. Ohne beschreibbares Laufwerk gelten sie nur für die Sitzung, die App zeigt das als Fehler an.

Der **Taskmanager** zeigt Auslastung der Desktop-Schleife mit 60-Sekunden-Verlauf (keine Gesamt-CPU), freien physischen Speicher laut Cosmos-Seitenverwalter, Bildrate und Betriebszeit, die laufenden Anwendungen mit Aktivieren und regulärem Schließen samt Rückfragen sowie die Abschnitte Datenträger, Netzwerk (erkannte Adapter, MAC, Link, IPv4) und System. Weitere Abschnitte werden als `TaskManagerSection` (Titel und Zeilenquelle) übergeben.

Eine Anzeigeauswahl wird erst mit **Beibehalten** dauerhaft gespeichert. Beim nächsten Anmelden wird sie erneut mit Bestätigungsfrist angewendet und die Einstellungsapp geöffnet. Ohne bestätigte Wahl bleibt die Auflösung des Bootmenüs erhalten; ältere Einstellungsdateien ohne ausdrückliche Bestätigung übernehmen nur die Personalisierung. Esc/F4 zur Textshell nimmt einen noch offenen Auflösungswechsel sofort zurück. Scheitert die Rücknahme, endet der Versuch mit einer Fehlermeldung und der tatsächlich gemeldeten Auflösung.

Eigene 32-Bit-BMPs dürfen auch unkomprimierte Farbmasken (`BI_BITFIELDS`/`BI_ALPHABITFIELDS`, getrennte 8-Bit-RGB-Kanäle) mit V4-/V5-Kopf verwenden; der Hintergrund wird deckend dargestellt. Auch das Laden von Bilddateien prüft die Dateirechte. Beim Abmelden verschwinden private Hintergrundbilder; nach Anmeldung werden die Einstellungen aus dem jeweiligen Benutzerverzeichnis neu geladen. Die Anwendungsliste des Taskmanagers hält ihre Auswahl an der Fensteridentität fest, damit eine geschlossene Anwendung nicht versehentlich durch eine andere ersetzt wird.

Ein Klick aktiviert ein Fenster; **Alt+Tab** beziehungsweise **Alt+Shift+Tab** wechseln durch die Fenster des aktuellen Desktops. Die Titelleiste lässt sich mit der linken Maustaste ziehen, die Ränder und Ecken verändern die Größe bis zur jeweiligen Mindestgröße. **_** minimiert, **□** maximiert beziehungsweise stellt die bisherige Geometrie wieder her, **X** schließt mit der gegebenenfalls erforderlichen Rückfrage. Fenster bleiben oberhalb der unteren Leiste. Ein Klick auf einen Eintrag in der Fensterleiste aktiviert oder restauriert ihn; beim aktiven Fenster minimiert er es. Bei wenig Platz kürzt die Leiste die Titel hinter dem Programmsymbol.

In QEMU stellen die Visual-Studio-Projektkonfiguration und `scripts/start-qemu.ps1` ein USB-Tablet bereit. Es überträgt die absolute Position des Hostzeigers und vertikale Scrollbewegungen an ZehOS. Ein Trackpad am Host kann damit Zeiger, Klicks und Zwei-Finger-Scrollen an QEMU weiterreichen, sofern das QEMU-Fenster beziehungsweise der verwendete Anzeigeclient diese Ereignisse übernimmt. Ein direkter Hardwaretreiber für Laptop-Trackpads ist damit nicht verbunden. Bei eingefangenem Zeiger liefern PS/2-Mäuse auch am Gastbildschirmrand relative Bewegungen; absolute Tablets bleiben auf den Bildschirm- beziehungsweise Hostfensterrand begrenzt. `cosmos run` benötigt die Geräteargumente ausdrücklich wie im Startbeispiel unten; die CLI 3.0.94 übernimmt sie nicht aus der Projektkonfiguration.

Es gibt vier virtuelle Desktops. **Strg+Alt+Pfeil links/rechts** wechselt zum vorherigen beziehungsweise nächsten, **Strg+Alt+1** bis **4** direkt; mit zusätzlicher **Umschalttaste** wandert das aktive Fenster mit. Ein Klick auf **1–4** in der Taskleiste wechselt ebenfalls; ein Punkt darunter zeigt, dass dort Fenster geöffnet sind. Neue Fenster erscheinen auf dem aktuellen Desktop, Fenster anderer Desktops laufen weiter, ohne gezeichnet zu werden.

Auf der freien Desktopfläche liegen Verknüpfungen, anfangs zu Terminal, Dateien, Texteditor und Einstellungen. Ein Klick markiert, ein Doppelklick startet; nach einem Klick bedienen Pfeiltasten und Enter die Symbole. Die Auswahl wird zusammen mit den übrigen Desktopeinstellungen in `zehos-desktop.conf` gespeichert. Verknüpfungen zu deinstallierten Paketen bleiben gespeichert und erscheinen nach einer erneuten Installation wieder.

Höchstens acht Fenster sind gleichzeitig geöffnet, über alle Desktops und einschließlich minimierter Fenster. Jedes Terminal besitzt seine eigene Shell, Eingabe, Historie und sein eigenes Arbeitsverzeichnis, anfangs `/data`. Die letzten **256 Ausgabezeilen** werden beim Ändern der Breite neu umgebrochen; überschrittene Puffergrenzen verdrängen weiterhin die älteste Ausgabe. **Scrollrad/Trackpad-Geste und Bild auf/Bild ab (PageUp/PageDown)** blättern durch ältere Ausgabe; Texteingabe springt zur neuesten Ausgabe. `clear` leert dessen Ausgabe. Auch nach dem Schließen aller Fenster bleibt das Menü erreichbar.

**Esc** schließt zuerst ein geöffnetes Programmmenü. Bei eingefangener App-Maus gibt das erste Esc den Zeiger frei und erreicht die App; das nächste Esc führt zur Textshell. Sonst führt Esc direkt zur Textshell; **F4** wechselt zwischen Desktop und Textshell. Fensterzustände, Terminalausgaben und Eingabeentwürfe bleiben erhalten. Der Textshell-Ausgabepuffer bleibt beim Wechsel erhalten; deren eigener Eingabeentwurf und Verlauf bleiben erhalten. Grafikfehler beim Öffnen oder Zeichnen führen zur erreichbaren Textshell zurück.

`WindowManager` zeichnet sichtbare Fenster von hinten nach vorn und beschneidet jeden Inhalt am Fensterbereich. Hintergrund und Fenster werden im vorhandenen Cosmos-Puffer gezeichnet; zusätzliche Bildpuffer pro Fenster sind nicht erforderlich. `IApplication` enthält auch Größenänderung und abbrechbares Schließen. Das Menü verwendet dieselben UI-Bausteine wie die [Anwendungen aus Etappe 8](docs/DESKTOP_APPS.md). Der [Desktop-Umbau](docs/DESKTOP_REDESIGN.md) hält Integration und neue Prüfergebnisse getrennt von deren ursprünglichen Nachweisen fest.

Die gemeinsame Schleife verarbeitet pro Durchlauf höchstens 64 rohe Tastaturereignisse, verteilt Eingaben, aktualisiert die Statusanzeige und zeichnet bei Änderungen mit höchstens etwa 60 Bildern pro Sekunde. Die Zeitmessung verwendet `Stopwatch`; zwischen Durchläufen wartet Cosmos kurz. Datei- und Laufwerksbefehle laufen weiterhin synchron und können die Anzeige während ihrer Ausführung anhalten. Die spätere Hintergrundverarbeitung ist damit noch nicht umgesetzt.

Die Anzeige **FPS** zählt abgeschlossene Bildausgaben im Messintervall, **ms** daneben die längste Zeichen-/Ausgabedauer dieses Intervalls. Der ruhende Desktop zeichnet absichtlich nur bei Änderungen; eine niedrige FPS-Zahl im Leerlauf ist daher kein Leistungslimit. Bei dauernder Mausbewegung beziehungsweise Pong werden feste 60-Hz-Bildtermine verwendet. Im QEMU-Referenztest stieg die gemessene Bildrate von etwa 15–16 auf 58–61 FPS. Bei Full HD zeichnet der Desktop die Szene nur bei inhaltlichen Änderungen neu; reine Mausbewegung stellt aus einer gespeicherten Szene nur den Bereich unter dem alten Zeiger wieder her. Ursache und Grenzen beschreibt [DESKTOP_PERFORMANCE.md](docs/DESKTOP_PERFORMANCE.md). Die Bildrate steht im Taskmanager statt auf dem Desktop.

Geprüfte Cosmos-Schnittstellen, Referenz-VM und Grenzen stehen unter [`docs/COSMOS_SUPPORT.md`](docs/COSMOS_SUPPORT.md).

Gemeinsame Ergebnis- und Fehlertypen liegen unter [`ProjektZeh/Shared`](ProjektZeh/Shared/README.md). Die für ZehOS entwickelte Implementierung trennt unveränderliche Statusinformationen von optionalen Ergebniswerten. Erfolg wird über `CreateSuccess` gesetzt, Fehler über `CreateFailure` oder `CreateFromException`; `IsOK()` prüft den Status.

Die MSTest-Tests mit Moq prüfen dieselben Shell-, Datei-, Fenster-, Desktop- und Terminalquellen in einer normalen .NET-Umgebung ohne den Bare-Metal-Kernel. Der Dateidienst wird gegen echte temporäre Verzeichnisse getestet; Cosmos-Treiber und dauerhafte Speicherung nach einem VM-Neustart benötigen zusätzlich einen Gasttest:

```powershell
dotnet test ProjektZeh.Tests\ProjektZeh.Tests.csproj
```

Unter Windows benötigen die Symlink-Prüfungen das Recht zum Erstellen symbolischer Links (etwa über den Entwicklermodus). Ohne dieses Recht werden nur diese Tests als nicht ausführbar gemeldet; die übrigen Pfadprüfungen laufen weiter. Die Python-Skripttests starten mit `python -m unittest discover -s scripts/tests`. Für die Release-Skripttests muss Bash im `PATH` liegen, unter Windows beispielsweise Git Bash.

Die CI misst die Zeilenabdeckung der im Hostbuild enthaltenen Produktivquellen und scheitert unter der in `.github/workflows/ci.yml` gesetzten Grenze. Lokal entsteht der Bericht mit dem Sammler des Test-SDK; `scripts/check-coverage.py` wertet ihn aus:

```powershell
dotnet test ProjektZeh.Tests\ProjektZeh.Tests.csproj --configuration Release --collect "Code Coverage;Format=cobertura" --results-directory TestResults
python scripts/check-coverage.py (Get-ChildItem TestResults -Recurse -Filter *.cobertura.xml)[0].FullName --min-total 92 --min-file 50 --exclusions scripts/coverage-exclusions.txt
```

`scripts/coverage-exclusions.txt` nennt die wenigen Dateien, die nicht mitzählen, jeweils mit Grund. Die Abdeckung sagt nur, welche Zeilen ein Test ausführt; Kernelstart, Treiber und alle `Cosmos*`-Adapter sind darin nicht enthalten.

## Dateisystem in QEMU

Der Dateizugriff verwendet das bereits eingebundene `Cosmos.Kernel.System` 3.0.97. Ein zusätzliches NuGet-Paket ist nicht erforderlich. `CosmosStorageService` übernimmt Laufwerkseinrichtung und Mounten, `FileService` den Zugriff über `System.IO`. Die Shell hat ein eigenes Arbeitsverzeichnis, anfangs `/data`; relative Pfade sowie `.` und `..` beziehen sich darauf. Textdateien sind beim Lesen und Schreiben auf 65.536 Zeichen begrenzt.

Kernel bauen und QEMU mit einer separaten virtuellen SATA/AHCI-Datenplatte starten:

```powershell
cosmos build --project ProjektZeh\ProjektZeh.csproj --arch x64
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-qemu.ps1
```

Das Skript erstellt beim ersten Aufruf ein leeres 64-MiB-Image unter `.tools\qemu\data-fat32.img`. Bei weiteren Starts verwendet es dieselbe Datei unverändert wieder. Das Image ist lokal und wird nicht in Git aufgenommen. Ein bisheriges FAT16-Image unter `.tools\qemu\data.img` bleibt erhalten; es wird weder überschrieben noch automatisch konvertiert. Mit `-IsoPath <Pfad>` lässt sich auch das in Visual Studio gebaute ISO starten. Das bisherige Cosmos-`F5`-Debugprofil hängt diese Datenplatte nicht automatisch an. Mit `-DataImagePath <Pfad>` verwendet das Skript stattdessen ein vorhandenes RAW-Datenimage (reguläre Datei, mindestens 64 MiB, Vielfaches von 512 Byte, Pfad ohne Komma). Das Image wird nur geprüft, nie angelegt, formatiert oder vergrößert; mit `-PrepareDiskOnly` endet das Skript nach der Prüfung.

Nur beim ersten Start in der ZehOS-Shell ausführen:

```text
disk list
disk init sata0
disk mount sata0
```

`disk init` ist auf leere Platten von 64 MiB bis 128 GiB mit 512-Byte-Sektoren begrenzt. Bis 4 GiB wird die ganze Platte auf Nullen geprüft; darüber nur Kopf und Ende (je 64 MiB, mit MBR/GPT- und Dateisystemkennungen) sowie 4 KiB alle 64 MiB, und nur mit `disk init <Name> --yes-large`. Daten zwischen den Stichproben werden dort nicht erkannt. Bereits vorhandene Partitionen oder Daten werden abgelehnt. Die Einrichtung erstellt eine FAT32-LBA-Partition (MBR-Typ `0x0C`) und formatiert FAT32; Cosmos bestimmt die dazu passende Clustergröße. Das Laufwerk wird anschließend mit `disk mount` eingebunden. Ein fehlgeschlagener oder abgebrochener Einrichtungsvorgang wird beim Booten nicht automatisch repariert oder wiederholt. Beim Boot gilt der Bestand auf `sata0` (Bestand = `sata0` = AHCI-Port 0): trägt `sata0` eine Partition vom Typ `0x0C` (Label egal, im Zweifel gilt Bestand), wird nur sie (genau eine) unter `/data` eingebunden; eine `ZEHOSDATA`-Platte daneben wird nicht bevorzugt und nur im seriellen Protokoll vermerkt. Ohne Bestand wird eine einzelne `ZEHOSDATA`-Platte nur dann automatisch eingebunden, wenn sie schon eine Kontendatei `.zehos-security.db` trägt (der Kernel bindet sie dazu schreibgeschützt ein, prüft und hängt sie wieder aus); eine leere `ZEHOSDATA`-Platte wird nie automatisch eingebunden, sondern nur gemeldet und erst mit `disk use <Name> --yes` verwendet. Zwei oder mehr `ZEHOSDATA`-Platten ergeben einen Fehler (`AmbiguousDataDisk`). `disk mount <Name>` bindet nur `sata0` ohne Bestätigung ein, jede andere Platte nur mit `--yes`. Bei `AmbiguousDataDisk`, einer erkannten, aber nicht eingebundenen Platte oder einem fehlgeschlagenen Einbinden wird keine Administrator-Ersteinrichtung angeboten und kein Konto angelegt, stattdessen steht eine Fehlerzeile (`STORAGE Fehler: …`) im seriellen Protokoll; ohne jede Datenplatte bleibt der Boot wie bisher. Die Ersteinrichtung gibt es nur beim Boot und nur mit tatsächlich eingebundenem `/data` und ohne vorhandene Kontendatei; wechselt `/data` in einer laufenden Sitzung (`disk mount`, `disk use`) auf eine Platte ohne Kontendatei, wird gesperrt statt eingerichtet (neue Konten dann nur im Sicherheits-Recovery-Build mit `security recover`). Schlägt `disk init` nach dem Schreiben des MBR fehl, bleibt dieser MBR zurück und ein zweites `disk init` lehnt die Platte als nicht leer ab; die Fehlermeldung nennt das. Datenplatten werden nur über MBR-Partitionen erkannt; GPT-Platten gelten nie als Datenplatte. Jeder Build schreibt beim Boot je Laufwerk Größe und Partitionszahl ins serielle Systemprotokoll (für die Gasttests).

Danach beispielsweise:

```text
mkdir texte
cd texte
pwd
write hallo.txt "Hallo ZehOS!"
cat hallo.txt
ls
disk unmount
```

Mit `shutdown` ausschalten und mit demselben Startskript erneut öffnen oder mit `reboot` direkt neu starten. ZehOS versucht beim Start ausschließlich die vorhandene Partition von `sata0` unter `/data` einzubinden; es formatiert dabei nichts. Mit `cat /data/texte/hallo.txt` muss der zuvor gespeicherte Inhalt erscheinen. Beide Energieaktionen hängen ein eingebundenes Datenlaufwerk vorab mit Schreiben der Dateisystempuffer aus. Schlägt das Aushängen fehl, wird die Aktion abgebrochen und die Shell bleibt bedienbar. Ohne eingebundenes Datenlaufwerk sind beide Befehle ebenfalls verfügbar. Sie akzeptieren keine Argumente und verwenden die Energieverwaltung von Cosmos; die Unterstützung hängt von der Plattform ab. Vor einem manuellen Schließen der VM weiterhin `disk unmount` ausführen.

Das QEMU-Startskript und die Visual-Studio-Projekteinstellungen in `ProjektZeh/.cosmos/config.json` setzen `-action shutdown=poweroff,reboot=reset`: `shutdown` beendet die VM, `reboot` startet den Gast in derselben VM neu. Damit werden die von Cosmos vorgegebenen Optionen (geprüft mit 3.0.94) `-no-shutdown` und `-no-reboot` überschrieben. Die Einstellung gilt ab dem nächsten VM-Start. Die vorhandene Vorbereitung mit Aushängen des Datenlaufwerks bleibt unverändert.

Mit `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-power-commands.ps1` werden beide Befehle in einer eigenen unsichtbaren QEMU-VM ohne Datenplatte geprüft. Nach `reboot` müssen ein zweiter Shellstart und ein laufender Gast nachweisbar sein; nach `shutdown` muss QEMU mit Exitcode 0 enden. Das ISO vorher bauen; ein anderes ISO lässt sich mit `-IsoPath` auswählen.

Mit dem Zusatz `-WithDebugger` hängt derselbe Test das installierte `gdb-multiarch` samt ELF-Symbolen an die zunächst angehaltene VM. GDB muss beim Neustart verbunden bleiben und nach dem Ausschalten einen normalen Gastabschluss melden; ein Stoppsignal wie `SIGQUIT` lässt den Test fehlschlagen. Die ELF-Datei muss neben dem ausgewählten ISO liegen und denselben Namen mit Endung `.elf` tragen.

`write` schreibt zunächst eine temporäre Datei im selben Verzeichnis und ersetzt das Ziel erst nach erfolgreichem Schreiben und Leeren der Schreibpuffer. Die alte Datei wird dafür unter einem kurzen Zufallsnamen gesichert und bei einem fehlgeschlagenen Austausch zurückbenannt. Schlägt auch die Wiederherstellung fehl, bleibt die Sicherung erhalten und ihr Pfad steht in der Fehlermeldung. Ein Schreibfehler, etwa ein voller Datenträger, lässt den bisherigen Inhalt erhalten. Dafür muss vorübergehend Platz für die alte und die neue Datei vorhanden sein; ein Schutz vor Stromausfall während des Ersetzens ist damit nicht zugesichert. `write hallo.txt ""` leert die Datei. Ein bereits belegter Mountpunkt `/data` wird bei `disk mount` abgelehnt.

Pfade mit Leerzeichen werden wie bei `echo` in Anführungszeichen gesetzt. Ohne Datenplatte oder bei einem Dateifehler bleibt die Shell bedienbar. Fehler werden über die gemeinsamen Result-Typen an den Kernel weitergereicht und dort einmal ausgegeben.

## Entwicklungsplan

Der Bootablauf protokolliert Eingabe, Anzeige und Dateien mit Beginn, Erfolg oder Fehler auf der seriellen Ausgabe und im begrenzten Speicherpuffer. Netzwerk wird bis zur Implementierung ausdrücklich übersprungen. Fehlende Dateien-/Speicherdienste verhindern den Shellstart nicht. Die Prüfungen lesen den Zustand der bereits von Cosmos initialisierten Dienste; sie ersetzen keine Treiberinitialisierung. Der Dateischritt bindet zusätzlich die vorhandene Datenpartition von `sata0` unter `/data` ein und protokolliert einen fehlgeschlagenen Mount. Protokoll, `uptime` und Statusanzeige verwenden gemeinsam eine `Stopwatch` ab ZehOS-Konstruktion, keine Kalenderuhr. `version` zeigt Assemblyversionen, keine aus dem Netz ermittelte Releaseversion.

Für ein Wiederherstellungs-ISO mit `dotnet build ProjektZeh/ProjektZeh.csproj -p:ZehOSRecovery=true` bauen. Dieser Modus umgeht die normale ZehOS-Anzeigeinitialisierung und startet direkt in der Cosmos-Textshell. Ein Fehler oder eine Exception in der normalen Anzeigeinitialisierung aktiviert denselben Rückfall automatisch. Der normale Start öffnet den Desktop nach den Bootschritten; F4 kann ihn auch aus der Recovery-Textshell öffnen. Ein Fehler beim Desktopstart oder Zeichnen wird protokolliert und lässt die Textshell erreichbar. Die Wiederherstellung benötigt weiterhin einen funktionierenden Cosmos-Framebuffer und Tastatur. Fehler vor `BeforeRun` oder eine ebenfalls fehlgeschlagene Textkonsole bleiben nur seriell diagnostizierbar.

Die [Roadmap für ZehOS](ROADMAP.md) beschreibt die nächsten Schritte bis zu einem Desktop mit Terminal, Dateiverwaltung, spielbarem Doom und einem einfachen Browser. Jede Etappe enthält konkrete Aufgaben und ein überprüfbares Ergebnis.

## Hintergrundaufgaben und Round-Robin (6a)

Der Kernel verwendet eine eigene Round-Robin-Policy mit 20-ms-Zeitscheibe über den vorhandenen Cosmos-Timer. Alle Terminals und die Textshell sehen dieselbe begrenzte Liste von höchstens acht Aufgaben:

```text
tasks start busy
tasks start busy
tasks start wait
tasks list
tasks scheduler
tasks cancel 1
```

`busy` rechnet ohne freiwillige CPU-Pause, `wait` wartet jeweils 250 ms. `tasks list` zeigt Kennung, Zustand, Fortschritt und gegebenenfalls Fehler. Die von `start` ausgegebene Kennung an `cancel` übergeben; Abbruch erfolgt kooperativ. Beendete Einträge bleiben zur Diagnose sichtbar und werden bei Bedarf für neue Aufgaben entfernt; Kennungen werden nicht wiederverwendet. `scheduler` zeigt die feste Zeitscheibe, die Zahl angeforderter Timer-Verdrängungen und die letzten 128 ausgewählten Kernelthread-Kennungen.

Worker schreiben weder ins Terminal noch ins Dateisystem. Statusübergaben sind synchronisiert, Fortschrittszähler atomar. Eingabe und Zeichnen bleiben im Hauptthread. Vor Ausschalten/Neustart werden die Aufgaben abgebrochen und höchstens zwei Sekunden abgewartet; laufende Aufgaben verhindern danach die Energieaktion. Dies ist Multitasking im gemeinsamen Kernel und bietet keine Prozessisolation.

### Isolierte Thread- und Warteprobe

Ein ausdrücklich aktivierter Testbuild prüft vor dem Desktop zwei echte Cosmos-Threads ohne freiwillige CPU-Abgabe sowie Schlafen, Abbruch, Join, Ereignisse und Wartezeitlimits:

```powershell
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSThreadProbe=true -warnaserror
./scripts/test-thread-probe.ps1 -IsoPath ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso
```

Der Helfer startet eine eigene QEMU-VM ohne Datenlaufwerk, wartet höchstens 45 Sekunden auf `THREAD_PROBE PASS` und beendet nur diese Test-VM. Serielle Ausgabe und QEMU-Fehlerausgabe liegen in einem jeweils neuen Ordner unter `.tools/thread-probe/`. Fehlendes Ergebnis, `THREAD_PROBE FAIL` oder ein vorzeitiges VM-Ende lassen den Helfer fehlschlagen. Ein normales ISO erfüllt diese Probe ausdrücklich nicht.

Der Test verwendet den ZehOS-Round-Robin-Scheduler und prüft auch Ressourcenbereinigung der Aufgabenverwaltung sowie die Nachrichtenwarteschlangen aus 6b. Ohne Buildoption ist die Probe nicht enthalten. Da beide Builds denselben ISO-Ausgabepfad verwenden, anschließend für den normalen Betrieb erneut ohne Probe bauen:

```powershell
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSThreadProbe=false -warnaserror
```

Ergebnisse und Grenzen stehen in [COSMOS_SUPPORT.md](docs/COSMOS_SUPPORT.md#aufgabenverwaltung-und-round-robin-am-13-november-2026).

## Automatische Builds und Codeprüfung

GitHub Actions prüft Pull Requests und jeden Push auf `master`. Beide Workflows können außerdem unter **Actions → Run workflow** manuell gestartet werden:

- **Shell tests and .NET analysis** baut das Testprojekt mit den empfohlenen .NET-10-Analyseregeln und führt die Shelltests aus. Compiler- und Analyzerwarnungen lassen den CI-Build fehlschlagen.
- **Cosmos x64 build** installiert Cosmos CLI und Patcher in Version 3.0.97 samt nativer Toolchain und erstellt ein bootfähiges Release-ISO. Auch hier gelten die .NET-Analyseregeln.
- **CodeQL (C#)** prüft den eigenen C#-Produktionscode mit der erweiterten Sicherheitsabfrage. Eine zusätzliche Analyse läuft wöchentlich. Ergebnisse stehen unter **Security → Code scanning** und gegebenenfalls direkt am Pull Request.

Das ISO `ZehOS-x64`, der Kernel-Buildlog und die TRX-Testergebnisse werden am Workflow-Lauf für 14 Tage als Artefakte gespeichert. Die Tests laufen auf dem Host; ein erfolgreicher CI-Lauf bestätigt keinen automatisierten Start oder Bedienungstest des Kernels in QEMU.

Die Action-Versionen sind auf feste Commits gesetzt. Die native Cosmos-Toolchain wird vom offiziellen, fortlaufend aktualisierten Release `tools-latest` bezogen. Für CodeQL werden keine separaten Sonar-Zugangsdaten benötigt. Die PR-Workflows verwenden `pull_request`, damit Code aus Forks ohne Zugriff auf Repository-Secrets geprüft werden kann.

Die Regeln liegen in `Directory.Build.props`. Die `.editorconfig` enthält gezielte Ausnahmen für beschreibende Testnamen, konstante Arrayargumente an Assertions und den gemeinsamen Namespace `Shared`. Lokal lässt sich derselbe Buildcheck mit `dotnet build ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` ausführen.

## Voraussetzungen

- .NET SDK 10; `global.json` wählt die neueste installierte 10.0-Featureversion. Ein allein installiertes SDK 11 reicht für Cosmos 3.0.97 nicht aus.
- Cosmos Gen3 CLI und Bauwerkzeuge (`cosmos check` muss erfolgreich sein)
- Für die Arbeit in Visual Studio: Visual Studio 2026 (Version 18.0 oder neuer). Visual Studio 2022 kann dieses `net10.0`-Projekt nicht bauen.
- Für Breakpoints in Visual Studio: die [Cosmos-Visual-Studio-Erweiterung](https://github.com/CosmosOS/CosmosVsExtension/releases) und der Visual-Studio-Workload **Linux and embedded development with C++**. Der Workload stellt die GDB-Debugengine bereit.

Das Projekt verwendet Cosmos 3.0.97. Die offizielle Installationsanleitung steht unter <https://cosmosos.github.io/articles/user/install.html>.

Die Solution `ProjektZeh.sln` muss ausdrücklich in Visual Studio 2026 geöffnet werden. Visual Studio 2022 meldet bei diesem Projekt einen Fehler zum .NET-10-Ziel und gegebenenfalls einen Folgefehler zur Assembly-Kürzung.

Die Solution in Visual Studio 2026 bauen (`Strg+Umschalt+B`). Das ISO liegt unter `ProjektZeh\bin\Debug\net10.0\linux-x64\cosmos\ProjektZeh.iso`. Die Zielplattform `linux-x64` ist für den Kernel festgelegt.

## Debuggen in Visual Studio mit QEMU

`ProjektZeh` als Startprojekt der Solution auswählen. Mit installierter Cosmos-Erweiterung startet `F5` den Kernel in QEMU und verbindet Visual Studio über GDB. Breakpoints in `Kernel.cs`, `F10` (Prozedurschritt) und die Aufrufliste funktionieren damit wie beim bisherigen Debugstart. `Strg+F5` startet QEMU ohne Debugger. Die serielle Ausgabe erscheint im Fenster **Cosmos OS - Output**.

Für `shutdown` und `reboot` muss die geöffnete Arbeitskopie auch `ProjektZeh/.cosmos/config.json` aus diesem Stand enthalten. Ohne diese Datei pausiert Cosmos den Gast beim Ausschalten; QEMU meldet dann `SIGQUIT`, und Visual Studio zeigt **Unbehandelte Ausnahme: Quit** an einer gerade zugeordneten Quellzeile an. Das ist in diesem Fall keine C#-Exception aus der markierten Methode. Nach einem Branch- oder Konfigurationswechsel die bisherige Debugsitzung beenden und mit `F5` neu starten. Geprüft wurden die Meldung ohne die Korrektur sowie mit Korrektur ein Neustart bis zur Shell, anschließendes Unterbrechen/Fortsetzen im Debugger und Ausschalten mit **Kernel exited with code 0**.

Bei `Console.Write` und anderen Aufrufen der .NET-Laufzeit mit `F10` zur nächsten Zeile des eigenen Kernels gehen. `F11` steigt in die aufgerufene Methode ein; bei `Console.Write` führt das in `System.Console` und Visual Studio kann mangels lokalem Laufzeit-Quellcode „Quelle nicht verfügbar“ beziehungsweise „Rahmen ist nicht im Modul“ anzeigen. Im GDB-Test führte `F10` von `Console.Write` korrekt zu `Console.ReadLine()`.

Die Cosmos-Erweiterung benötigt das global installierte `Cosmos.Tools`-CLI. Die Werkzeuge mit `cosmos check` prüfen. Falls die GDB-Debugengine fehlt, in Visual Studio 2026 den Workload über **Visual Studio Installer → Ändern → Linux and embedded development with C++** hinzufügen und Visual Studio danach neu starten. Unter **Extras → Optionen → Cosmos OS** muss **Handle F5 and Ctrl+F5** aktiviert sein (Standardwert).

Wenn `F5` meldet **QEMU for x64 not found**, `cosmos check` direkt im Terminal der geöffneten Visual-Studio-Instanz ausführen. Fehlt QEMU dort, im selben Terminal `& "$env:USERPROFILE\.dotnet\tools\cosmos.exe" install --auto --tools` starten und Visual Studio anschließend neu öffnen. Entscheidend ist die Werkzeuginstallation im Windows-Benutzerprofil, unter dem Visual Studio läuft.

Das Gen3-Debugging verwendet QEMU und GDB. NativeAOT kann die Anzeige einzelner Variablen einschränken; Haltepunkte und Aufruflisten sind davon unabhängig.

## Netzwerk in der Shell

`netinfo` zeigt Adapter, Link und IPv4-Konfiguration. Netzwerkbefehle starten einen Hintergrundauftrag pro Terminal; das Ergebnis erscheint automatisch in derselben Sitzung, `net cancel` fordert den Abbruch an. Ein laufender Auftrag blockiert keine weitere Shell-Eingabe. Beim Schließen des Terminals wird sein Auftrag abgebrochen.

Nach dem Boot versucht ZehOS bei Link ohne IPv4-Adresse automatisch DHCP, bei fehlender Antwort erneut nach zehn Sekunden. Eine manuell als Administrator mit `net static 10.0.2.15 255.255.255.0 10.0.2.2 10.0.2.3` gesetzte Adresse bleibt erhalten; `net dhcp` fordert DHCP ausdrücklich erneut an. Das Netzwerksymbol unten rechts zwischen Desktopumschaltung und Uhr zeigt Verbindung, Suche oder fehlenden Link/Adapter. Ein Linksklick öffnet den Zustand und die IPv4-Adresse, der nächste Klick oder Esc schließt die Anzeige. „Verbunden“ bedeutet Link mit IPv4-Adresse, keine geprüfte Internetverbindung.

Mit konfigurierter Adresse stehen `ping <IPv4>`, `nslookup <Name>` und `fetch http://10.0.2.2:18080/ok` bereit. Der Testserver läuft auf dem Entwicklungsrechner; `10.0.2.2` bezeichnet ihn aus dem QEMU-Gast. `fetch https://Host/Pfad` verwendet TLS 1.2/1.3 mit Zertifikatsketten-, Namens- und Gültigkeitsprüfung gegen ein eingebettetes Mozilla-Rootbundle. HTTPS verlangt eine korrekte UTC-Uhr und Hardwarezufall (RDSEED/RDRAND oder virtio-rng); ohne diese Voraussetzungen scheitert der Abruf. Unterstützt werden IPv4 und ASCII-DNS-Namen; IPv6 und Unicode-Zielnamen werden abgelehnt.

Die Automatik belegt einen der acht Hintergrundplätze. Während DHCP die Netzwerksperre hält, kann die Update-Prüfung „Netzwerkdienst ist belegt.“ melden; nach Ende der Suche erneut versuchen. Cosmos-DHCP lässt sich erst nach seinem Empfangs-Warteabschnitt abbrechen. Dauert das länger als die zwei Sekunden zum Beenden aller Aufgaben, bricht die Energieaktion sicher ab und lässt sich danach erneut anfordern.

Der erste HTTP-Vertrag verlangt Status 200, unkomprimierte Inhalte und `Content-Length`. Header sind auf 8192 Bytes, Antwortkörper auf 16384 Bytes begrenzt. Weiterleitungen und Transfercodierungen werden abgelehnt. Cosmos 3.0.94 verarbeitet einen Abbruch während DHCP oder TCP-Verbindungsaufbau erst nach Rückkehr seines internen Warteaufrufs. Die übrige Bedienung läuft auf dem Hauptthread weiter. [Architektur, HTTPS-Plan und Prüfnachweise](docs/NETWORK_TESTS.md).

## Apps als Pakete installieren (Doom)

Der normale Kernelbuild enthält standardmäßig den WebAssembly-Loader (wasm3) und den Paketmanager `pkg`. Apps werden nicht mehr in den Kernel gebaut, sondern als signierte Pakete installiert. Sie erscheinen danach im Startmenü. Doom ist das erste Paket: doomgeneric als Wasm plus Freedoom.

Nach `cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release` startet `scripts/start-qemu.ps1` das normale ISO einschließlich Paketmanager. Alternativ mit `dotnet build` bauen und dessen ISO ausdrücklich auswählen:

```powershell
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/start-qemu.ps1 -IsoPath ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso
```

Installieren braucht ein eingebundenes Datenlaufwerk, eine Anmeldung als Administrator und Netzwerkzugang. Doom belegt dort rund 30 MB. Danach in der Textshell oder einem Terminal:

```text
pkg install doom
```

Ändernde `pkg`-Befehle sind Administratoren vorbehalten und laufen im Hintergrund; das Ergebnis erscheint automatisch im aufrufenden Terminal. `pkg list` und `pkg remove doom` zeigen beziehungsweise entfernen Pakete. Ein laufendes Paket lässt sich weder entfernen noch aktualisieren. Beim ersten Start nach jedem Neustart werden alle Paketdateien erneut gegen das signierte Manifest geprüft.

Standardquelle ist `https://packages.pjoter.de/`; eine mit `pkg source <https-URL>` gespeicherte eigene Quelle bleibt erhalten. `pkg install` und `pkg upgrade` laden die signierte Paketliste automatisch vor dem Auftrag. `pkg update` bleibt als freiwilliger Katalogabruf verfügbar. Installierte Apps werden nur mit `pkg upgrade` aktualisiert.

Während des Auftrags zeigt dieselbe Shell eine laufend ersetzte Statuszeile: Katalog-/Signaturphase, Paket und Datei, Downloadbalken, Prozent und übertragene/Gesamtbytes. Eine abgeschlossene Übertragung ist noch keine abgeschlossene Installation: Erst nach Hashprüfung und Veröffentlichung folgt das Ergebnis. `pkg cancel` bricht ab. Der Fortschritt bleibt an die Sitzung gebunden; andere Terminals erhalten ihn nicht.

Ein Build vertraut nur dem eingebetteten öffentlichen Herausgeberschlüssel `ProjektZeh/Packages/package-key.pub`; `-p:ZehOSPackageKeyPath=<Datei>` ersetzt ihn, etwa für Tests. Den privaten Schlüssel hält der Herausgeber außerhalb des Repositorys. Das Repository wird aus dem Branch `packages` gespeist und von `deploy/package-repo` ausgeliefert (Docker-Compose mit automatischer Aktualisierung). Format, Speicherort und Werkzeug `scripts/build-package.py` beschreibt [docs/PACKAGE_FORMAT.md](docs/PACKAGE_FORMAT.md). QEMU-Abläufe prüft `scripts/test-wasm-doom-desktop.py` mit lokalem Testrepository und Snapshot (Aufruf und Szenarien in [docs/PACKAGE_FORMAT.md](docs/PACKAGE_FORMAT.md)), das Szenario `full` auch mit `--source https://packages.pjoter.de/`.

NetSurf 3.11 ist als separates Offline-Paket baubar: lokale HTML-/CSS-Seiten, Links, Zurück, Scrollen und
Formulareingabe. Der Kernel enthält nur die allgemeinen Hostdienste; NetSurf und seine Bibliotheken bleiben im
Paket. NetSurf ist noch nicht im öffentlichen Paketkatalog veröffentlicht. Build, lokale Testinstallation und
geprüfte Grenzen stehen in [docs/NETSURF.md](docs/NETSURF.md). JavaScript ist standardmäßig aus;
Strg+J schaltet es für die laufende Sitzung ein (Statusleiste „JS an“), siehe dort.

Eigene Apps baut man als wasm32-WASIp1-Modul, etwa mit wasi-sdk für C/C++. Was eine App darf, erklärt [docs/PAKETE_ERSTELLEN.md](docs/PAKETE_ERSTELLEN.md), ebenso den Weg bis zur Veröffentlichung. Die wichtigsten Grenzen:
- Nur grafische Fensterapps; eine App zur Zeit.
- Dateien nur lesend und nur aus dem eigenen Paket.
- Kein Netzwerk, keine Threads, keine Speicherstände.
- Höchstens 256 MiB Speicher; die App muss `should_exit` abfragen.
- Der Interpreter ist unter QEMU deutlich langsamer als nativer Code.

`help` zeigt `pkg` mit seinen Unterbefehlen. Nur ausdrücklich mit `-p:ZehOSWasm=false` gebaute Kernel und das Installer-ISO bieten keinen `pkg`-Befehl.

## Start in VMware Player aus Visual Studio

Die Cosmos-Erweiterung übernimmt standardmäßig `F5` und `Strg+F5` für QEMU. Für einen Start in VMware ohne Umstellen der Debug-Einstellung zuerst in Visual Studio bauen (`Strg+Umschalt+B`) und dann im Visual-Studio-Terminal aus dem Repository-Stamm ausführen:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-vmware.ps1 -IsoPath .\ProjektZeh\bin\Debug\net10.0\linux-x64\cosmos\ProjektZeh.iso
```

Alternativ unter **Extras → Optionen → Cosmos OS** die Option **Handle F5 and Ctrl+F5** ausschalten, das Startprofil **VMware Player** wählen und mit `Strg+F5` starten. Zum QEMU-Debuggen die Option wieder aktivieren.

Die VMware-VM liegt im ignorierten Ordner `.tools\vmware`; die frühere Cosmos-Gen2-VM wird nicht verändert. Bei einem weiteren Start die laufende VM vorher in VMware ausschalten. Die VM hat derzeit keine Festplatte, daher bleiben Dateien im Gastsystem nicht erhalten. Der serielle Bootlog liegt unter `.tools\vmware\serial.log`.

VMware hat in Gen3 über dieses Startprofil keine Visual-Studio-Debugger-Anbindung. Für Breakpoints QEMU mit `F5` verwenden.

ZehOS 0.1.2 verwendet unter VMware dessen gemeldete TSC-/LAPIC-Frequenzen und ergänzt die von LAI benötigten PCI-Zugriffe für das Ausschalten. Cosmos bleibt bei 3.0.94. Die [VMware-Prüfungen](docs/VMWARE_STABILITY_TESTS.md) unterscheiden Windows/Player, Linux/Workstation und noch offene Abläufe.

## Bauen und starten

Im Wurzelverzeichnis des Repositories:

```powershell
cosmos build --project ProjektZeh\ProjektZeh.csproj --arch x64
cosmos run --iso ProjektZeh\output-x64\ProjektZeh.iso --arch x64 -- -action shutdown=poweroff,reboot=reset -device qemu-xhci,id=tabletbus -device usb-tablet,bus=tabletbus.0
```

Der erste Befehl erzeugt `ProjektZeh\output-x64\ProjektZeh.iso`. Für einen Start ohne grafisches QEMU-Fenster kann `cosmos run` vor `--` mit `--headless` ausgeführt werden. Die eigentliche Konsole wird dann nicht angezeigt, weil sie auf dem Framebuffer gezeichnet wird.

## Dateibetrachter, Texteditor und Pong

Der Desktop-Starter öffnet **Dateien**, **Editor** und **Pong**. Tab/Shift+Tab wechseln den Fokus, Enter aktiviert Schaltflächen und Listeneinträge. Im Editor öffnen F3 und speichern F2 die Datei; Strg+A markiert ein Textfeld vollständig. Ungespeicherte Änderungen werden beim Schließen mit „Zurueck“, „Verwerfen“ oder „Speichern“ behandelt. Schreibfehler erhalten den Entwurf. Der Datei-Explorer bietet **<**/**>**/**^** für Zurück, Vor und den übergeordneten Ordner, ein Pfadfeld und links die Orte *Persönlich* (Startverzeichnis) und *Laufwerk* (`/data`). Die Liste zeigt Name, Größe und Typ; Doppelklick oder Enter öffnet Ordner beziehungsweise Dateien im Editor, Rücktaste geht eine Ebene höher, F5 lädt neu. **Ordner neu** und **Umbenennen** (auch F2) fragen den Namen in einer Zeile unter der Liste ab. Enter übernimmt; ein erneuter Klick auf dieselbe Schaltfläche oder die Auswahl eines anderen Eintrags bricht ab (Esc wechselt wie überall im Desktop zur Textshell). **Löschen** (auch Entf) verlangt eine zweite Bestätigung desselben Eintrags; Ordner müssen leer sein. Ab 1400 Pixel Fensterbreite erscheint rechts eine Textvorschau. Alle Aktionen laufen über den gemeinsamen Dateidienst mit dessen Rechteprüfung. Umbenennen gilt nur für Dateien.

Pong verwendet W/S oder Pfeil auf/ab und Leertaste für Pause. Beim Fokusverlust pausiert das Spiel; Esc/F4 erhält weiterhin die Fenster und öffnet die Textshell. Bildrate, Speicher und Auslastung zeigt der Taskmanager; der Desktop selbst bleibt frei von Diagnosetexten. Bedeutung, Grenzen und geprüfte Abläufe stehen in [Desktopanwendungen](docs/DESKTOP_APPS.md). Dateioperationen bleiben synchron; maximal acht Fenster und 65.536 Zeichen je Textdatei bleiben die Grenzen.

## Desktop-Dateifehler gezielt prüfen

Roadmap 7.5 ist mit fehlendem Datenlaufwerk und einer vollständig belegten isolierten FAT32-Kopie in QEMU geprüft. Die [Prüfanleitung](docs/DESKTOP_STORAGE_TESTS.md) beschreibt den Ablauf und den Helfer `scripts/create-full-fat32-test-copy.py`. Dieser erzeugt ausschliesslich eine neue Testkopie und ersetzt kein vorhandenes Image. VMware-Dateiprüfungen bleiben offen.

### Nachrichten und Synchronisation (Etappe 6b)

`messages start slow-producer` startet zwei Hintergrundaufgaben mit 24 nummerierten Nachrichten und einem Puffer für drei Einträge. `messages start slow-consumer` macht den vollen Puffer sichtbar. `messages status` zeigt Fortschritt, Wartevorgänge, Reihenfolgefehler und die gemeinsame Kontosumme (2000). Die Bedienung bleibt während der Wartezeiten verfügbar; `tasks list` zeigt beide Aufgaben.

`messages close` schließt den Puffer: Neue Nachrichten werden abgewiesen, vorhandene noch verarbeitet. `messages stop` bricht beide Aufgaben ab. Auch `tasks cancel <Consumer-ID>` beendet den am vollen Puffer wartenden Producer. Nach dem Ende beider Aufgaben lässt sich die Vorführung erneut starten. Die Vorführung gehört zum gemeinsamen Kerneldienst; alle Terminals sehen denselben Zustand.

`MessageQueue<T>` verwendet den vorhandenen Cosmos-Monitor mit einer gemeinsamen Sperre für Zustand und Wecksignale. Wartende Varianten liefern `Cancelled`, `TimedOut` beziehungsweise `Closed`; nicht wartende Varianten unterscheiden `Empty` und `Full`. Ein geschlossener Puffer lässt sich noch leeren. Beide Übertragungsrichtungen des kontrollierten Kontobeispiels erwerben zuerst die Sperre von Konto 1 und dann von Konto 2, damit keine zyklische Warteabhängigkeit entsteht.

Der optionale Threadprobe-Build prüft zusätzlich nummerierte Zustellung, Kapazität, Zeitlimits, Schließen/Abbruch wartender Teilnehmer und den Neustart nach alleinigem Consumer-Abbruch bei vollem Puffer. Erfolg meldet `MESSAGES_PROBE PASS`; erst nach allen Teilprüfungen folgt `THREAD_PROBE PASS`. Einzelheiten und tatsächlich geprüfte Plattformen stehen in [docs/ETAPPE6B_STATUS.md](docs/ETAPPE6B_STATUS.md).

## UEFI-Installation und signierte Kernelupdates

Der Installer ist ein eigener Build mit normalem Kernel und Limine-EFI als eingebetteter Payload. Beide Builds müssen dieselbe Version verwenden. `cosmos build` nimmt keine zusätzlichen MSBuild-Eigenschaften an; dafür direkt `dotnet build` verwenden:

```bash
payload="$PWD/.tools/installer-payload"
mkdir -p "$payload"
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -p:Version=0.5.18
cp ProjektZeh/bin/Release/net10.0/linux-x64/ProjektZeh.elf "$payload/ProjektZeh.elf"
cp "$HOME/.nuget/packages/cosmos.build.common/3.0.97/tools/Limine/BOOTX64.EFI" "$payload/BOOTX64.EFI"
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -p:Version=0.5.18 \
  -p:ZehOSInstaller=true -p:ZehOSPayloadDir="$payload"
```

Das ISO liegt unter `ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso`. Vor dem nächsten Build benötigte ISO-/ELF-Dateien kopieren; beide Varianten schreiben dieselben Ausgabepfade. Die Standardversion ist `0.5.18`. `-p:ZehOSRecovery=true` lässt sich zusätzlich mit dem Installer kombinieren.

Das Installer-ISO startet in den grafischen Einrichtungsassistenten (Willkommen → Hinweise → Zielplatte → Bestätigung → Installation → Neustart), bedienbar mit Maus und Tastatur. Bei einem Anzeigefehler oder mit `-p:ZehOSRecovery=true` startet stattdessen eine lokale Text-Einrichtungssitzung mit Installationsberechtigung, ohne vorher ein Konto auf der Zielplatte anzulegen. `setup list` zeigt Ziele und `setup install sata0` installiert auf eine Platte ohne Partitionen. Bei vorhandenen Partitionen ist die ausdrückliche Löschbestätigung `setup install sata0 --erase` nötig. Ohne Löschbestätigung wird nur eine Platte angenommen, die keine Partition hat und vollständig als leer gelesen wurde. Platten über 256 MiB werden dafür nicht gelesen und verlangen deshalb immer die Bestätigung. Mindestens 128 MiB, 512-Byte-Sektoren und höchstens 2 TiB werden unterstützt; eingebundene Platten werden abgewiesen. ISO9660-Medien werden als Installationsmedium ausgeschlossen, auch wenn das ISO roh auf einer USB-Platte liegt. Eine auf FAT entpackte Kopie des Installers ist kein unterstütztes Startmedium.

Die Installation ersetzt das Partitionslayout durch einen MBR mit einer einzigen FAT32-EFI-Partition (`0xEF`) ab LBA 2048. Sie schreibt `/EFI/BOOT/BOOTX64.EFI`, `/boot/ProjektZeh.elf`, `/boot/ProjektZeh.elf.bak`, `/boot/zehos-version.txt` und `/boot/limine/limine.conf`. Nach dem Abschluss sind die Dateisystempuffer geschrieben und das Ziel ausgehängt. Installationsmedium entfernen und neu starten. Das installierte System benötigt UEFI; ein BIOS-Bootsektor wird nicht installiert. Im normalen System mountet dieselbe Partition unter `/data`, die Bootdateien sind daher dort unter `/data/boot` erreichbar. Beim ersten Start den Administrator anlegen.

Alle `update`-Operationen erfordern einen angemeldeten Administrator, unabhängig von einer Freigabe durch `chperm`:

```text
update status
update source 10.0.2.2:8000
update check
update install
reboot
```

`status` zeigt laufende Version und Quelle. Die Quelle steht in `/data/.zehos-update.conf`; ohne Datei gilt `10.0.2.2:8080`. Bei einem Hostnamen ohne Port gilt HTTP-Port 80. Der Gast verwendet DHCP, DNS für Hostnamen und einfaches HTTP/1.0. Der Spiegel muss `Content-Length` liefern; Weiterleitungen, Transfercodierungen und HTTPS werden nicht unterstützt. Manifest und Signatur heißen `update-manifest.txt` und `update-manifest.sig`, der Kernelname steht im Manifest. Manifest, Signatur und Kernel sind auf 4096 Byte, 64 Byte beziehungsweise 64 MiB begrenzt. Ein eigener Spiegel lässt sich auf dem Host mit `python3 -m http.server 8000 --directory <Paketverzeichnis>` bereitstellen; QEMU mit `e1000e` und User-Netz starten.

`update check` prüft die rohe Ed25519-Signatur über die unveränderten Manifestbytes und verlangt eine echt höhere `major.minor.patch`-Version. `update install` verwendet diesen geprüften Stand und prüft Größe und SHA-256 des Kernels vor dem Schreiben. Der öffentliche Schlüssel ist eingebaut; der private Schlüssel gehört ausschließlich zur Veröffentlichung auf dem Host und nicht auf das ISO oder die Datenplatte.

Für ein vollständig lokales Update die drei Paketdateien unter `/data` in dasselbe Verzeichnis kopieren und beispielsweise `update install /data/updates/ProjektZeh-0.5.18.elf` ausführen. Das liest und prüft das benachbarte Manifest und seine Signatur ohne Netzwerk. Ein vorheriger Onlinecheck ist dafür nicht nötig.

Beim Ersetzen bleibt der alte Kernel als `/boot/ProjektZeh.elf.bak` erhalten. Der Installer richtet mit drei Sekunden Timeout die Limine-Einträge **ZehOS** (1920×1080), **ZehOS (1024x768)** und **ZehOS (vorherige Version)** ein. Der Installer legt dazu bereits eine Kopie des installierten Kernels als `.bak` an. Nach einem Start über den Rückfalleintrag (laufende und installierte Version in `/boot/zehos-version.txt` weichen ab) darf ein Update nicht älter als die installierte Version sein; es ersetzt dann nur den Hauptkernel und lässt den laufenden Kernel in `.bak` unverändert. Nach Beginn des Dateiaustauschs ist bis zum Neustart kein weiteres Update erlaubt, auch wenn Austausch oder Bereinigung einen Fehler melden. Die Sicherungs-/Wiederherstellungslogik schützt vor behandelten Dateifehlern; sie garantiert keine Wiederherstellung nach Stromausfall. Netzwerk- und Dateioperationen der Shell laufen synchron; antwortet ein Spiegel nach dem Verbindungsaufbau nicht, steht das System bis zum Cosmos-Zeitlimit (wegen des groben Cosmos-Wartetimers deutlich länger als die angeforderten 10 s). [Prüfergebnisse und Plattformgrenzen](docs/INSTALLER_UPDATES_TESTS.md).

## Releases und Updates

`scripts/build-release.sh 0.5.18 [Ausgabeverzeichnis]` baut Live-ISO, Setup-ISO, Kernel und Update-Manifest. Zum Signieren erzeugt `python3 scripts/sign-update.py <Manifest> <privater-Ed25519-PEM>` die rohe Datei `update-manifest.sig` neben dem Manifest. Mit `python3 scripts/sign-update.py verify <Manifest> <Signatur> <öffentlicher-Schlüssel-base64>` lässt sich die Signatur prüfen. Der private Schlüssel bleibt außerhalb des Repositories; der Release-Workflow verwendet das Secret `ZEHOS_UPDATE_SIGNING_KEY`.

Für Online-Updates stellt `python3 scripts/update-mirror.py --repo owner/name` das neueste GitHub-Release bereit; `GITHUB_TOKEN` ist optional. Alternativ dient `--dir <Releaseverzeichnis>` für lokale Dateien. Der Spiegel bindet standardmäßig an `0.0.0.0:8080`; QEMU erreicht ihn über `10.0.2.2:8080`. `--bind` und `--port` ändern diese Werte. Der Gast verwendet HTTP; die Signatur schützt die Echtheit des Manifests. Der Spiegel lädt die Dateien beim Start und muss für ein neues Release neu gestartet werden.

## Moderne Konsole

Die Gestaltung lehnt sich an Hyprland und Konsole an: abgerundete Ansichten mit Abständen, ein Akzentrahmen um die aktive Ansicht, Tabs und Statussegmente in Pillenform. Das Profil (Catppuccin, Tokyo Night, Breeze Dunkel/Hell, Solarisiert) färbt auch die Fensterflächen. Der Prompt erscheint in der Akzentfarbe, die Eingabe mit Blockcursor. Wie in fish blendet die Eingabezeile den jüngsten passenden Verlaufsbefehl grau ein; **Pfeil rechts** am Zeilenende übernimmt ihn.

Jedes Konsolenfenster bietet bis zu acht Tabs und pro Tab bis zu vier getrennte Ansichten. Rechts in der Tabzeile öffnet **+** einen neuen Tab und der Pfeil das Terminalmenü; dort teilen **Nebeneinander** und **Uebereinander** die Ansicht (auch Strg+Umschalt+E/H), außerdem stehen Suche, Profil und Schriftgröße bereit; zu kleine Ansichten werden mit einer Meldung abgewiesen. Jede neue Ansicht erhält eine eigene Shell, Benutzeridentität, ein eigenes Arbeitsverzeichnis, Eingabe und Verlauf. Neue Sitzungen starten als Desktopbenutzer; eine Anmeldung in einer Ansicht verändert andere Sitzungen nicht. Ein Mausklick fokussiert die gewünschte Ansicht. F4/Esc wechseln weiterhin zwischen Desktop und Textshell, ohne offene Konsolen zu verwerfen.

| Tastenkürzel | Aktion |
| --- | --- |
| Ctrl+Shift+T | Neuer Tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Nächster / vorheriger Tab |
| Ctrl+Shift+E / Ctrl+Shift+H | Nebeneinander / übereinander teilen |
| Ctrl+Shift+N | Nächste Ansicht im Tab |
| Ctrl+Shift+W | Aktive Ansicht schließen; die letzte Ansicht schließt den Tab |
| Ctrl+Shift+M | Kontextmenü öffnen; Pfeile und Enter bedienen es |
| Ctrl+Shift+F | Ausgabesuche öffnen; Enter / Shift+Enter suchen vorwärts / rückwärts, `x` schließt die Suchleiste |
| Ctrl+Shift+C / Ctrl+Shift+V | Auswahl kopieren / Text einfügen |
| Ctrl++ / Ctrl+- / Ctrl+0 | Bitmap-Schrift 100–300 % vergrößern / verkleinern / zurücksetzen |
| Ctrl+L | Ausgabe leeren |
| Tab | Befehle beziehungsweise Dateipfade vervollständigen; ist das gemeinsame Präfix erreicht, listet Tab alle Treffer wie zsh auf (höchstens 96). Gilt auch in der Textshell |
| Pfeil rechts | Grau eingeblendeten Verlaufsvorschlag übernehmen |
| Ctrl+R | Rückwärts im Befehlsverlauf nach dem aktuellen Eingabetext suchen |
| PageUp / PageDown | Ausgabe seitenweise scrollen |

Ziehen mit der linken Maustaste markiert Ausgabe. Die rechte Maustaste öffnet das Kontextmenü, unter anderem mit „Alles auswählen“. Die Zwischenablage ist nur innerhalb von ZehOS verfügbar. Einfügen ersetzt Zeilenumbrüche durch Leerzeichen und führt keinen Befehl automatisch aus. Kennworteingaben sind von Auswahlkopieren, Einfügen, Verlauf und Vervollständigung ausgenommen. Die Scrollleiste kann gezogen oder ober-/unterhalb des Griffes angeklickt werden. Die Ausgabesuche berücksichtigt auch durch Fensterbreite umgebrochene Textzeilen; zum Weiterarbeiten die Suchleiste schließen.

`Profil` wechselt zwischen Breeze Dunkel, Breeze Hell und Solarisiert im aktuellen Fenster. Profile, Schriftgröße und Aliase sind sitzungslokal. Die Statusleiste zeigt Pfad, Ansichten und Zoom beziehungsweise Bedienhinweise. Der normale Scrollback hält die letzten 256 Anzeigezeilen; `less` besitzt einen eigenen Puffer für den vollständigen, auf 65.536 Zeichen begrenzten Eingabetext.

Zusätzliche Shellbefehle:

| Befehl | Umfang |
| --- | --- |
| `cp <Quelle> <Ziel>` / `mv <Quelle> <Ziel>` | Eine Datei kopieren / samt Rechten verschieben; vorhandene Ziele werden abgelehnt |
| `rm <Datei>` / `rmdir <Verzeichnis>` | Eine Datei / ein leeres Verzeichnis löschen; kein rekursives Löschen |
| `touch <Datei>` | Datei anlegen oder Inhalt erhalten; Cosmos aktualisiert FAT-Zeitstempel noch nicht (geprüft mit 3.0.94) |
| `find [Pfad] [Teilname]` | Lesbare Einträge rekursiv nach Teilnamen suchen, maximal 1.024 Einträge, keine Verknüpfungen |
| `du [Pfad]` / `df [Pfad]` | Lesbare Inhaltsbytes summieren / tatsächliche Dateisystemkapazität und freien Platz anzeigen |
| `head [-n Anzahl] [Datei]` / `tail [-n Anzahl] [Datei]` | Erste / letzte Zeilen, standardmäßig zehn |
| `grep [-i] <Text> [Datei]` | Zeilen nach festem Teiltext filtern, optional ohne Beachtung der Großschreibung |
| `wc [-l|-w|-c] [Datei]` | Zeilenumbrüche, Wörter oder Zeichen zählen; `-c` zählt Zeichen, keine UTF-8-Bytes |
| `sort [Datei]` / `uniq [Datei]` | Zeilen ordinal sortieren / direkt aufeinanderfolgende gleiche Zeilen entfernen |
| `less [Datei]` | Pager mit Pfeilen, Bildtasten, Home/End, Leertaste und `q`; Textshell und Desktop bleiben nichtblockierend |
| `history` / `alias [Name=Befehl Argumente ...]` | Letzte 32 Befehle / bis zu 32 sitzungslokale Aliase anzeigen oder definieren |
| `date` / `uname` / `free` | Ortszeit Europe/Berlin mit MEZ/MESZ aus der UTC-Hardwareuhr, Kernel/Architektur und tatsächliche physische Speicherwerte anzeigen |
| `exit` | Aktive Terminalsitzung schließen; die Textshell bleibt als Wiederherstellungskonsole geöffnet |

Textfilter können statt einer Datei die Ausgabe einer vorherigen Pipelinestufe lesen. Beispielsweise erzeugt `echo blau >> farben.txt` eine Datei oder hängt Text an, `cat farben.txt | sort | uniq > eindeutig.txt` speichert eine gefilterte Ausgabe und `help | less` öffnet die Hilfe im Pager. `alias ll=ls` legt einen Argumentalias an; eingebettete Pipelines und Umleitungen gehören nicht zu Aliaswerten. Für Gruppenargumente ist etwa `alias suche=grep "zwei Worte"` möglich.

Eine Pipeline umfasst höchstens 16 Stufen und 65.536 Ausgabezeichen pro Stufe. Jeder ausgeführte Befehl und jeder Dateizugriff prüft weiterhin seine Berechtigungen. Ein Fehler stoppt die Pipeline; ein Ausgabeziel wird dabei nicht ersetzt. Konto- und Kennwortdialoge sind mit Pipes und Umleitungen ausgeschlossen. Pipes und Umleitungen sind für ausgebende Befehle vorgesehen; Dateiveränderungen und Energieaktionen werden nicht als Pipelinefilter ausgeführt. `>` verwendet die vorhandene Sicherungs-/Wiederherstellungslogik; das ist keine Garantie bei Stromausfall. `>>` benötigt bei vorhandenen Dateien Lese- und Schreibrechte. Es gibt keine GNU-/POSIX-Kompatibilitätszusage, keine Ausführung gewöhnlicher Linux-Programme. Dieser Konsolenausbau ergänzt keine Netzwerkbefehle; separat integrierte Netzwerkfunktionen bleiben erhalten.

### Scrollen in Textshell und GUI

Das vertikale Scrollrad bewegt den Inhalt unter dem Zeiger um drei Zeilen pro Raster. Es funktioniert auch in geteilten GUI-Terminals, im Pager (`less`), in Listen und mehrzeiligen Textfeldern. Der Tastaturfokus und die aktuelle Auswahl bleiben beim Scrollen erhalten. Auch verkürzte Terminalmenüs lassen sich mit dem Rad blättern. Für die Schriftgröße gelten auf der deutschen Tastatur **Strg+Plus/Minus** beziehungsweise **Strg+0**.

Die Textshell ohne Desktop besitzt ebenfalls einen begrenzten Puffer von **256 Ausgabezeilen**. Scrollrad und Bildtasten blättern darin; Status und Eingabe bleiben sichtbar. Bearbeiten der Eingabe oder Tab-Vervollständigung kehrt zur neuesten Ausgabe zurück. `clear` und ein Benutzerwechsel leeren den Puffer. F4/Esc erhalten ihn; nach einer Auflösungsänderung wird er neu umgebrochen. Dies ist die lokale Textshell; `cosmos run --headless` ohne Anzeigeclient stellt weiterhin keine sichtbare Fernkonsole bereit.

Die beiden mit Cosmos ausgelieferten Bitmapfonts (geprüft mit 3.0.94) werden für die deutschen Zeichen und das Eurozeichen passend zugeordnet. Eigene Fonts benötigen eine eigene PSF-Zeichenzuordnung.

## Lizenz

ZehOS steht unter der GNU General Public License Version 3 ([LICENSE](LICENSE)). Eingebundene Fremdbestandteile behalten ihre eigenen Lizenzen:

- wasm3 (MIT): `ProjektZeh/ThirdParty/wasm3/LICENSE`
- Rasterizer für Wasm-Apps, übertragen aus ClassiCubes SoftGPU (BSD-3-Clause): `ProjektZeh/Graphics/Raster/LICENSE-ClassiCube.txt`
- BouncyCastle (MIT) als NuGet-Paket
- Cosmos (BSD-3-Clause) als NuGet-Pakete; übernommene Teile aus Cosmos.Network.Http in `ProjektZeh/ThirdParty/CosmosHttp` mit `LICENSE.txt` und `THIRD-PARTY-NOTICES.txt` (darin auch die Mozilla-Zertifikatsliste `cacert.pem`, MPL-2.0)
- Die Doom-Plattformschicht in `experiments/Wasm.DoomProbe` steht wie doomgeneric unter GPL-2.0-or-later ([LICENSE](experiments/Wasm.DoomProbe/LICENSE)).

Das Doom-Paket enthält doomgeneric (GPL-2.0-or-later) und die Spieldaten von Freedoom (BSD), jeweils mit ihren Lizenztexten. Die Quellen liegen öffentlich: doomgeneric am festen Commit sowie Plattformschicht und Buildskript in diesem Repository.
