# Paketmanager-Komfort – Entwurf

Am 16.11.2026 vom Benutzer bestätigt: https://packages.pjoter.de/ als Standardquelle, automatische Paketliste vor Installation/Upgrade, Livefortschritt mit Datei, Bytes/Prozent und Prüfphase. Apps werden nur auf ausdrücklichen Upgrade-Befehl aktualisiert.

## Ansätze
Vorhandenen PackageStore und Hintergrundaufträge erweitern (gewählt): kleinste Änderung, Signatur- und Rückstufungsschutz bleiben erhalten. Ein zweiter Paketdienst oder neue Abhängigkeiten bringen hierfür keinen Nutzen. Worker melden Fortschritt; ausschließlich die Ereignisschleife schreibt ins Terminal.

## Grenzen
Keine Paketveröffentlichung, kein Merge, keine Abhängigkeitsupdates. Bestehende Quellen erhalten. Serverwartezeit gegen Gastmessung abgrenzen; Optimierungen erst nach Messung. Gasttests verwenden isolierte Images. Parallel bearbeitet feature/netzwerk-automatik Shell-Polling und automatische Katalogaktualisierung; Schnittstelle über IRC abstimmen und zusammen prüfen.
