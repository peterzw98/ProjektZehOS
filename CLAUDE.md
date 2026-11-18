# Regeln für KI-Assistenten in diesem Branch

Dieser Branch ist die Cosmos-Quelle für ZehOS, kein Cosmos-Entwicklungszweig. Zuerst `ZEHOS.md` lesen.

- Die Projektregeln von ZehOS stehen in `AGENTS.md` auf dem Branch `master` und gelten auch hier.
- Keine Issues, Pull Requests oder Kommentare bei CosmosOS/Cosmos oder anderen fremden Projekten.
- Änderungen nur per Pull Request gegen `cosmos`; ein Patch wird per Squash ein Commit und hat eine eigene Datei `patches/<Kurzname>.md` (siehe `PATCHES.md`).
- Keine ZehOS-Policy in diesem Baum; ein Patch behebt einen Fehler oder öffnet eine Schnittstelle.
- Kommentare im Cosmos-Code bleiben englisch wie der umgebende Code; eigene Stellen tragen den Vermerk
  `// ZehOS patch <Kurzname>, see PATCHES.md`.
- Nicht `.devcontainer/postCreateCommand.sh` oder `.ps1` ausführen (siehe `ZEHOS.md`, Abschnitt „Bauen“).
