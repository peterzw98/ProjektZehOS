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
- [ ] DefaultSource-Test und Fortschritts-/Fehlerfalltests in PackageTests schreiben und rot ausführen.
- [ ] PackageStore.ReadSource liefert DefaultSource bei fehlender Datei. Install bekommt optional Action<PackageProgress>; Manifest-/Prüfphase und HashingStream melden Fortschritt.
- [ ] Tests grün; Shell-Integration mit bestehendem Agenten abstimmen.

## Task 2: Ladezeit
- [ ] Server und Gast getrennt messen: DNS, TCP, Trustbundle, TLS, Body, Katalogprüfung.
- [ ] Belegte Ursache minimal beheben; Regression und Vorher/Nachher vergleichen.

## Task 3: Integration und Abschluss
- [ ] Shell-Integration übernehmen oder nach Freigabe selbst ergänzen; automatischer Katalog und Pacman-artige Anzeige testen.
- [ ] Hosttests Release -warnaserror; nativer Build und Gastablauf; VMware offen benennen falls nicht ausführbar.
- [ ] README und Diagnosebericht aktualisieren, Version gegenüber Zielbranch erhöhen, unabhängiges Review, Push und PR ohne Merge.

## Ledger
- Servermessung: catalog 204 Bytes 0,217s, Signatur 64 Bytes 0,179s (Windows curl, 16.11.2026). Gast noch nicht gemessen.
