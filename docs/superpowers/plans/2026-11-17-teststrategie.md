# Teststrategie: Umsetzungsplan

> **Für ausführende Agenten:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (empfohlen) oder superpowers:executing-plans. Schritte verwenden Checkboxen (`- [ ]`).

**Ziel:** Tests, die Fehler tatsächlich finden: eine gemessene und in der CI erzwungene Abdeckung der Hostlogik, Ende-zu-Ende-Tests der Oberfläche auf dem Host mit Bildvergleich und ein echter Gaststart mit Bedienung in der CI.

**Architektur:** Drei Ebenen, jede als eigener Pull Request. Ebene A misst und schließt Lücken der Hosttests und entfernt Tests ohne Wert. Ebene B steuert `Desktop` auf dem Host mit Eingaben und vergleicht die gezeichneten Bilder mit Referenzbildern. Ebene C startet das in der CI gebaute ISO in QEMU und bedient es über QMP mit den vorhandenen Bausteinen aus `scripts/test-wasm-doom-desktop.py`.

**Technik:** MSTest und Moq (vorhanden), eingebauter Sammler `Code Coverage` aus `Microsoft.NET.Test.Sdk` (vorhanden, kein neues Paket), Python 3 ohne Zusatzpakete, QEMU über QMP, GitHub Actions.

**Spec:** Es gibt kein eigenes Designdokument. Der Auftrag lautete, selbst zu planen; die Entscheidungen stehen im Abschnitt „Ausgangslage und Entscheidungen".

## Ausgangslage und Entscheidungen

Gemessen am 17. November 2026 auf `master` (d5e5676), Windows:

| Messung | Ergebnis |
| --- | --- |
| Hosttests | 696 Tests, 0 Fehler, 1 übersprungen (Symlink-Recht), 1 min 38 s |
| Zeilenabdeckung der im Hostbuild enthaltenen Produktivquellen | 7022 von 7771 Zeilen, 90,4 % |
| Python-Skripttests | 99 Tests, 0 Fehler, 1 übersprungen, 584 s |
| Kernelcode ohne automatischen Test | rund 4.250 Zeilen `Cosmos*.cs` sowie `Kernel.cs`, `InteractiveSession.cs`, `InstallerSession.cs`, `WasmRuntime.cs`, `DisplayService.cs`, `SerialLogSink.cs` |
| Gaststart in der CI | keiner; `ci.yml` baut das ISO, startet es aber nicht |

Dateien im Hostbuild mit der schwächsten Abdeckung: `Shell/TaskShellCommands.cs` 0 %, `Shell/MessageShellCommands.cs` 0 %, `Network/TlsRoots.cs` 0 %, `Runtime/ConsoleLineView.cs` 3 %, `Shell/SetupShellCommands.cs` 18 %, `Shell/UpdateShellCommands.cs` 19 %, `Apps/TaskManager/TaskManagerApp.cs` 60 %, `Network/TlsTransport.cs` 71 %, `Shell/AccountShellCommands.cs` 77 %, `Apps/Terminal/TerminalWorkspaceApp.cs` 77 %.

Entscheidungen:

- **Abdeckung messen ohne neues Paket.** `dotnet test --collect "Code Coverage;Format=cobertura"` funktioniert mit dem vorhandenen Test-SDK (unter Windows geprüft). coverlet wäre ein neues Paket und braucht nach `AGENTS.md` eine Rückfrage.
- **Kein Mutationstest-Werkzeug.** Stryker.NET erwartet ein eigenes Quellprojekt; hier bindet das Testprojekt die Quellen direkt ein. Stattdessen eine Stichprobe von Hand (Aufgabe A4).
- **Bildvergleich über Prüfsumme der Pixel**, nicht über PNG-Bytes: die zlib-Ausgabe kann sich zwischen Plattformen unterscheiden, die Pixel nicht. Das PNG liegt nur zum Ansehen daneben.
- **Gasttest prüft über serielle Marken und Bildausschnitte**, ohne neue Kernelmarken. Der Inhalt der Fenster wird auf Ebene B geprüft, der Gast prüft den Weg Eingabe → Treiber → Bildpuffer.
- **Vorhandene Gastskripte für Pakete, Netzwerk und Storage bleiben außerhalb dieses Plans.** Sie brauchen Test-ISOs mit Testschlüssel, ein Doom-Repository beziehungsweise ein Recovery-ISO. Ob und wie sie nachts in der CI laufen, wird nach Ebene C mit den dort gemessenen Laufzeiten gesondert geplant.

