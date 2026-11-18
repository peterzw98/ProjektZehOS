# Code: Architektur und Bausteine

Ausgelagert aus `AGENTS.md`, damit nicht jede Sitzung diese Regeln mitlädt. Sie gelten unverändert; bei Widerspruch gilt `AGENTS.md`.

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
