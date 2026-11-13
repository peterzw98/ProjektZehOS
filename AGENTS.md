# Projektregeln für KI-Assistenten

Diese Datei beschreibt die technischen Arbeitsregeln für ProjektZehOS. Sie gilt für das gesamte Repository. Konkrete Arbeitsaufträge bestimmen den Umfang einer Änderung; offene Roadmap-Aufgaben sind keine automatische Beauftragung.

## Zusammenarbeit und Wiederverwendung

- Beauftragte Aufgaben selbstständig bis zum geprüften Ergebnis umsetzen. Übliche Umsetzungsentscheidungen eigenständig treffen; bei fachlich wesentlichen Unklarheiten gezielt fragen und unabhängige Arbeit fortsetzen.
- Kommentare auf Deutsch schreiben. Sie erklären Gründe, Grenzen oder eine notwendige Reihenfolge, statt den sichtbaren Code nachzuerzählen. Bei bestehenden Dateien Schreibweise und Zeilenenden erhalten.
- Vor einer neuen Funktion prüfen, welche vorhandenen Projektbausteine, Cosmos-Funktionen oder verfügbaren Pakete sie bereits lösen. Auch eine Lösung, die ungefähr 80 Prozent abdeckt und sinnvoll ergänzt werden kann, vor einer vollständigen Eigenentwicklung bewerten.
- Paketkandidaten anhand aktueller Primärquellen prüfen: Funktionsumfang, Lizenz, Abhängigkeiten und Kompatibilität mit .NET 10, Cosmos und NativeAOT. Eine Desktopbibliothek ist nicht allein deshalb im Kernel verwendbar, weil sie in C# geschrieben ist. Die Wahl beziehungsweise einen notwendigen Eigenbau kurz begründen.
- Vor Updates bestehender Pakete, SDKs, Cosmos-Versionen oder Buildwerkzeuge nachfragen. Recherche und Kompatibilitätsprüfung dürfen vorher stattfinden; die bestehende Version bis zur Entscheidung erhalten.

## Projekt und Orientierung

ZehOS ist ein in C# entwickelter Cosmos-Gen3-Kernel für x64 mit Desktop, Terminalfenstern und einer Textshell als Rückfall. Die Anwendungen laufen derzeit gemeinsam im Kernel. Geplante Funktionen sind von bereits implementierten und tatsächlich geprüften Funktionen zu unterscheiden.

Bei Bedarf die passende Quelle lesen:

- `README.md`: Bedienung, Voraussetzungen, Bauen, Debuggen und VM-Start.
- `ROADMAP.md`: Ziele, Reihenfolge, Abnahmekriterien und offene Aufgaben. Datierte Ausgangsbeschreibungen sind historische Stände.
- `docs/COSMOS_SUPPORT.md`: geprüfte Cosmos-Schnittstellen, Referenzumgebung und bekannte Grenzen.
- `ProjektZeh/Shared/README.md`: gemeinsame Ergebnis- und Fehlertypen.
- `docs/superpowers/specs/` und `docs/superpowers/plans/`: vorhandene Entwürfe und Umsetzungspläne für die jeweilige Aufgabe.

Wichtige Zuständigkeiten:

| Bereich | Aufgabe |
| --- | --- |
| `ProjektZeh/Kernel.cs` | Boot, Zusammenbau der Dienste und Vorbereitung von Energieaktionen |
| `ProjektZeh/Runtime/` | gemeinsame Ereignisschleife und Wechsel zwischen Textshell und Desktop |
| `ProjektZeh/Input/` | Eingabeereignisse, Warteschlangen, Zeileneditor und Cosmos-Anbindung |
| `ProjektZeh/Graphics/` | Zeichenfläche, Cosmos-Anzeige, Fensterverwaltung und Desktop |
| `ProjektZeh/Apps/` | Anwendungsschnittstelle und Terminalanwendung |
| `ProjektZeh/Shell/` | Parser, Befehlstabelle und Ausgabe über `ITextOutput` |
| `ProjektZeh/Storage/` | Dateioperationen und Cosmos-Laufwerksanbindung |
| `ProjektZeh/SystemServices/` und `ProjektZeh/Shared/` | Bootdiagnose, Protokoll und gemeinsame Result-Typen |
| `ProjektZeh.Tests/` | Tests der plattformunabhängigen Logik auf dem Entwicklungsrechner |
| `scripts/` und `.github/workflows/` | VM-Helfer, Energiekommando-Prüfung und CI |

