# ProjektZeh

ProjektZeh ist ein Cosmos-Gen3-Kernel für x64 mit einem grafischen Desktop und einer kleinen Shell in unabhängigen Terminalfenstern.

## Shell

Nach dem Start öffnen sich zwei Terminalfenster mit dem Prompt `zehos>`. Im Wiederherstellungsmodus erscheint derselbe Prompt direkt in der Textshell. Die Befehle sind:

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
| `disk init <Name>` | Vollständig leere virtuelle 64-MiB-Testplatte mit MBR und FAT32 einrichten |
| `disk mount <Name>` | Einzige FAT-Partition des Laufwerks unter `/data` einbinden |
| `disk unmount` | `/data` mit Schreiben der Dateisystempuffer aushängen |
| `version` | Versionen der geladenen ZehOS- und Cosmos-Assemblies anzeigen |
| `uptime` | Millisekunden seit ZehOS-Start anzeigen; ohne initialisierten Timer als nicht verfügbar melden |
| `log` | Die letzten maximal 128 Systemdiagnosen in zeitlicher Reihenfolge anzeigen |

Befehlsnamen sind unabhängig von Groß- und Kleinschreibung. Doppelte Anführungszeichen fassen Argumente zusammen, beispielsweise `echo "Hallo ZehOS"`. Backslashes bleiben normale Zeichen; Escape-Sequenzen, Pipes und Umleitungen sind noch nicht implementiert. Leerzeilen werden ignoriert. Bei unbekannten Befehlen, falschen Argumenten oder nicht geschlossenen Anführungszeichen erscheint eine Fehlermeldung und danach der nächste Prompt.

Die Befehlsauswertung in `ProjektZeh/Shell` ist von der Ausgabe über `ITextOutput` getrennt und kann später in einem Terminalfenster verwendet werden. Die Texteingabe verwendet einen nicht blockierenden `LineEditor`: Links/Rechts, Pos1/Ende, Backspace und Entf bearbeiten die Zeile; Hoch/Runter blättern durch die letzten 32 Befehle und zurück zum aktuellen Entwurf. Lange Zeilen scrollen horizontal und sind auf 66.560 Zeichen begrenzt. Das bisherige US-Tastaturlayout bleibt erhalten.

In der Textshell läuft oben eine Statusanzeige für Zeit, gedrückte Tasten und verlorene Eingabeereignisse weiter, auch während eine Zeile bearbeitet wird. Der Desktop zeigt Fensterzahl und Laufzeit in der unteren Leiste. Physische Tastendrücke und das Loslassen werden getrennt von Texteingaben verarbeitet. Bei einem Überlauf der begrenzten Eingabepuffer werden die Tastenzustände zurückgesetzt und ein Ziehvorgang beendet.

## Desktop und Fenster

ZehOS startet zu einem freien Desktop mit **1024 × 768 Pixeln und 32 Bit Farbtiefe**. **Anwendungen** oder **Alt+F1** öffnet das Programmmenü mit Suche für Terminal, Dateien, Texteditor und Pong. Tab/Shift+Tab wechseln den Fokus, Pfeiltasten die Programmauswahl und Enter startet die Auswahl. **>_** startet direkt ein Terminal. Gestaltung und Bedienung orientieren sich an KDE/Cinnamon; ZehOS verwendet weiterhin Cosmos und führt keine Linux-Programme aus.

Ein Klick aktiviert ein Fenster; **Alt+Tab** beziehungsweise **Alt+Shift+Tab** wechseln durch alle Fenster. Die Titelleiste lässt sich mit der linken Maustaste ziehen, die Ränder und Ecken verändern die Größe bis zur jeweiligen Mindestgröße. **_** minimiert, **□** maximiert beziehungsweise stellt die bisherige Geometrie wieder her, **X** schließt mit der gegebenenfalls erforderlichen Rückfrage. Fenster bleiben oberhalb der unteren Leiste. Ein Klick auf einen Eintrag in der Fensterleiste aktiviert oder restauriert ihn; beim aktiven Fenster minimiert er es. Bei wenig Platz zeigen die Einträge kurze Kennungen für Terminal, Dateien, Editor und Pong.