## Globale Vorgaben

- Regeln aus `AGENTS.md` gelten für jede Aufgabe: deutsche Kommentare, Stil der betroffenen Datei, keine Paket- oder SDK-Updates ohne Rückfrage.
- Jede Ebene (A, B, C) ist ein eigener Branch und Pull Request mit erhöhter ZehOS-Version in `ProjektZeh/ProjektZeh.csproj`, unabhängigem Review und Ankündigung im IRC-Kanal `#general`.
- Hosttests laufen mit `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` und bleiben vollständig grün.
- Für Tests keine öffentliche API schaffen; interne optionale Parameter sind zulässig.
- Gasttests verwenden immer eine Wegwerfplatte im Arbeitsordner unter `.tools/`; `.tools/qemu/data-fat32.img` wird nie gelesen oder geschrieben.
- Ein Test wird nur gelöscht, wenn ein Kriterium aus Aufgabe A4 zutrifft. Jede Löschung steht mit Grund in der PR-Beschreibung.
- Ein QEMU-Ergebnis belegt keine VMware-Kompatibilität; VMware bleibt in jedem PR als offen genannt.

## Review-Schwerpunkte

1. **Sammler unter Linux:** Der Abdeckungssammler ist nur unter Windows geprüft. Liefert er auf `ubuntu-24.04` keine Cobertura-Datei, muss der CI-Schritt scheitern statt mit 0 Zeilen durchzulaufen (Test in A1: leere oder fehlende Datei → Exitcode 2).
2. **Uhr im Referenzbild:** `Desktop.cs` liest `DateTime.UtcNow` für die Uhr in der Leiste. Ohne feste Zeit schlägt jedes Referenzbild nach einer Minute fehl (Test in B1: zwei Renderläufe mit verschiedener Systemzeit ergeben dieselbe Prüfsumme).
3. **Fehlende Schrift:** `ScreenshotSupport.LoadCosmosFont()` liefert `null`, wenn das Cosmos-Paket nicht im NuGet-Cache liegt. Mit Leerschrift wären alle Referenzbilder ohne Text und wertlos (Test in B1: in der CI ist `null` ein Fehler, kein Überspringen).
4. **Verlorene Tasten im Gast:** Unter Last gehen in QEMU getippte Zeichen verloren (siehe Kommentare in `Guest.shell` und `Guest.dhcp`). Der Gasttest wartet deshalb auf Marken und wiederholt nur die Eingabe, nie den ganzen Test (C1).
5. **Hängender Gast:** Bleibt der Gast vor der Bootmarke stehen, muss der Test nach der Zeitgrenze QEMU beenden, Bildschirmfoto und serielles Protokoll sichern und scheitern (Test in C1 mit einem Prozess, der nie eine Marke schreibt).

---

## Ebene A: Hosttests messen, Lücken schließen, Wertloses entfernen

Branch `task/tests-abdeckung`.

### Aufgabe A1: Abdeckung auswerten und in der CI erzwingen

**Dateien:**
- Neu: `scripts/check-coverage.py`
- Neu: `scripts/tests/test_check_coverage.py`
- Neu: `scripts/coverage-exclusions.txt`
- Ändern: `.github/workflows/ci.yml` (Job `tests`)

**Schnittstellen:**
- Erzeugt: `python3 scripts/check-coverage.py <cobertura.xml> --min-total <Prozent> --min-file <Prozent> --exclusions scripts/coverage-exclusions.txt [--summary <Datei>]`. Exitcode 0 bei Erfolg, 1 bei unterschrittener Grenze, 2 bei fehlender, leerer oder nicht lesbarer Eingabe.
- `coverage-exclusions.txt`: eine Zeile je Datei, `Pfad relativ zu ProjektZeh/ # Grund`. Ausgenommene Dateien zählen weder in die Gesamtzahl noch in die Dateigrenze.

