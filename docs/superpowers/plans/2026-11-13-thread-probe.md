# Cosmos-Threadprobe Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Roadmap 6a.1 mit einem wiederholbaren Gastversuch prüfen.

**Architecture:** Opt-in-Buildprobe vor der bestehenden interaktiven Sitzung. Cosmos übernimmt Threads, Kontextwechsel und Warten; der Host überwacht den begrenzten Gastversuch.

**Tech Stack:** C#, .NET 10, Cosmos 3.0.94, PowerShell, QEMU.

**Spec:** `docs/superpowers/specs/2026-11-13-thread-probe-design.md`

## Global Constraints

- Keine Versionsupdates, keine Datenimages, kein Schedulerwechsel im normalen Desktop.
- Experimentelle COSMOS0001-Verwendung nur lokal in der Probe bestätigen.
- Fehlgeschlagene oder fehlende Gastprüfungen ausdrücklich dokumentieren.

## Review Focus

- Busy-Loops ohne Yield: echter Fortschritt beider Threads und des Hauptthreads.
- Ereignis bereits gesetzt: kein verlorenes Signal.
- Nicht gesetztes Ereignis: begrenzter Timeout statt Dauerwarten.
- Fehlerpfad: Stopflag setzen und beide Threads begrenzt joinen.
- Normal-/Recovery-Build: keine Probe starten.

## Aufgabe 1: Isolierte Probe und wiederholbarer Gasttest

Dateien: `ProjektZeh/Runtime/CosmosThreadProbe.cs`, `ProjektZeh/Kernel.cs`, `ProjektZeh/ProjektZeh.csproj`, `scripts/test-thread-probe.ps1`.

- [x] Testhelfer zuerst erstellen: ein normales ISO muss wegen fehlendem `THREAD_PROBE PASS` fehlschlagen.
- [x] Probe mit `Run()` ergänzen: Schedulerbereitschaft/-name protokollieren; zwei Worker ohne Yield zählen lassen; Hauptthread schläft; Zählerfortschritt vor Abbruch prüfen; Join, Sleep, Signal vor/nach Warten und Timeout prüfen.
- [x] Native Probe bauen und denselben Helfer erfolgreich ausführen; Desktop und Shell im Probe-Gast bedienen.
- [x] Normalen Build und Recovery bauen und starten; bestehende Hosttests ausführen.
- [x] README, Cosmos-Unterstützung und Roadmap anhand tatsächlicher Ergebnisse aktualisieren.
- [x] Unabhängiges Review von Implementierung und Ergebnisdokumentation: keine konkreten Befunde.

Abschluss: Den geprüften Stand committen, pushen und einen PR gegen master erstellen. Der tatsächliche Veröffentlichungsstand wird im Projektarbeitsstand geführt.
