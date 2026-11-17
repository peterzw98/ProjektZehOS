# Root-Migration Shell und Cosmos Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Der freigegebene Ablauf `system migrate <Ziel> --yes` bindet E4.4 sicher an Cosmos an und bleibt bis zum 4.0-Go hinter `ZehOSRootMigration=true`.

**Architecture:** Vorhandene LayoutMigration und ShellJob/BackgroundTaskService wiederverwenden. Ein kurzer Lease-Zaehler sperrt alle Quellschreiber exklusiv fuer die Migration, ohne Lesen oder Oberflaeche zu blockieren. Ein dateibasierter IMigrationTarget ist auf dem Host pruefbar; Cosmos uebernimmt Plattenidentitaet, schreibfreie Vorpruefung, Mount und Sync.

**Tech Stack:** C#/.NET 10, Cosmos 3.0.97, vorhandenes MSTest und BouncyCastle; keine Paket-/SDK-/Werkzeugupdates.

**Spec:** ../specs/2026-11-17-storage-etappe4-design.md, Abschnitt 3; Koordinator-Zuteilung vom 17.11.2026, 18:11.

## Global Constraints

- Quelle `/data` bleibt unveraendert und bootfaehig. Keine Formatierung und keine GPT-Aenderung.
- Nur Administrator und Legacy-Profil; ausdrueckliches `--yes`. Root-Migrationsbefehl im normalen Build nicht registrieren.
- Ziel: andere, nicht eingebundene Platte mit geprueftem 7a-Layout, Healthy-Identitaet, ext2-Gate Ok und passendem Root-Marker fresh/migrating. Keine Ready-Ziele.
- ESP und Root vor dem ersten RW-Mount wirklich schreibfrei pruefen; Cosmos-Sync/Drop/Release koennen trotz Lese-Mount schreiben. Vorhandenen RO-Schutz wiederverwenden.
- 8190-Verzeichnisgrenze bleibt. Kein Parser-Plug, IL-Patch oder Upstream-Issue.
- Kein G4-G6-Gastlauf ohne 4.0-Go; fehlende Gastabnahme und VMware explizit offen halten. Nur isolierte Testimages.
- Kommentare Deutsch; fremde Aenderungen erhalten. Kernel-Verdrahtung mit parallel laufendem 4.3-Auftrag abstimmen.

## Review Focus

- Bereits laufender Schreiber gegen gleichzeitig beginnende Migration: keine TOCTOU-Luecke, sofortige Ablehnung statt UI-Warten.
- Gesperrter Konten-Schreibversuch darf die angemeldeten Sitzungen nicht ueber AccountService.Save/Lock abmelden.
- Quelle/Ziel identisch, fremder Mount, Symlink, defekte Marker/GUID oder Schreibversuch eines RO-Treibers: vor Zielschreiben verweigern.
- Abbruch beim Hashen/Kopieren/Wiederholen: eigene Tempdateien und Journal bleiben wiederholbar, Marker nie faelschlich ready.
- Abbruch nach dem Commit von ready ist kein rueckwirkender Abbruch; Synchronisierung abschliessen und Erfolg melden.

## 1. Quell-Schreibgate

**Dateien:** `Storage/Migration/MigrationWriteGate.cs`, `ProjektZeh.Tests/MigrationWriteGateTests.cs`.

- [ ] Rote Tests fuer mehrere Schreib-Leases, exklusives TryLockSource, abgelehnte neue Schreiber, Freigabe/idempotentes Dispose und gleichzeitige Rennen schreiben und ausfuehren.
- [ ] Konkrete API: `IDisposable? TryBeginWrite()`, `IDisposable? TryLockSource()`, `bool IsLocked`. Kurzer lock nur um Zustand/Zaehler; kein Monitor ueber Dateioperationen halten.
- [ ] Gruen pruefen und kleinen Commit fuer Jensen bereitstellen. `/save` verwendet denselben Lease (EBUSY bei null); kein eigener Sperrzustand.

## 2. Alle Quellschreiber anschliessen

**Dateien:** FileService, FileAccessControl/AccountService, PackageStore, FileUpdateTarget/UpdateController, CosmosStorageService, deren Tests; Kernel erst nach Abstimmung.

- [ ] Rote Verhaltenspruefungen: Lesen und Anmeldung gehen, FileService-Mutationen/ACLs/Metadaten/Paketmutationen/Update/Desktopeinstellungen scheitern unveraendert bei Sperre. Ein vor der Sperre begonnener Schreiber verhindert TryLockSource.
- [ ] Optionalen `Func<IDisposable?> tryBeginWrite` an den vorhandenen Grenzen weiterreichen. Fehlender Callback erhaelt das bisherige Verhalten. Den Lease ueber die ganze Mutation einschliesslich Temp/Backup/ACL-Persist halten.
- [ ] Konten-Apply-Callbacks vor jeder Speicheraenderung mit Lease schuetzen; erwartete Sperre vor Save verhindern, statt Save/Lock als Sperrmechanismus zu verwenden.
- [ ] Laufwerkswechsel/Aushaengen/Formatieren und Energieaktionen waehrend Migration ablehnen. Reine Leseabfragen bleiben nutzbar.
- [ ] Fokuspruefungen gruen; keine neue Rechte- oder Quotenverwaltung.

