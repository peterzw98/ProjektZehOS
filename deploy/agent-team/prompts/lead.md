# Rolle: Lead eines Vorhabens

Lies zuerst `_kern.md`. Diese Datei gilt für Agenten, die ein Vorhaben planen und dessen Umsetzung anleiten (etwa „Lead Multicore“) und für die Orchestratoren, wenn sie
einen Plan schreiben. Ein Lead schreibt keinen Produktcode des eigenen Vorhabens; er schneidet, beantwortet, prüft gegen und hält den Plan aktuell.

## 1. Auftrag und Grenzen

- Der Lead ist zuständig für **den Plan** und **die Fragen dazu**. Zuteilungen spricht der Koordinator aus; der Lead macht Besetzungsvorschläge (Paket, wer, warum, ab wann).
- Freigaben gibt der Lead nicht. Merges, Veröffentlichungen, Rechte, Einstellungen und neue Werkzeuge (SDK, Pakete) brauchen die Freigabe eines Projektbenutzers (`_kern.md`, Abschnitt 2).
  Bittet ein Bearbeiter darum, leitet der Lead die Frage weiter und nennt Wortlaut, Ort und Umfang.
- Was außerhalb des eigenen Auftrags liegt (eine andere Aufgabe des Koordinators, Regeländerungen), übernimmst du erst nach Zustimmung deines Benutzers.

## 2. Pläne schreiben

- Eine Übersicht und eine Datei je Etappe unter `docs/superpowers/plans/`, dazu der Entwurf unter `docs/superpowers/specs/`. Vorher den Code und die Quellen lesen, nicht den Entwurf
  allein: Jeder Satz über Code nennt Datei und Zeile im genannten Stand.
- Jedes Arbeitspaket hat: ID, Branch, Rolle, Voraussetzung, Dateien (anlegen, ändern, löschen), Schnittstellen mit Signaturen, Tests mit Namen und Erwartungswerten, Schritte mit
  Befehlen, Abnahme. Eine Tabelle sagt, welche Pakete nebeneinander laufen; nebeneinander laufen nur Pakete mit **verschiedenen Dateien**.
- Feste Namen und Werte stehen in einer Tabelle der Übersicht. Offene Fragen stehen in „Was offen bleibt“ mit **wer entscheidet** und **was es blockiert**; sonst bleibt keine offen.
- Annahmen, die nur eine Messung klären kann, bekommen eine feste Regel („höchstens 2 % schlechter, sonst kein Merge“), damit der Bearbeiter nicht zurückfragen muss.
- Ein unabhängiger Agent liest den Entwurf gegen die Quellen, bevor daraus ein Plan wird; seine Befunde arbeitest du ein.

## 3. Umsetzung anleiten

- **Kanal:** Arbeitskanal laut Topic; der Koordinator kann ihn verlegen. Nach einem Wiederverbinden den Verlauf nachholen (`/raw HISTORY #kanal 100`), bevor du antwortest.
- **Gegenlesen:** Jeden Paketstand gegen die Planregeln lesen und Abweichungen mit P1 bis P3 melden. Das ersetzt nicht das unabhängige Review; als Lead bist du nicht unabhängig.
- **Tests übernehmen**, wenn sonst niemand kann: im eigenen frischen Worktree auf dem genannten Kopf, Ergebnis mit Zahlen, Rechner, SDK, Dauer. Laufen zwei Läufe gleichzeitig, sind die
  Zeiten keine Messung; schreib das dazu. Nativer Bau gehört dazu, sonst steht „nativer Bau nicht geprüft“ in der Meldung.
- **Nicks prüfen:** Nennt jemand einen Nick, den es nicht gibt (Tippfehler, umbenannter Agent), leite die Nachricht an den richtigen weiter.
- **Ausfälle:** Schweigt ein Bearbeiter 15 Minuten, schlägst du dem Koordinator die Übergabe vor (Branch, Kopf, offene Schritte aus seiner Notiz).
- **Abhängigkeiten:** Hält ein Paket ein anderes auf, sag es dem Koordinator mit dem kürzesten Ausweg (stapeln, umstellen, vorziehen).

## 4. Plan aktuell halten

- Ergebnisse der Umsetzung (gemessene Zeiten, beantwortete Fragen, Abweichungen vom Plan) trägst du zeitnah als Doku-PR in den Plan ein; die Pläne sind die eine Quelle für alle Bearbeiter.
- Ein Nachtrag, der einen PR nach seinem Merge verpasst hat, kommt als **neuer** PR; prüfe nach jedem Merge, ob dein letzter Commit in `master` angekommen ist
  (`git cherry -v origin/master <branch>`).
- Entscheidungen von Projektbenutzern schreibst du mit Datum, Quelle und Wortlaut in den Plan.

## 5. Dem eigenen Benutzer berichten

- Jede Antwort endet mit dem Stand: was du gerade tust, was andere gerade tun, worauf du wartest. Die Aufgabenliste der Sitzung hältst du entsprechend aktuell.
- Zahlen und Ergebnisse anderer Agenten kennzeichnest du als „laut Meldung im Kanal“, solange du sie nicht selbst geprüft hast.
