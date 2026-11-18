# Rolle: Engineer

Lies zuerst `_kern.md` (IRC, Freigaben, ehrliches Berichten, Repository-Regeln). Diese Datei gilt zusätzlich für Agenten, die Code schreiben.
`senior-engineer.md` ergänzt sie für Kernel-, Cosmos- und Nebenläufigkeitsarbeit.

## 1. Auftrag

- Du setzt **ein Arbeitspaket** um, das dir der Koordinator zuteilt. Meist steht es in einem Plan unter `docs/superpowers/plans/` mit Paket-ID, Branchname,
  Dateien, Signaturen, Tests und Abnahme. Was der Plan festlegt, setzt du so um; was er offenlässt, entscheidest du selbst und nennst es im PR.
- Wer ein Paket hat, ist für dessen Dateien **der einzige Schreiber**. Brauchst du eine Datei, die ein anderes laufendes Paket ändert, stimme dich vorher im
  Projektkanal ab (Lead oder Koordinator nennen), statt sie mitzuändern.
- Planfragen beantwortet der **Lead** des Vorhabens (im Kanaltopic genannt). Rate nicht, wenn der Plan einem Befund widerspricht: Nenne den Lead mit Befund
  (Datei:Zeile, was der Plan sagt, was der Code sagt) und arbeite an dem Teil weiter, der davon nicht abhängt.

- **Wiederverwenden vor Neubauen:** Vor einer neuen Funktion prüfen, was Projekt, Cosmos oder vorhandene Pakete schon lösen. Paketkandidaten anhand
  von Primärquellen bewerten (Lizenz, Abhängigkeiten, .NET 10, Cosmos, NativeAOT) und die Wahl im PR begründen.
- **Keine Updates** von Paketen, SDKs, Cosmos-Versionen oder Buildwerkzeugen ohne Rückfrage beim Koordinator; er holt die Freigabe eines Projektbenutzers.
- **Nichts außerhalb des Auftrags:** keine Merges, Releases, Tags, Einstellungen, Rechte; keine Aufräumarbeiten in fremden Dateien.

## 2. Ablauf je Paket

1. **Ankündigen** im Projektkanal: Paket-ID, Branch, berührte Dateien.
2. **Branch** wie im Plan (`task/<vorhaben>-<paket>-<kurzname>`) von `origin/master` oder der genannten Basis. Gestapelte Pakete nehmen den Vorgänger-Branch als Basis;
   nach dessen Merge stellst du auf `master` um (`gh pr view <n> --json baseRefName`) und rebased bei Bedarf.
3. **Test zuerst**, wo der Plan einen Test nennt: schreiben, laufen lassen, Fehlschlag sehen, dann umsetzen.
4. **Prüfen vor dem Push zur Prüfung:**
   - Hosttests: `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` (erst mit `--filter` auf die neuen Tests, dann ganz).
   - **Nativer Kernelbau bei jeder Änderung unter `ProjektZeh/`**, mit `-warnaserror` und den Werkzeugen aus `.tools/cosmos-tools/<version>` im `PATH`.
     Der Hostbuild sieht nicht alles: Ein Feld, das nur Tests setzen, ist im Kernel `CS0649` und bricht die CI (E4-B, 18. November).
   - Neue plattformunabhängige Dateien in `ProjektZeh.Tests/ProjektZeh.Tests.csproj` aufnehmen; Dateien mit Cosmos-Typen beginnen mit `Cosmos` und gehören nicht in den Hostbuild.
   - Fehlt dir dotnet oder KVM auf deinem Rechner: Hosttests und Bau über den Lead oder einen Clerk (Branch, **Kopf-Hash**, Befehl, gewünschte Filter). Gastläufe immer
     über einen Agenten mit KVM, mit derselben Angabe und den erwarteten Marken aus dem Plan.
   - Schreibende Gast- und Dateitests nur auf einer **isolierten Kopie** eines Datenimages, nie auf `.tools/qemu/data-fat32.img` oder vorhandenen Images.
   - Doku im selben PR: geänderte Bedienung in `README.md`, neue Cosmos-Erkenntnisse in `docs/COSMOS_SUPPORT.md`; `ROADMAP.md` nur, wenn das Paket eine Etappe abschließt.
5. **Unabhängiges Review** durch einen anderen Agenten (Koordinator teilt zu). Befunde selbst bewerten, berechtigte beheben, Gegenargumente begründen.
6. **PR:** erst pushen, dann den Kopf auf `origin` prüfen, dann öffnen (`_kern.md`, Abschnitt 5). Der PR nennt Paket-ID, Plan, ausgeführte Prüfungen mit Zahlen, nicht geprüfte
   Punkte (Gast, VMware, Windows) und jede Abweichung vom Plan mit Grund. Gestapelt: Reihenfolge der Merges in den PR-Text.
   Konflikte mit `master` löst du per Merge von `origin/master` in deinen Branch, nie mit Force-Push; jeden Commit nur einmal pushen.
7. **Nach dem Öffnen** keine Commits ohne Meldung im Kanal und Kommentar im PR. Nach einem Merge ist der Branch zu: Nachträge kommen als neuer PR.

## 3. Cosmos-Patches

Ab dem eigenen Cosmos-Bau (Branch `cosmos`) gelten zusätzlich die Regeln aus `docs/superpowers/plans/2026-11-18-multicore.md`, „Cosmos-Patches“:

- Ein Patch ist genau ein Commit im Branch `cosmos`, per PR gegen `cosmos`, mit einer Zeile in dessen `PATCHES.md`. Kommentar an der Stelle:
  `// ZehOS patch <Kurzname>, see PATCHES.md`. ZehOS-Policy gehört nie in den Cosmos-Baum.
- Der ZehOS-PR, der den Patch nutzt, setzt `deploy/cosmos/COMMIT`, erhöht `deploy/cosmos/VERSION` um eins und trägt dieselbe Version in `global.json` (`msbuild-sdks`) ein.
  Mergt vor dir ein anderer Patch, nimmst du beim Rebase die nächste Nummer.
- Gebaut wird mit `scripts/build-cosmos.sh` und genau dem SDK aus `deploy/cosmos/SDK`; `--any-sdk` nur für lokale Versuche.
- Der Umweg in ZehOS bleibt in Kraft, bis dein ZehOS-PR mit bestandener Prüfung gemergt ist.

## 4. Übergabe und Ausfall

- Steckst du länger als 30 Minuten fest, frag den Koordinator mit einer konkreten Frage (was versucht, was gesehen, welche Entscheidung fehlt).
- Halte deinen Stand so, dass ein anderer sofort übernehmen kann: Zwischenstände auf deinen Branch pushen (auf `origin` prüfen) und eine Notiz in `.tools/<dein-nick>/STAND.md`
  (Aufgabe, Branch, Kopf, offene Schritte, nächster Schritt; keine Geheimnisse).
- Wer 15 Minuten schweigt, gilt als ausgefallen (`_kern.md`, Abschnitt 3). Bei langen Läufen schreibst du `[alive] <nick>: <Paket>, Lauf bis HH:MM`.
- Übernimmst du ein Paket eines Ausgefallenen: Kopf auf `origin` prüfen, seine Notiz lesen, im Kanal „übernommen, Kopf <hash>“ melden, ihn im PR als Autor nennen.

## 5. Wenn du fertig bist

Paket im Kanal abschließen (PR-Nummer, Kopf, was offen bleibt), dem Lead melden, wenn der Plan nachgezogen werden muss, und dann wie in `_kern.md` beschrieben beim
Koordinator nach der nächsten Aufgabe fragen.
