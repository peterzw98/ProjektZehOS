# Entwicklung, Tests und Debuggen

Teil des Handbuchs zu ZehOS; Überblick und Schnellstart in der [README](../../README.md).

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

`disk init` ist auf leere Platten von 64 MiB bis 128 GiB mit 512-Byte-Sektoren begrenzt. Bis 4 GiB wird die ganze Platte auf Nullen geprüft; darüber nur Kopf und Ende (je 64 MiB, mit MBR/GPT- und Dateisystemkennungen) sowie 4 KiB alle 64 MiB, und nur mit `disk init <Name> --yes-large`. Daten zwischen den Stichproben werden dort nicht erkannt. Bereits vorhandene Partitionen oder Daten werden abgelehnt. Eine Platte mit GPT-Spuren (Schutz-MBR `0xEE` oder Signatur `EFI PART` in LBA 1 bzw. der letzten LBA, etwa eine installierte ZehOS-Platte oder eine abgebrochene Installation, sobald Schutz-MBR oder ein GPT-Kopf geschrieben ist) lehnt `disk init` immer ab (`InstalledLayout`), auch mit `--yes-overwrite`; eine installierte ZehOS-Platte lässt sich nur über die Installation mit `--erase` neu einrichten, eine fremde GPT-Platte (Windows, Linux) muss vorher außerhalb von ZehOS geleert werden. Die Einrichtung erstellt eine FAT32-LBA-Partition (MBR-Typ `0x0C`) und formatiert FAT32; Cosmos bestimmt die dazu passende Clustergröße. Das Laufwerk wird anschließend mit `disk mount` eingebunden. Ein fehlgeschlagener oder abgebrochener Einrichtungsvorgang wird beim Booten nicht automatisch repariert oder wiederholt. Beim Boot gilt der Bestand auf `sata0` (Bestand = `sata0` = AHCI-Port 0): trägt `sata0` eine Partition vom Typ `0x0C` (Label egal, im Zweifel gilt Bestand), wird nur sie (genau eine) unter `/data` eingebunden; eine `ZEHOSDATA`-Platte daneben wird nicht bevorzugt und nur im seriellen Protokoll vermerkt. Ohne Bestand wird eine einzelne `ZEHOSDATA`-Platte nur dann automatisch eingebunden, wenn sie schon eine Kontendatei `.zehos-security.db` trägt (der Kernel bindet sie dazu schreibgeschützt ein, prüft und hängt sie wieder aus); eine leere `ZEHOSDATA`-Platte wird nie automatisch eingebunden, sondern nur gemeldet und erst mit `disk use <Name> --yes` verwendet. Zwei oder mehr `ZEHOSDATA`-Platten ergeben einen Fehler (`AmbiguousDataDisk`). `disk mount <Name>` bindet nur `sata0` ohne Bestätigung ein, jede andere Platte nur mit `--yes`. Bei `AmbiguousDataDisk`, einer erkannten, aber nicht eingebundenen Platte oder einem fehlgeschlagenen Einbinden wird keine Administrator-Ersteinrichtung angeboten und kein Konto angelegt, stattdessen steht eine Fehlerzeile (`STORAGE Fehler: …`) im seriellen Protokoll; ohne jede Datenplatte bleibt der Boot wie bisher. Die Ersteinrichtung gibt es nur beim Boot und nur mit tatsächlich eingebundenem `/data` und ohne vorhandene Kontendatei; wechselt `/data` in einer laufenden Sitzung (`disk mount`, `disk use`) auf eine Platte ohne Kontendatei, wird gesperrt statt eingerichtet (neue Konten dann nur im Sicherheits-Recovery-Build mit `security recover`). Schlägt `disk init` nach dem Schreiben des MBR fehl, bleibt dieser MBR zurück und ein zweites `disk init` lehnt die Platte als nicht leer ab; die Fehlermeldung nennt das. Datenplatten werden nur über MBR-Partitionen erkannt; GPT-Platten gelten nie als Datenplatte. Der Installer bietet dagegen jede Platte mit einer Partition `ZEHOSDATA` nicht an, auch wenn sie als GPT-Partition (Basic Data oder ESP) angelegt ist. Jeder Build schreibt beim Boot je Laufwerk Größe und Partitionszahl ins serielle Systemprotokoll (für die Gasttests).

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