## 3. Abbruch und dateibasierter Zieladapter

**Dateien:** IMigrationTarget, LayoutMigration, MigrationInventory, neue `FileMigrationTarget.cs`, gegebenenfalls kleiner wiederverwendbarer CancellationReadStream; Migrationstests.

- [ ] Rote Tests: Abbruch bei Inventarhash/Kopierstrom/Verify, Quelllease immer frei, kein ready vor vollstaendigem Erfolg; spaeter Abbruch nach ready ergibt keinen falschen Fehler.
- [ ] Optionalen CancellationToken an vorhandene E4.4-APIs anhaengen; pro Datei/Leseblock pruefen, nicht nur zwischen Gesamtphasen. Letzte Ready-Commitphase ohne Abbruchfenster abschliessen.
- [ ] FileMigrationTarget bildet normalisierte Kernelpfade auf einen festen Mount ab, verwendet injizierten Pfadwaechter/StatFs/Sync, schreibt ueber den vereinbarten Tempnamen und sichert Fehler-/Wiederholungsfaelle ab. Keine Fremdpfade, Symlinks oder Mountgrenzen zulassen.
- [ ] Journalabsicht mit `flush=true` muss vor Mutation dauerhaft sein. Bytes und Inodes ueber passende VFS-Statistik liefern; erwartete IO-Fehler als vorhandene Result-Typen.
- [ ] Marker-Ersetzen/Temp-Reste/Wiederholung vor Cosmos-Anschluss unabhaengig pruefen lassen; keine neue Stromausfallgarantie behaupten.

## 4. Shell und Eingabe

**Dateien:** `Storage/Migration/IMigrationService.cs`, `Shell/MigrationShellCommands.cs`, ShellService, TerminalApp, InteractiveSession und zugehoerige Tests.

- [ ] Reiner Vertrag: `ResultObject<MigrationReport> Run(string diskName, Action<string> log, CancellationToken cancellation)`.
- [ ] Abgegrenzte Syntax-/Dispatchtests lokal delegieren; Diff/Testergebnis selbst pruefen. Sicherheit, Architektur und Cosmos nicht delegieren.
- [ ] Syntax ohne `--yes`, zusaetzliche Argumente, nicht-Admin/Root und doppelte Auftraege zuerst rot; `MigrationShellCommands(service,tasks,isAdministrator,isLegacy,...)` wie NetworkShellCommands auf ShellJob aufbauen.
- [ ] `system cancel` und Strg+C in derselben Shell fordern denselben Tokenabbruch an; F4/Escape und bestehendes Kopieren mit Strg+Shift+C erhalten.
- [ ] Fortschritt/Log nur durch die Ereignisschleife anzeigen, Hintergrundthread schreibt keine UI. Ergebnis/Abbruch je Sitzung, Logout/Fensterschliessen beendet den Auftrag und gibt Sperren frei.

## 5. Cosmos-Anbindung hinter Buildflag

**Dateien:** neue Cosmos-Adapter ausserhalb des host-eingebundenen Migration-Globs, Projektdatei, Kernel; vorhandene RO-Komponente nur gezielt wiederverwenden.

- [ ] Alle Partitionen gegen gepruefte GPT/GUID/LBA vergleichen, Quelle und bereits gemountete Zielplatte ausschliessen. Vorpruefung liest ausschliesslich; dafuer vorhandene RO-Geraete-/VFS-Schutzschicht auch fuer FAT bewerten.
- [ ] Erst nach erfolgreicher RO-Pruefung Ziel-Root RW unter `/mnt/target` mounten; Source-Gate halten, LayoutMigration ausfuehren, Sync/Unmount in finally. Unmountfehler nicht verschlucken oder faelschlich als geschlossen markieren.
- [ ] Kernel teilt eine Gate-Instanz an alle Schreiber. Mit Senior InitializeServices/SystemPaths und Jensen `/save` abstimmen; parallele Dateien erst nach deren aktuellem Commit integrieren.
- [ ] `ZehOSRootMigration=true` definiert ZEHOS_ROOT_MIGRATION; normaler Build behaelt seine Befehle und sein Profil. Kein Root-Go durch das Flag behaupten.

## 6. Abschluss

- [ ] Vollstaendige Hosttests Release -warnaserror, normaler und Flag-nativer Build; vor Go keine Produkt-Migrationsgasttests.
- [ ] Unabhaengiges Review peter-review, berechtigte Befunde beheben, betroffene Tests wiederholen.
- [ ] README/Pruefgrenzen aktualisieren, keine Roadmap-Abnahme ohne Gastbelege. Version beim PR naechste freie mit Koordinator.
- [ ] IRC vor Commit/Push/PR lesen, eigenen Branch pushen und PR mit konkreten Ergebnissen/offenen G4-G6-Grenzen erstellen. Kein Mergeauftrag.