Höchstens acht Fenster sind gleichzeitig geöffnet, einschließlich minimierter Fenster. Jedes Terminal besitzt seine eigene Shell, Eingabe, Historie und sein eigenes Arbeitsverzeichnis, anfangs `/data`. Die letzten **256 Ausgabezeilen** werden beim Ändern der Breite neu umgebrochen; überschrittene Puffergrenzen verdrängen weiterhin die älteste Ausgabe. **Bild auf/Bild ab (PageUp/PageDown)** blättern durch ältere Ausgabe; Texteingabe springt zur neuesten Ausgabe. `clear` leert dessen Ausgabe. Auch nach dem Schließen aller Fenster bleibt das Menü erreichbar.

**Esc** schließt zuerst ein geöffnetes Programmmenü und führt sonst zur Textshell; **F4** wechselt zwischen Desktop und Textshell. Fensterzustände, Terminalausgaben und Eingabeentwürfe bleiben erhalten. Die alte Textkonsolenausgabe wird beim Wechsel geleert; deren eigener Eingabeentwurf und Verlauf bleiben erhalten. Grafikfehler beim Öffnen oder Zeichnen führen zur erreichbaren Textshell zurück.

`WindowManager` zeichnet sichtbare Fenster von hinten nach vorn und beschneidet jeden Inhalt am Fensterbereich. Hintergrund und Fenster werden im vorhandenen Cosmos-Puffer gezeichnet; zusätzliche Bildpuffer pro Fenster sind nicht erforderlich. `IApplication` enthält auch Größenänderung und abbrechbares Schließen. Das Menü verwendet dieselben UI-Bausteine wie die [Anwendungen aus Etappe 8](docs/DESKTOP_APPS.md). Der [Desktop-Umbau](docs/DESKTOP_REDESIGN.md) hält Integration und neue Prüfergebnisse getrennt von deren ursprünglichen Nachweisen fest.

Die gemeinsame Schleife verarbeitet pro Durchlauf höchstens 64 rohe Tastaturereignisse, verteilt Eingaben, aktualisiert die Statusanzeige und zeichnet bei Änderungen mit höchstens etwa 60 Bildern pro Sekunde. Die Zeitmessung verwendet `Stopwatch`; zwischen Durchläufen wartet Cosmos kurz. Datei- und Laufwerksbefehle laufen weiterhin synchron und können die Anzeige während ihrer Ausführung anhalten. Die spätere Hintergrundverarbeitung ist damit noch nicht umgesetzt.

Geprüfte Cosmos-Schnittstellen, Referenz-VM und Grenzen stehen unter [`docs/COSMOS_SUPPORT.md`](docs/COSMOS_SUPPORT.md).

Gemeinsame Ergebnis- und Fehlertypen liegen unter [`ProjektZeh/Shared`](ProjektZeh/Shared/README.md). Die für ZehOS entwickelte Implementierung trennt unveränderliche Statusinformationen von optionalen Ergebniswerten. Erfolg wird über `CreateSuccess` gesetzt, Fehler über `CreateFailure` oder `CreateFromException`; `IsOK()` prüft den Status.

Die MSTest-Tests mit Moq prüfen dieselben Shell-, Datei-, Fenster-, Desktop- und Terminalquellen in einer normalen .NET-Umgebung ohne den Bare-Metal-Kernel. Der Dateidienst wird gegen echte temporäre Verzeichnisse getestet; Cosmos-Treiber und dauerhafte Speicherung nach einem VM-Neustart benötigen zusätzlich einen Gasttest:

```powershell
dotnet test ProjektZeh.Tests\ProjektZeh.Tests.csproj
```

## Dateisystem in QEMU

Der Dateizugriff verwendet das bereits eingebundene `Cosmos.Kernel.System` 3.0.94. Ein zusätzliches NuGet-Paket ist nicht erforderlich. `CosmosStorageService` übernimmt Laufwerkseinrichtung und Mounten, `FileService` den Zugriff über `System.IO`. Die Shell hat ein eigenes Arbeitsverzeichnis, anfangs `/data`; relative Pfade sowie `.` und `..` beziehen sich darauf. Textdateien sind beim Lesen und Schreiben auf 65.536 Zeichen begrenzt.

Kernel bauen und QEMU mit einer separaten virtuellen SATA/AHCI-Datenplatte starten:

```powershell
cosmos build --project ProjektZeh\ProjektZeh.csproj --arch x64
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-qemu.ps1
```

Das Skript erstellt beim ersten Aufruf ein leeres 64-MiB-Image unter `.tools\qemu\data-fat32.img`. Bei weiteren Starts verwendet es dieselbe Datei unverändert wieder. Das Image ist lokal und wird nicht in Git aufgenommen. Ein bisheriges FAT16-Image unter `.tools\qemu\data.img` bleibt erhalten; es wird weder überschrieben noch automatisch konvertiert. Mit `-IsoPath <Pfad>` lässt sich auch das in Visual Studio gebaute ISO starten. Das bisherige Cosmos-`F5`-Debugprofil hängt diese Datenplatte nicht automatisch an.

