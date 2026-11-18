# Bridge Split Messages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Lange Nachrichten vollstÃ¤ndig, geordnet und ohne Ã„nderung des Signaturvertrags weiterleiten.
**Architecture:** Bestehende Split-Helfer ergÃ¤nzen; vollstÃ¤ndige VorprÃ¼fung, Signatur je Teil und gemeinsamer Sendelock mit Abstand.
**Tech Stack:** Python3.13, asyncio, vorhandenes discord.py/GPG.
**Spec:** docs/superpowers/specs/2026-11-18-bridge-split-messages-design.md

## Global Constraints
- HÃ¶chstens 100 Teile, darÃ¼ber komplette Nachricht sichtbar ablehnen, niemals abschneiden.
- Mindestens 1 s Abstand, vollstÃ¤ndige signierte Zeilen vor Versand auf Bytebudget prÃ¼fen.
- `(i/n m=id)` bleibt signierter Bestandteil; bestehendes verify.sh und EmpfÃ¤ngerfreigabe-Vertrag erhalten.
- Keine AbhÃ¤ngigkeiten, Deployments, Secrets, Rechte oder Merges.

## Review Focus
- 99/100-Ãœbergang, UTF-8, lange EmpfÃ¤ngerprÃ¤fixe und Signatur-/Namensoverhead.
- Ganze Nachricht vor dem ersten Send prÃ¼fen; Verbindungsabbruch und gleichzeitige Nachrichten.
- Fehlende/vertauschte/doppelte/fremde Teile ergeben keinen vollstÃ¤ndigen Satz.
- Bereits signierte Texte niemals nachtrÃ¤glich zerlegen.
- Discord-Limits einschlieÃŸlich Nummerierung und Mentions.

## Task 1: Transport und Tests
- [x] Lokalen Agenten beauftragt; Timeout nach fÃ¼nf Minuten ohne Diff, verworfen. Roten Split-Test selbst belegt und Tests ergÃ¤nzt.
- [x] IRC-Helfer, Transport-VorprÃ¼fung/Sendelock und Discord-Teilausgabe minimal implementieren.
- [x] UTF-8-/Grenz-/Transport-/Signaturformat- und Fehlerfalltests sowie README aktualisieren.
- [x] Bridge-, Teambot-, Python-Suite und SyntaxprÃ¼fungen durchfÃ¼hren; bekannte Windows-Grenzen getrennt melden.
- [ ] UnabhÃ¤ngiges Review (pink und eigener Agent), Befunde beheben und betroffene Tests wiederholen.
- [ ] IRC prÃ¼fen, committen/pushen, origin-Hash prÃ¼fen, danach PR mit Reviewern Ã¶ffnen; kein Deploy/Merge.

## PrÃ¼fstand
Bridge- und Teambot-Selbsttest, drei echte GPG-Tests mit temporÃ¤rem TestschlÃ¼ssel, py_compile und diff --check bestanden. UnabhÃ¤ngiges Nachreview: kein offener P1/P2; P2 zu signierten inneren Zeilen erst reproduziert, dann fÃ¼r LF/CRLF/U+2028 behoben. Ganze Python-Suite: 287 Tests, 13 Fehler, 3 FehlschlÃ¤ge, 8 Ã¼bersprungen. Genau dieselben 13 Fehler und 3 FehlschlÃ¤ge im sauberen Basisstand c3bf341 in den betroffenen 40 Tests reproduziert (Bash fehlt im PATH, os.SEEK_DATA unter Windows fehlt, vorhandene Storage-Tests). Kein Livebetrieb und keine echten IRC-Flood-/Discord-Limits geprÃ¼ft.

Teamreview-Nachtrag: Discord-Anzeige wird immer bereinigt, veränderte signierte Anzeige verweist auf das IRC-Original; lange signierte Log-Zitate bleiben abgelehnt. Echte RSA2048-Signatur überschreitet das Budget und wird im Test vor jedem Send sichtbar abgelehnt. Selbsttest, drei GPG-Tests, py_compile und diff --check bestanden; unabhängiges Nachreview ohne P1/P2. AGENTS-Wortlaut liegt getrennt auf docs/agents-bridge-mehrteil zur freigegebenen Doku-PR-Erstellung.