- [ ] **Schritt 1: Fehlschlagende Tests schreiben** (`scripts/tests/test_check_coverage.py`, `unittest`, Cobertura-XML als Zeichenkette im Test):
  - `test_line_hit_in_any_class_counts_once`: dieselbe Datei in zwei `<class>`-Elementen, Zeile 5 einmal mit `hits="0"` und einmal mit `hits="3"` → Datei 1 von 1 Zeilen.
  - `test_only_product_sources_count`: Einträge unter `ProjektZeh.Tests/` und außerhalb von `ProjektZeh/` werden ignoriert.
  - `test_total_below_minimum_fails`: 8 von 10 Zeilen bei `--min-total 90` → Exitcode 1, Ausgabe nennt `80.0`.
  - `test_file_below_minimum_fails_and_is_named`: Datei mit 1 von 10 Zeilen bei `--min-file 50` → Exitcode 1, Ausgabe nennt den Pfad.
  - `test_excluded_file_is_ignored`: dieselbe Datei in der Ausnahmeliste → Exitcode 0.
  - `test_exclusion_without_reason_is_rejected`: Zeile ohne `# Grund` → Exitcode 2.
  - `test_missing_or_empty_report_fails`: fehlende Datei und XML ohne `<class>` → jeweils Exitcode 2.
  - `test_summary_lists_worst_files`: `--summary` schreibt eine Markdown-Tabelle mit den zehn Dateien mit den meisten offenen Zeilen.
- [ ] **Schritt 2:** `python -m unittest scripts/tests/test_check_coverage.py -v` → alle Tests scheitern (Modul fehlt).
- [ ] **Schritt 3: `scripts/check-coverage.py` schreiben.** Nur Standardbibliothek (`xml.etree.ElementTree`, `argparse`). Pfade vor dem Vergleich auf `/` normalisieren; der Teil nach dem letzten `/ProjektZeh/` ist der relative Pfad.
- [ ] **Schritt 4:** Tests laufen grün.
- [ ] **Schritt 5: `coverage-exclusions.txt` anlegen** mit genau einem Eintrag: `Runtime/ConsoleLineView.cs # Draw schreibt auf die Kernelkonsole; geprüft im Gasttest der Textshell (Ebene C)`.
- [ ] **Schritt 6: CI ändern.** Im Job `tests` den Schritt „Run shell tests" um `--collect "Code Coverage;Format=cobertura"` ergänzen und danach einen Schritt „Check coverage" einfügen:
  `python3 scripts/check-coverage.py "$(find TestResults -name '*.cobertura.xml' | head -n 1)" --min-total 90 --min-file 0 --exclusions scripts/coverage-exclusions.txt --summary "$GITHUB_STEP_SUMMARY"`.
  Die Cobertura-Datei zusätzlich im vorhandenen Artefakt `shell-test-results` hochladen. Jobzeitgrenze auf 15 Minuten.
- [ ] **Schritt 7:** Lokal `dotnet test ... --collect "Code Coverage;Format=cobertura" --results-directory TestResults` und danach das Skript ausführen → Exitcode 0, Gesamtwert 90,4 % (± Rundung). Branch pushen und prüfen, dass der Schritt auf `ubuntu-24.04` eine Datei findet. Findet er keine: Aufgabe anhalten und den Benutzer fragen, ob `coverlet.collector` als neues Paket zulässig ist.
- [ ] **Schritt 8: Commit.**

### Aufgabe A2: Shellbefehle ohne Tests abdecken

**Dateien:**
- Neu: `ProjektZeh.Tests/TaskShellCommandsTests.cs`, `ProjektZeh.Tests/MessageShellCommandsTests.cs`
- Ändern: `ProjektZeh.Tests/UpdateCommandTests.cs`, `ProjektZeh.Tests/UserManagementTests.cs` (für `AccountShellCommands`), vorhandene Testdatei zu `SetupShellCommands` (falls keine besteht: neu `ProjektZeh.Tests/SetupShellCommandsTests.cs`)

Muster für alle Befehlstests: Befehl über `Commands[0]` holen, mit Argumenten und einem `ITextOutput`-Sammler aufrufen, `ResultCode` **und** die ausgegebenen Zeilen prüfen. `BackgroundTaskService` wird wie in `MessageDemoTests.cs` mit `new BackgroundTaskService()` gebaut.

