# Abschlussreview der Desktop-Einstellungen

Stand: 14. November 2026. Fortsetzung von Claudes Stand `817d866` auf `codex/desktop-settings`, integriert mit `origin/master` bei `62b064c`.

## Korrekturen

- Die neue Anmeldung, Ersteinrichtung und Befehlsrechte aus master bleiben erhalten. Fenster und Terminals enden beim Identitätswechsel. Einstellungen werden danach mit einem neuen Dateidienst im richtigen Benutzerverzeichnis geladen; der gesperrte Bildschirm zeigt keinen privaten Hintergrund.
- Personalisierung speichert keine erfundene Standardauflösung mehr. Die Konfiguration Version 2 speichert Auflösungen nur mit ausdrücklicher Bestätigung. Alte Dateien ohne Bestätigungsmerkmal übernehmen ausschließlich Personalisierung. Gespeicherte Modi werden beim ersten Desktop-Update nach Anmeldung mit tatsächlicher Uhrzeit, sichtbarer Einstellungsapp und 15 Sekunden Frist angewendet.
- Fehlerhafte Rückkehr endet nach einem Versuch; der Controller liest die tatsächlich aktive Größe. Beim Verlassen mit Esc/F4 wird eine offene Wahl sofort zurückgenommen. Auch eine Treiberausnahme dabei führt weiter zur Textshell und wird diagnostiziert.
- Die Auflösungsliste wählt den aktiven Modus vor und folgt erfolgreichen Änderungen. Ein Kommentar erklärt die Neuberechnung der Textkonsolengeometrie über den Cosmos-Fontsetter.
- 32-Bit-BMPs mit Farbmasken und V4-/V5-Kopf werden unterstützt. Getrennte 8-Bit-RGB-Masken werden geprüft; überlappende, fehlende oder unvollständige Masken werden abgewiesen. Alpha wird für den deckenden Hintergrund ignoriert. Vor dem Binärlesen gelten dieselben Dateirechte wie beim Textlesen.
- Der Taskmanager bewahrt die Auswahl per Fensteridentität und aktualisiert vor Aktionen. Geschlossene Fenster werden nicht durch andere Zeilen ersetzt. Reguläre Schließrückfragen bleiben erhalten.
- Das globale Fensterlimit blockiert weder Abmelden noch das Aktivieren bereits vorhandener Einstellungen/Taskmanager. Neue Fenster prüfen weiterhin ihre Kapazität.
- Die beim Übernehmen vorgefundenen lokalen Korrekturen bleiben erhalten: Freigabe temporärer Energieshells und Textshell-Recovery bei Eingabe-/Update-/Renderfehlern.

## Prüfungen

- 277 Hosttests mit Release und Warnungen als Fehler bestanden. Die gemeldeten Fehler und zusätzlichen Reviewbefunde wurden mit Regressionstests zuerst reproduziert und anschließend behoben.
- Normale und Recovery-NativeAOT-Release-Builds mit unverändertem SDK 10.0.403/Cosmos 3.0.94: jeweils 0 Warnungen und 0 Fehler.
- Zwei unabhängige lesende Reviews; zusätzlich gezielte Nachprüfung der Rücknahme-/Textshell-Fehlerbehandlung. Abschließend keine offenen Befunde.
- QEMU q35, CPU max, 512 MiB, Standard-VGA: Full-HD-Start mit 1920×1080, Ersteinrichtung, Anmeldung, Einstellungen, Hellmodus und Speichern im Benutzerhome geprüft. Die gespeicherte Datei enthält nach reiner Personalisierung keine Auflösung. Taskmanager mit Speicher, Laufzeit und Bildrate sichtbar; bei fortlaufender Mausbewegung zeigte die Stichprobe 57 FPS, längstes Bild 29 ms. Das ist eine aktuelle Einzelmessung unter Hostlast, keine Garantie von 60 FPS.
- Esc/F4 zwischen Desktop und Textshell geprüft. Nach Logout sind Fenster geschlossen und der Anmeldeschirm neutral. Normal- und Recovery-Proben verwenden ausschließlich eine eigene Kopie des FAT32-Images; SHA256 des Originals bleibt `81BDA458C2B40BF139CED8B767C6B82BB11DFE264C27967C75DD139FDB523576`.
- Recovery startet nachweislich in der Textshell; Anmeldung, `echo recovery-ok`, F4 zum Desktop mit erneut geladener heller Personalisierung und anschließender regulärer Shutdown funktionieren. Abschließend ist der normale Kernel wieder als Standard-Buildausgabe vorhanden.

## Grenzen

Laufzeit-Auflösungswechsel einschließlich echter Treiberfehler wurden auf dem Host mit einem simulierten Display geprüft. QEMUs Standard-VGA meldet nur den aktuellen Limine-Modus; das ist kein Gastnachweis für VMware-SVGA-Moduswechsel. Ein vollständiger interaktiver VMware-Test und echte Hardware bleiben offen. Das Taskmanagerdiagramm misst Desktoparbeit, keine gesamte System-CPU. Keine SDK-, Cosmos- oder Paketaktualisierung, kein Merge des Pull Requests.

BMP-Referenz: [Microsoft BITMAPV5HEADER](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-bitmapv5header).
