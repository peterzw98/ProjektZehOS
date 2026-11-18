# Bedienung

Teil des Handbuchs zu ZehOS; Überblick und Schnellstart in der [README](../../README.md).

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
| `cat <Datei>` | Textdatei lesen (nur gültiges UTF-8; andere Kodierungen werden mit `NotUtf8` abgelehnt statt umkodiert, auch beim Anhängen mit `>>` und im Texteditor) |
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
| `wasmaot [on\|off]` | Paket-Apps mit passendem `.aot` beim nächsten Start nativ über WAMR ausführen (Standard aus). Nur in Kernels mit `-p:ZehOSWasmAot=true` wirksam; sonst meldet der Befehl „nicht verfügbar“, und Apps laufen unter wasm3. Jeder App-Start schreibt `WASM Start <id> Laufzeit=…` ins serielle Protokoll |

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

Live-ISO und Recovery starten ohne Bootmenü und Auswahlpause. Limine fordert **1920 × 1080 Pixel mit 32 Bit Farbtiefe** an; die tatsächlich aktive Auflösung hängt vom Anzeigetreiber ab. Der normale Start führt zum Desktop, Recovery zur Textshell. Unterstützt werden 1024 × 720 bis 1920 × 1080.

Die Taskleiste unten zeigt mittig nur Symbole: Start, angeheftete Starter für Dateien, Terminal und Einstellungen sowie die Fenster des aktuellen virtuellen Desktops. Rechts folgen die Desktopumschaltung als vier Punkte (aktueller Desktop als Akzentpille, belegte hell), das Netzwerksymbol und die Uhr. Die Uhr zeigt die Ortszeit Europe/Berlin (MEZ/MESZ nach EU-Sommerzeitregel) und setzt voraus, dass die Hardwareuhr auf UTC steht. Aktive Fenster tragen einen langen Akzentbalken, geöffnete einen kurzen, minimierte erscheinen gedämpft; Fenstertitel stehen nur in der Titelleiste. **Start** oder **Alt+F1** öffnet das Startmenü mittig im Stil von Windows 11: Suchfeld oben, darunter alle Apps einschließlich installierter Pakete als Kacheln, unten Benutzername und Ein/Aus-Symbol. Ein Klick markiert, ein Doppelklick auf dieselbe Kachel innerhalb von 450 ms startet sie genau einmal; Enter startet die Auswahl ebenfalls, Pfeiltasten bewegen die Auswahl im Raster. Rechtsklick oder **Umschalt+F10** auf eine Kachel öffnet ein Kontextmenü mit **Auf Desktop legen** bzw. **Vom Desktop entfernen**. Tab/Shift+Tab wechseln den Fokus zwischen Suche, Kacheln und Ein/Aus. Das Ein/Aus-Symbol öffnet ein Untermenü mit **Abmelden**, **Neu starten** und **Herunterfahren**; Pfeiltasten wählen, Enter führt aus, Esc schließt zuerst nur das Untermenü. Neu starten und Herunterfahren verwenden dieselben Befehle wie die Shell. Den Taskmanager öffnet seine Kachel. Gestaltung und Bedienung orientieren sich an KDE; ZehOS verwendet weiterhin Cosmos und führt keine Linux-Programme aus.