Nur beim ersten Start in der ZehOS-Shell ausführen:

```text
disk list
disk init sata0
disk mount sata0
```

`disk init` ist ausdrücklich auf eine vollständig mit Nullen gefüllte 64-MiB-Platte mit 512-Byte-Sektoren begrenzt. Bereits vorhandene Partitionen oder Daten werden abgelehnt. Die Einrichtung erstellt eine FAT32-LBA-Partition (MBR-Typ `0x0C`) und formatiert FAT32; Cosmos bestimmt die dazu passende Clustergröße. Das Laufwerk wird anschließend mit `disk mount` eingebunden. Ein fehlgeschlagener oder abgebrochener Einrichtungsvorgang wird beim Booten nicht automatisch repariert oder wiederholt.

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

Das QEMU-Startskript und die Visual-Studio-Projekteinstellungen in `ProjektZeh/.cosmos/config.json` setzen `-action shutdown=poweroff,reboot=reset`: `shutdown` beendet die VM, `reboot` startet den Gast in derselben VM neu. Damit werden die von Cosmos 3.0.94 vorgegebenen Optionen `-no-shutdown` und `-no-reboot` überschrieben. Die Einstellung gilt ab dem nächsten VM-Start. Die vorhandene Vorbereitung mit Aushängen des Datenlaufwerks bleibt unverändert.

Mit `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-power-commands.ps1` werden beide Befehle in einer eigenen unsichtbaren QEMU-VM ohne Datenplatte geprüft. Nach `reboot` müssen ein zweiter Shellstart und ein laufender Gast nachweisbar sein; nach `shutdown` muss QEMU mit Exitcode 0 enden. Das ISO vorher bauen; ein anderes ISO lässt sich mit `-IsoPath` auswählen.

Mit dem Zusatz `-WithDebugger` hängt derselbe Test das installierte `gdb-multiarch` samt ELF-Symbolen an die zunächst angehaltene VM. GDB muss beim Neustart verbunden bleiben und nach dem Ausschalten einen normalen Gastabschluss melden; ein Stoppsignal wie `SIGQUIT` lässt den Test fehlschlagen. Die ELF-Datei muss neben dem ausgewählten ISO liegen und denselben Namen mit Endung `.elf` tragen.

`write` schreibt zunächst eine temporäre Datei im selben Verzeichnis und ersetzt das Ziel erst nach erfolgreichem Schreiben und Leeren der Schreibpuffer. Die alte Datei wird dafür unter einem kurzen Zufallsnamen gesichert und bei einem fehlgeschlagenen Austausch zurückbenannt. Schlägt auch die Wiederherstellung fehl, bleibt die Sicherung erhalten und ihr Pfad steht in der Fehlermeldung. Ein Schreibfehler, etwa ein voller Datenträger, lässt den bisherigen Inhalt erhalten. Dafür muss vorübergehend Platz für die alte und die neue Datei vorhanden sein; ein Schutz vor Stromausfall während des Ersetzens ist damit nicht zugesichert. `write hallo.txt ""` leert die Datei. Ein bereits belegter Mountpunkt `/data` wird bei `disk mount` abgelehnt.

Pfade mit Leerzeichen werden wie bei `echo` in Anführungszeichen gesetzt. Ohne Datenplatte oder bei einem Dateifehler bleibt die Shell bedienbar. Fehler werden über die gemeinsamen Result-Typen an den Kernel weitergereicht und dort einmal ausgegeben.

## Entwicklungsplan

Der Bootablauf protokolliert Eingabe, Anzeige und Dateien mit Beginn, Erfolg oder Fehler auf der seriellen Ausgabe und im begrenzten Speicherpuffer. Netzwerk wird bis zur Implementierung ausdrücklich übersprungen. Fehlende Dateien-/Speicherdienste verhindern den Shellstart nicht. Die Prüfungen lesen den Zustand der bereits von Cosmos initialisierten Dienste; sie ersetzen keine Treiberinitialisierung. Der Dateischritt bindet zusätzlich die vorhandene Datenpartition von `sata0` unter `/data` ein und protokolliert einen fehlgeschlagenen Mount. Protokoll, `uptime` und Statusanzeige verwenden gemeinsam eine `Stopwatch` ab ZehOS-Konstruktion, keine Kalenderuhr. `version` zeigt Assemblyversionen, keine aus dem Netz ermittelte Releaseversion.

