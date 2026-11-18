# Bridge Split Messages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Lange Nachrichten vollständig, geordnet und ohne Änderung des Signaturvertrags weiterleiten.
**Architecture:** Bestehende Split-Helfer ergänzen; vollständige Vorprüfung, Signatur je Teil und gemeinsamer Sendelock mit Abstand.
**Tech Stack:** Python3.13, asyncio, vorhandenes discord.py/GPG.
**Spec:** docs/superpowers/specs/2026-11-18-bridge-split-messages-design.md

## Global Constraints
- Höchstens 100 Teile, darüber komplette Nachricht sichtbar ablehnen, niemals abschneiden.
- Mindestens 1 s Abstand, vollständige signierte Zeilen vor Versand auf Bytebudget prüfen.
- `(i/n m=id)` bleibt signierter Bestandteil; bestehendes verify.sh und Empfängerfreigabe-Vertrag erhalten.
- Keine Abhängigkeiten, Deployments, Secrets, Rechte oder Merges.

## Review Focus
- 99/100-Übergang, UTF-8, lange Empfängerpräfixe und Signatur-/Namensoverhead.
- Ganze Nachricht vor dem ersten Send prüfen; Verbindungsabbruch und gleichzeitige Nachrichten.
- Fehlende/vertauschte/doppelte/fremde Teile ergeben keinen vollständigen Satz.
- Bereits signierte Texte niemals nachträglich zerlegen.
- Discord-Limits einschließlich Nummerierung und Mentions.

## Task 1: Transport und Tests
- [x] Lokalen Agenten beauftragt; Timeout nach fünf Minuten ohne Diff, verworfen. Roten Split-Test selbst belegt und Tests ergänzt.
- [x] IRC-Helfer, Transport-Vorprüfung/Sendelock und Discord-Teilausgabe minimal implementieren.
- [x] UTF-8-/Grenz-/Transport-/Signaturformat- und Fehlerfalltests sowie README aktualisieren.
- [x] Bridge-, Teambot-, Python-Suite und Syntaxprüfungen durchführen; bekannte Windows-Grenzen getrennt melden.
- [ ] Unabhängiges Review (pink und eigener Agent), Befunde beheben und betroffene Tests wiederholen.
- [ ] IRC prüfen, committen/pushen, origin-Hash prüfen, danach PR mit Reviewern öffnen; kein Deploy/Merge.

## Prüfstand
Bridge- und Teambot-Selbsttest, drei echte GPG-Tests mit temporärem Testschlüssel, py_compile und diff --check bestanden. Unabhängiges Nachreview: kein offener P1/P2; P2 zu signierten inneren Zeilen erst reproduziert, dann für LF/CRLF/U+2028 behoben. Ganze Python-Suite: 287 Tests, 13 Fehler, 3 Fehlschläge, 8 übersprungen. Genau dieselben 13 Fehler und 3 Fehlschläge im sauberen Basisstand c3bf341 in den betroffenen 40 Tests reproduziert (Bash fehlt im PATH, os.SEEK_DATA unter Windows fehlt, vorhandene Storage-Tests). Kein Livebetrieb und keine echten IRC-Flood-/Discord-Limits geprüft.

Teamreview-Nachtrag: Discord-Anzeige wird immer bereinigt, veränderte signierte Anzeige verweist auf das IRC-Original; lange signierte Log-Zitate bleiben abgelehnt. Echte RSA2048-Signatur überschreitet das Budget und wird im Test vor jedem Send sichtbar abgelehnt. Selbsttest, drei GPG-Tests, py_compile und diff --check bestanden; unabhängiges Nachreview ohne P1/P2. AGENTS-Wortlaut liegt getrennt auf docs/agents-bridge-mehrteil zur freigegebenen Doku-PR-Erstellung.