## Technischer Rahmen

- Maßgeblich sind `ProjektZeh/ProjektZeh.csproj`, `global.json` und `Directory.Build.props`: .NET 10, Cosmos Gen3 3.0.94 und Zielplattform `linux-x64`. Der Kernel läuft direkt in der VM; er ist keine normale Linux-Anwendung.
- `global.json` wählt ein installiertes SDK aus der 10.0-Reihe. Ein allein installiertes SDK 11 genügt für den bestehenden Cosmos-Build nicht. Bei Buildproblemen zuerst die lokale SDK-Version prüfen.
- Cosmos SDK, Kernelpakete, CLI und Patcher müssen zusammenpassen. Versionswechsel als eigene technische Änderung behandeln und durch nativen Build und Gaststart prüfen.
- Vorhandene Cosmos-Dienste zuerst verwenden. Bei neuen Schnittstellen die Verfügbarkeit in der tatsächlich verwendeten Version prüfen; aktuelle Online-Dokumentation kann einen neueren Stand beschreiben.
- Die Unterstützung einer .NET-API auf dem Host beweist nicht ihre Verfügbarkeit im Cosmos-/NativeAOT-Kernel.

## Änderungen und Architektur

- Vor Änderungen Arbeitsbaum und betroffene Aufrufer prüfen. Vorhandene, nicht zur Aufgabe gehörende Änderungen erhalten; keine pauschalen Bereinigungen, Rücksetzungen oder Formatierungen durchführen.
- Den vorhandenen Stil der betroffenen Dateien sowie `.editorconfig` und die Analyseregeln aus `Directory.Build.props` verwenden. Änderungen auf den beauftragten Umfang begrenzen.
- Hardwareanbindung und plattformunabhängige Logik getrennt halten. Die Shell über `ShellService`, die vorhandene Befehlstabelle und `ITextOutput` erweitern, damit Textshell und Terminals dieselbe Logik verwenden können.
- Vorhandene `ResultErr`, `ResultObject<T>`, `ResultObjectFactory` und `ResultCode` verwenden. Erfolg über den Status beziehungsweise `IsOK()` prüfen; ein optionaler Wert allein entscheidet nicht über Erfolg.
- Erwartete Eingabe-, Datei- und Laufwerksfehler so behandeln, dass die Shell weiter bedienbar bleibt. Fehler mit verwertbarer Ursache weitergeben; Diagnose nicht durch leere Fehlerbehandlung verlieren.
- Der interaktive Pfad liest Eingaben nicht blockierend. `Console.ReadLine()` und `Console.ReadKey()` nicht wieder in die gemeinsame Ereignisschleife einführen. Tastenzustände und Texteingaben getrennt behandeln, insbesondere beim Ziffernblock.
- Die derzeitige Schleife ist kooperativ; Dateioperationen sind synchron. Bestehende Pufferbegrenzungen und Eingabe-Reset erhalten. Hintergrundarbeit, Scheduling und Synchronisation nur im jeweils beauftragten Umfang ergänzen.
- Den vorhandenen Cosmos-Bildpuffer und das Clipping von `PixelSurface` verwenden. Unnötige Vollbildallokationen pro Bild vermeiden.
- Textshell-Recovery und den Wechsel mit F4/Esc erhalten. Fenster und Terminals behalten dabei ihren jeweiligen Zustand; Shell, Eingabe und Arbeitsverzeichnis eines Terminals bleiben unabhängig von anderen Terminals.

## Verständliche und wiederverwendbare Bausteine

