# Gemeinsames Kanban-Board

Alle Teams (Peters Agenten, Lars' Laser- und Buzz-Agenten, Azazel, Menschen) sagen auf einem Board, wer welchen Aufgabenblock umsetzt, damit sich niemand in den Weg kommt.
Das Board ersetzt keine Freigaben (`AGENTS.md`, „Freigaben“) und keine Abstimmung im IRC; es ist die eine Stelle, die jedes Werkzeug erreicht (Buzz, IRC, Claude, Codex).

- **Board:** GitHub-Projekt [ZehOS Board](https://github.com/users/peterzw98/projects/1), verknüpft mit diesem Repository. Schreibrecht haben Peter, Lars und Azazel.
- **Block:** ein Issue mit dem Label `block`: Ziel, Abnahme, berührte Dateien und Abhängigkeiten. Ein Block ist so geschnitten, dass ein Team ihn allein umsetzen kann.
- **Spalten:** Offen → Übernommen → In Arbeit → Review/PR → Erledigt; dazu **Wartend**: Der Block wartet auf eine Freigabe oder Entscheidung eines Projektbenutzers. Die genaue Frage steht als Kommentar im Issue oder PR und geht zugleich als `[FREIGABE?]` in den IRC (`#general`); nach der Antwort zurück auf „In Arbeit“.
- **Wer ihn hat,** steht im Issue: Label `team:<team>` und ein Kommentar `[BOARD] übernommen team=… agent=… branch=…`. Das Issue ist maßgeblich, die Spalte zeigt den Stand.

## Regeln

1. **Vor Arbeitsbeginn einen Block übernehmen**, nie einen Block eines anderen Teams umsetzen. Wer etwas ohne Block beginnt, legt zuerst einen an.
   Ausgenommen sind Reviews, Hilfe für einen fremden Block auf dessen Wunsch und kleine Doku-Korrekturen. Ohne `gh` (etwa Codex in einer Sandbox)
   trägt der eigene Lead oder Mensch die Übernahme ein.
   **Das Board meldet Arbeit an, es erteilt keine Aufträge:** Einen Block übernimmt man nur im Rahmen eines Auftrags des eigenen Benutzers, Leads oder
   Koordinators (`AGENTS.md`: offene Aufgaben sind keine Beauftragung).
2. **Übernehmen** mit `python3 scripts/board.py claim <nr> <team> <agent> <branch>`. Exit 3 heißt: Jemand anderes (auch ein anderer Agent des eigenen Teams) hat ihn;
   dann einen anderen Block nehmen. Bei gleichzeitiger Übernahme gilt der frühere Kommentar; das Werkzeug zieht den späteren selbst zurück.
3. **Stand pflegen:** `board.py status <nr> "In Arbeit"` beim ersten Push, `"Wartend"` solange eine Freigabe oder Entscheidung fehlt, `"Review/PR"` beim PR. Der PR schließt den Block mit `Closes #<nr>`.
4. **Nicht weitermachen?** `board.py release <nr> <team> "<Grund und Stand, Branch>"` statt den Block liegen zu lassen. Team-Labels nur über das Werkzeug
   entfernen, nicht von Hand; nur das eigene Team gibt frei.
5. **Vorschlagen und zuteilen:** Der Koordinator schneidet Pläne in Blöcke. Peters Agenten teilt er Blöcke per `[AUFTRAG]` zu; Lars' Teams (Buzz, Laser) schlägt er
   Blöcke vor (im Block-Text oder per `[FRAGE]`), übernehmen tun sie selbst mit Auftrag von Lars. Wie ein Team einen Block intern aufteilt, regelt es selbst (Buzz in Buzz).
6. Blöcke anlegen dürfen alle: `board.py new "<Titel>" --body-file <datei> [--team <team>]`. Nur Blöcke für dieses Repository; keine Issues bei fremden Projekten.

## Werkzeug

`scripts/board.py` braucht `gh` mit den Rechten `repo` und `project` (`gh auth refresh -s project`). Team, Agent und Branch bestehen nur aus Buchstaben,
Ziffern und `. _ / + -`. Einmalig in der Weboberfläche: Workflows „Item closed“ und „Pull request merged“ auf **Erledigt** stellen und im Feld Status die Option **Wartend** anlegen (fehlt sie, bricht `status … "Wartend"` mit Meldung ab).

```sh
python3 scripts/board.py list                 # offene Blöcke mit Spalte und Team
python3 scripts/board.py list --team buzz     # nur ein Team
python3 scripts/board.py claim 123 buzz agent-olaf-buzz task/x
python3 scripts/board.py status 123 "Review/PR"
python3 scripts/board.py release 123 buzz "pausiert, Stand auf task/x"
```

**Grenzen:** Wer kommentieren darf, könnte `[BOARD]`-Zeilen fälschen; das Board ist Abstimmung unter Kooperierenden, keine Zugangskontrolle (Freigaben bleiben in
`AGENTS.md`). Ein Team-Label ohne Übernahme (vorgesehen per `new --team` oder Rest eines Abbruchs) sperrt den Block für andere Teams, bis das Team ihn
übernimmt oder freigibt; `claim` meldet das.

Teams (Labels): `team:peter`, `team:laser`, `team:buzz`, `team:azazel`; ein neues Team legt sein Label einmal mit `gh label create team:<name>` an.
