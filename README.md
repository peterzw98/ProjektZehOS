# ProjektZeh

ProjektZeh ist ein Cosmos-Gen3-Kernel für x64 mit einer kleinen Textshell.

## Shell

Nach dem Start erscheint der Prompt `zehos>`. Die Befehle sind:

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

Oben läuft eine Statusanzeige für Zeit, gedrückte Tasten und verlorene Eingabeereignisse weiter, auch während eine Zeile bearbeitet wird. Physische Tastendrücke und das Loslassen werden getrennt von Texteingaben verarbeitet. Bei einem Überlauf der begrenzten Eingabepuffer werden die Tastenzustände zurückgesetzt, damit keine Taste dauerhaft gedrückt bleibt.

## Grafikprobe

**F4** öffnet eine Grafikfläche mit 1024 × 768 Pixeln und 32 Bit Farbtiefe. Sie zeigt Rechtecke, Linien, Bitmaptext, einen Pixelpuffer, einen beschnittenen Zeichenbereich und einen beweglichen Mauszeiger. Maustasten, Klickzähler und ein Marker machen Klicks sichtbar. Gezeichnet wird direkt in den wiederverwendeten Cosmos-Bildpuffer; erst das fertige Bild wird ausgegeben.

**Esc** oder erneut **F4** kehrt zur Shell zurück. Die bisherige Konsolenausgabe wird dabei geleert; die aktuelle Eingabe und der Befehlsverlauf bleiben erhalten. Die Grafikprobe ist die Grundlage für die spätere Fensterverwaltung und noch kein Desktop. Kann sie nicht geöffnet werden, bleibt die Shell erreichbar.

Die gemeinsame Schleife verarbeitet pro Durchlauf höchstens 64 rohe Tastaturereignisse, verteilt Eingaben, aktualisiert die Statusanzeige und zeichnet bei Änderungen mit höchstens etwa 60 Bildern pro Sekunde. Die Zeitmessung verwendet `Stopwatch`; zwischen Durchläufen wartet Cosmos kurz. Datei- und Laufwerksbefehle laufen weiterhin synchron und können die Anzeige während ihrer Ausführung anhalten. Die spätere Hintergrundverarbeitung ist damit noch nicht umgesetzt.

Geprüfte Cosmos-Schnittstellen, Referenz-VM und Grenzen stehen unter [`docs/COSMOS_SUPPORT.md`](docs/COSMOS_SUPPORT.md).

Gemeinsame Ergebnis- und Fehlertypen liegen unter [`ProjektZeh/Shared`](ProjektZeh/Shared/README.md). Die für ZehOS entwickelte Implementierung trennt unveränderliche Statusinformationen von optionalen Ergebniswerten. Erfolg wird über `CreateSuccess` gesetzt, Fehler über `CreateFailure` oder `CreateFromException`; `IsOK()` prüft den Status.

Die MSTest-Tests mit Moq prüfen dieselben Shell- und Dateidienstquellen in einer normalen .NET-Umgebung ohne den Bare-Metal-Kernel. Der Dateidienst wird gegen echte temporäre Verzeichnisse getestet; Cosmos-Treiber und dauerhafte Speicherung nach einem VM-Neustart benötigen zusätzlich einen Gasttest:

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

`write` schreibt zunächst eine temporäre Datei im selben Verzeichnis und ersetzt das Ziel erst nach erfolgreichem Schreiben und Leeren der Schreibpuffer. Die alte Datei wird dafür unter einem kurzen Zufallsnamen gesichert und bei einem fehlgeschlagenen Austausch zurückbenannt. Schlägt auch die Wiederherstellung fehl, bleibt die Sicherung erhalten und ihr Pfad steht in der Fehlermeldung. Ein Schreibfehler, etwa ein voller Datenträger, lässt den bisherigen Inhalt erhalten. Dafür muss vorübergehend Platz für die alte und die neue Datei vorhanden sein; ein Schutz vor Stromausfall während des Ersetzens ist damit nicht zugesichert. `write hallo.txt ""` leert die Datei. Ein bereits belegter Mountpunkt `/data` wird bei `disk mount` abgelehnt.

Pfade mit Leerzeichen werden wie bei `echo` in Anführungszeichen gesetzt. Ohne Datenplatte oder bei einem Dateifehler bleibt die Shell bedienbar. Fehler werden über die gemeinsamen Result-Typen an den Kernel weitergereicht und dort einmal ausgegeben.

## Entwicklungsplan

Der Bootablauf protokolliert Eingabe, Anzeige und Dateien mit Beginn, Erfolg oder Fehler auf der seriellen Ausgabe und im begrenzten Speicherpuffer. Netzwerk wird bis zur Implementierung ausdrücklich übersprungen. Fehlende Dateien-/Speicherdienste verhindern den Shellstart nicht. Die Prüfungen lesen den Zustand der bereits von Cosmos initialisierten Dienste; sie ersetzen keine Treiberinitialisierung. Der Dateischritt bindet zusätzlich die vorhandene Datenpartition von `sata0` unter `/data` ein und protokolliert einen fehlgeschlagenen Mount. Protokoll, `uptime` und Statusanzeige verwenden gemeinsam eine `Stopwatch` ab ZehOS-Konstruktion, keine Kalenderuhr. `version` zeigt Assemblyversionen, keine aus dem Netz ermittelte Releaseversion.