**Einstellungen** hat zwei Bereiche. *Anzeige* zeigt die tatsächlich aktive Auflösung. Meldet der Treiber mehrere Modi (in den installierten Cosmos-Paketen nur VMware SVGA), wird eine Auswahl mit 15 Sekunden Bestätigungsfrist und automatischer Rückkehr angeboten. `scripts/start-qemu.ps1` startet QEMU deshalb mit `-vga vmware`; dann lässt sich die Auflösung zwischen 1024×768 und 1920×1080 im Betrieb ändern. Der Gast startet dabei mit 1024×768 und stellt eine bestätigte Auflösung nach der Anmeldung wieder her. Mit `-RelativeMouse` startet es ohne USB-Tablet, damit Spiele den Zeiger relativ einfangen können. QEMU braucht dann für jede Mauseingabe den Fang des Hostfensters: Ein Klick ins Fenster fängt die Maus, Strg+Alt+G gibt sie an den Host zurück. Esc und F4 geben den Fang im Gast immer frei. Mit `-StandardVga` verwendet das Skript die Standardgrafik; deren Limine-Bildpuffer meldet genau einen Modus, dann ist kein Auflösungswechsel im Betrieb verfügbar. *Personalisierung* wechselt sofort zwischen hell und dunkel, sechs Akzentfarben und vier eingebauten Hintergründen. Ein eigenes Bild ist ein unkomprimiertes 24/32-Bit-BMP bis 4 Megapixel und 16 MiB; ungültige oder fehlende Dateien lassen den bisherigen Hintergrund unverändert. Die Werte werden als `zehos-desktop.conf` im Heimatverzeichnis der Desktopsitzung gespeichert. Ohne beschreibbares Laufwerk gelten sie nur für die Sitzung, die App zeigt das als Fehler an.

Der **Taskmanager** zeigt Auslastung der Desktop-Schleife mit 60-Sekunden-Verlauf (keine Gesamt-CPU), freien physischen Speicher laut Cosmos-Seitenverwalter, Bildrate und Betriebszeit, die laufenden Anwendungen mit Aktivieren und regulärem Schließen samt Rückfragen sowie die Abschnitte Datenträger, Netzwerk (erkannte Adapter, MAC, Link, IPv4) und System. Weitere Abschnitte werden als `TaskManagerSection` (Titel und Zeilenquelle) übergeben.

Eine Anzeigeauswahl wird erst mit **Beibehalten** dauerhaft gespeichert. Beim nächsten Anmelden wird sie erneut mit Bestätigungsfrist angewendet und die Einstellungsapp geöffnet. Ohne bestätigte Wahl bleibt die Bootauflösung erhalten; ältere Einstellungsdateien ohne ausdrückliche Bestätigung übernehmen nur die Personalisierung. Esc/F4 zur Textshell nimmt einen noch offenen Auflösungswechsel sofort zurück. Scheitert die Rücknahme, endet der Versuch mit einer Fehlermeldung und der tatsächlich gemeldeten Auflösung.

Eigene 32-Bit-BMPs dürfen auch unkomprimierte Farbmasken (`BI_BITFIELDS`/`BI_ALPHABITFIELDS`, getrennte 8-Bit-RGB-Kanäle) mit V4-/V5-Kopf verwenden; der Hintergrund wird deckend dargestellt. Auch das Laden von Bilddateien prüft die Dateirechte. Beim Abmelden verschwinden private Hintergrundbilder; nach Anmeldung werden die Einstellungen aus dem jeweiligen Benutzerverzeichnis neu geladen. Die Anwendungsliste des Taskmanagers hält ihre Auswahl an der Fensteridentität fest, damit eine geschlossene Anwendung nicht versehentlich durch eine andere ersetzt wird.

Ein Klick aktiviert ein Fenster; **Alt+Tab** beziehungsweise **Alt+Shift+Tab** wechseln durch die Fenster des aktuellen Desktops. Die Titelleiste lässt sich mit der linken Maustaste ziehen, die Ränder und Ecken verändern die Größe bis zur jeweiligen Mindestgröße. **_** minimiert, **□** maximiert beziehungsweise stellt die bisherige Geometrie wieder her, **X** schließt mit der gegebenenfalls erforderlichen Rückfrage. Fenster bleiben oberhalb der unteren Leiste. Ein Klick auf einen Eintrag in der Fensterleiste aktiviert oder restauriert ihn; beim aktiven Fenster minimiert er es. Bei wenig Platz kürzt die Leiste die Titel hinter dem Programmsymbol.

