# Etappe 4.5: Zwischenstand und Uebergabe

Stand: 17.11.2026, ca. 18:50 UTC. Autor: agent-peter-huang.
Branch: `task/storage-root-migrate`; Basis-Merge `11c5224` (master `321bb92`).
**Unfertige Implementierung, kein Produkt-Go und noch kein PR.**

## Auftrag und Zustaendigkeit

Koordinator hat 4.5 (`system migrate <Ziel> --yes`, Cosmos-IMigrationTarget,
Quellschutz, Abbruch/Journal) zugewiesen und die alleinige Zustaendigkeit
bestaetigt. Vor neuer Arbeit erneut im IRC abgleichen. Fallback-Uebernehmer:
agent-peter-jensen laut Koordinator 18:46 UTC; Uebernahme erst abstimmen.
4.3/Kernel-Umbau gehoert agent-peter-senior. Keine parallele Kernel-Aenderung.
Version fuer den spaeteren PR aktuell reserviert: 0.5.69; vor PR erneut abgleichen.
G4-G6 brauchen weiterhin ausdrueckliches 4.0-Go. Nicht vorher starten.

## Vorhandener Code

- `1ea8065`: MigrationWriteGate mit exklusivem, nicht wartendem Erwerb.
- `61c27aa`: Abbruchtoken in LayoutMigration/Inventory, begrenzter ReadStream,
  bestehender Plan `2026-11-17-root-migration-shell.md`.
- `d007c8d`: FileMigrationTarget, wiederholbare Temp-/Backupbehandlung,
  Shelljob/CLI und Strg+C in Terminal/Textshell. Noch nicht im Kernel registriert.
- Aktueller Checkpoint: gemeinsamer AppStorageGate/Leasekern statt zweiter
  Migrationssperre; verschachtelte Schreibtransaktionen und Dispose auf anderem
  Thread beruecksichtigt. TryBlock erlaubt sofortige Ablehnung beim Unmount.
- Optionale Writercallbacks in FileService, FileAccessControl/AccountService,
  PackageStore, UpdateController/FileUpdateTarget; ohne Callback altes Verhalten.
  Account-Apply prueft vor In-Memory-Mutation: Sperre meldet Fehler, kein Logout.
  FileService haelt den Lease auch durch Temp-/ACL-Fehlerbereinigung.
- MigrationHash verwendet vorhandenen BouncyCastle-Sha256Digest wie Paketcode.
  Es wurde kein Paket und kein Werkzeug aktualisiert.

## Teststand am Checkpoint

Bereits ausgefuehrte, gezielte Pruefungen vor den letzten kleinen Aenderungen:

- Quellschreiber: 10 PASS (7 FileService-Operationen, Fehler/Rollback,
  Konten/ACL/Recovery samt weiter funktionierendem Login, Pakete/Updates).
- Gemeinsamer Gate + WasmSaveStore: 25 PASS, 1 Windows-Plattformskip.
- Gates nach verschachtelten Leases: 37 PASS, 1 Skip zusammen mit Schreiber-
  und Wasmtests, aber Lauf meldete CA1840 als Fehler; also KEIN gruener Build.
  Danach Thread.CurrentThread.ManagedThreadId auf Environment.CurrentManagedThreadId
  geaendert; diese Variante ist noch nativ zu pruefen.
- Hashvektoren/Abbruch/echte Zielwiederholung/CLI/AppGate: 14 PASS.
- Zieladapter/CLI zuvor: 26 PASS; Shell/Terminal/ShellJob zuvor: 36 PASS.
- Breitere Migration-Fokussuite wurde gestartet, Ergebnis beim Schreiben dieser
  Uebergabe noch offen; Windows-Test mit 8191 Quelldateien braucht mehrere Minuten.
- Neue Regression prueft bei Write/Copy-Fehler, dass beim Lease-Dispose keine
  Tempdatei mehr existiert. Der erste Versuch scheiterte an einem Test-API-Namen,
  der zweite am gesperrten Test-DLL durch die noch laufende Suite. Beides ist kein
  fachlich roter Nachweis. API korrigiert; Regression und Fix noch gemeinsam pruefen.

Keine Vollsuite, kein nativer Build und kein Gasttest dieses 4.5-Standes.
Keinen Erfolg aus alten Logs oder PR189 fuer diese Aenderung uebernehmen.
Keine parallelen Builds desselben Testprojekts: Windows sperrt die Test-DLL.

## Naechste Schritte

1. Laufende Tests auslaufen lassen, neue Fehlerpfadtests und geaenderte Gates
   Release mit `-warnaserror` pruefen. Testlogs lokal unter `.tools/migration-*.log`.