Für ein Wiederherstellungs-ISO mit `dotnet build ProjektZeh/ProjektZeh.csproj -p:ZehOSRecovery=true` bauen. Dieser Modus umgeht die normale ZehOS-Anzeigeinitialisierung und startet direkt in der Cosmos-Textshell. Ein Fehler oder eine Exception in der normalen Anzeigeinitialisierung aktiviert denselben Rückfall automatisch. Der normale Start öffnet den Desktop nach den Bootschritten; F4 kann ihn auch aus der Recovery-Textshell öffnen. Ein Fehler beim Desktopstart oder Zeichnen wird protokolliert und lässt die Textshell erreichbar. Die Wiederherstellung benötigt weiterhin einen funktionierenden Cosmos-Framebuffer und Tastatur. Fehler vor `BeforeRun` oder eine ebenfalls fehlgeschlagene Textkonsole bleiben nur seriell diagnostizierbar.

Die [Roadmap für ZehOS](ROADMAP.md) beschreibt die nächsten Schritte bis zu einem Desktop mit Terminal, Dateiverwaltung, spielbarem Doom und einem einfachen Browser. Jede Etappe enthält konkrete Aufgaben und ein überprüfbares Ergebnis.

## Thread- und Warteprobe (Roadmap 6a.1)

Ein ausdrücklich aktivierter Testbuild prüft vor dem Desktop zwei echte Cosmos-Threads ohne freiwillige CPU-Abgabe sowie Schlafen, Abbruch, Join, Ereignisse und Wartezeitlimits:

```powershell
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSThreadProbe=true -warnaserror
./scripts/test-thread-probe.ps1 -IsoPath ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso
```

Der Helfer startet eine eigene QEMU-VM ohne Datenlaufwerk, wartet höchstens 45 Sekunden auf `THREAD_PROBE PASS` und beendet nur diese Test-VM. Serielle Ausgabe und QEMU-Fehlerausgabe liegen in einem jeweils neuen Ordner unter `.tools/thread-probe/`. Fehlendes Ergebnis, `THREAD_PROBE FAIL` oder ein vorzeitiges VM-Ende lassen den Helfer fehlschlagen. Ein normales ISO erfüllt diese Probe ausdrücklich nicht.

Der Test verwendet den vorhandenen Stride-Scheduler; ZehOS-Aufgabenverwaltung und Round-Robin folgen in 6a.2–6a.5. Ohne Buildoption ist die Probe nicht enthalten. Da beide Builds denselben ISO-Ausgabepfad verwenden, anschließend für den normalen Betrieb erneut ohne Probe bauen:

```powershell
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSThreadProbe=false -warnaserror
```

