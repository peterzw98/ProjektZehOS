# Etappe 6c – gesicherter Zwischenstand

Stand: 13.11.2026, Branch `codex/etappe6c`, Ausgangscommit `5c8c185`.
Dies ist ein WIP-Sicherungsstand zur späteren Fortsetzung, keine fertige Etappe.
Kein PR und keine Zusammenführung mit anderen Arbeitszweigen. Die Roadmap-Abnahme bleibt offen.

## Vorhandene Umsetzung

- Benutzerkonten mit stabiler ID, getrennten Sitzungen, Administratorrolle und Home-Verzeichnis.
- Persistierte Konto-/ACL-Metadaten, gesperrter Zugriff bei fehlenden oder beschädigten Metadaten und expliziter Sicherheits-Recovery-Build (`ZehOSSecurityRecovery=true`).
- Datei-Zugriffskontrolle im FileService, Eigentümer, benutzerspezifische Lese-/Schreibfreigaben und explizite Zuordnung bestehender Dateien.
- Shellbefehle `whoami`, `login`, `logout`, `user`, `grant` und `security`; verdeckte Kennworteingabe ohne Verlauf. Kennwortberechnung ist in Hintergrundarbeit und anschließende Zustandsübernahme aufgeteilt; Textshell und Terminal sind angeschlossen.
- PBKDF2-SHA256 über die ausdrücklich genehmigte Abhängigkeit BouncyCastle.Cryptography 2.7.0; 600000 Iterationen, zufälliges Salt und Vergleich über FixedTimeEquals. CosmosSaltSource nutzt die vorhandenen nativen RDSEED-/RDRAND-Funktionen; fehlt die Quelle, wird Kennwortanlage abgewiesen.
- Opt-in-Kryptografieprobe und plattenloser QEMU-Prüfhelfer. Der Helfer wurde für diese Sicherung nicht gestartet.

## Bisherige Kryptografieprobe – begrenzter Nachweis

Bereits vorhandene serielle QEMU-Protokolle wurden beim Sichern erneut gelesen:

- `.tools/security-probe/246bea333f0042dda96ac4a260b93093/serial.log`: Zufallsprobe abgeschlossen; Plattform-.NET-PBKDF2 scheiterte anschließend mit `System.TypeInitializationException`, innere `System.ArgumentNullException` (`value`). Diese Implementierung wird daher nicht für das Kennwortmodell verwendet.
- `.tools/security-probe/9838dd188ab54e59acfa3f44c384c18d/serial.log`: `SECURITY_PROBE PASS PBKDF2-SHA256 vector and RNG` mit BouncyCastle. Geprüft wurde der feste SHA256-Testvektor mit einer Iteration sowie die Zufallsquelle, nicht der vollständige interaktive Kontoablauf mit 600000 Iterationen.
- Komponentenübergreifend übermittelter Cosmos-Befund aus Etappe 6b (hier nicht erneut ausgeführt): die minimale RoundRobin-Probe meldete `MESSAGES_CLEANUP first-finally=0 second-finally=0 remaining=2 caught=2` und `THREAD_PROBE FAIL cleanupstate`. Bei ThrowIfCancellationRequested erreichten beide Worker ihren äußeren OperationCanceledException-Catch, während die verschachtelten finally-Flags ausblieben. Die 6a/6b-Korrektur verwendet für diesen Abbruchpfad normale Rückgaben und explizites Aufräumen; der Hauptagent meldet die anschließende Cleanup-Probe als bestanden. Das beweist die finally-Pfade von 6c nicht: insbesondere das Nullsetzen temporärer Kennwortbytes in PasswordHasher muss im Gast noch verifiziert beziehungsweise angepasst werden. Der ursprüngliche Nachweis liegt laut Hauptagent im getrennten 6b-Worktree unter `.tools/messages-interactive/serial-cleanup.log`, Zeilen 534–535, die Folgeprobe unter `serial-cleanup-fixed.log`.

Die alten Probeprotokolle sind lokale, ignorierte Diagnoseartefakte und werden nicht eingecheckt. Ihre Ergebnisse sind kein neuer Gasttest des hier gesicherten Gesamtstands.

## Modellreview und behobene Befunde

Zwei zuvor festgestellte Modellbefunde sind im Quellstand korrigiert und durch Hostregressionen abgedeckt:

1. Kontoanlage darf ein vorhandenes oder bereits mit ACL versehenes Home nicht automatisch übernehmen. Die Zustandsübernahme prüft beides erneut und verweigert diese Kontoanlage.
2. Sicherheits-Recovery muss auch eine beschädigte Metadatendatei oberhalb des Shell-Textlimits erhalten. Die Datei wird unverändert mit File.Move in eine eindeutig benannte Recovery-Sicherung verschoben statt über ReadText/WriteText kopiert.

Der Regressionstest `UserCreationDoesNotAdoptExistingHomesAndRecoveryPreservesOversizedMetadata` prüft diese Fälle einschließlich einer 70000 Zeichen großen beschädigten Datei. Ein unabhängiges finales Review des Gesamtstands steht weiterhin aus.

## Aktuelle Prüfung dieses Sicherungsstands

- SDK: `dotnet --version` meldet 10.0.403.
- `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`: bestanden, 159 erfolgreich, 0 fehlgeschlagen, 0 übersprungen, Exitcode 0.
- `cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release`: `Build completed successfully!`, Exitcode 0; normales Release-ISO unter `ProjektZeh/output-x64/ProjektZeh.iso`.
- `git diff --check`: ohne Befund vor der Statusdatei; nach dem Staging nochmals geprüft.
- Erste eingeschränkte Versuche scheiterten am Zugriff auf die vorhandene NuGet.Config; dieselben Prüfungen liefen anschließend mit freigegebenem Zugriff erfolgreich. Keine Paket-/Werkzeugupdates vorgenommen.
- Für diese Sicherung keine VM gestartet und keine vorhandenen Datenimages angefasst. Kein neuer Recovery-Build, kein QEMU-/VMware- oder Hardwaretest.

## Offen für die Fortsetzung

- Vollständiger interaktiver Gasttest von Login/Logout, verdeckter Kennworteingabe und Hintergrundberechnung in Textshell und mehreren Terminals.
- Zwei Benutzer: Eigentum, verweigerte Zugriffe, explizite Freigaben, unabhängige Sitzungen und Dateien im Gast prüfen.
- Persistenz nach Neustart sowie fehlende/beschädigte/überlange Metadaten und explizite Recovery auf einer isolierten Kopie des Testimages prüfen. Auch fehlschlagende Ressourcenoperationen und Wiederherstellung prüfen.
- Cosmos-Ausnahme-/finally-Pfade, Kennwortberechnung mit produktiver Iterationszahl, Sicherheits-Recovery-Build und Fehlerbehandlung im Gast verifizieren.
- Integration mit aktuellem master und dessen Anwendungen sowie Etappen 6a/6b steht aus. Dieser Branch enthält diese Zusammenführung ausdrücklich noch nicht; alle späteren Datei-Aufrufer müssen dieselben Rechte beachten.
- Unabhängiges finales Review einschließlich Sicherheits-/Fehlerpfaden und anschließende Wiederholung betroffener Prüfungen.
- QEMU zuerst; entsprechende VMware-Abläufe und echte Hardware sind nicht nachgewiesen.

Alle Etappe-6c-Abnahmekriterien bleiben bis zu diesen Nachweisen offen. Dieser Stand ist für Sicherung und Weiterarbeit bestimmt.