Für ein Wiederherstellungs-ISO mit `dotnet build ProjektZeh/ProjektZeh.csproj -p:ZehOSRecovery=true` bauen. Dieser Modus umgeht die normale ZehOS-Anzeigeinitialisierung und stellt direkt die Cosmos-Textkonsole her. Ein Fehler oder eine Exception in der normalen Anzeigeinitialisierung aktiviert denselben Rückfall automatisch. Auch der normale Start beginnt in der Textshell; F4 öffnet die Grafikprobe. Ein Desktop ist noch nicht implementiert. Die Wiederherstellung benötigt weiterhin einen funktionierenden Cosmos-Framebuffer und Tastatur. Fehler vor `BeforeRun` oder eine ebenfalls fehlgeschlagene Textkonsole bleiben nur seriell diagnostizierbar.

Die [Roadmap für ZehOS](ROADMAP.md) beschreibt die nächsten Schritte bis zu einem Desktop mit Terminal, Dateiverwaltung, spielbarem Doom und einem einfachen Browser. Jede Etappe enthält konkrete Aufgaben und ein überprüfbares Ergebnis.

## Automatische Builds und Codeprüfung

GitHub Actions prüft Pull Requests und jeden Push auf `master`. Beide Workflows können außerdem unter **Actions → Run workflow** manuell gestartet werden:

- **Shell tests and .NET analysis** baut das Testprojekt mit den empfohlenen .NET-10-Analyseregeln und führt die Shelltests aus. Compiler- und Analyzerwarnungen lassen den CI-Build fehlschlagen.
- **Cosmos x64 build** installiert Cosmos CLI und Patcher in Version 3.0.94 samt nativer Toolchain und erstellt ein bootfähiges Release-ISO. Auch hier gelten die .NET-Analyseregeln.
- **CodeQL (C#)** prüft den eigenen C#-Produktionscode mit der erweiterten Sicherheitsabfrage. Eine zusätzliche Analyse läuft wöchentlich. Ergebnisse stehen unter **Security → Code scanning** und gegebenenfalls direkt am Pull Request.

Das ISO `ZehOS-x64`, der Kernel-Buildlog und die TRX-Testergebnisse werden am Workflow-Lauf für 14 Tage als Artefakte gespeichert. Die Tests laufen auf dem Host; ein erfolgreicher CI-Lauf bestätigt keinen automatisierten Start oder Bedienungstest des Kernels in QEMU.

Die Action-Versionen sind auf feste Commits gesetzt. Die native Cosmos-Toolchain wird vom offiziellen, fortlaufend aktualisierten Release `tools-latest` bezogen. Für CodeQL werden keine separaten Sonar-Zugangsdaten benötigt. Die PR-Workflows verwenden `pull_request`, damit Code aus Forks ohne Zugriff auf Repository-Secrets geprüft werden kann.

Die Regeln liegen in `Directory.Build.props`. Die `.editorconfig` enthält gezielte Ausnahmen für beschreibende Testnamen, konstante Arrayargumente an Assertions und den gemeinsamen Namespace `Shared`. Lokal lässt sich derselbe Buildcheck mit `dotnet build ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` ausführen.

## Voraussetzungen

- .NET SDK 10 oder neuer
- Cosmos Gen3 CLI und Bauwerkzeuge (`cosmos check` muss erfolgreich sein)
- Für die Arbeit in Visual Studio: Visual Studio 2026 (Version 18.0 oder neuer). Visual Studio 2022 kann dieses `net10.0`-Projekt nicht bauen.
- Für Breakpoints in Visual Studio: die [Cosmos-Visual-Studio-Erweiterung](https://github.com/CosmosOS/CosmosVsExtension/releases) und der Visual-Studio-Workload **Linux and embedded development with C++**. Der Workload stellt die GDB-Debugengine bereit.

Das Projekt verwendet Cosmos 3.0.94. Die offizielle Installationsanleitung steht unter <https://cosmosos.github.io/articles/user/install.html>.

Die Solution `ProjektZeh.sln` muss ausdrücklich in Visual Studio 2026 geöffnet werden. Visual Studio 2022 meldet bei diesem Projekt einen Fehler zum .NET-10-Ziel und gegebenenfalls einen Folgefehler zur Assembly-Kürzung.

Die Solution in Visual Studio 2026 bauen (`Strg+Umschalt+B`). Das ISO liegt unter `ProjektZeh\bin\Debug\net10.0\linux-x64\cosmos\ProjektZeh.iso`. Die Zielplattform `linux-x64` ist für den Kernel festgelegt.

## Debuggen in Visual Studio mit QEMU

`ProjektZeh` als Startprojekt der Solution auswählen. Mit installierter Cosmos-Erweiterung startet `F5` den Kernel in QEMU und verbindet Visual Studio über GDB. Breakpoints in `Kernel.cs`, `F10` (Prozedurschritt) und die Aufrufliste funktionieren damit wie beim bisherigen Debugstart. `Strg+F5` startet QEMU ohne Debugger. Die serielle Ausgabe erscheint im Fenster **Cosmos OS - Output**.

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
cosmos run --iso ProjektZeh\output-x64\ProjektZeh.iso --arch x64
```

Der erste Befehl erzeugt `ProjektZeh\output-x64\ProjektZeh.iso`. Für einen Start ohne grafisches QEMU-Fenster kann `cosmos run` mit `--headless` ausgeführt werden. Die eigentliche Konsole wird dann nicht angezeigt, weil sie auf dem Framebuffer gezeichnet wird.