In QEMU stellen die Visual-Studio-Projektkonfiguration und `scripts/start-qemu.ps1` ein USB-Tablet bereit. Es überträgt die absolute Position des Hostzeigers und vertikale Scrollbewegungen an ZehOS. Ein Trackpad am Host kann damit Zeiger, Klicks und Zwei-Finger-Scrollen an QEMU weiterreichen, sofern das QEMU-Fenster beziehungsweise der verwendete Anzeigeclient diese Ereignisse übernimmt. Ein direkter Hardwaretreiber für Laptop-Trackpads ist damit nicht verbunden. Bei eingefangenem Zeiger liefern PS/2-Mäuse auch am Gastbildschirmrand relative Bewegungen; absolute Tablets bleiben auf den Bildschirm- beziehungsweise Hostfensterrand begrenzt. `cosmos run` benötigt die Geräteargumente ausdrücklich wie im Startbeispiel unten; die CLI 3.0.94 übernimmt sie nicht aus der Projektkonfiguration.

Es gibt vier virtuelle Desktops. **Strg+Alt+Pfeil links/rechts** wechselt zum vorherigen beziehungsweise nächsten, **Strg+Alt+1** bis **4** direkt; mit zusätzlicher **Umschalttaste** wandert das aktive Fenster mit. Ein Klick auf **1–4** in der Taskleiste wechselt ebenfalls; ein Punkt darunter zeigt, dass dort Fenster geöffnet sind. Neue Fenster erscheinen auf dem aktuellen Desktop, Fenster anderer Desktops laufen weiter, ohne gezeichnet zu werden.

Auf der freien Desktopfläche liegen Verknüpfungen, anfangs zu Terminal, Dateien, Texteditor und Einstellungen. Ein Klick markiert, ein Doppelklick startet; nach einem Klick bedienen Pfeiltasten und Enter die Symbole. Die Auswahl wird zusammen mit den übrigen Desktopeinstellungen in `zehos-desktop.conf` gespeichert. Verknüpfungen zu deinstallierten Paketen bleiben gespeichert und erscheinen nach einer erneuten Installation wieder.

Höchstens acht Fenster sind gleichzeitig geöffnet, über alle Desktops und einschließlich minimierter Fenster. Jedes Terminal besitzt seine eigene Shell, Eingabe, Historie und sein eigenes Arbeitsverzeichnis, anfangs `/data`. Die letzten **256 Ausgabezeilen** werden beim Ändern der Breite neu umgebrochen; überschrittene Puffergrenzen verdrängen weiterhin die älteste Ausgabe. **Scrollrad/Trackpad-Geste und Bild auf/Bild ab (PageUp/PageDown)** blättern durch ältere Ausgabe; Texteingabe springt zur neuesten Ausgabe. `clear` leert dessen Ausgabe. Auch nach dem Schließen aller Fenster bleibt das Menü erreichbar.

**Esc** schließt zuerst ein geöffnetes Programmmenü. Bei eingefangener App-Maus gibt das erste Esc den Zeiger frei und erreicht die App; das nächste Esc führt zur Textshell. Sonst führt Esc direkt zur Textshell; **F4** wechselt zwischen Desktop und Textshell. Fensterzustände, Terminalausgaben und Eingabeentwürfe bleiben erhalten. Der Textshell-Ausgabepuffer bleibt beim Wechsel erhalten; deren eigener Eingabeentwurf und Verlauf bleiben erhalten. Grafikfehler beim Öffnen oder Zeichnen führen zur erreichbaren Textshell zurück.

`WindowManager` zeichnet sichtbare Fenster von hinten nach vorn und beschneidet jeden Inhalt am Fensterbereich. Hintergrund und Fenster werden im vorhandenen Cosmos-Puffer gezeichnet; zusätzliche Bildpuffer pro Fenster sind nicht erforderlich. `IApplication` enthält auch Größenänderung und abbrechbares Schließen. Das Menü verwendet dieselben UI-Bausteine wie die [Anwendungen aus Etappe 8](../../docs/DESKTOP_APPS.md). Der [Desktop-Umbau](../../docs/DESKTOP_REDESIGN.md) hält Integration und neue Prüfergebnisse getrennt von deren ursprünglichen Nachweisen fest.

