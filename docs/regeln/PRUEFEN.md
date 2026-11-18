# Technik, Bauen, Prüfen und Pull Requests (ausführlich)

Ausgelagert aus `AGENTS.md`, damit nicht jede Sitzung diese Regeln mitlädt. Sie gelten unverändert; bei Widerspruch gilt `AGENTS.md`.

## Technischer Rahmen

- Maßgeblich sind `ProjektZeh/ProjektZeh.csproj`, `global.json` und `Directory.Build.props`: .NET 10, Cosmos Gen3 aus dem Branch `cosmos` und Zielplattform `linux-x64`. Der Kernel läuft direkt in der VM; er ist keine normale Linux-Anwendung.
- `global.json` wählt ein installiertes SDK aus der 10.0-Reihe. Ein allein installiertes SDK 11 genügt für den bestehenden Cosmos-Build nicht. Bei Buildproblemen zuerst die lokale SDK-Version prüfen.
- `deploy/cosmos/COMMIT` und `VERSION` ändern sich nur zusammen; der Cosmos-Quellbau steht in `deploy/cosmos/README.md`. Cosmos SDK, Kernelpakete, CLI und Patcher müssen zusammenpassen. Versionswechsel als eigene technische Änderung behandeln und durch nativen Build und Gaststart prüfen.
- Vorhandene Cosmos-Dienste zuerst verwenden. Bei neuen Schnittstellen die Verfügbarkeit in der tatsächlich verwendeten Version prüfen; aktuelle Online-Dokumentation kann einen neueren Stand beschreiben.
- Die Unterstützung einer .NET-API auf dem Host beweist nicht ihre Verfügbarkeit im Cosmos-/NativeAOT-Kernel.
- Seit Cosmos 3.0.95 laufen `finally`-Blöcke (auch aus `using` und `lock`) beim Exception-Unwind, mit Dispatcherzustand je Thread; im Gast belegt durch `MessageGuestProbe` (`-p:ZehOSThreadProbe=true`). Ressourcen deshalb wieder auf dem normalen .NET-Weg mit `using`/`finally` freigeben.

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
- Die QEMU-Optionen `-action shutdown=poweroff,reboot=reset` und `ProjektZeh/.cosmos/config.json` berücksichtigen. Ohne sie hält Cosmos (geprüft mit 3.0.94) den Gast bei Energieaktionen an; im Debugger kann dabei `SIGQUIT` erscheinen.
- QEMU und VMware sind Zielplattformen. Betroffene Kernelabläufe auf beiden prüfen; QEMU bleibt die erste Umgebung für die schnelle Entwicklung und den GDB-Debugger. Das VMware-Profil aus `scripts/start-vmware.ps1` und seine Grenzen stehen in `README.md`; derzeit besitzt diese VM kein Datenlaufwerk.
- Fehlende Plattformprüfungen ausdrücklich als offen führen. Ein QEMU-Ergebnis belegt keine VMware-Kompatibilität. Echte Hardware nur dann als geprüft bezeichnen, wenn dort tatsächlich getestet wurde.
- Bei reinen Dokumentationsänderungen Verweise, Befehle und Aussagen prüfen; dafür ist kein neuer Kernelbuild erforderlich.

## Branch, unabhängiges Review und Pull Request