- [ ] **Schritt 1: `TaskShellCommandsTests` schreiben** (Konstruktor `new TaskShellCommands(tasks, () => ["rr quantum=1"])`):
  - `List_WithoutArgumentsAndWithList_PrintsSameSnapshot`: `[]` und `["list"]` liefern Erfolg und dieselben Zeilen.
  - `Scheduler_PrintsEntriesFromCallback`: `["scheduler"]` → Ausgabe genau `rr quantum=1`.
  - `Start_BusyAndWait_ReportStartedId`: `["start", "busy"]` → Erfolg, Zeile beginnt mit `Aufgabe ` und endet mit ` gestartet.`; danach zeigt `list` eine Zeile mit `busy`.
  - `Cancel_UnknownId_ReturnsServiceFailure`: `["cancel", "999"]` → kein Erfolg, Code wie `tasks.Cancel(999)`.
  - `Cancel_NonNumericId_AndUnknownVerb_ReturnUsage`: `["cancel", "x"]`, `["start", "other"]`, `["bogus"]` → `ResultCode.InvalidArgument`, Meldung beginnt mit `Verwendung: tasks`.
- [ ] **Schritt 2: `MessageShellCommandsTests` schreiben** (`new MessageShellCommands(new MessageDemoService(tasks))`):
  - `Start_UnknownMode_ReturnsInvalidArgumentWithoutOutput`.
  - `Start_SlowProducer_PrintsDescription`: Zeile beginnt mit `24 nummerierte Nachrichten, Puffer 3.`.
  - `Status_PrintsDemoStatus`: Ausgabe gleich `demo.Status()`.
  - `WrongArgumentCount_ReturnsInvalidArgument`: `[]` und `["start"]`.
  - Je ein Test für `close` und `stop` mit dem Ergebnis, das `MessageDemoService` in `MessageDemoTests.cs` für denselben Zustand liefert.
- [ ] **Schritt 3: Für `SetupShellCommands`, `UpdateShellCommands` und `AccountShellCommands`** je Zweig von `Execute` einen Test nach demselben Muster ergänzen. Fertig ist der Schritt, wenn `check-coverage.py` für jede der drei Dateien mindestens 85 % meldet.
- [ ] **Schritt 4: `TlsRoots`**: Test `Read_MissingResource_ThrowsWithResourceName` in `ProjektZeh.Tests/TlsTransportTests.cs`: `TlsRoots.Read("fehlt")` wirft `InvalidOperationException`, Meldung enthält `fehlt`.
- [ ] **Schritt 5:** Hosttests grün; `check-coverage.py` meldet für `TaskShellCommands.cs` und `MessageShellCommands.cs` mindestens 90 %.
- [ ] **Schritt 6: Commit.**

### Aufgabe A3: Grenzen anheben

**Dateien:** `.github/workflows/ci.yml`

- [ ] **Schritt 1:** Nach A2 den gemessenen Gesamtwert ablesen und `--min-total` auf den abgerundeten ganzen Prozentwert setzen, `--min-file` auf 50.
- [ ] **Schritt 2:** Jede Datei unter 50 % entweder mit Tests über die Grenze bringen oder mit Grund in `coverage-exclusions.txt` aufnehmen. Zulässiger Grund ist nur: Code schreibt direkt auf Konsole oder Hardware und wird in Ebene C geprüft.
- [ ] **Schritt 3:** Hosttests und Abdeckungsprüfung grün. Commit.

### Aufgabe A4: Tests ohne Wert finden und löschen

**Dateien:** `ProjektZeh.Tests/*Tests.cs`, `scripts/tests/*.py`

Ein Test wird gelöscht oder mit einem anderen zusammengelegt, wenn eines zutrifft:

1. Er bleibt grün, wenn die geprüfte Bedingung im Produktivcode umgekehrt wird (Stichprobe unten).
2. Er prüft nur Sprach- oder Frameworkverhalten (Standardwert eines Feldes, Getter gibt Konstruktorwert zurück, Moq liefert, was eingestellt wurde).
3. Er wiederholt Eingaben und Prüfungen eines anderen Tests derselben Klasse.

Tests, die einen behobenen Fehler nachstellen (erkennbar am Namen oder Kommentar), bleiben, auch wenn sie klein sind.