Die gemeinsame Schleife verarbeitet pro Durchlauf höchstens 64 rohe Tastaturereignisse, verteilt Eingaben, aktualisiert die Statusanzeige und zeichnet bei Änderungen mit höchstens etwa 60 Bildern pro Sekunde. Die Zeitmessung verwendet `Stopwatch`; zwischen Durchläufen wartet Cosmos kurz. Datei- und Laufwerksbefehle laufen weiterhin synchron und können die Anzeige während ihrer Ausführung anhalten. Die spätere Hintergrundverarbeitung ist damit noch nicht umgesetzt.

Die Anzeige **FPS** zählt abgeschlossene Bildausgaben im Messintervall, **ms** daneben die längste Zeichen-/Ausgabedauer dieses Intervalls. Der ruhende Desktop zeichnet absichtlich nur bei Änderungen; eine niedrige FPS-Zahl im Leerlauf ist daher kein Leistungslimit. Bei dauernder Mausbewegung beziehungsweise Pong werden feste 60-Hz-Bildtermine verwendet. Im QEMU-Referenztest stieg die gemessene Bildrate von etwa 15–16 auf 58–61 FPS. Bei Full HD zeichnet der Desktop die Szene nur bei inhaltlichen Änderungen neu; reine Mausbewegung stellt aus einer gespeicherten Szene nur den Bereich unter dem alten Zeiger wieder her. Ursache und Grenzen beschreibt [DESKTOP_PERFORMANCE.md](../../docs/DESKTOP_PERFORMANCE.md). Die Bildrate steht im Taskmanager statt auf dem Desktop.

Geprüfte Cosmos-Schnittstellen, Referenz-VM und Grenzen stehen unter [`docs/COSMOS_SUPPORT.md`](../../docs/COSMOS_SUPPORT.md).

Gemeinsame Ergebnis- und Fehlertypen liegen unter [`ProjektZeh/Shared`](../../ProjektZeh/Shared/README.md). Die für ZehOS entwickelte Implementierung trennt unveränderliche Statusinformationen von optionalen Ergebniswerten. Erfolg wird über `CreateSuccess` gesetzt, Fehler über `CreateFailure` oder `CreateFromException`; `IsOK()` prüft den Status.

Die MSTest-Tests mit Moq prüfen dieselben Shell-, Datei-, Fenster-, Desktop- und Terminalquellen in einer normalen .NET-Umgebung ohne den Bare-Metal-Kernel. Der Dateidienst wird gegen echte temporäre Verzeichnisse getestet; Cosmos-Treiber und dauerhafte Speicherung nach einem VM-Neustart benötigen zusätzlich einen Gasttest:

```powershell
dotnet test ProjektZeh.Tests\ProjektZeh.Tests.csproj
```

Unter Windows benötigen die Symlink-Prüfungen das Recht zum Erstellen symbolischer Links (etwa über den Entwicklermodus). Ohne dieses Recht werden nur diese Tests als nicht ausführbar gemeldet; die übrigen Pfadprüfungen laufen weiter. Die Python-Skripttests starten mit `python -m unittest discover -s scripts/tests`. Für die Release-Skripttests muss Bash im `PATH` liegen, unter Windows beispielsweise Git Bash.

Die CI misst die Zeilenabdeckung der im Hostbuild enthaltenen Produktivquellen und scheitert unter der in `.github/workflows/ci.yml` gesetzten Grenze. Lokal entsteht der Bericht mit dem Sammler des Test-SDK; `scripts/check-coverage.py` wertet ihn aus:

```powershell
dotnet test ProjektZeh.Tests\ProjektZeh.Tests.csproj --configuration Release --collect "Code Coverage;Format=cobertura" --results-directory TestResults
python scripts/check-coverage.py (Get-ChildItem TestResults -Recurse -Filter *.cobertura.xml | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName --min-total 92 --min-file 50 --exclusions scripts/coverage-exclusions.txt --sources ProjektZeh
```

`scripts/coverage-exclusions.txt` nennt die wenigen Dateien, die nicht mitzählen, jeweils mit Grund. Die Abdeckung sagt nur, welche Zeilen ein Test ausführt, und gilt nur für die Quellen, die das Testprojekt einbindet. Kernelstart, Treiber und die Cosmos-Hardwareadapter fehlen im Nenner; `--sources ProjektZeh` listet diese ungemessenen Dateien auf.

