# Etappe 6b: Nachrichtenwarteschlangen und Synchronisation

Stand: 13. November 2026. Branch: `codex/etappe6b`. Der Stand enthält den aktuellen `origin/master`, die gemeinsame Aufgabenverwaltung aus 6a sowie deren Cancellation-Cleanup und Interruptmasken-Korrektur. Cosmos bleibt bei **3.0.94**, das installierte SDK bei **10.0.112**; keine Abhängigkeiten wurden aktualisiert.

## Fertiger Funktionsumfang

- Begrenzter FIFO-Puffer mit `Success`, `Empty`, `Full`, `Closed`, `Cancelled` und `TimedOut`. Schließen weckt wartende Teilnehmer; vorhandene Nachrichten bleiben lesbar. Abbruch verändert den Puffer nicht.
- `Monitor.Wait` und `Monitor.PulseAll` verwenden dieselbe Sperre wie die Zustandsprüfung; Cancellation-Callbacks wecken wartende Teilnehmer ohne aktives Dauerabfragen. Diese vorhandenen Laufzeitbausteine vermeiden eine zusätzliche, im Kernel ungeprüfte Paketabhängigkeit.
- Erzeuger-Verbraucher-Vorführung mit 24 nummerierten Nachrichten, Kapazität drei und wählbar langsamem Erzeuger oder Verbraucher. Die interaktive Shell wartet dabei nicht auf Nachrichten. `messages status` zeigt Fortschritt und Reihenfolgefehler; `close` leert den geschlossenen Puffer, `stop` bricht die Aufgaben ab. Nach Abschluss ist ein Neustart möglich.
- Beide Richtungen des kontrollierten Kontobeispiels erwerben zuerst Konto 1, dann Konto 2. So bleibt die Summe 2000 ohne zyklische Sperrabhängigkeit.
- Alleiniger Consumer-Abbruch schließt den Puffer und beendet dadurch auch den wartenden Producer. Die Aufgabenverwaltung gibt Cancellation-Ressourcen nach Abbruch und Fehler ausdrücklich frei.

## Tatsächlich ausgeführte Prüfungen

- `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`: **216 erfolgreich**, null fehlgeschlagen/übersprungen. Darunter FIFO, Kapazität, Argumentgrenzen, konkurrierende Sender/Empfänger ohne Verlust/Duplikate, Abbruch, Zeitlimit, Schließen, feste Sperrreihenfolge und Demo-Neustart nach Consumer-Abbruch bei vollem Puffer.
- `cosmos check`: alle Werkzeuge vorhanden. Native Probe-, Normal- und Recovery-Builds mit Warnungen als Fehler: jeweils null Warnungen/Fehler. Für den Build wird das vorhandene Verzeichnis `~/.dotnet/tools` im `PATH` benötigt; MSBuild-IPC erforderte hier die Ausführung außerhalb der Sandbox.
- Isolierter QEMU-Probe-Gast ohne Datenlaufwerk (`q35`, CPU `max`, 512 MiB, Standard-VGA): `THREAD_PROBE PASS` auf dem ZehOS-Round-Robin-Scheduler. Die Nachrichtenprobe meldet `numbered=100 consumed=100 duplicates=0 order=ok close/cancel/timeout/locks=ok` und `consumer-cancel/full-buffer/restart PASS`. Damit sind auch sauberes Aufgabenende und vollständige Zustellung nach erneutem Start im Gast geprüft.
- Die Threadprobe bestätigt geschlossene Cancellation-Wartehandles nach Abbruch und Fehler. Die zusätzliche Diagnose reproduziert übersprungene innere `finally`-Blöcke bei `OperationCanceledException`: `first-finally=0 second-finally=0 remaining=2 caught=2`. Die produktive Demo verwendet für erwarteten Abbruch normale Rückwege.
- Separater normaler QEMU-Gast und separater Recovery-Gast: Shell-Eingabe mit `messages start slow-consumer`, `uptime` und `messages status` geprüft. Während aktiver Aufgaben wird der volle Puffer `3/3` angezeigt; die Shell bleibt bedienbar. Anschließend erreichen beide Ansichten `active=0 produced=24 consumed=24 order-errors=0 buffered=0/3 lock-total=2000`. Im normalen Desktop wurde das Terminal über den Anwendungsstarter geöffnet. Die Bildschirmaufnahmen wurden visuell geprüft; Normal-/Recovery-Logs enthalten keine Threadprobe.
- `git diff --check`: ohne Fehler.

## Offene Plattformgrenzen

VMware und echte Hardware wurden für diese Nachrichtenimplementierung nicht geprüft. Frühere Threadproben auf VMware sind kein Nachweis für die neue Nachrichtenprobe. Die vollständige Plattformabnahme bleibt dort offen. Es wurden keine Datenimages verwendet oder verändert.
