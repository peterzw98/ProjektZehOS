# Cosmos-Update einspielen

Ablauf für einen Agenten von „neue Cosmos-Version da“ bis zum Pin-PR. Ergänzt `docs/regeln/COSMOS_PATCHES.md` (Regeln für den Branch `cosmos`) und `deploy/cosmos/README.md` (Quellbau, Pin-Dateien). Bei Widerspruch gilt `AGENTS.md`. Das Skript `scripts/cosmos-update.py` übernimmt nur die Handarbeit rund um den Pin; Merge und Rebase bleiben Git.

## Regeln

- Ein Wechsel der Cosmos-Basisversion (etwa 3.0.97 auf 3.0.99) ist ein Update und braucht vorher die Zustimmung eines Projektbenutzers (Terminal oder gültig signierte Discord-Zeile). Recherche und `check` sind vorher erlaubt; die bestehende Version bleibt bis dahin unverändert.
- Der Merge beider PRs (`cosmos` und ZehOS) braucht ebenfalls die Zustimmung eines Projektbenutzers.
- Nie Force-Push; nie Rebase eines bereits gepushten Branches. Stände zusammenführen (Merge), nicht umschreiben.
- Keine Issues, Pull Requests oder Kommentare bei `CosmosOS/Cosmos` oder anderen fremden Projekten.
- Update als eigene technische Änderung: keine Funktionsänderung am ZehOS-Code im Pin-PR, außer sie ist für den Bau nötig.

## Schritte

1. **Ankündigen und freigeben lassen.** In `#general` Aufgabe und Branch melden, auf Überschneidung prüfen. Zustimmung für die Zielversion einholen (siehe oben); ohne sie hier abbrechen.
2. **Lage prüfen** (nur lesend):
   ```bash
   git fetch origin
   python scripts/cosmos-update.py verify
   python scripts/cosmos-update.py check --to v3.0.99
   ```
   `check` nennt Pin, neueste NuGet-Version von `Cosmos.Kernel` und die Upstream-Dateien, die zwischen Grundstand (`PATCHES.md` im Branch `cosmos`) und `--to` geändert wurden, je Patch aus `patches/*.md`. Ohne `--to` zeigt es nur die Versionen. Die Vergleichs-API liefert höchstens 300 Dateien (das Skript meldet Abschneiden); die Dateinamen in `patches/*.md` sind teils unvollständig, ein Patch ohne Treffer ist deshalb nicht belegt unberührt. Die Upstream-Commit-Liste (`gh api repos/CosmosOS/Cosmos/compare/<alt>...<neu>`) selbst durchgehen und für jeden dokumentierten Umweg und jede Grenze in `docs/COSMOS_SUPPORT.md` prüfen, ob sie entfällt oder bricht.
   **Abbruch**, wenn `verify` schon vor dem Update Abweichungen meldet: erst klären.
3. **Branch `cosmos` aktualisieren.** Eigener Branch von `origin/cosmos`, der Upstream-Stand als kumulierter Unterschied ohne Workflows (Muster aus `PATCHES.md`, Abschnitt „Übernommene Upstream-Stände“):
   ```bash
   git fetch https://github.com/CosmosOS/Cosmos.git <tag>
   git diff --binary <alter-grundstand> FETCH_HEAD -- . ':(exclude).github/workflows' | git apply --3way --index
   ```
   Konflikte je Patch lösen; die Patchdatei bekommt eine Zeile zum neuen Stand, `PATCHES.md` den neuen Grundstand. Patches, die Upstream inzwischen enthält, streichen (Datei löschen, im ZehOS-PR den zugehörigen Umweg beachten). Die Host-Tests des Cosmos-Baums (`tests/Cosmos.Kernel.Tests.System`) müssen grün sein. Den PR gegen `cosmos` erstellen; Merge nur nach Zustimmung. Der Commit, der danach im Branch `cosmos` steht, ist der Pin-Commit (voller Hash).
   **Abbruch** bei einem Konflikt, dessen fachliche Auflösung unklar ist: im Kanal fragen, nichts raten.
4. **ZehOS-Branch** `task/cosmos-<version>` von `origin/master`. Pin setzen und prüfen (neue Version, weil der Commit neu ist; Schema `<Basis>-zehos.<N>`, bei neuer Basis `.0`):
   ```bash
   python scripts/cosmos-update.py pin 3.0.99-zehos.0 <voller-commit>
   python scripts/cosmos-update.py verify
   python -m unittest discover -s scripts/tests -p test_build_cosmos.py
   ```
   Das ändert `deploy/cosmos/COMMIT`, `deploy/cosmos/VERSION` und `global.json` (`msbuild-sdks`); ein neuer Commit mit gleicher Version wird abgelehnt. Alle anderen Stellen lesen `VERSION` (`Directory.Build.props`, Workflows über `scripts/build-cosmos.sh`). Neu auftretende Textverweise auf die alte Version (`git grep "3.0.97-zehos"`) nur in lebenden Dokumenten anpassen, nicht in datierten Berichten.
5. **Bauen und prüfen** (Befehle und Plattformen siehe `AGENTS.md`, `docs/regeln/PRUEFEN.md`):
   ```bash
   scripts/build-cosmos.sh --global
   dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror
   cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release
   dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSRecovery=true
   python scripts/run-tests.py
   ```
   Dazu im Gast (QEMU, isolierte Imagekopie): Boot, Shell, Eingabe, Desktop, Dateien, Netzwerk, Paket-Apps, `scripts/test-power-commands.ps1`, die Gastsuite (`scripts/run-guest-suite.py`) und die ext2-/Speicherproben, die der Pin berührt; VMware, soweit der Pin Boot, Anzeige, Energie oder Zeit berührt. Aufträge an die Clerks, wenn im Team gearbeitet wird.
   **Abbruch** bei jedem roten Befehl, der nicht eindeutig an einer Testumgebung liegt: Befund im Kanal melden, Pin nicht pushen als „fertig“. Nicht geprüfte Plattformen ausdrücklich als offen führen.
6. **Dokumentation.** In `docs/COSMOS_SUPPORT.md` einen datierten Abschnitt „Pin `<Version>`“ ergänzen (Muster: PR #575): Cosmos-Commit, Patches, Prüfungen, offene Punkte. `ROADMAP.md` nur bei erfüllten Abnahmekriterien.
7. **Review und PR.** Unabhängiges Review nach `AGENTS.md`, dann pushen (normal, ohne Force) und den Pin-PR erstellen; im PR den `cosmos`-PR nennen und dass er zuerst gemergt werden muss. PR-Beschreibung: Problem, Änderung, ausgeführte Prüfungen, Grenzen. Ohne Zustimmung eines Projektbenutzers nicht mergen. Nach dem Merge ist der Pin auf `master` wirksam; der Feed wird dann in CI und Release aus `deploy/cosmos/COMMIT` gebaut.