- [ ] **Schritt 1: Stichprobe von Hand** für die 25 Produktivdateien mit den meisten Zeilen im Hostbuild: in der zentralen Methode eine Bedingung umkehren, `dotnet test --filter "FullyQualifiedName~<Testklasse>"` ausführen, Änderung zurücknehmen. Erwartet: mindestens ein Test scheitert. Scheitert keiner, die Testklasse verstärken (Prüfung auf das Ergebnis statt auf den Aufruf) oder nach Kriterium 1 löschen. Ergebnis je Datei in einer Tabelle für die PR-Beschreibung festhalten: `Datei | umgekehrte Bedingung | gescheiterte Tests`.
- [ ] **Schritt 2: Durchsicht aller Testdateien** nach den Kriterien 2 und 3. Tabelle für die PR-Beschreibung: `Test | Kriterium | gelöscht oder zusammengelegt mit`.
- [ ] **Schritt 3: Langsame Python-Tests finden:** `python -m unittest discover -s scripts/tests -v` unter Windows braucht 584 s. Die Tests mit mehr als 10 s nennen (Dauer je Test messen) und echte Wartezeiten durch die vorhandenen injizierbaren Zeit- oder Prozessattrappen ersetzen. Ziel: unter 60 s.
- [ ] **Schritt 4:** Hosttests, Python-Tests und Abdeckungsprüfung grün; `git status` zeigt keine Änderung an Produktivcode aus Schritt 1. Commit.
- [ ] **Schritt 5: PR für Ebene A** nach `AGENTS.md` (Version erhöhen, unabhängiges Review, Reviewer anfordern).

---

## Ebene B: Oberfläche Ende zu Ende auf dem Host

Branch `feature/tests-desktop-e2e`.

### Aufgabe B1: Referenzbilder und feste Uhr

**Dateien:**
- Neu: `ProjektZeh.Tests/Golden.cs`, `ProjektZeh.Tests/GoldenTests.cs`, Ordner `ProjektZeh.Tests/Golden/`
- Ändern: `ProjektZeh/Graphics/Desktop.cs` (Uhr), `ProjektZeh.Tests/ProjektZeh.Tests.csproj`, `ProjektZeh.Tests/Ext2FormatterUsageTests.cs` (gemeinsame Stammsuche), `README.md` (Abschnitt Tests)

**Schnittstellen:**
- Erzeugt: `internal static class Golden` mit
  - `static UiPainter Painter()` — echte Cosmos-Schrift aus `ScreenshotSupport.LoadCosmosFont()`. Bei `null`: `Assert.Fail`, wenn die Umgebungsvariable `CI` gesetzt ist, sonst `Assert.Inconclusive`.
  - `static void Check(string name, int width, int height, int[] pixels)` — SHA-256 über die Pixel als Little-Endian-Bytes, Vergleich mit `ProjektZeh.Tests/Golden/<name>.sha256`. Bei `ZEHOS_UPDATE_GOLDEN=1` schreibt sie `<name>.sha256` und `<name>.png` in diesen Ordner. Bei Abweichung schreibt sie `<name>.actual.png` nach `TestResults/golden/` und scheitert mit beiden Pfaden in der Meldung.
  - `static string RepositoryRoot()` — die Suche aus `Ext2FormatterUsageTests.FindRepositoryRoot`, dorthin verschoben; `Ext2FormatterUsageTests` ruft sie auf.
- Erzeugt: `Desktop`-Konstruktor erhält am Ende den optionalen Parameter `Func<DateTime>? getUtcNow = null`; die Uhr in der Leiste verwendet ihn statt `DateTime.UtcNow` (Standard bleibt `DateTime.UtcNow`).
- `ProjektZeh.Tests.csproj`: `<PackageDownload Include="Cosmos.Kernel.System" Version="[3.0.97]" />`, damit die Schrift auch ohne Kernelbuild im NuGet-Cache liegt. Das ist dieselbe Version wie im Kernel und keine neue Abhängigkeit des Builds.

- [ ] **Schritt 1: Fehlschlagende Tests in `GoldenTests.cs`:**
  - `Check_SamePixels_Passes` und `Check_OnePixelChanged_FailsAndWritesActualPng` (eigener Name `selftest`, Referenz im Test mit `ZEHOS_UPDATE_GOLDEN` erzeugt, Datei danach entfernt).
  - `Check_MissingReference_FailsWithHintToUpdateVariable`: Meldung enthält `ZEHOS_UPDATE_GOLDEN`.
  - `DesktopClock_UsesInjectedTime`: `Desktop` mit `getUtcNow: () => new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)` zweimal im Abstand von 61 s Systemzeit gezeichnet → identische Prüfsumme; mit zweiter fester Zeit `03:05` → andere Prüfsumme.
