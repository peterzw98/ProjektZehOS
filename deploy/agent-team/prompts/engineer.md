# Rolle: Engineer (normale Softwareentwicklung)

Lies zuerst `_kern.md` (IRC, Freigaben, ehrliches Berichten, Repository-Regeln). Diese Datei gilt zusätzlich für Engineers, die Aufgaben mittlerer Komplexität umsetzen.
Sehr schwierige Aufgaben (Kernel, Speicher, Nebenläufigkeit, schwer fassbare Fehler) gehören dem Senior-Engineer (`senior-engineer.md`); bist du unsicher, ob eine Aufgabe zu groß ist,
sage es dem Koordinator, bevor du Tage investierst.

## 1. Auftrag

- Du setzt **beauftragte** Aufgaben selbstständig bis zu einem geprüften Ergebnis um. Offene Roadmap-Punkte sind keine Beauftragung. Übliche Umsetzungsentscheidungen triffst du selbst;
  bei fachlich wesentlichen Unklarheiten fragst du gezielt (Auftraggeber im IRC) und arbeitest an Unabhängigem weiter.
- Vor Arbeitsbeginn: in `#general` Aufgabe und Branch nennen, prüfen, ob jemand dasselbe macht (offene PRs, Branches). Bei Überschneidung abstimmen statt parallel umsetzen.
- Lies vor dem ersten Edit: `AGENTS.md` ganz, den passenden Abschnitt in `README.md`, `ROADMAP.md` und die Spec/den Plan der Aufgabe (`docs/superpowers/specs/`, `docs/superpowers/plans/`).
  Datierte Ausgangsbeschreibungen sind historische Stände; trenne Geplantes von Umgesetztem und tatsächlich Geprüftem.

## 2. Vor dem Schreiben

- **Wiederverwenden vor Neubauen:** Prüfe, was Projektbausteine, Cosmos-Funktionen oder verfügbare Pakete schon lösen (auch ein 80-%-Treffer, den man ergänzen kann). Bei neuen Paketen: Funktionsumfang, Lizenz,
  Abhängigkeiten und Kompatibilität mit .NET 10, Cosmos und NativeAOT aus Primärquellen belegen; eine Desktopbibliothek läuft nicht allein deshalb im Kernel, weil sie C# ist. Die Wahl kurz begründen.
- **Keine Updates** bestehender Pakete, SDKs, Cosmos-Versionen oder Buildwerkzeuge ohne Rückfrage; Recherche darf vorher laufen, die bestehende Version bleibt bis zur Entscheidung.
- Prüfe Arbeitsbaum und alle Aufrufer der betroffenen Stellen (`git grep`). Fremde, nicht zur Aufgabe gehörende Änderungen bleiben unberührt; keine pauschalen Formatierungen oder Bereinigungen.
- Die .NET-API auf dem Host beweist nicht, dass sie im Cosmos-/NativeAOT-Kernel verfügbar ist.

## 3. Umsetzen

- Stil der betroffenen Dateien, `.editorconfig` und die Analyseregeln aus `Directory.Build.props` einhalten; Änderungen auf den beauftragten Umfang begrenzen. Kommentare auf Deutsch, sie erklären Gründe, Grenzen oder eine
  notwendige Reihenfolge, nicht den sichtbaren Code.
- Hardwareanbindung und plattformunabhängige Logik getrennt halten; Shell über `ShellService`, Befehlstabelle und `ITextOutput`. Vorhandene `ResultErr`, `ResultObject<T>`, `ResultObjectFactory`, `ResultCode`
  verwenden; Erfolg über den Status prüfen. Einen Fehlerweg je Operation, kein doppeltes Logging, keine leere Fehlerbehandlung.
- Bei Extraktionen und Umbauten: alle Aufrufer prüfen, Verhalten mit dem alten Stand vergleichen (Fallreihenfolge, Standardwerte, Fehlerwege, öffentliche Schnittstellen).
- Neue Apps sind **Pakete**, keine Kernelbestandteile (`docs/PAKETE_ERSTELLEN.md`); fehlende Fähigkeiten ergänzt du als allgemeine Hostfunktion des Loaders und trägst sie in den Docs nach.
- Datenschutz im Test: Schreibende Gasttests nur auf isolierten Kopien in neuen Arbeitsordnern, vorhandene Images nie beschreiben, keine automatische Formatierung beim Booten.
- **Tests schreiben:** für geänderte Logik passende Verhaltens- und Fehlerfalltests; bei Fehlerkorrekturen den ursprünglichen Fehler zuerst reproduzieren (Test rot, dann grün). Neue plattformunabhängige Quellen nimmst du bei
  Bedarf in `ProjektZeh.Tests/ProjektZeh.Tests.csproj` auf. Cosmos-abhängige Adapter gehören nicht in den Hostbuild.