- Funktionen und Klassen erhalten eine klar umrissene Aufgabe. Namen beschreiben das tatsächliche Verhalten; Abfragen erhalten keine überraschenden Schreibzugriffe oder sonstigen Seiteneffekte.
- Bestehende Logik wiederverwenden, bevor eine zweite Implementierung entsteht. Keine zusätzlichen Abstraktionen oder Containerklassen ohne konkreten Nutzen einführen.
- Bei Extraktionen und Umbauten alle Aufrufer prüfen und das Verhalten mit dem bisherigen Stand vergleichen: Fallreihenfolgen, Standardwerte, Fehlerbehandlung und öffentliche Schnittstellen bleiben erhalten, soweit keine Änderung beauftragt ist.
- Externe Eingaben und verwendete Werte an der zuständigen Grenze validieren. Prüfungen eines aufgerufenen Bausteins nicht ohne zusätzlichen Zweck wiederholen; unerreichbare Zustände nicht durch tote Sonderfälle absichern.
- Ressourcenoperationen selbst absichern. Eine vorherige Existenzprüfung garantiert nicht, dass eine Datei oder ein Laufwerk beim Zugriff noch denselben Zustand hat. Die tatsächliche Fehlerursache behandeln und weitergeben.
- Einen eindeutigen Fehlerweg je Operation verwenden. Result-Status nicht durch parallele, versteckte Fehlerfelder ergänzen. Derselbe Fehler wird an der zuständigen Stelle protokolliert oder ausgegeben; doppeltes Logging vermeiden.
- Verzweigungen so schreiben, dass fachlich unterschiedliche Gründe einzeln erkennbar und prüfbar sind. Zusammengehörige Bereichs- und Schutzprüfungen dürfen gemeinsame Bedingungen bilden.
- Aussagekräftige Namen verwenden: PascalCase für Typen und Methoden, camelCase für Variablen und Parameter, `I` als Präfix für Interfaces. Vergleiche mit dem passenden `StringComparison` durchführen, statt Zeichenketten für Vergleiche in Klein- oder Großschreibung umzuwandeln.
- Für Tests keine unnötige öffentliche API schaffen. Das Hostprojekt bindet die plattformunabhängigen Quellen direkt ein; dadurch sind deren interne Bausteine im Testprojekt verfügbar.

## Dateien und Testdaten schützen

- Automatische Formatierung beim Booten vermeiden. Die bestehenden Schutzprüfungen von `disk init` für vollständig leere virtuelle Testplatten erhalten.
- Schreib-, Ersetzungs- und Fehlerfalltests im Gast mit einer isolierten Kopie des Datenimages durchführen. Vorhandene Images nicht überschreiben oder als leere Wegwerfdateien behandeln.
- Der normale Helfer `scripts/start-qemu.ps1` verwendet das bestehende Image `.tools/qemu/data-fat32.img` weiter. Er wählt keine frische Kopie für jeden Test.
- Beim Ersetzen von Dateien die bestehende Sicherungs- und Wiederherstellungslogik erhalten. Daraus keine garantierte Sicherheit bei Stromausfall ableiten.
- Vor `shutdown` und `reboot` ein eingebundenes Datenlaufwerk mit Schreiben der Puffer aushängen. Bei einem Fehler muss die Energieaktion abbrechen und die Shell bedienbar bleiben.
- Generierte Dateien unter `.tools/`, `bin/`, `obj/`, `output-*/` sowie lokale VM-Images und Protokolle nicht in Quelländerungen aufnehmen.

## Bauen und prüfen

Alle Befehle gelten vom Repository-Stamm aus. Voraussetzungen und Installationswege stehen in `README.md`; die folgenden Befehle installieren keine Werkzeuge.

SDK und Cosmos-Werkzeuge prüfen:

```powershell
dotnet --version
cosmos check
```

Hosttests einschließlich Build und Analyse mit Warnungen als Fehler:

```powershell
dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror
```

Das Testprojekt verwendet MSTest und Moq und bindet produktive Quelldateien direkt ein. Neue plattformunabhängige Quellen bei Bedarf in `ProjektZeh.Tests/ProjektZeh.Tests.csproj` ergänzen; Cosmos-abhängige Adapter gehören nicht in den Hostbuild. Für geänderte Logik passende Verhaltens- und Fehlerfalltests ergänzen; bei Fehlerkorrekturen möglichst den ursprünglichen Fehler reproduzieren.

Nativer Release-Build und Start ohne Datenlaufwerk:

```powershell
cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release
cosmos run --iso ProjektZeh/output-x64/ProjektZeh.iso --arch x64 -- -action shutdown=poweroff,reboot=reset
```