`DesktopE2ETests` bedient den Desktop auf dem Host Ende zu Ende nur über Eingabeereignisse (Anmeldung, Startmenü, Terminal, Dateien, Texteditor, Einstellungen, Fenster, Arbeitsflächen, Taskmanager) und vergleicht das gezeichnete Bild mit Referenzbildern unter `ProjektZeh.Tests/Golden/`. Verglichen wird die SHA-256-Prüfsumme der Pixel (`<Name>.sha256`); das PNG daneben dient nur zum Ansehen. Die Uhr steht dabei fest auf 2. Januar 2026, 03:04:05 UTC. Bereiche, die den Hostpfad der temporären Testplatte zeigen, werden vor dem Vergleich magentafarben übermalt. Die Tests brauchen die Cosmos-Schrift aus dem NuGet-Paket `Cosmos.Kernel.System` 3.0.97, das `dotnet restore` lädt. Fehlt sie, gelten die Tests lokal als nicht ausführbar; ist die Umgebungsvariable `CI` gesetzt, scheitern sie. Weicht ein Bild ab, liegt das erhaltene Bild unter `TestResults/golden/<Name>.actual.png`. Nach einer gewollten Änderung der Oberfläche die Referenzbilder neu erzeugen und jedes geänderte PNG im Pull Request ansehen:

```powershell
$env:ZEHOS_UPDATE_GOLDEN = "1"; dotnet test ProjektZeh.Tests\ProjektZeh.Tests.csproj --filter "FullyQualifiedName~DesktopE2ETests"; Remove-Item Env:ZEHOS_UPDATE_GOLDEN
```

Unter Linux oder macOS entsprechend `ZEHOS_UPDATE_GOLDEN=1 dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --filter "FullyQualifiedName~DesktopE2ETests"`. Die Referenzbilder der Setup-Seiten (`setup-*`) gehören zu `SetupWizardTests`; dafür denselben Aufruf mit `FullyQualifiedName~SetupWizardTests` verwenden. Ist zugleich `CI` gesetzt, scheitert das Aktualisieren absichtlich. Ein Test prüft, dass jedes PNG genau die Pixel seiner Prüfsumme zeigt.

## Dateibetrachter, Texteditor und Pong

Der Desktop-Starter öffnet **Dateien**, **Editor** und **Pong**. Tab/Shift+Tab wechseln den Fokus, Enter aktiviert Schaltflächen und Listeneinträge. Im Editor öffnen F3 und speichern F2 die Datei; Strg+A markiert ein Textfeld vollständig. Ungespeicherte Änderungen werden beim Schließen mit „Zurueck“, „Verwerfen“ oder „Speichern“ behandelt. Schreibfehler erhalten den Entwurf. Der Datei-Explorer bietet **<**/**>**/**^** für Zurück, Vor und den übergeordneten Ordner, ein Pfadfeld und links die Orte *Persönlich* (Startverzeichnis) und *Laufwerk* (`/data`). Die Liste zeigt Name, Größe und Typ; Doppelklick oder Enter öffnet Ordner beziehungsweise Dateien im Editor, Rücktaste geht eine Ebene höher, F5 lädt neu. **Ordner neu** und **Umbenennen** (auch F2) fragen den Namen in einer Zeile unter der Liste ab. Enter übernimmt; ein erneuter Klick auf dieselbe Schaltfläche oder die Auswahl eines anderen Eintrags bricht ab (Esc wechselt wie überall im Desktop zur Textshell). **Löschen** (auch Entf) verlangt eine zweite Bestätigung desselben Eintrags; Ordner müssen leer sein. Ab 1400 Pixel Fensterbreite erscheint rechts eine Textvorschau. Alle Aktionen laufen über den gemeinsamen Dateidienst mit dessen Rechteprüfung. Umbenennen gilt nur für Dateien.

