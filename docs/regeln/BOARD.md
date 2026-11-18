# Gemeinsames Kanban-Board

Alle Teams (Peters Agenten, Lars' Laser- und Buzz-Agenten, Azazel, Menschen) sagen auf einem Board, wer welchen Aufgabenblock umsetzt, damit sich niemand in den Weg kommt.
Das Board ersetzt keine Freigaben (`AGENTS.md`, „Freigaben“) und keine Abstimmung im IRC; es ist die eine Stelle, die jedes Werkzeug erreicht (Buzz, IRC, Claude, Codex).

- **Board:** GitHub-Projekt [ZehOS Board](https://github.com/users/peterzw98/projects/1), verknüpft mit diesem Repository. Schreibrecht haben Peter, Lars und Azazel.
- **Block:** ein Issue mit dem Label `block`: Ziel, Abnahme, berührte Dateien und Abhängigkeiten. Ein Block ist so geschnitten, dass ein Team ihn allein umsetzen kann.
- **Spalten:** Offen → Übernommen → In Arbeit → Review/PR → Erledigt.
- **Wer ihn hat,** steht im Issue: Label `team:<team>` und ein Kommentar `[BOARD] übernommen team=… agent=… branch=…`. Das Issue ist maßgeblich, die Spalte zeigt den Stand.

## Regeln

1. **Vor Arbeitsbeginn einen Block übernehmen**, nie einen Block eines anderen Teams umsetzen. Wer etwas ohne Block beginnt, legt zuerst einen an.
2. **Übernehmen** mit `python3 scripts/board.py claim <nr> <team> <agent> <branch>`. Exit 3 heißt: Ein anderes Team war schneller; dann einen anderen Block nehmen.
   Übernehmen zwei Teams gleichzeitig, gilt der frühere Kommentar; das Werkzeug zieht den späteren selbst zurück.
3. **Stand pflegen:** `board.py status <nr> "In Arbeit"` beim ersten Push, `"Review/PR"` beim PR. Der PR schließt den Block mit `Closes #<nr>`.
4. **Nicht weitermachen?** `board.py release <nr> "<Grund und Stand, Branch>"` statt den Block liegen zu lassen.
5. **Zuteilen:** Der Koordinator (oder ein Mensch) kann einen Block einem Team geben (`board.py new … --team <team>` oder Label setzen); wie das Team ihn intern aufteilt,
   regelt es selbst (bei Buzz in Buzz). Offene Blöcke ohne Team darf sich jedes Team nehmen.
6. Blöcke anlegen dürfen alle: `board.py new "<Titel>" --body-file <datei> [--team <team>]`. Nur Blöcke für dieses Repository; keine Issues bei fremden Projekten.

## Werkzeug

`scripts/board.py` braucht `gh` mit den Rechten `repo` und `project` (`gh auth refresh -s project`).

```sh
python3 scripts/board.py list                 # offene Blöcke mit Spalte und Team
python3 scripts/board.py list --team buzz     # nur ein Team
python3 scripts/board.py claim 123 buzz agent-olaf-buzz task/x
python3 scripts/board.py status 123 "Review/PR"
python3 scripts/board.py release 123 "pausiert, Stand auf task/x"
```

Teams (Labels): `team:peter`, `team:laser`, `team:buzz`, `team:azazel`; ein neues Team legt sein Label einmal mit `gh label create team:<name>` an.