- Hosttests ersetzen keinen Kernelstart. Bei Kerneländerungen den nativen Build und den betroffenen Ablauf in QEMU prüfen. Zum Abschluss einer Funktion Boot, Shell, Eingabe und die betroffenen Desktop-/Dateifunktionen kurz prüfen.
- Bei Boot- oder Anzeigeänderungen auch Recovery mit `dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSRecovery=true` bauen und starten. Normale und Recovery-Ausgabe eindeutig auseinanderhalten.
- Für Änderungen an Energieaktionen oder VM-Startkonfiguration `powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/test-power-commands.ps1` verwenden. `-WithDebugger` prüft zusätzlich GDB und benötigt `gdb-multiarch` sowie die zum ISO passende ELF-Datei.
- Die QEMU-Optionen `-action shutdown=poweroff,reboot=reset` und `ProjektZeh/.cosmos/config.json` berücksichtigen. Ohne sie hält Cosmos 3.0.94 den Gast bei Energieaktionen an; im Debugger kann dabei `SIGQUIT` erscheinen.
- QEMU und VMware sind Zielplattformen. Betroffene Kernelabläufe auf beiden prüfen; QEMU bleibt die erste Umgebung für die schnelle Entwicklung und den GDB-Debugger. Das VMware-Profil aus `scripts/start-vmware.ps1` und seine Grenzen stehen in `README.md`; derzeit besitzt diese VM kein Datenlaufwerk.
- Fehlende Plattformprüfungen ausdrücklich als offen führen. Ein QEMU-Ergebnis belegt keine VMware-Kompatibilität. Echte Hardware nur dann als geprüft bezeichnen, wenn dort tatsächlich getestet wurde.
- Bei reinen Dokumentationsänderungen Verweise, Befehle und Aussagen prüfen; dafür ist kein neuer Kernelbuild erforderlich.

## Branch, unabhängiges Review und Pull Request

- Für die Aufgabe einen eigenen Branch mit Präfix `codex/` verwenden. Vom aktuellen passenden Zielbranch ausgehen und fremde, nicht zur Aufgabe gehörende Änderungen aus Commit und PR heraushalten. Bei paralleler Arbeit eine getrennte Arbeitskopie verwenden.
- Änderungen passend prüfen und committen; Pushen gehört zum Abschluss des Arbeitsauftrags. Sobald die Aufgabe fertig ist und keine offenen fachlichen Fragen oder ungeklärten Reviewbefunde bleiben, selbstständig einen Pull Request erstellen.
- **Vor dem Erstellen des PRs einen unabhängigen Agenten prüfen lassen.** Ihm den fertigen Änderungsstand, Anforderungen und relevante Prüfergebnisse geben. Er prüft lesend mit frischem Kontext und verändert weder Dateien noch Git-Zustand.
- Die Rückmeldung selbst fachlich bewerten. Berechtigte Befunde beheben und betroffene Prüfungen wiederholen; abweichende Bewertungen mit Gründen festhalten. Nach wesentlichen Korrekturen den geänderten Stand erneut unabhängig prüfen lassen. Offene Probleme nicht durch das bloße Vorliegen eines Reviews als erledigt behandeln.
- Als Reviewer Lars (`larsernst`) und Peter (`peterzw98`) anfordern, soweit GitHub die jeweilige Person zulässt. Der PR-Autor kann nicht als eigener Reviewer eingetragen werden; in diesem Fall den anderen Reviewer anfordern, den Autor als Assignee eintragen und die Einschränkung nennen.
- Der PR beschreibt Problem, fertige Änderung, ausgeführte Prüfungen und bekannte Grenzen. Alle eingehenden Reviewbemerkungen bewerten, berechtigte Korrekturen bearbeiten und erledigte Threads passend auflösen. Keine externen Reviewbots ohne Auftrag auslösen.
- Das Erstellen eines PRs beauftragt keinen Merge. Nach einem Merge schließt GitHub den PR automatisch; er bleibt als Verlauf erhalten. Für den gemergten Arbeitsbranch die Repository-Einstellung `delete_branch_on_merge` verwenden und ihren Zustand prüfen, damit GitHub den Remote-Branch automatisch löscht.

## Dokumentation und Abschluss

- Bei geänderter Bedienung `README.md`, bei neuen Cosmos-Erkenntnissen `docs/COSMOS_SUPPORT.md` und bei erledigten Etappen `ROADMAP.md` passend aktualisieren.
- Roadmap-Aufgaben erst nach Erfüllung ihrer Abnahmekriterien abhaken. Geplante Arbeit, Codebefunde und tatsächliche Testergebnisse klar auseinanderhalten.
- Im Ergebnis beschreiben, was geändert wurde, welche Prüfungen ausgeführt wurden und welche Grenzen offenbleiben. Fehlende Werkzeuge oder fehlgeschlagene Prüfungen ausdrücklich nennen; Erfolg nicht aus älteren Protokollen übernehmen.
- Diese Datei auf dauerhafte Projektregeln beschränken. Laufende Aufgaben, wechselnde Testzahlen und aktuelle PR-Status gehören in die dafür vorgesehenen Arbeitsstände und Dokumente.
