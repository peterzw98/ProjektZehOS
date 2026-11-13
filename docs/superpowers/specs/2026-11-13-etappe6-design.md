# Etappe 6a bis 6c vollständig

## Auftrag und Architektur

Der Nutzer beauftragt ausdrücklich die gesamte Gruppe 6a–6c und getrennte Worktrees für unabhängig bearbeitbare Teile. Die bereits auf master integrierte Threadprobe aus 6a.1 bleibt die geprüfte Grundlage. Implementierung und übliche Entscheidungen erfolgen selbstständig; Versionsupdates benötigen weiterhin Zustimmung.

6a verwendet echte Cosmos-Threads mit einer eigenen Round-Robin-Policy über die öffentliche experimentelle Schnittstelle. Cosmos behält Kontextwechsel und Timer. Eine begrenzte Aufgabenverwaltung bietet Kennung, Zustandsanzeige, Fortschritt, kooperativen Abbruch und Bereinigung. Zeichen-/Eingabe-/Dateidienste verbleiben im Vordergrund; Worker erhalten nur kontrollierte Zustände. Scheduler-Hooks blockieren und allokieren nicht im Tickpfad; Auswahlereignisse landen in einem vorab angelegten Ringpuffer und werden erst im Vordergrund formatiert.

6b verwendet eine begrenzte FIFO mit explizitem Voll-/Leer-/Geschlossen-Ergebnis sowie wartenden Operationen mit Abbruch und Zeitlimit. Ein geschützter Zustand und signalisierte Weckvorgänge vermeiden aktives Abfragen/verlorene Signale. Eine Erzeuger-Verbraucher-Demo verwendet die Aufgabenverwaltung aus 6a. Bei mehreren Sperren gilt eine dokumentierte feste Reihenfolge.

6c erweitert jeden FileService um eine sitzungsgebundene Identität. Zentral geprüfte kanonische Pfade und persistente Eigentümer-/Lese-/Schreibrechte schützen auch Verzeichnisse und den sicheren Ersetzungspfad. Konten-/Rechtefehler sperren Zugriffe. Konten besitzen stabile IDs und Administrator-/Benutzerrollen; Anmeldung und Benutzerwechsel beeinflussen nur die eigene Sitzung. Für Kennwörter werden ausschließlich geprüfte gesalzene KDFs mit im Gast geprüfter sicherer Zufallsquelle verwendet. Die Metadaten werden getrennt auf FAT32 gesichert, vorhandene Dateien ausdrücklich zugeordnet, administrativer Recovery-Zugriff ausdrücklich gestartet. Gemeinsamer Kernel bietet weiterhin keine Isolation gegen fremden Kernelcode.

## Alternativen und Grenzen

Kooperative Update-Verteilung erfüllt das Thread-Scheduling-Ziel nicht. Eine eigene Kontextwechsel-/Kryptografieimplementierung wird vermieden; vorhandene Cosmos-/Plattformfunktionen werden zuerst geprüft. Ungeprüfte Dateioperationen wandern nicht in Worker. Fehlende sichere Kryptografie wird als echter Blocker behandelt, nicht durch schwache Hashes oder Zeitstempel-Zufall ersetzt.

## Zusammenführung und Nachweise

Drei Worktrees/Branches entwickeln 6a, 6b und 6c. Der 6b-Kern und 6c können auf master parallel entstehen; die 6b-Demo erhält anschließend die fertige 6a-API. Änderungen an Kernel/Shell werden kontrolliert zusammengeführt. Pro Teil: Hosttests mit Warnungen als Fehler, native Builds, Gastprüfungen in QEMU/VMware, unabhängiges Review. Abschließend kombinierter Boot/Recovery, aktive Aufgaben während Terminaleingaben, Queuefehler und geordnetes Ende sowie Benutzertrennung/Persistenz/Fehlerfälle auf Imagekopien. Roadmap-Haken erst nach tatsächlichem Nachweis; kein automatischer Merge nach GitHub.