Pong verwendet W/S oder Pfeil auf/ab und Leertaste für Pause. Beim Fokusverlust pausiert das Spiel; Esc/F4 erhält weiterhin die Fenster und öffnet die Textshell. Bildrate, Speicher und Auslastung zeigt der Taskmanager; der Desktop selbst bleibt frei von Diagnosetexten. Bedeutung, Grenzen und geprüfte Abläufe stehen in [Desktopanwendungen](../../docs/DESKTOP_APPS.md). Dateioperationen bleiben synchron; maximal acht Fenster und 65.536 Zeichen je Textdatei bleiben die Grenzen.

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
| `date` / `uname` / `free` | Ortszeit Europe/Berlin mit MEZ/MESZ aus der UTC-Hardwareuhr (nach einem SNTP-Abgleich korrigiert), Kernel/Architektur und tatsächliche physische Speicherwerte anzeigen |
| `exit` | Aktive Terminalsitzung schließen; die Textshell bleibt als Wiederherstellungskonsole geöffnet |

Textfilter können statt einer Datei die Ausgabe einer vorherigen Pipelinestufe lesen. Beispielsweise erzeugt `echo blau >> farben.txt` eine Datei oder hängt Text an, `cat farben.txt | sort | uniq > eindeutig.txt` speichert eine gefilterte Ausgabe und `help | less` öffnet die Hilfe im Pager. `alias ll=ls` legt einen Argumentalias an; eingebettete Pipelines und Umleitungen gehören nicht zu Aliaswerten. Für Gruppenargumente ist etwa `alias suche=grep "zwei Worte"` möglich.

Eine Pipeline umfasst höchstens 16 Stufen und 65.536 Ausgabezeichen pro Stufe. Jeder ausgeführte Befehl und jeder Dateizugriff prüft weiterhin seine Berechtigungen. Ein Fehler stoppt die Pipeline; ein Ausgabeziel wird dabei nicht ersetzt. Konto- und Kennwortdialoge sind mit Pipes und Umleitungen ausgeschlossen. Pipes und Umleitungen sind für ausgebende Befehle vorgesehen; Dateiveränderungen und Energieaktionen werden nicht als Pipelinefilter ausgeführt. `>` verwendet die vorhandene Sicherungs-/Wiederherstellungslogik; das ist keine Garantie bei Stromausfall. `>>` benötigt bei vorhandenen Dateien Lese- und Schreibrechte. Es gibt keine GNU-/POSIX-Kompatibilitätszusage, keine Ausführung gewöhnlicher Linux-Programme. Dieser Konsolenausbau ergänzt keine Netzwerkbefehle; separat integrierte Netzwerkfunktionen bleiben erhalten.

### Scrollen in Textshell und GUI

Das vertikale Scrollrad bewegt den Inhalt unter dem Zeiger um drei Zeilen pro Raster. Es funktioniert auch in geteilten GUI-Terminals, im Pager (`less`), in Listen und mehrzeiligen Textfeldern. Der Tastaturfokus und die aktuelle Auswahl bleiben beim Scrollen erhalten. Auch verkürzte Terminalmenüs lassen sich mit dem Rad blättern. Für die Schriftgröße gelten auf der deutschen Tastatur **Strg+Plus/Minus** beziehungsweise **Strg+0**.

Die Textshell ohne Desktop besitzt ebenfalls einen begrenzten Puffer von **256 Ausgabezeilen**. Scrollrad und Bildtasten blättern darin; Status und Eingabe bleiben sichtbar. Bearbeiten der Eingabe oder Tab-Vervollständigung kehrt zur neuesten Ausgabe zurück. `clear` und ein Benutzerwechsel leeren den Puffer. F4/Esc erhalten ihn; nach einer Auflösungsänderung wird er neu umgebrochen. Dies ist die lokale Textshell; `cosmos run --headless` ohne Anzeigeclient stellt weiterhin keine sichtbare Fernkonsole bereit.

Die beiden mit Cosmos ausgelieferten Bitmapfonts (geprüft mit 3.0.94) werden für die deutschen Zeichen und das Eurozeichen passend zugeordnet. Eigene Fonts benötigen eine eigene PSF-Zeichenzuordnung.