Mit `shutdown` ausschalten und mit demselben Startskript erneut öffnen oder mit `reboot` direkt neu starten. ZehOS versucht beim Start ausschließlich die vorhandene Partition von `sata0` unter `/data` einzubinden; es formatiert dabei nichts. Mit `cat /data/texte/hallo.txt` muss der zuvor gespeicherte Inhalt erscheinen. Beide Energieaktionen hängen ein eingebundenes Datenlaufwerk vorab mit Schreiben der Dateisystempuffer aus. Schlägt das Aushängen fehl, wird die Aktion abgebrochen und die Shell bleibt bedienbar. Im Root-Profil verweigern beide Befehle, solange eine Paket-App läuft, sperren dann alle Schreibzugriffe auf das Root-Volume, hängen Zusatzplatten und zuletzt `/boot/efi` mit Sync aus und markieren den ext2-Root als sauber; jeder Fehler nimmt das zurück (Root wieder als offen markiert, Platten wieder eingehängt, Schreiben wieder frei). Gelingt das erneute Öffnen des Root nicht, bleibt er bis zum Neustart schreibgesperrt. Ohne eingebundenes Datenlaufwerk sind beide Befehle ebenfalls verfügbar. Sie akzeptieren keine Argumente und verwenden die Energieverwaltung von Cosmos; die Unterstützung hängt von der Plattform ab. Vor einem manuellen Schließen der VM weiterhin `disk unmount` ausführen.

Das QEMU-Startskript und die Visual-Studio-Projekteinstellungen in `ProjektZeh/.cosmos/config.json` setzen `-action shutdown=poweroff,reboot=reset`: `shutdown` beendet die VM, `reboot` startet den Gast in derselben VM neu. Damit werden die von Cosmos vorgegebenen Optionen (geprüft mit 3.0.94) `-no-shutdown` und `-no-reboot` überschrieben. Die Einstellung gilt ab dem nächsten VM-Start. Die vorhandene Vorbereitung mit Aushängen des Datenlaufwerks bleibt unverändert.

Mit `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-power-commands.ps1` werden beide Befehle in einer eigenen unsichtbaren QEMU-VM ohne Datenplatte geprüft. Nach `reboot` müssen ein zweiter Shellstart und ein laufender Gast nachweisbar sein; nach `shutdown` muss QEMU mit Exitcode 0 enden. Das ISO vorher bauen; ein anderes ISO lässt sich mit `-IsoPath` auswählen. Ohne PowerShell (Linux) prüft `python3 scripts/test-desktop-guest.py --iso <Recovery-ISO> --power` denselben Ablauf (reboot in derselben VM bis zur Textshell, danach shutdown mit Exitcode 0) mit einem Recovery-Build (`-p:ZehOSRecovery=true`), weil der normale Build auf dem Desktop startet; GDB prüft diese Fassung nicht. Die Kryptografieprobe (`-p:ZehOSSecurityProbe=true`) startet unter Linux `python3 scripts/test-security-probe.py --iso <ISO>`.

Mit dem Zusatz `-WithDebugger` hängt derselbe Test das installierte `gdb-multiarch` samt ELF-Symbolen an die zunächst angehaltene VM. GDB muss beim Neustart verbunden bleiben und nach dem Ausschalten einen normalen Gastabschluss melden; ein Stoppsignal wie `SIGQUIT` lässt den Test fehlschlagen. Die ELF-Datei muss neben dem ausgewählten ISO liegen und denselben Namen mit Endung `.elf` tragen.

`write` schreibt zunächst eine temporäre Datei im selben Verzeichnis und ersetzt das Ziel erst nach erfolgreichem Schreiben und Leeren der Schreibpuffer. Die alte Datei wird dafür unter einem kurzen Zufallsnamen gesichert und bei einem fehlgeschlagenen Austausch zurückbenannt. Schlägt auch die Wiederherstellung fehl, bleibt die Sicherung erhalten und ihr Pfad steht in der Fehlermeldung. Ein Schreibfehler, etwa ein voller Datenträger, lässt den bisherigen Inhalt erhalten. Dafür muss vorübergehend Platz für die alte und die neue Datei vorhanden sein; ein Schutz vor Stromausfall während des Ersetzens ist damit nicht zugesichert. `write hallo.txt ""` leert die Datei. Ein bereits belegter Mountpunkt `/data` wird bei `disk mount` abgelehnt.