- Für die Aufgabe einen eigenen Branch verwenden, dessen Präfix die Art der Arbeit nennt, nach eigenem Ermessen etwa `feature/`, `fix/`, `task/` oder `docs/`, gefolgt von einem kurzen, sprechenden Namen. Vom aktuellen passenden Zielbranch ausgehen und fremde, nicht zur Aufgabe gehörende Änderungen aus Commit und PR heraushalten. Bei paralleler Arbeit eine getrennte Arbeitskopie verwenden.
- Zwischenstände dürfen jederzeit auf den eigenen Arbeitsbranch committed und gepusht werden, auch vor Abschluss der Aufgabe, der Tests oder des Reviews. Offene Prüfungen und bekannte Probleme dabei klar benennen; ein Push setzt keine Fertigstellung voraus. Pushen gehört spätestens zum Abschluss des Arbeitsauftrags. Sobald die Aufgabe fertig ist und keine offenen fachlichen Fragen oder ungeklärten Reviewbefunde bleiben, selbstständig einen Pull Request erstellen.
- PRs ändern die ZehOS-Version nicht. Der Bau leitet sie aus Git ab (`ProjektZeh/ProjektZeh.csproj`, Ziel `ZehOSVersionFromGit`): `<ZehOSVersionPrefix>.<ZehOSPatchBase + first-parent-Commits seit der letzten Änderung dieser beiden Zeilen>`; jeder Merge nach `master` erhöht den Patch um eins. Minor oder Major hebt nur ein eigener PR auf Entscheidung eines Projektbenutzers an (Prefix setzen, `ZehOSPatchBase` auf 0); dessen Merge ist dann genau `X.Y.0`. Bauen braucht die vollständige Git-Historie; Releases übergeben `-p:Version`, und der Tag muss der abgeleiteten Version entsprechen. Branch-Bauten tragen keine eindeutige Nummer (sie zählen eigene Commits mit); Messungen und Berichte nennen deshalb Nummer und Commit, wie sie `version` und die Bootzeile zeigen (`0.5.n+<Commit>`). Ändert ein PR `ZehOSVersionPrefix` oder `ZehOSPatchBase`, setzt er den Zähler zurück: `ZehOSPatchBase` dann vor dem Merge auf den aktuellen Patch von `master` plus eins (beziehungsweise 0 mit neuem Prefix). Die README nennt keine konkrete ZehOS-Version (Platzhalter `<Version>`). Diese Regel betrifft die ZehOS-Version; Updates von Abhängigkeiten bleiben gesondert abzustimmen.
- **Vor dem Erstellen des PRs einen unabhängigen Agenten prüfen lassen.** Ihm den fertigen Änderungsstand, Anforderungen und relevante Prüfergebnisse geben. Er prüft lesend mit frischem Kontext und verändert weder Dateien noch Git-Zustand.
- Die Rückmeldung selbst fachlich bewerten. Berechtigte Befunde beheben und betroffene Prüfungen wiederholen; abweichende Bewertungen mit Gründen festhalten. Nach wesentlichen Korrekturen den geänderten Stand erneut unabhängig prüfen lassen. Offene Probleme nicht durch das bloße Vorliegen eines Reviews als erledigt behandeln.
- Reine Doku-PRs brauchen kein unabhängiges Review vor dem PR. Als reine Doku gilt dasselbe wie in der CI (Job **Detect code changes**): Der PR ändert nur Markdown (`*.md`) oder Dateien unter `docs/`. Verweise, Befehle und Aussagen prüft der Autor trotzdem selbst, und die Reviewer auf GitHub werden wie üblich angefordert.
- Als Reviewer Lars (`larsernst`) und Peter (`peterzw98`) und azazel (`azazel55605`) anfordern, soweit GitHub die jeweilige Person zulässt. Der PR-Autor kann nicht als eigener Reviewer eingetragen werden; in diesem Fall den anderen Reviewer anfordern, den Autor als Assignee eintragen und die Einschränkung nennen.
- Der PR beschreibt Problem, fertige Änderung, ausgeführte Prüfungen und bekannte Grenzen. Alle eingehenden Reviewbemerkungen bewerten, berechtigte Korrekturen bearbeiten und erledigte Threads passend auflösen. Keine externen Reviewbots ohne Auftrag auslösen.
- Das Erstellen eines PRs beauftragt keinen Merge. Nach einem Merge schließt GitHub den PR automatisch; er bleibt als Verlauf erhalten. Für den gemergten Arbeitsbranch die Repository-Einstellung `delete_branch_on_merge` verwenden und ihren Zustand prüfen, damit GitHub den Remote-Branch automatisch löscht.