## Netzwerk in der Shell

`netinfo` zeigt Adapter, Link und IPv4-Konfiguration. Netzwerkbefehle starten einen Hintergrundauftrag pro Terminal; das Ergebnis erscheint automatisch in derselben Sitzung, `net cancel` fordert den Abbruch an. Ein laufender Auftrag blockiert keine weitere Shell-Eingabe. Beim Schließen des Terminals wird sein Auftrag abgebrochen.

Nach dem Boot versucht ZehOS bei Link ohne IPv4-Adresse automatisch DHCP, bei fehlender Antwort erneut nach zehn Sekunden. Eine manuell als Administrator mit `net static 10.0.2.15 255.255.255.0 10.0.2.2 10.0.2.3` gesetzte Adresse bleibt erhalten; `net dhcp` fordert DHCP ausdrücklich erneut an. Das Netzwerksymbol unten rechts zwischen Desktopumschaltung und Uhr zeigt Verbindung, Suche oder fehlenden Link/Adapter. Ein Linksklick öffnet den Zustand und die IPv4-Adresse, der nächste Klick oder Esc schließt die Anzeige. „Verbunden“ bedeutet Link mit IPv4-Adresse, keine geprüfte Internetverbindung.

Sobald eine IPv4-Adresse vorliegt, gleicht ZehOS die Uhr einmal per SNTP mit `pool.ntp.org` ab (bei Fehlern höchstens dreimal je Lauf der Netzwerkautomatik). Administratoren können das mit `net time [Server] [Port]` erneut anstoßen. Die Hardwareuhr wird dabei nicht verändert. Ein Abgleich darf die Uhr beliebig vor-, aber höchstens 5 Minuten zurückstellen, solange die Uhr plausibel ist. Eine unplausible Uhr (etwa vor dem Stand des Wurzelbundles) wird vollständig übernommen. SNTP ist nicht authentifiziert: In diesem Fall kann ein Angreifer im Netz jede Zeit ab dem Bundle-Stand setzen. Die Grenzen stehen in [NETWORK_TESTS.md](../../docs/NETWORK_TESTS.md).

Mit konfigurierter Adresse stehen `ping <IPv4>`, `nslookup <Name>` und `fetch http://10.0.2.2:18080/ok` bereit. Der Testserver läuft auf dem Entwicklungsrechner; `10.0.2.2` bezeichnet ihn aus dem QEMU-Gast. `fetch https://Host/Pfad` verwendet TLS 1.2/1.3 mit Zertifikatsketten-, Namens- und Gültigkeitsprüfung gegen ein eingebettetes Mozilla-Rootbundle. HTTPS verlangt eine korrekte UTC-Uhr und Hardwarezufall (RDSEED/RDRAND oder virtio-rng); ohne diese Voraussetzungen scheitert der Abruf. Unterstützt werden IPv4 und ASCII-DNS-Namen; IPv6 und Unicode-Zielnamen werden abgelehnt.

Die Automatik belegt einen der acht Hintergrundplätze. Während DHCP die Netzwerksperre hält, kann die Update-Prüfung „Netzwerkdienst ist belegt.“ melden; nach Ende der Suche erneut versuchen. Cosmos-DHCP lässt sich erst nach seinem Empfangs-Warteabschnitt abbrechen. Dauert das länger als die zwei Sekunden zum Beenden aller Aufgaben, bricht die Energieaktion sicher ab und lässt sich danach erneut anfordern.

Der erste HTTP-Vertrag verlangt Status 200, unkomprimierte Inhalte und `Content-Length`. Header sind auf 8192 Bytes, Antwortkörper auf 16384 Bytes begrenzt. Weiterleitungen und Transfercodierungen werden abgelehnt. Cosmos 3.0.94 verarbeitet einen Abbruch während DHCP oder TCP-Verbindungsaufbau erst nach Rückkehr seines internen Warteaufrufs. Die übrige Bedienung läuft auf dem Hauptthread weiter. [Architektur, HTTPS-Plan und Prüfnachweise](../../docs/NETWORK_TESTS.md).
