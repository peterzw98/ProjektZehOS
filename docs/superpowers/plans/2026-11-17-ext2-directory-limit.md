# ext2-Verzeichnisgrenze Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** FileService meldet die ext2-Grenze und Migration verweigert uebergrosse Quellverzeichnisse vor Schreibzugriffen.
**Architecture:** Optionaler FileService-Callback wie diskSpace, separater Cosmos-Adapter, reine Zaehlung in MigrationPlan. Keine Aenderung fremder Dateisysteme.
**Tech Stack:** Bestehendes C#/.NET 10, Cosmos 3.0.97, MSTest.
**Spec:** ../specs/2026-11-17-ext2-directory-limit.md

## Global Constraints

- 8190 direkte Kinder je ext2-Verzeichnis; keine Paket-/SDK-/Werkzeugupdates.
- Bestehende Daten, ACLs und Wiederherstellung erhalten; Gasttests nur auf isolierten Images.
- Kommentartexte Deutsch; produktiven Stil und Zeilenenden erhalten.

## Review Focus

- Volles Verzeichnis: vorhandene Touch-/mkdir-Ziele bleiben bedienbar.
- Temporaere Schreibdatei: Fehler veraendert die Originaldatei nicht und hinterlaesst keinen Rest.
- Gleicher Elternordner: Move/Rename bleibt trotz voller Kapazitaet moeglich.
- Fremder Mount: FAT bzw. innerer FAT-Mount erbt keine ext2-Grenze.
- Migration: Grenzkonflikt entsteht vor Marker-, Journal- und sonstigen Zielschreibzugriffen.

## Schritte

- [x] FileService-Grenztests zuerst rot ausfuehren; optionalen Callback und Pruefungen an allen Erzeugungspfaden implementieren; Tests gruen.
- [x] Migrationstests fuer 8190/8191 und getrennte Ordner zuerst rot, dann Quellzaehlung implementieren; Ziel bleibt bei Konflikt unangetastet.
- [x] Cosmos-Adapter und produktive Callback-Weitergabe; nativer Build und isolierte Gastpruefung.
- [ ] Bediengrenze dokumentieren, vollstaendige Hosttests und unabhaengiges Review, Version/PR nach aktueller Zielversion.

## Arbeitsstand

Lokale Testdelegation versuchte OpenCode/Ollama, scheiterte vor einer Aenderung mit ECONNREFUSED. Architektur und hardwarebezogene Quellpruefung bleiben beim Hauptagenten. Umsetzung und Tests erfolgen deshalb hier.

13 gezielte Tests gruen nach den roten FileService-/Migrationsproben. Unabhaengiger Review fand eine Zielverdichtung durch die Desktop-Konfiguration; der zusaetzliche Regressionstest reproduzierte das rot. Zielzaehlung ergaenzt, Nachreview ohne offene Befunde. Vollstaendige Hosttests: 1041 bestanden, 3 plattformbedingt uebersprungen. Nach master-Integration 40 gezielte Datei-/Migrations-/Konten-/TLS-Tests bestanden; normaler nativer Release-Build sowie Storage-Probe-Build erfolgreich (je 0 Warnungen/Fehler). QEMU-TCG auf neuen isolierten 72/64-MiB-Images: 20+9 PASS, Nur-Lese-Image bitgleich. Gastgrenze 2 prueft den Pfad, nicht einen echten 8190-Dateien-Bestand. e2fsck, VMware und Hardware offen.

Abschlussreview durch peter-review auf 9ba5b1a: kein P1/P2. P3: Zaehlkosten O(n) je Anlegen (kein unsicherer Cache eingefuehrt; grosser Gast-Messlauf offen), kosmetischer Schraegstrich an der Quellwurzel bleibt, Geltungsbereich FileService in README ausdruecklich ergaenzt. Metadaten-Callback und MutationBlockedReason nach master-Merge erhalten. Reservierte Version 0.5.48.