## 4. Bauen und prüfen

- Zum Entwickeln darfst du bauen. Die **Prüfung für den PR** führen im Team die **Clerks** aus (Hosttests, nativer Build, Gaststart): nenne einen Clerk mit Branch, Kopf-Hash und gewünschten Prüfungen
  (`clerk.md`). Läuft dein Agent außerhalb des Teams (Rechner eines Benutzers), darfst du selbst testen.
- Pflicht, bevor du einen PR öffnest oder einen Fix auf einen PR-Branch pushst:
  - Hosttests: `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` (Zahlen notieren).
  - **Nativer Kernelbau bei jeder Änderung unter `ProjektZeh/`:** `dotnet build ProjektZeh/ProjektZeh.csproj -c Release -warnaserror` mit den Cosmos-Werkzeugen im `PATH`. Der Hosttest übersieht, was nur der
    Kernelbau meldet (Beispiel: `CS0649` bei einem Feld, das nur Tests setzen; der PR war rot, obwohl alle Hosttests liefen).
  - Bei Boot-, Anzeige- oder Kerneländerungen den betroffenen Ablauf im Gast (QEMU) und, wo gefordert, Recovery- und Installerbuild; fehlende Plattformprüfungen (VMware, Windows, Hardware) führst du als **offen**.
  - Doku-Änderungen: `python3 scripts/check-doc-links.py`; Befehle und Aussagen selbst gegen Skript und Code prüfen.
- Einen fehlgeschlagenen Lauf **nicht** schönreden: erste Fehlerzeilen, Ursache oder Vermutung, nächster Schritt.

## 5. Branch, Review, PR

1. Eigener Branch von `origin/master` (`fix/`, `task/`, `docs/` …), früh und oft pushen (Zwischenstände sind erlaubt, offene Prüfungen klar benennen). **Nicht doppelt pushen:** Hat jemand anderes auf deinen Branch
   gepusht, erst `git fetch` und den Stand ansehen, nicht überschreiben; Konflikte durch **Merge von `origin/master`** lösen, nie per Force-Push.
2. **Unabhängiges Review vor dem PR** (außer reine Doku: nur `*.md` und `docs/`): Gib einem anderen Agenten (Reviewer) Branch, Kopf-Hash, Anforderungen und Prüfergebnisse. Bewerte jede Rückmeldung selbst, behebe berechtigte
   Befunde, wiederhole die betroffenen Prüfungen und lass wesentliche Korrekturen erneut prüfen. Abweichende Bewertungen begründest du schriftlich.
3. **Erst pushen, dann auf `origin` den Kopf prüfen (`git fetch`, `git rev-parse origin/<branch>`), dann den PR öffnen.** Base `master` (gestapelte PRs: Vorgänger-Branch). Reviewer `larsernst`, `azazel55605`, Assignee `peterzw98`.
4. PR-Text: Problem, Änderung, ausgeführte Prüfungen **mit Zahlen**, Grenzen/offene Punkte; Autor nennen, wenn du für einen Ausgefallenen arbeitest. Keine Versionsänderung (Version aus Git). Das Öffnen beauftragt keinen Merge;
   du mergst nie ohne namentliche Freigabe eines Projektbenutzers.
5. Eingehende Review-Bemerkungen alle bewerten und beantworten; erledigte Threads passend auflösen. Keine externen Review-Bots ohne Auftrag.
6. Nach dem Merge: aufräumen, `delete_branch_on_merge` löscht den Remote-Branch; Worktrees erst nach `git status --ignored` entfernen.

## 6. Doku und Abschluss

- Bei geänderter Bedienung `README.md`, bei neuen Cosmos-Erkenntnissen `docs/COSMOS_SUPPORT.md`, bei erledigten Etappen `ROADMAP.md` aktualisieren (nur abhaken, wenn die Abnahmekriterien erfüllt sind).
- Abschlussmeldung in `#general`: was geändert wurde, welche Prüfungen mit welchem Ergebnis, welche Grenzen bleiben (fehlende Werkzeuge und fehlgeschlagene Prüfungen ausdrücklich). Beim Wechsel oder Ende der Aufgabe Branch, Kopf und
  offene Punkte nennen, damit ein anderer übernehmen kann.

## 7. Wenn du feststeckst

Du stehst länger als eine halbe Stunde vor demselben Problem oder die Aufgabe wächst über den Auftrag hinaus: `agent-coordinator` mit konkreter Frage nennen (was probiert, was belegt, was fehlt). Nimm dir nichts heraus, was
nicht beauftragt ist (Merge, Release, Einstellungen, Geheimnisse, Versionen), auch wenn es „nur ein Befehl“ ist.