- [ ] **Schritt 2:** Tests ausführen → scheitern (Typ `Golden` und Parameter fehlen).
- [ ] **Schritt 3:** `Golden`, den Parameter in `Desktop` und den `PackageDownload` umsetzen.
- [ ] **Schritt 4:** Tests grün; alle bisherigen Hosttests grün.
- [ ] **Schritt 5: CI:** Im Job `tests` den Ordner `TestResults/golden/` bei Fehlschlag als Artefakt `golden-actual` hochladen. In `README.md` beschreiben: Referenzbilder aktualisieren mit `ZEHOS_UPDATE_GOLDEN=1`, geänderte PNGs im PR ansehen.
- [ ] **Schritt 6: Commit.**

### Aufgabe B2: Bedienabläufe als Tests

**Dateien:**
- Neu: `ProjektZeh.Tests/DesktopScenario.cs`, `ProjektZeh.Tests/DesktopE2ETests.cs`, Referenzbilder unter `ProjektZeh.Tests/Golden/`

**Schnittstellen:**
- Verwendet: `Golden.Painter()`, `Golden.Check(...)`, `Desktop(..., getUtcNow)` aus B1.
- Erzeugt: `internal sealed class DesktopScenario : IDisposable` mit eigenem Temp-Wurzelordner und festem Takt:
  - `DesktopScenario(int width = 1024, int height = 768, bool withLogin = false)` — baut `Desktop` mit `FileService` auf dem Temp-Ordner, fester Uhr `2026-01-02 03:04:05 UTC` und bei `withLogin` mit `DesktopSession` wie in `UserManagementTests.cs`.
  - `Desktop Desktop { get; }`, `string Root { get; }`
  - `void Key(PhysicalKey key)`, `void Chord(PhysicalKey modifier, PhysicalKey key)`, `void Text(string text)`, `void Click(int x, int y)`, `void Drag(int x, int y, int toX, int toY)`
  - `void Frame(long milliseconds = 16)` — ruft `Update` und `Render` mit fortlaufender Zeit auf.
  - `void Check(string name)` — zeichnet ein Bild und ruft `Golden.Check`.

Jeder Test prüft das fachliche Ergebnis **und** ein Referenzbild. Die Eingaben laufen ausschließlich über `Desktop.Handle`, nicht über `OpenTerminal()` und ähnliche Abkürzungen.

- [ ] **Schritt 1: Tests in `DesktopE2ETests.cs` schreiben:**
  - `Login_WrongThenRightPassword_ShowsDesktop`: falsches Kennwort → kein Fenster bedienbar, Bild `login-fehler`; richtiges Kennwort → Startmenü lässt sich öffnen, Bild `desktop-leer`.
  - `StartMenu_SearchAndEnter_OpensTerminalAndRunsEcho`: Alt+F1, `term`, Enter → aktives Fenster ist `TerminalWorkspaceApp`; `echo hallo` + Enter → Terminalpuffer enthält die Zeile `hallo`; Bild `terminal-echo`.
  - `Terminal_WriteFile_AppearsInFileManagerAndOpensInEditor`: im Terminal eine Datei `notiz.txt` mit Inhalt `eins` anlegen; Dateien-App über das Startmenü öffnen → Liste enthält `notiz.txt`; Enter öffnet den Texteditor mit `eins`; `zwei` tippen und speichern → Datei auf der Platte enthält beide Texte; Bild `editor-notiz`.
  - `Settings_SwitchTheme_PersistsAndRepaints`: Einstellungen öffnen, Design umschalten → `zehos-desktop.conf` enthält `dark=False`; Bilder `einstellungen-hell` und `einstellungen-dunkel` unterscheiden sich.
  - `Window_DragCloseAndLimit`: Fenster an der Titelleiste ziehen → `X`/`Y` geändert; Klick auf das Schließfeld → `Windows.Count` sinkt; neun Starts → `Windows.Count == Desktop.MaximumWindows`.
  - `Workspaces_SwitchAndMove_KeepWindowsSeparate`: Fenster auf Arbeitsfläche 2 verschieben → `CountOn(0)` und `CountOn(1)` passen; Bild `arbeitsflaeche-2`.
  - `TaskManager_EndSelectedWindow_ClosesIt`: Taskmanager öffnen, Terminal in der Liste wählen und beenden → Terminalfenster ist weg, Taskmanager bleibt.
  - `EscapeAndSuspend_KeepTerminalState`: Text im Terminal, `Desktop.Suspend()` und erneutes Zeichnen → Terminalpuffer und Arbeitsverzeichnis unverändert.
  - `Layout_AtSupportedResolutions` (`DataRow` 800×600, 1024×768, 1920×1080): Startmenü offen, Bilder `menue-<Breite>x<Höhe>`.
