# Paketmanager-Komfort Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Standardquelle, automatischer Katalog und sichtbarer Fortschritt; Katalogwartezeit messen und beheben.
**Architecture:** Bestehenden PackageStore und HTTPS-Transport ergänzen; Shell-Integration auf feature/netzwerk-automatik abstimmen.
**Tech Stack:** C#/.NET 10, Cosmos 3.0.94, vorhandenes BouncyCastle und MSTest.
**Spec:** ../specs/2026-11-16-pkg-komfort-design.md

## Global Constraints
Keine Abhängigkeitsupdates, keine Veröffentlichung/Merges, deutsche Kommentare, vorhandene Quellen und Trustprüfungen erhalten.

## Review Focus
Eigene Quelle bleibt erhalten; fehlerhafter Katalog verhindert Installation; Abbruch gibt Schreibhandles frei; Fortschritt bedeutet vor Abschluss keine erfolgreiche Installation; Worker schreiben nicht in die Anzeige.

## Task 1: Store und Fortschritt
- [x] DefaultSource-Test und Fortschritts-/Fehlerfalltests in PackageTests schreiben und rot ausführen.
- [x] PackageStore.ReadSource liefert DefaultSource bei fehlender Datei. Install bekommt optional Action<PackageProgress>; Manifest-/Prüfphase und HashingStream melden Fortschritt.
- [x] Tests grün; Shell-Integration mit bestehendem Agenten abstimmen.

## Task 2: Ladezeit
- [ ] Server und Gast getrennt messen: DNS, TCP, Trustbundle, TLS, Body, Katalogprüfung.
- [ ] Belegte Ursache minimal beheben; Regression und Vorher/Nachher vergleichen.

## Task 3: Integration und Abschluss
- [x] Shell-Integration übernehmen oder nach Freigabe selbst ergänzen; automatischer Katalog und Pacman-artige Anzeige testen.
- [ ] Hosttests Release -warnaserror; nativer Build und Gastablauf; VMware offen benennen falls nicht ausführbar.
- [ ] README und Diagnosebericht aktualisieren, Version gegenüber Zielbranch erhöhen, unabhängiges Review, Push und PR ohne Merge.

## Ledger
- Servermessung: catalog 204 Bytes 0,217s, Signatur 64 Bytes 0,179s (Windows curl, 16.11.2026). QEMU/TCG: pkg update 35 s beim ersten, 30 s beim zweiten Abruf (cbb27c9-Normalbuild, statische IPv4, isoliertes Datenimage). Belege: .tools/pkg-komfort/baseline-1794830751.
- 553 Hosttests bestanden, 1 Symlink-Test übersprungen; nativer Releasebuild cbb27c9 erfolgreich. Nach Aufbau auf a42df56: 32 Paket-/Shelltests bestanden; Statuszeilenbaustein: 10 Terminaltests bestanden.
- 89683d1: Statuszeilen ersetzen nur eigene vorherige Zeilen; Ringpuffer, Umbruch, Reflow, Clear und fremde Ausgabe geprüft. Nicht im fertigen Paket-Polling integriert.
- Unabhängiger Zwischenreview: unerwartete Schreibfehler konnten in DirectoryPackageSource.Copy das Quellhandle offen lassen. Erst reproduziert, dann catch zur Freigabe vor jeder erneuten Ausnahme erweitert; Regression grün. Nichtwerfen-Vereinbarung des internen Progress-Empfängers dokumentiert; produktiver Empfänger darf nur Zustand speichern.
- Benutzer 16.11.2026: Orange übernimmt die weitere Ladezeitprüfung; eigene temporäre Instrumentierung entfernt (kein gültiger Diagnosebuild erzeugt). Abschlussreview ausdrücklich durch einen Agenten aus IRC. Kein Abschlussreview/PR vor fertigem Stand.- Folgebranch auf 5146421 (Netzwerk-Automatik) aufgebaut; ausdrückliche IRC-Freigabe PackageShellCommands/ShellService am 16.11.2026 12:38 UTC. ShellJob nicht verändert. Netzwerk-PR #101 hat inzwischen Kopf ed93f73; gegenüber 5146421 nur Prüfdokumentation.
- Version 0.4.1, Standardquelle und Fortschrittsanbindung implementiert. Voller Hostlauf: 580 bestanden, 1 Symlink-Test übersprungen, 0 Fehler. Nach Entfernen der unerreichbaren Keine-Quelle-Abfrage nochmals 46 gezielte Tests grün. Gegenprobe ohne Generation-Schutz erkennt Identitätsleck (1/2 rot), mit Schutz grün.
- Normalbuild und Recoverybuild erfolgreich; Recovery-MSBuild-Zusammenfassung 0 Warnungen/0 Fehler. QEMU/TCG, isolierte Plattenkopien: Textshell installiert Doom 1.1.0 ohne source/update; Statuszeile bis100%, Abschluss und begonnene Eingabe erhalten, F4-Desktop. Desktop-Terminal separat ebenfalls bis Installation100% und Echo des erhaltenen Entwurfs geprüft. Recovery: Anmeldung, Quelle, Liste, Echo geprüft. Belege lokal unter .tools/pkg-komfort/functional-1794833136, terminal-1794833524 und recovery-1794833373. Kein VMware-/Hardware-Nachweis.
- Oranges Zwischenbefund 12:28 UTC: Gate0ms, DNS12–116ms, TCP55–170ms, LoadAnchors~65ms, TLS-Kette~1.1s/Handshake~1.35s, Body sofort. Viele Abrufe gesamt~11.5s, davon~10s NACH Body. Dispose-vs-Close noch nicht getrennt. Messlauf wegen belegter Fixture-Ports abgebrochen, danach andere Sitzung pausiert. Keine Performancekorrektur implementiert.
- Benutzer wünscht jetzt Übergabe an einen IRC-Agenten; Koordinator soll Restarbeit verteilen. Verteilbare Übergabe: 2026-11-16-pkg-komfort-handoff.md. Kein eigener PR und kein Merge vor Performancekorrektur und abschließendem unabhängigen IRC-Review.