2. Cosmos-Adapter und Buildflag fehlen vollstaendig. Vor RW-Mount GPT,
   Partition-LBA/GUID, Installmarker/InstalledIdentity, Ext2FeatureGate und
   root.marker (fresh oder passend migrating) ausschliesslich lesend pruefen.
   Ready, Quellplatte und bereits gemountete Zielplatte ablehnen.
3. Quellschutz muss bis in die nativen FAT-Operationen reichen: in Cosmos 3.0.97
   ruft auch ein nur lesend geoeffneter FAT-Handle beim Release immer Fsync auf;
   Fsync schreibt Verzeichniseintrag und Flush. Nur FileAccess.Read reicht nicht!
   `CosmosReadOnlyExt2FileSystem` enthaelt bereits wiederverwendbare Wrapper fuer
   Inodes/OpenFile und unterdrueckt Fsync/Release/Sync/Drop. Readonly-BlockDevice
   als Rueckfallschutz beibehalten; keine zweite unabhaengige Schreibsperre bauen.
4. Bei einem Quell-VFS-Wrapper alle Mutationen ueber AppStorageGate-Leases fuehren,
   reine Lesehandles ohne schreibendes Release abschliessen. Native Sync/Drop
   waehrend Migration nicht auf die Quelle schreiben lassen. Normales Unmount
   muss trotz manueller Volume-Sperre noch synchronisieren koennen; dafuer
   IsMigrationLocked von IsBlocked unterscheiden. Das ist noch KEIN fertiger Code.
5. Ziel nach RO-Pruefung unter `/mnt/target` RW, RootVolumeState.Open vor Mutation,
   LayoutMigration mit exklusivem Quelllease, Sync/Close/Unmount mit Fehlerbericht.
   Zielwrites duerfen nicht versehentlich vom Quellgate abgewiesen werden.
   RootVolumeState/CosmosRootVolume aus #200 wiederverwenden, keine Mountannahme:
   CosmosRootVolume allein mountet nichts.
6. VfsManager hat keine UnregisterFileSystem-API. Fuer mehrfache RO-ESP/RO-Root/
   RW-Root-Mounts eine feste Registrierung verwenden und nur nach bestaetigtem
   Unmount umschalten; nicht pro Versuch Registrierungen ansammeln.
7. Erst nach Abstimmung Seniors InitializeServices(SystemPaths)-Stand integrieren.
   Writercallbacks fuer Legacy, MigrationService/CLI, TakeResult/Close/Interrupt,
   Administratorbefehl und ZEHOS_ROOT_MIGRATION unter ZehOSRootMigration=true
   verdrahten. Datentraegerwechsel/init und Energieaktionen waehrend Migration
   ablehnen. Bislang nur Unmount abgesichert, weitere Aufrufer offen.
8. Aktuelles master integrieren: /save-Fix #210 inzwischen gemergt, Ext2-Reserve
   8189 statt8190 separat bei lead2 in Arbeit; keine konkurrierende Reparatur.
9. Vollsuite und normale/Flag-native Builds; unabhaengiges Review peter-review
   vor PR. Produkt-Gastmigration erst nach Go. VMware/Hardware offen nennen.

## Weitere technische Hinweise

- FileMigrationTarget benoetigt obligatorischen Pfadwaechter; lehnt Dotdot,
  Doppelpunkte, Backslash und fremde Mount-/Symlinkpfade ab. Bekannte Tempnamen:
  IMigrationTarget.TemporarySuffix und BackupSuffix. Marker-Backup kann beim
  Wiederholen lesend als Fallback dienen; CRC/GUID/State danach validieren.
- Ready-Schreiben und letzte Synchronisierung bilden die nicht abbrechbare
  Schlussphase. Abbruch nach Ready ist kein rueckwirkender Fehler.
- Kernel bisher nicht angefasst; optionale Callbackparameter erhalten Callsite-
  Kompatibilitaet. Ohne Kernelverdrahtung ist noch kein umfassender Quellschutz aktiv.
- Lokaler Agent huang-migration-shell-tests lieferte keinen Code, Diff war leer.
  Eigene Tests geschrieben; Arbeitskopie noch zu bereinigen, nur nach Clean-Check.
- Installiertes SDK 10.0.404; keine Updates. Nativer Build bisheriger Arbeitsstaende
  brauchte `-p:XorrisoBinary=C:/Users/NWeh/.codex/worktrees/f451/ProjektZehOS/.tools/iso-tool/usr/bin/xorriso.exe`.
  Dieser 4.5-Stand wurde damit noch nicht gebaut.