- [ ] **Schritt 2:** Tests ausführen → scheitern an fehlenden Referenzbildern (die fachlichen Prüfungen müssen bereits grün sein; scheitert eine davon, ist das ein Fehlerbericht und kein Anlass, die Prüfung abzuschwächen).
- [ ] **Schritt 3:** Referenzbilder mit `ZEHOS_UPDATE_GOLDEN=1` erzeugen und **jedes PNG ansehen**: Text lesbar, kein leeres Fenster, Uhr zeigt `04:04` (MEZ) beziehungsweise die Ortszeit laut `LocalTime`.
- [ ] **Schritt 4:** Tests ohne die Variable grün, zweimal hintereinander; unter Linux (CI) dieselben Prüfsummen. Weichen sie dort ab, Ursache suchen (Schrift oder Reihenfolge), nicht je Plattform eigene Bilder anlegen.
- [ ] **Schritt 5:** `check-coverage.py` meldet für `TaskManagerApp.cs` und `TerminalWorkspaceApp.cs` mindestens 85 %. Commit und PR für Ebene B.

---

## Ebene C: Gaststart mit Bedienung in der CI

Branch `feature/tests-gast-ci`.

### Aufgabe C1: Gasttest für Boot, Anmeldung, Desktop und Textshell

**Dateien:**
- Neu: `scripts/test-desktop-guest.py`, `scripts/tests/test_desktop_guest.py`
- Ändern: `README.md` (Aufruf), `docs/COSMOS_SUPPORT.md` (nur bei neuen Erkenntnissen)

**Schnittstellen:**
- Verwendet aus `scripts/test-wasm-doom-desktop.py`, geladen über `importlib` wie in `scripts/test-classicube-desktop.py`: `create_disk(directory, image)`, `free_port()`, `start_qemu(iso, disk, work, port, accel, None)`, `Guest(work, port, "", started)` mit `wait`, `mark`, `login`, `shell`, `desktop`, `launch`, `shot` sowie `Qmp.tap`, `Qmp.line`, `Qmp.click`, `Qmp.command`.
- Erzeugt: `python3 scripts/test-desktop-guest.py --iso <ISO> [--accel tcg|kvm|whpx] [--recovery] [--boot-timeout 300]`. Arbeitsordner `.tools/desktop-guest/<zufällig>/` mit `serial.log`, `stderr.log` und nummerierten PNGs. Exitcode 0 nur, wenn alle Schritte bestanden sind.
- Erzeugt: `region(ppm: bytes, box: tuple[int, int, int, int]) -> bytes` (Ausschnitt aus einem P6-Bild) und `error_lines(serial: str) -> list[str]` (Zeilen der Form `[<n> ms] Error <Komponente>: ...` nach `SystemLogEntry.ToString()`).

Ablauf des Skripts (normales ISO), jeder Schritt mit Zeitgrenze und Bildschirmfoto bei Fehlschlag:

1. Leere Wegwerfplatte anlegen, QEMU starten, auf `Desktop ist bereit` warten.
2. `guest.login(first=True)` → `Benutzer angemeldet.`
3. Ausschnitt der Arbeitsfläche ohne Leiste merken. `guest.launch("Terminal")` → Ausschnitt hat sich geändert.
4. `echo e2e` + Enter → Ausschnitt hat sich erneut geändert.
5. Fenster schließen (Bedienung laut `README.md`) → Ausschnitt gleich dem aus Schritt 3.
6. `guest.launch("Dateien")`, `guest.launch("Einstellungen")` → jeweils Änderung gegenüber dem Stand davor.
7. Esc → `Textshell aktiv.`; `logout` → `Benutzer abgemeldet.`; Anmeldung mit `first=False` → `Benutzer angemeldet.`; F4 → `Desktop ist bereit.`
8. Esc, `shutdown` → der QEMU-Prozess endet binnen 60 s mit Exitcode 0.
9. `error_lines(serial.log)` ist leer.