Ergebnisse und Grenzen stehen in [COSMOS_SUPPORT.md](docs/COSMOS_SUPPORT.md#thread--und-warteprobe-am-13-november-2026).

## Automatische Builds und Codeprüfung

GitHub Actions prüft Pull Requests und jeden Push auf `master`. Beide Workflows können außerdem unter **Actions → Run workflow** manuell gestartet werden:

- **Shell tests and .NET analysis** baut das Testprojekt mit den empfohlenen .NET-10-Analyseregeln und führt die Shelltests aus. Compiler- und Analyzerwarnungen lassen den CI-Build fehlschlagen.
- **Cosmos x64 build** installiert Cosmos CLI und Patcher in Version 3.0.94 samt nativer Toolchain und erstellt ein bootfähiges Release-ISO. Auch hier gelten die .NET-Analyseregeln.
- **CodeQL (C#)** prüft den eigenen C#-Produktionscode mit der erweiterten Sicherheitsabfrage. Eine zusätzliche Analyse läuft wöchentlich. Ergebnisse stehen unter **Security → Code scanning** und gegebenenfalls direkt am Pull Request.

Das ISO `ZehOS-x64`, der Kernel-Buildlog und die TRX-Testergebnisse werden am Workflow-Lauf für 14 Tage als Artefakte gespeichert. Die Tests laufen auf dem Host; ein erfolgreicher CI-Lauf bestätigt keinen automatisierten Start oder Bedienungstest des Kernels in QEMU.

Die Action-Versionen sind auf feste Commits gesetzt. Die native Cosmos-Toolchain wird vom offiziellen, fortlaufend aktualisierten Release `tools-latest` bezogen. Für CodeQL werden keine separaten Sonar-Zugangsdaten benötigt. Die PR-Workflows verwenden `pull_request`, damit Code aus Forks ohne Zugriff auf Repository-Secrets geprüft werden kann.

Die Regeln liegen in `Directory.Build.props`. Die `.editorconfig` enthält gezielte Ausnahmen für beschreibende Testnamen, konstante Arrayargumente an Assertions und den gemeinsamen Namespace `Shared`. Lokal lässt sich derselbe Buildcheck mit `dotnet build ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` ausführen.

## Voraussetzungen

- .NET SDK 10; `global.json` wählt die neueste installierte 10.0-Featureversion. Ein allein installiertes SDK 11 reicht für Cosmos 3.0.94 nicht aus.
- Cosmos Gen3 CLI und Bauwerkzeuge (`cosmos check` muss erfolgreich sein)
- Für die Arbeit in Visual Studio: Visual Studio 2026 (Version 18.0 oder neuer). Visual Studio 2022 kann dieses `net10.0`-Projekt nicht bauen.
- Für Breakpoints in Visual Studio: die [Cosmos-Visual-Studio-Erweiterung](https://github.com/CosmosOS/CosmosVsExtension/releases) und der Visual-Studio-Workload **Linux and embedded development with C++**. Der Workload stellt die GDB-Debugengine bereit.

Das Projekt verwendet Cosmos 3.0.94. Die offizielle Installationsanleitung steht unter <https://cosmosos.github.io/articles/user/install.html>.

Die Solution `ProjektZeh.sln` muss ausdrücklich in Visual Studio 2026 geöffnet werden. Visual Studio 2022 meldet bei diesem Projekt einen Fehler zum .NET-10-Ziel und gegebenenfalls einen Folgefehler zur Assembly-Kürzung.

Die Solution in Visual Studio 2026 bauen (`Strg+Umschalt+B`). Das ISO liegt unter `ProjektZeh\bin\Debug\net10.0\linux-x64\cosmos\ProjektZeh.iso`. Die Zielplattform `linux-x64` ist für den Kernel festgelegt.

## Debuggen in Visual Studio mit QEMU

`ProjektZeh` als Startprojekt der Solution auswählen. Mit installierter Cosmos-Erweiterung startet `F5` den Kernel in QEMU und verbindet Visual Studio über GDB. Breakpoints in `Kernel.cs`, `F10` (Prozedurschritt) und die Aufrufliste funktionieren damit wie beim bisherigen Debugstart. `Strg+F5` startet QEMU ohne Debugger. Die serielle Ausgabe erscheint im Fenster **Cosmos OS - Output**.

Für `shutdown` und `reboot` muss die geöffnete Arbeitskopie auch `ProjektZeh/.cosmos/config.json` aus diesem Stand enthalten. Ohne diese Datei pausiert Cosmos den Gast beim Ausschalten; QEMU meldet dann `SIGQUIT`, und Visual Studio zeigt **Unbehandelte Ausnahme: Quit** an einer gerade zugeordneten Quellzeile an. Das ist in diesem Fall keine C#-Exception aus der markierten Methode. Nach einem Branch- oder Konfigurationswechsel die bisherige Debugsitzung beenden und mit `F5` neu starten. Geprüft wurden die Meldung ohne die Korrektur sowie mit Korrektur ein Neustart bis zur Shell, anschließendes Unterbrechen/Fortsetzen im Debugger und Ausschalten mit **Kernel exited with code 0**.

Bei `Console.Write` und anderen Aufrufen der .NET-Laufzeit mit `F10` zur nächsten Zeile des eigenen Kernels gehen. `F11` steigt in die aufgerufene Methode ein; bei `Console.Write` führt das in `System.Console` und Visual Studio kann mangels lokalem Laufzeit-Quellcode „Quelle nicht verfügbar“ beziehungsweise „Rahmen ist nicht im Modul“ anzeigen. Im GDB-Test führte `F10` von `Console.Write` korrekt zu `Console.ReadLine()`.

Die Cosmos-Erweiterung benötigt das global installierte `Cosmos.Tools`-CLI. Die Werkzeuge mit `cosmos check` prüfen. Falls die GDB-Debugengine fehlt, in Visual Studio 2026 den Workload über **Visual Studio Installer → Ändern → Linux and embedded development with C++** hinzufügen und Visual Studio danach neu starten. Unter **Extras → Optionen → Cosmos OS** muss **Handle F5 and Ctrl+F5** aktiviert sein (Standardwert).

Wenn `F5` meldet **QEMU for x64 not found**, `cosmos check` direkt im Terminal der geöffneten Visual-Studio-Instanz ausführen. Fehlt QEMU dort, im selben Terminal `& "$env:USERPROFILE\.dotnet\tools\cosmos.exe" install --auto --tools` starten und Visual Studio anschließend neu öffnen. Entscheidend ist die Werkzeuginstallation im Windows-Benutzerprofil, unter dem Visual Studio läuft.

Das Gen3-Debugging verwendet QEMU und GDB. NativeAOT kann die Anzeige einzelner Variablen einschränken; Haltepunkte und Aufruflisten sind davon unabhängig.

## Start in VMware Player aus Visual Studio

Die Cosmos-Erweiterung übernimmt standardmäßig `F5` und `Strg+F5` für QEMU. Für einen Start in VMware ohne Umstellen der Debug-Einstellung zuerst in Visual Studio bauen (`Strg+Umschalt+B`) und dann im Visual-Studio-Terminal aus dem Repository-Stamm ausführen:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-vmware.ps1 -IsoPath .\ProjektZeh\bin\Debug\net10.0\linux-x64\cosmos\ProjektZeh.iso
```

Alternativ unter **Extras → Optionen → Cosmos OS** die Option **Handle F5 and Ctrl+F5** ausschalten, das Startprofil **VMware Player** wählen und mit `Strg+F5` starten. Zum QEMU-Debuggen die Option wieder aktivieren.

Die VMware-VM liegt im ignorierten Ordner `.tools\vmware`; die frühere Cosmos-Gen2-VM wird nicht verändert. Bei einem weiteren Start die laufende VM vorher in VMware ausschalten. Die VM hat derzeit keine Festplatte, daher bleiben Dateien im Gastsystem nicht erhalten. Der serielle Bootlog liegt unter `.tools\vmware\serial.log`.

VMware hat in Gen3 über dieses Startprofil keine Visual-Studio-Debugger-Anbindung. Für Breakpoints QEMU mit `F5` verwenden.

## Bauen und starten

Im Wurzelverzeichnis des Repositories:

```powershell
cosmos build --project ProjektZeh\ProjektZeh.csproj --arch x64
cosmos run --iso ProjektZeh\output-x64\ProjektZeh.iso --arch x64 -- -action shutdown=poweroff,reboot=reset
```

Der erste Befehl erzeugt `ProjektZeh\output-x64\ProjektZeh.iso`. Für einen Start ohne grafisches QEMU-Fenster kann `cosmos run` vor `--` mit `--headless` ausgeführt werden. Die eigentliche Konsole wird dann nicht angezeigt, weil sie auf dem Framebuffer gezeichnet wird.

## Dateibetrachter, Texteditor und Pong

Der Desktop-Starter öffnet **Dateien**, **Editor** und **Pong**. Tab/Shift+Tab wechseln den Fokus, Enter aktiviert Schaltflächen und Listeneinträge. Im Editor öffnen F3 und speichern F2 die Datei; Strg+A markiert ein Textfeld vollständig. Ungespeicherte Änderungen werden beim Schließen mit „Zurueck“, „Verwerfen“ oder „Speichern“ behandelt. Schreibfehler erhalten den Entwurf. Der Dateibetrachter zeigt Ordner und eine Textvorschau und öffnet die gewählte Datei im Editor.

Pong verwendet W/S oder Pfeil auf/ab und Leertaste für Pause. Beim Fokusverlust pausiert das Spiel; Esc/F4 erhält weiterhin die Fenster und öffnet die Textshell. Leiste und Kopfbereich zeigen Renderdauer, den Heapstand der letzten GC und kumulativ allokierte Bytes. Bedeutung, Grenzen und geprüfte Abläufe stehen in [Desktopanwendungen](docs/DESKTOP_APPS.md). Dateioperationen bleiben synchron; maximal acht Fenster und 65.536 Zeichen je Textdatei bleiben die Grenzen.

## Desktop-Dateifehler gezielt prüfen

Roadmap 7.5 ist mit fehlendem Datenlaufwerk und einer vollständig belegten isolierten FAT32-Kopie in QEMU geprüft. Die [Prüfanleitung](docs/DESKTOP_STORAGE_TESTS.md) beschreibt den Ablauf und den Helfer `scripts/create-full-fat32-test-copy.py`. Dieser erzeugt ausschliesslich eine neue Testkopie und ersetzt kein vorhandenes Image. VMware-Dateiprüfungen bleiben offen.