Auf beschreibbarem ext2 begrenzt der Dateidienst jedes Verzeichnis auf 8189 direkte Einträge (ein Platz unter der Cosmos-Grenze bleibt für Umbenennungen frei), einschließlich versteckter Dateien und Unterverzeichnisse. Ein weiterer Eintrag liefert `cause='DirectoryEntryLimit'`; Dateien dann auf mehrere Ordner verteilen. Temporäre Schreibdateien zählen mit: Das Ersetzen in einem bereits vollen Ordner kann deshalb scheitern, wobei das Original erhalten bleibt. Eine Umbenennung im selben Ordner und `touch` auf vorhandene Dateien bleiben möglich. FAT erhält diese zusätzliche Grenze nicht. Die Layout-Migration lehnt übergroße Quellverzeichnisse und durch ihre Zuordnung überfüllte Zielverzeichnisse vor dem ersten Zielschreiben ab.

Ist ext2 als `/` eingehängt, hält Cosmos 3.0.97 jede seit dem Start berührte Datei im Speicher, bis der Datenträger ausgehängt wird. Nach etwa 17.500 verschiedenen Dateien je Sitzung können weitere Dateioperationen mit einem Fehler scheitern, weil der Speicher zu stark zerstückelt ist. Die genaue Zahl hängt vom Arbeitsspeicher und vom bisherigen Verlauf ab.

- **Betroffen** sind Vorgänge, die viele verschiedene Dateien berühren: große Ordner auflisten, ganze Bäume durchsuchen oder kopieren, Pakete mit vielen Dateien installieren.
- **Kaum betroffen** ist das normale Speichern, weil es freigegebene Dateinummern wiederverwendet.
- **Abhilfe:** ZehOS neu starten. Danach beginnt die Zählung von vorn.

ZehOS umgeht die Grenze bewusst nicht (Entscheidung 17. November 2026). Hintergrund steht in [COSMOS_SUPPORT.md](../../docs/COSMOS_SUPPORT.md).

Die Eintragsprüfung gilt für den Dateidienst (FileService). Direkte interne Schreibzugriffe, etwa des Paketbestands oder der Desktop-Einstellungen, sind davon nicht erfasst; sie ist deshalb keine harte Dateisystemgarantie.

Pfade mit Leerzeichen werden wie bei `echo` in Anführungszeichen gesetzt. Ohne Datenplatte oder bei einem Dateifehler bleibt die Shell bedienbar. Fehler werden über die gemeinsamen Result-Typen an den Kernel weitergereicht und dort einmal ausgegeben.

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

Ohne PowerShell (Linux) leistet `python3 scripts/test-thread-probe.py --iso <ISO>` dasselbe mit denselben QEMU-Argumenten; `--accel kvm` und `ZEHOS_QEMU_SMP` wirken wie in den übrigen Gastskripten. Es verlangt zusätzlich die Zeile `MESSAGES_PROBE PASS`.

Der Helfer startet eine eigene QEMU-VM ohne Datenlaufwerk, wartet höchstens 45 Sekunden auf `THREAD_PROBE PASS` und beendet nur diese Test-VM. Serielle Ausgabe und QEMU-Fehlerausgabe liegen in einem jeweils neuen Ordner unter `.tools/thread-probe/`. Fehlendes Ergebnis, `THREAD_PROBE FAIL` oder ein vorzeitiges VM-Ende lassen den Helfer fehlschlagen. Ein normales ISO erfüllt diese Probe ausdrücklich nicht.

Der Test verwendet den ZehOS-Round-Robin-Scheduler und prüft auch Ressourcenbereinigung der Aufgabenverwaltung sowie die Nachrichtenwarteschlangen aus 6b. Ohne Buildoption ist die Probe nicht enthalten. Da beide Builds denselben ISO-Ausgabepfad verwenden, anschließend für den normalen Betrieb erneut ohne Probe bauen:

```powershell
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSThreadProbe=false -warnaserror
```