Mit `--recovery`: auf `Textshell ist bereit.` warten, prüfen, dass `Desktop ist bereit` **nicht** erscheint, `uname` tippen, `shutdown` wie in Schritt 8.

- [ ] **Schritt 1: Fehlschlagende Tests in `scripts/tests/test_desktop_guest.py`:**
  - `test_region_crops_rows_and_columns`: 4×3-Bild, Kasten `(1, 1, 3, 3)` → genau die erwarteten 12 Bytes.
  - `test_region_rejects_box_outside_image`: `ValueError`.
  - `test_error_lines_finds_only_error_level`: Protokoll mit `Information`-, `Warning`- und `Error`-Zeile → nur die `Error`-Zeile.
  - `test_error_lines_ignores_split_debug_lines`: `[LowLevelMonitor] ...`-Zeilen zählen nicht.
  - `test_boot_timeout_stops_process_and_keeps_log`: Ablauf mit einem Attrappenprozess, der nie eine Marke schreibt → `TimeoutError` nach der Zeitgrenze, Prozess beendet, `serial.log` bleibt liegen.
- [ ] **Schritt 2:** Tests ausführen → scheitern (Modul fehlt).
- [ ] **Schritt 3:** Skript nach obigem Ablauf schreiben. Wartezeiten nur als Zeitgrenzen für Marken; feste Pausen höchstens 1 s nach einer Marke, wie in `Guest`.
- [ ] **Schritt 4:** Python-Tests grün.
- [ ] **Schritt 5: Lokal gegen ein frisches ISO** (`cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release`): `python scripts/test-desktop-guest.py --iso ProjektZeh/output-x64/ProjektZeh.iso` → Exitcode 0, dreimal hintereinander. Die PNGs ansehen. Laufzeit notieren.
- [ ] **Schritt 6: Gegenprobe:** einmal mit absichtlich falscher Marke (`Desktop ist bereitX`) starten → Skript scheitert mit Zeitüberschreitung und hinterlässt Bildschirmfoto und Protokoll.
- [ ] **Schritt 7:** Recovery-ISO bauen (`dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSRecovery=true`) und mit `--recovery` prüfen.
- [ ] **Schritt 8: Commit.**

### Aufgabe C2: CI-Job für den Gasttest

**Dateien:** `.github/workflows/ci.yml`

- [ ] **Schritt 1: Job `guest` ergänzen** (`runs-on: ubuntu-24.04`, `needs: kernel`, `timeout-minutes: 20`, `permissions: contents: read`):
  1. Checkout wie in den anderen Jobs (gleiche festgelegte Action-Versionen).
  2. Artefakt `ZehOS-x64` mit `actions/download-artifact` (auf einen Commit festgelegt wie die übrigen Actions) laden.
  3. `sudo apt-get update && sudo apt-get install --no-install-recommends -y qemu-system-x86 qemu-utils`.
  4. `python3 scripts/test-desktop-guest.py --iso ProjektZeh.iso --accel tcg`.
  5. Immer: `.tools/desktop-guest/` als Artefakt `guest-desktop` hochladen (14 Tage).
- [ ] **Schritt 2:** Branch pushen, Job dreimal laufen lassen (`workflow_dispatch`). Alle drei grün; Laufzeit in die PR-Beschreibung. Scheitert der Job nur wegen der QEMU-Version des Runners (Ubuntu 24.04 liefert 8.2, lokal läuft 10.2.2), den Befund in `docs/COSMOS_SUPPORT.md` festhalten und QEMU stattdessen über `cosmos install --auto --tools` beziehen, wie im Job `kernel`.
- [ ] **Schritt 3:** Recovery nicht in jeden PR-Lauf aufnehmen; als zweiter Aufruf nur bei `workflow_dispatch` und auf `master`.
- [ ] **Schritt 4:** `README.md` um den Gasttest ergänzen (Aufruf, Artefakte, Grenzen: nur QEMU/TCG, VMware offen). Commit und PR für Ebene C.

---

## Nicht Teil dieses Plans

- Nächtliche CI-Läufe für `test-wasm-doom-desktop.py`, `test-network.py`, `test-storage-data-disk.py` und `test-storage-ext2-guest.py` (eigener Plan nach Ebene C).
- Automatische Tests unter VMware und auf echter Hardware.
- KVM-Beschleunigung auf den GitHub-Runnern; erst sinnvoll, wenn die TCG-Laufzeit aus C2 stört.
