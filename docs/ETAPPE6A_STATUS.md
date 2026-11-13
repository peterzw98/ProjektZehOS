# Zwischenstand 6a – 13. November 2026

## Auftrag und Ablage

Der Nutzer möchte die gesamte Gruppe 6a–6c fertigstellen. Zuletzt hat er ausdrücklich beauftragt, den aktuellen Stand getrennt zu sichern und zu pushen, **noch keine Pull Requests**. Die drei Branches bleiben getrennt:

- `codex/etappe6a`: dieser Worktree `scheduling/ProjektZehOS`.
- `codex/etappe6b`: Worktree `etappe6b/ProjektZehOS`; enthält eine Kopie der ersten 6a-Implementierung als Voraussetzung.
- `codex/etappe6c`: Worktree `etappe6c/ProjektZehOS`; eigenständiger, noch nicht vollständig im Gast geprüfter Sicherheitsstand.

Die Ausgangsbasis war master `5c8c185`. Inzwischen enthält master `6685bb1` zusätzliche Desktop-/App-/FPS-Änderungen. Diese wurden bewusst **nicht** in die Zwischenstände integriert. Die parallel laufenden Einstellungen-/Taskmanager-Arbeiten in anderen Worktrees gehören nicht zu diesem Auftrag.

## Implementiert und geprüft

- `e247da1`: echte Round-Robin-Policy über Cosmos 3.0.94, feste 20-ms-Zeitscheibe, begrenzte FIFO, vorallokierte Auswahlspur. Kontextwechsel und Timer bleiben bei Cosmos.
- Acht verwaltete Aufgaben maximal, fortlaufende Kennungen, Status/Fortschritt, kooperativer Abbruch, Fehleranzeige und Ressourcenbereinigung. `tasks start busy`, `tasks start wait`, `tasks list`, `tasks cancel <ID>`, `tasks scheduler`.
- `c2e8037`: Abbruch/Bereinigung nach dem belegten Cosmos-Unwindfehler korrigiert. `BackgroundTaskContext.Sleep(int)` liefert jetzt `false` bei Abbruch. Die Aufgabenbereinigung steht ausdrücklich nach den Catches und verlässt sich nicht auf ein verschachteltes `finally`.
- Aktuell **156/156 Hosttests**, Release mit Warnungen als Fehler, erfolgreich.
- Native Round-Robin-Probe, Normal- und Recovery-Builds erfolgreich. QEMU und VMware bestätigen Verdrängung/Warten unter Round-Robin. QEMU bestätigt Desktop-Bedienung mit zwei Rechenaufgaben, wartende Aufgabe, Abbruch, erneuten Start, Textshell, Recovery und Energieaktionen.
- Zusätzliche native Bereinigungsprobe nach `c2e8037`: `THREAD_PROBE tasks cancellation/fault resource cleanup PASS`, anschließend globales PASS. Dabei waren die Cancellation-Wartehandles nach Abbruch und Workerfehler tatsächlich geschlossen. Dieser letzte Bereinigungspfad wurde in QEMU geprüft; VMware-Nachprüfung dieses Zusatzes steht aus.
- Unabhängiges Code-Review ohne Befunde zur ersten 6a-Implementierung; zwei Dokumentationskorrekturen übernommen. Abschließendes Review des gemeinsamen Gesamtstands steht aus.

## Belegte Laufzeitgrenze

Cosmos 3.0.94 überspringt beim Exception-Unwind teilweise verschachtelte `finally`-Blöcke. Die 6b-Minimalprobe meldete bei zwei abgebrochenen Workern `first-finally=0 second-finally=0 remaining=2 caught=2`. Kein Verlust durch `Interlocked`: die Aufräumblöcke selbst wurden nicht ausgeführt. Produktive Abbruchpfade verwenden kontrollierte Rückgaben beziehungsweise explizite Fortsetzung nach Catches. Keinen eigenen Cosmos-Updateversuch oder allgemeinen Unwinderfix beginnen, ohne den Umfang abzustimmen.

## Später fortsetzen

1. Statusdateien aller drei Branches lesen und Remote-/Worktree-Status prüfen. Keine fremden laufenden Desktoparbeiten zurücksetzen.
2. 6b muss zusätzlich zur kopierten 6a-Basis den Bereinigungscommit `c2e8037` enthalten. Beide Probeaufrufe (`CheckTaskCleanup`, `MessageGuestProbe`) erhalten. Einen gezielten Gasttest für den im Review gefundenen Einzelabbruch des Consumers wiederholen.
3. 6c ist ein Zwischenstand: finale native/Gast-Prüfung von Anmeldung, Kennwortmaske, mehreren Sitzungen, zentralen ACLs, Altdateien, Recovery und Neustartpersistenz auf einer isolierten Imagekopie steht aus. BouncyCastle.Cryptography 2.7.0 ist ausdrücklich vom Nutzer genehmigt.
4. Erst danach die Integration mit dem inzwischen neuen master planen. Insbesondere neue FileManager-/TextEditor-FileService-Fabriken dürfen die 6c-Prüfungen nicht umgehen; Recovery muss getrennt bleiben.
5. Kombinierten Stand vollständig prüfen und unabhängig reviewen. PRs erst erstellen, wenn der Nutzer das aktuelle Verbot aufgehoben hat.

Ausführliche Befehle und frühere Nachweise: `README.md`, `docs/COSMOS_SUPPORT.md`, `docs/superpowers/plans/2026-11-13-etappe6.md`. Generierte Logs/ISOs/Bilder liegen ausschließlich lokal unter `.tools/` und werden nicht gepusht.