Ergebnisse und Grenzen stehen in [COSMOS_SUPPORT.md](../../docs/COSMOS_SUPPORT.md#aufgabenverwaltung-und-round-robin-am-13-november-2026).

## Desktop-Dateifehler gezielt prüfen

Roadmap 7.5 ist mit fehlendem Datenlaufwerk und einer vollständig belegten isolierten FAT32-Kopie in QEMU geprüft. Die [Prüfanleitung](../../docs/DESKTOP_STORAGE_TESTS.md) beschreibt den Ablauf und den Helfer `scripts/create-full-fat32-test-copy.py`. Dieser erzeugt ausschliesslich eine neue Testkopie und ersetzt kein vorhandenes Image. VMware-Dateiprüfungen bleiben offen.

### Nachrichten und Synchronisation (Etappe 6b)

`messages start slow-producer` startet zwei Hintergrundaufgaben mit 24 nummerierten Nachrichten und einem Puffer für drei Einträge. `messages start slow-consumer` macht den vollen Puffer sichtbar. `messages status` zeigt Fortschritt, Wartevorgänge, Reihenfolgefehler und die gemeinsame Kontosumme (2000). Die Bedienung bleibt während der Wartezeiten verfügbar; `tasks list` zeigt beide Aufgaben.

`messages close` schließt den Puffer: Neue Nachrichten werden abgewiesen, vorhandene noch verarbeitet. `messages stop` bricht beide Aufgaben ab. Auch `tasks cancel <Consumer-ID>` beendet den am vollen Puffer wartenden Producer. Nach dem Ende beider Aufgaben lässt sich die Vorführung erneut starten. Die Vorführung gehört zum gemeinsamen Kerneldienst; alle Terminals sehen denselben Zustand.

`MessageQueue<T>` verwendet den vorhandenen Cosmos-Monitor mit einer gemeinsamen Sperre für Zustand und Wecksignale. Wartende Varianten liefern `Cancelled`, `TimedOut` beziehungsweise `Closed`; nicht wartende Varianten unterscheiden `Empty` und `Full`. Ein geschlossener Puffer lässt sich noch leeren. Beide Übertragungsrichtungen des kontrollierten Kontobeispiels erwerben zuerst die Sperre von Konto 1 und dann von Konto 2, damit keine zyklische Warteabhängigkeit entsteht.

Der optionale Threadprobe-Build prüft zusätzlich nummerierte Zustellung, Kapazität, Zeitlimits, Schließen/Abbruch wartender Teilnehmer und den Neustart nach alleinigem Consumer-Abbruch bei vollem Puffer. Erfolg meldet `MESSAGES_PROBE PASS`; erst nach allen Teilprüfungen folgt `THREAD_PROBE PASS`. Einzelheiten und tatsächlich geprüfte Plattformen stehen in [docs/ETAPPE6B_STATUS.md](../../docs/ETAPPE6B_STATUS.md).

## Automatische Builds und Codeprüfung

GitHub Actions prüft Pull Requests und jeden Push auf `master`. Beide Workflows können außerdem unter **Actions → Run workflow** manuell gestartet werden:

- **Shell tests and .NET analysis** baut das Testprojekt mit den empfohlenen .NET-10-Analyseregeln und führt die Shelltests aus. Compiler- und Analyzerwarnungen lassen den CI-Build fehlschlagen. Der Job führt auch die Python-Skripttests, die Red-Zone-Prüfung der committeten AOT-Fixtures (`scripts/check-aot-redzone.py`, braucht `objdump`) und die Abdeckungsprüfung aus. Lokal laufen die Python-Skripttests mit `python3 scripts/run-tests.py` schneller (parallel, je Modul ein Prozess, dieselben Tests); die CI bleibt bei `unittest discover`.
- **Shell tests (Windows)** führt dieselben Hosttests zusätzlich auf `windows-2025` aus (ohne Abdeckungsbericht), weil Pfadtrenner, Zeilenenden und Groß-/Kleinschreibung dort abweichen. Der Linux-Job bleibt die maßgebliche Prüfung.
- **Docs, scripts and workflow lint** läuft immer, auch bei reinen Doku-PRs: `scripts/check-doc-links.py` prüft relative Markdown-Verweise und Überschriften-Anker ohne Netzzugriff, `shellcheck` (Fehler und Warnungen) prüft die Shellskripte, `actionlint` die Workflows.
- **Cosmos x64 build** baut Cosmos CLI und Patcher aus dem festgelegten Quellstand, installiert die native Toolchain und erstellt ein bootfähiges Release-ISO. Auch hier gelten die .NET-Analyseregeln.
- **Release images (live and installer)** baut parallel dazu die Live- und Installer-Images mit `scripts/build-release.sh` und prüft sie auf lokale Pfade. Der Job ist von **Cosmos x64 build** getrennt, damit der Gastlauf nicht auf diesen vollständigen Neubau wartet.
- **Detect code changes** prüft bei Pull Requests, ob etwas anderes als Markdown oder Dateien unter `docs/` geändert wurde. Reine Doku-PRs überspringen alle Jobs außer **Docs, scripts and workflow lint** (Hosttests, Windows-Tests, Paketbuilds, Kernel, Release-Images und Gastläufe); übersprungene Jobs zählen als bestanden. CodeQL ignoriert Doku-PRs über `paths-ignore`. Dependabot schlägt monatlich Updates der angehefteten GitHub-Actions vor (`.github/dependabot.yml`); Paket-, SDK- und Cosmos-Updates sind davon ausgenommen.
- **Guest boot and desktop (QEMU)** startet das ISO aus dem Job **Cosmos x64 build** unter QEMU mit TCG und bedient es mit `scripts/test-desktop-guest.py`: Boot, Anmeldung, Fenster öffnen und schließen, Textshell und `shutdown`. Bildschirmfotos und Protokolle liegen im Artefakt `guest-desktop`.
- **Guest recovery boot (QEMU)** baut das Recovery-ISO (`-p:ZehOSRecovery=true`) und prüft mit `scripts/test-desktop-guest.py --recovery` den Boot bis zur Textshell, das Ausbleiben des Desktops und `shutdown`. Der Job läuft parallel zu den übrigen Jobs bei jedem Code-PR, auf `master` und bei manuellem Start; er verlängert die Pipeline nicht.
- **CodeQL (C#, GitHub Actions, Python)** prüft den eigenen C#-Produktionscode sowie die Workflows und Python-Skripte mit der erweiterten Sicherheitsabfrage. Eine zusätzliche Analyse läuft wöchentlich. Ergebnisse stehen unter **Security → Code scanning** und gegebenenfalls direkt am Pull Request.

**Self-hosted Runner:** Nur die beiden QEMU-Jobs (**Guest boot and desktop**, **Guest recovery boot**) laufen auf dem Runner `MSI` (Labels `self-hosted`, `Linux`, `X64`), weil dort Cache und Downloads entfallen; alle anderen Jobs bleiben auf GitHub-Runnern. Das gilt nur für Pushes, manuelle Läufe und Pull Requests aus diesem Repository: Da das Repository öffentlich ist, laufen Pull Requests aus Forks auf `ubuntu-24.04` (`scripts/tests/test_ci_runner_policy.py` prüft das). `MSI` ist Peters Linux-Rechner, auf dem auch die Agenten arbeiten, und der Runner nutzt denselben Benutzer. Jeder dieser Jobs legt deshalb `HOME`, NuGet-Cache und globale dotnet-Werkzeuge unter `RUNNER_TEMP` an (`.github/actions/runner-state`) und räumt sie danach auf; per `sudo` wird nichts installiert, und die Gastläufe bleiben ohne KVM unter TCG. Ein Runner führt einen Job nach dem anderen aus, zwei Gast-Jobs laufen also nie gleichzeitig. Der Workflow **Self-hosted runner probe** (nur von Hand) liest nur Benutzer, Speicher, dotnet, Docker, `/dev/kvm`, QEMU und Werkzeuge des Runners aus. Empfehlung für den Runner-Dienst auf `MSI`: niedrige Priorität, etwa `Nice=19` und `CPUQuota=` in der systemd-Unit, damit ein CI-Lauf die Entwicklungsumgebung nicht ausbremst.

Das ISO `ZehOS-x64`, der Kernel-Buildlog und die TRX-Testergebnisse werden am Workflow-Lauf für 14 Tage als Artefakte gespeichert. Die Hosttests laufen ohne Kernel. Die Gastläufe belegen nur die oben genannten Abläufe unter QEMU mit TCG; KVM, VMware, echte Hardware und alle übrigen Funktionen prüft die CI nicht. Das Recovery-ISO wird bei jedem Code-PR gebaut und bis zur Textshell gestartet.

Lokal prüft `python3 scripts/test-desktop-guest.py --iso ProjektZeh/output-x64/ProjektZeh.iso [--accel tcg|kvm|whpx] [--boot-timeout 300]` ein ISO in QEMU: Boot, Ersteinrichtung des Kontos auf einer leeren Wegwerfplatte, Terminal öffnen, tippen und über **X** schließen, Dateien und Einstellungen, Ab- und Anmeldung in der Textshell, F4 und `shutdown` (QEMU muss mit Exitcode 0 enden); Error-Zeilen im seriellen Protokoll lassen ihn scheitern. Mit `--recovery` (ISO aus `dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSRecovery=true` unter `ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso`, ohne Datenplatte gestartet) prüft er beim Recovery-ISO nur Boot bis zur Textshell, das Ausbleiben des Desktops und `shutdown`; `uname` wird nur fotografiert. Bilder, `serial.log` und `stderr.log` liegen unter `.tools/desktop-guest/<Kennung>/`. Das ist nur ein QEMU-Ergebnis; VMware bleibt ungeprüft.

Die Action-Versionen sind auf feste Commits gesetzt. Die native Cosmos-Toolchain wird vom offiziellen, fortlaufend aktualisierten Release `tools-latest` bezogen. Für CodeQL werden keine separaten Sonar-Zugangsdaten benötigt. Die PR-Workflows verwenden `pull_request`, damit Code aus Forks ohne Zugriff auf Repository-Secrets geprüft werden kann.

Die Regeln liegen in `Directory.Build.props`. Die `.editorconfig` enthält gezielte Ausnahmen für beschreibende Testnamen, konstante Arrayargumente an Assertions und den gemeinsamen Namespace `Shared`. Lokal lässt sich derselbe Buildcheck mit `dotnet build ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` ausführen.

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

ZehOS 0.1.2 verwendet unter VMware dessen gemeldete TSC-/LAPIC-Frequenzen und ergänzt die von LAI benötigten PCI-Zugriffe für das Ausschalten. Cosmos bleibt bei 3.0.94. Die [VMware-Prüfungen](../../docs/VMWARE_STABILITY_TESTS.md) unterscheiden Windows/Player, Linux/Workstation und noch offene Abläufe.

## Entwicklungsplan

Der Bootablauf protokolliert Eingabe, Anzeige und Dateien mit Beginn, Erfolg oder Fehler auf der seriellen Ausgabe und im begrenzten Speicherpuffer. Netzwerk wird bis zur Implementierung ausdrücklich übersprungen. Fehlende Dateien-/Speicherdienste verhindern den Shellstart nicht. Die Prüfungen lesen den Zustand der bereits von Cosmos initialisierten Dienste; sie ersetzen keine Treiberinitialisierung. Der Dateischritt bindet zusätzlich die vorhandene Datenpartition von `sata0` unter `/data` ein und protokolliert einen fehlgeschlagenen Mount. Protokoll, `uptime` und Statusanzeige verwenden gemeinsam eine `Stopwatch` ab ZehOS-Konstruktion, keine Kalenderuhr. `version` zeigt Assemblyversionen, keine aus dem Netz ermittelte Releaseversion.

Für ein Wiederherstellungs-ISO mit `dotnet build ProjektZeh/ProjektZeh.csproj -p:ZehOSRecovery=true` bauen. Dieser Modus umgeht die normale ZehOS-Anzeigeinitialisierung und startet direkt in der Cosmos-Textshell. Ein Fehler oder eine Exception in der normalen Anzeigeinitialisierung aktiviert denselben Rückfall automatisch. Der normale Start öffnet den Desktop nach den Bootschritten; F4 kann ihn auch aus der Recovery-Textshell öffnen. Ein Fehler beim Desktopstart oder Zeichnen wird protokolliert und lässt die Textshell erreichbar. Die Wiederherstellung benötigt weiterhin einen funktionierenden Cosmos-Framebuffer und Tastatur. Fehler vor `BeforeRun` oder eine ebenfalls fehlgeschlagene Textkonsole bleiben nur seriell diagnostizierbar.

Die [Roadmap für ZehOS](../../ROADMAP.md) beschreibt die nächsten Schritte bis zu einem Desktop mit Terminal, Dateiverwaltung, spielbarem Doom und einem einfachen Browser. Jede Etappe enthält konkrete Aufgaben und ein überprüfbares Ergebnis.
