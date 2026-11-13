# Etappe 6a – Fortsetzung am 13. November 2026

Der aktuelle Auftrag setzt die getrennten Branches `codex/etappe6a`, `codex/etappe6b` und `codex/etappe6c` fort und erlaubt nach Abschluss Pull Requests. Frühere Anweisungen zur reinen WIP-Sicherung sind damit überholt.

## Änderung

`codex/etappe6a` enthält die Round-Robin-Policy (20 ms), bis zu acht echte Hintergrundaufgaben und die gemeinsamen `tasks`-Shellbefehle. Der aktuelle master `565ca20` wurde integriert; Desktop, Editor, Dateibetrachter und Pong bleiben erhalten. Interruptmasken in Eingabe und Schedulerdiagnose werden explizit über `try/finally` freigegeben, damit die native Patching-Stufe keine `IDisposable`-Konvertierung benötigt. Kontextwechsel, Timer und Warteprimitive kommen unverändert von Cosmos 3.0.94; neue Abhängigkeiten oder Versionsupdates sind nicht nötig.

Abbruch verwendet kontrollierte Rückgaben. Cosmos 3.0.94 übersprang in der früheren Minimalprobe verschachtelte `finally`-Blöcke beim Exception-Unwind. Die Aufgabenverwaltung bereinigt Cancellation-Ressourcen deshalb nach ihren Catches auf einem normalen Fortsetzungspfad.

## Frische Prüfung

- SDK 10.0.112; `cosmos check` bestätigt die installierten Werkzeuge.
- 200 Hosttests, Release mit Warnungen als Fehler: bestanden.
- Nativer Threadprobe-Build: null Warnungen/Fehler.
- QEMU 10.2.2, q35, CPU max, 512 MiB, Standard-VGA, ohne Datenplatte: Round-Robin, zwei Worker ohne Yield, Sleep/Join/Ereignisse/Zeitlimits sowie Cancellation-/Fehlerbereinigung melden PASS.
- Interaktive QEMU-Probe: zwei Busy-Aufgaben wachsen, dritte Aufgabe wartet, Terminal und separate Textshell nehmen Befehle an; Esc/F4 erhält Aufgaben. Abbruch und erneuter Start funktionieren, Shutdown beendet aktive Aufgaben und die VM mit Exitcode 0.
- Separater nativer Recovery-Build: null Warnungen/Fehler; QEMU startet textuell ohne Threadprobe, öffnet den Desktop über F4 und führt dieselben Aufgaben-/Shell-/Shutdownprüfungen erfolgreich aus.
- Separater normaler Release-Build: null Warnungen/Fehler; QEMU öffnet zwei Terminals über den Starter und zeigt fortschreitende Aufgaben. Reboot mit aktiven Aufgaben erreicht in derselben VM erneut die Shell; danach nimmt ein neues Terminal `echo reboot-alive` an. Shutdown beendet die VM mit Exitcode 0. Normal- und Recovery-Logs enthalten keine Threadprobe.
- Unabhängiges lesendes Agentenreview: keine konkreten Codebefunde; zwei veraltete Dokumentationsaussagen korrigiert.

Die erste interaktive Testausführung gab Befehle auf dem leeren Desktop ein; sie zählt nicht als Aufgabenprüfung. Der erfolgreiche Wiederholungslauf öffnete zuerst über Alt+F1 ein Terminal.

## Grenzen

Die früheren VMware-Nachweise sind historisch; eine frische VMware-Prüfung ist mangels installierter VMware-Werkzeuge offen. Echte Hardware und Dateipersistenz wurden nicht neu geprüft. PowerShell fehlt; der PowerShell-Energiehelfer kann hier nicht ausgeführt werden. Die aktuelle QEMU-Probe verwendet dieselben Energieoptionen und prüft Shutdown mit aktiven Aufgaben. Der allgemeine Cosmos-Unwinder wurde nicht verändert. Rechenarbeit bleibt im gemeinsamen Kernel; dies schafft keine Prozessisolation. Dateioperationen bleiben im Vordergrund synchron.

Etappe 6b ergänzt Nachrichten und deren Abbruchpfade, 6c die Konten-/Rechteintegration; diese Funktionen gehören nicht zum 6a-PR.
