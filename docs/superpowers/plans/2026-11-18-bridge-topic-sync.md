# Bridge Topic Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** IRC-Topics einschließlich Startzustand als Discord-Kanalbeschreibung spiegeln.
**Architecture:** Neuester Solltext pro Kanal, entprellter Hintergrundlauf, begrenzte API-Versuche. Vorhandene Discord-/IRC-Anbindung verwenden.
**Tech Stack:** Python, asyncio, bestehendes discord.py 2.6.4.
**Spec:** docs/superpowers/specs/2026-11-18-bridge-topic-sync-design.md

## Global Constraints
- Kein Deploy, keine Rechte-/Secret- oder Abhängigkeitsänderung.
- Maximal 1024 Zeichen; zwei Änderungsversuche je 600 Sekunden und Kanal.
- IRC-PING und Nachrichten dürfen nicht auf Topic-Updates warten.

## Review Focus
- Neues Topic während eines laufenden API-Aufrufs erhalten.
- 331/332 beim Reconnect und leere Topics abgleichen.
- Fehlende Berechtigung und HTTP-Fehler isolieren.
- Mehrere Kanäle und wiederholte gleiche Topics unterscheiden.
- Aufgaben beim Beenden abbrechen.

## Task 1: Topic-Abgleich
- [x] Lokalen Agenten klar begrenzte Tests in test_bridge.py ergänzen lassen; Bereinigung/Sicherheitslogik selbst implementieren.
- [x] Fehlende Topic-Schnittstelle im Test als roten Ausgang belegen.
- [x] bridge.py minimal erweitern: Bereinigung, Queue, Hintergrundlauf, TOPIC/331/332, Task-Abbruch.
- [x] Gesamten Bridge-Selbsttest, Python-Tests und Syntaxprüfung ausführen; README aktualisieren.
- [x] Fertigen Diff unabhängig lesend prüfen lassen, Befunde bewerten und beheben.
- [ ] IRC prüfen, Commit pushen, Remote-Hash prüfen und PR mit Reviewern erstellen; kein Merge.

## Prüfstand
Bridge-Selbsttest einschließlich Startabfrage/331/332, Queue-Überholung, Rate-Limit, API-Fehler und Task-Abbruch bestanden; Teambot-Selbsttest, py_compile und diff --check bestanden. Unabhängiges Nachreview: keine offenen P1/P2/P3. Gesamte Python-Suite: 240 Tests, 4 Fehler, 6 übersprungen (drei Windows-Fehler wegen fehlendem os.SEEK_DATA, ein isoliert reproduzierter Release-Shelltimeout nach 30 Sekunden). Kein echter Discord-Betrieb und kein Deployment geprüft. Der lokale Agent lieferte ungenutzte Hilfsfunktionen statt Tests; sein Diff wurde nach Prüfung verworfen.
