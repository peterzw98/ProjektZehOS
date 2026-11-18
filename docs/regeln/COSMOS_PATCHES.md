# Cosmos-Patches

Ausgelagert aus `AGENTS.md`, damit nicht jede Sitzung diese Regeln mitlädt. Sie gelten unverändert; bei Widerspruch gilt `AGENTS.md`.

- Änderungen am Branch `cosmos` laufen nur per Pull Request (Ruleset 27547602 auf `refs/heads/cosmos`). Ein Patch wird per Squash-Merge genau ein Commit dort und erhält eine eigene Datei `patches/<Kurzname>.md` im Branch `cosmos` (Felder Commit, Dateien, Grund, entfernter ZehOS-Umweg, Prüfung; Regel in `PATCHES.md`). Ein gemeinsamer Tabelleneintrag führte bei jedem Merge zu Konflikten.
- Der ZehOS-PR, der den Patch verwendet, setzt `deploy/cosmos/COMMIT` auf den Patch-Commit, entfernt den Umweg und wiederholt dessen bisherige Prüfung. Er wird erst nach dem `cosmos`-PR gemergt. Der Umweg bleibt bis zum Merge des geprüften ZehOS-PRs unverändert in Kraft.
- Ein Patch ändert `deploy/cosmos/COMMIT` und `VERSION` nur innerhalb von 3.0.97 (`3.0.97-zehos.N`); ein Wechsel der Cosmos-Basisversion bleibt ein Update, vor dem nachgefragt wird.
- Patches beheben Fehler oder öffnen Schnittstellen. ZehOS-Policy wie Scheduler, Topologie und Appregeln gehört nicht in den Cosmos-Baum.
- Keine Issues, Pull Requests oder Kommentare bei CosmosOS/Cosmos oder anderen fremden Projekten.
