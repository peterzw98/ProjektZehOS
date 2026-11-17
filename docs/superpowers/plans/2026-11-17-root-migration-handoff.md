# Etappe 4.5: Zwischenstand und Uebergabe

Stand: 17.11.2026, ca. 18:55 UTC. Autor: agent-peter-huang.
Branch: `task/storage-root-migrate`; Code-Checkpoint `aacaffa`,
Basis-Merge `11c5224` (master `321bb92`).
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
- Breitere Migration-Fokussuite: 108 PASS, 3 Windows-Plattformskips, 6m31s.
  Sie lief vor der letzten Temp-/ACL-Cleanup-Korrektur.
- Neue Regression prueft bei Write/Copy-Fehler, dass beim Lease-Dispose keine
  Tempdatei mehr existiert. Der erste Versuch scheiterte an einem Test-API-Namen,
  der zweite am gesperrten Test-DLL durch die noch laufende Suite. Beides ist kein
  fachlich roter Nachweis. API korrigiert; anschliessend 45 gezielte Tests gegen
  aacaffa mit Release -warnaserror bestanden, einschliesslich beider Cleanup-
  Fehlerfaelle, Gate/Writer/Hash, FileMigrationTarget, CLI/Interrupt und Abbruchstream.

Nativer normaler Release-Build aacaffa: erfolgreich, 0 Warnungen/0 Fehler, 1m38s.
Das belegt den normalen Build, noch nicht die native Ausfuehrung aller neuen,
teilweise noch nicht erreichbaren Migrationspfade oder Environment.CurrentManagedThreadId.
Keine Vollsuite, kein Recovery-/Flag-Build und kein Gasttest dieses 4.5-Standes.
Keinen Erfolg aus alten Logs oder PR189 fuer diese Aenderung uebernehmen.
Keine parallelen Builds desselben Testprojekts: Windows sperrt die Test-DLL.

## Naechste Schritte

1. Gezielte Tests sind beendet und gruen. Nach weiteren Aenderungen betroffene
   Tests und zum Abschluss Vollsuite pruefen; lokale Logs unter `.tools/migration-*.log`.
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

### Unabhaengiges Zwischenreview 18:52 UTC

agent-peter-review las Produktdelta/Gates/Zieladapter, aber weder Tests noch
Shell-/Terminalteil, und fuehrte nichts aus. Kein P1/P2 im gelesenen Umfang,
sieben P3. Das ersetzt kein Abschlussreview des noch fehlenden Adapters:

- P3-a: PackageStore.Publish haelt Lease einschliesslich Download. Vorlaeufige
  Bewertung: WriteVersion verzahnt Netzwerk und Schreiben in derselben Transaktion;
  nicht einfach den Lease zwischen Bloecken freigeben (Quelle wuerde sich waehrend
  Migration weiter aendern). Benutzerhinweis auf laufende Paketinstallation verbessern
  oder ein tatsaechliches Stagingkonzept gesondert bewerten. Noch nicht geaendert.
- P3-b: FileAccessControl.Check meldet bei Gate-Ablehnung Denied statt StorageBusy.
  Status/Text verbessern; FileService meldet bei eigener Lease-Ablehnung schon Busy.
- P3-c: MigrationPlan lehnt reservierte Suffixe nur fuer Dateien ab. Verzeichnis
  `x.zehos-tmp` neben Datei `x` kollidiert ebenfalls. Auch Verzeichnisse vorpruefen
  und Regression ergaenzen; noch nicht behoben.
- P3-d/e: Zwei Syncs je Journal-D/W-Zeile und Pfadwaechter pro List-Eintrag koennen
  unter TCG teuer sein. Erst nach Go messen; Haltbarkeit und Grenzschutz erhalten.
  Keine ungepruefte Optimierung an der Sicherheitsgrenze.
- P3-f: Expliziter OperationCanceledException-Abbruch mitten im WriteFile mit
  anschliessendem Wiederholen fehlt. Vorhanden sind IOException im Copy-Stream,
  Tokenabbruch beim Hashen/Kopieren und echte Zielwiederholung nach Copied; der
  kombinierte Fall ist nicht ausdruecklich nachgewiesen. Test ergaenzen.
- P3-g: Quell-FAT Release/Fsync bleibt zentrales offenes Adapterrisiko (siehe oben).

- FileMigrationTarget benoetigt obligatorischen Pfadwaechter; lehnt Dotdot,
  Doppelpunkte, Backslash und fremde Mount-/Symlinkpfade ab. Bekannte Tempnamen:
  IMigrationTarget.TemporarySuffix und BackupSuffix. Marker-Backup kann beim
  Wiederholen lesend als Fallback dienen; CRC/GUID/State danach validieren.
- Ready-Schreiben und letzte Synchronisierung bilden die nicht abbrechbare
  Schlussphase. Abbruch nach Ready ist kein rueckwirkender Fehler.
- Kernel bisher nicht angefasst; optionale Callbackparameter erhalten Callsite-
  Kompatibilitaet. Ohne Kernelverdrahtung ist noch kein umfassender Quellschutz aktiv.
- Lokaler Agent huang-migration-shell-tests lieferte keinen Code, Diff war leer.
  Eigene Tests geschrieben; Arbeitskopie/Branch/Logs nach Clean-Check entfernt.
- Installiertes SDK 10.0.404; keine Updates. Nativer Build bisheriger Arbeitsstaende
  brauchte `-p:XorrisoBinary=C:/Users/NWeh/.codex/worktrees/f451/ProjektZehOS/.tools/iso-tool/usr/bin/xorriso.exe`.
  Der normale Release-Build dieses 4.5-Checkpoints ist damit erfolgreich durchgelaufen.

## Uebernahme durch agent-peter-jensen (17.11.2026, 19:55 Ortszeit)

- Eigner ab b566305: agent-peter-jensen (Koordinator 19:53, huang „uebergeben“ 19:55).
- Erledigt: P3-c. `MigrationPlan.ValidTarget` lehnt den Temp-/Backup-Suffix jetzt auch fuer Ordner ab. Der Test ist angepasst; Migration-Fokustests 93/93 (Linux).
- Pause aller Peter-Agenten 20:10–21:40 (Peter, Discord, signiert).
- Danach in dieser Reihenfolge: P3-a (PackageStore-Lease nur Plattenphasen), P3-b (StorageBusy-Text statt Denied), P3-f (Abbruchtest Temp), dann Cosmos-Adapter + Kernelflag mit FAT-Quellschutz (P3-g). P3-d/e (Messung) und der Gast erst nach dem 4.0-Go.

## Vertretung durch agent-laser-left-7a-s4 (17.11.2026, 20:05–, Pause der peter-Agenten)

- Zuteilung Koordinator 20:04 (Peter im Terminal: laser-Agenten uebernehmen pausierte Aufgaben). Eigner bleibt agent-peter-jensen; Rueckgabe nach der Pause abstimmen.
- `2d0028e` P3-a: `MigrationWriteGate.TryBeginWrite(activity)` merkt sich lange Schreiber; `AppStorageGate.BusyDescription` nennt sie beim Aushaengen. Der Lease bleibt bewusst ueber die ganze Paketinstallation (siehe oben). Kernel muss `PackageStore` bei der Verdrahtung `() => AppStorageGate.TryBeginWrite("Paketinstallation")` uebergeben. P3-b: `FileAccessControl.Check` meldet Gate-Ablehnung als StorageBusy. Tests in MigrationWriteGateTests/MigrationSourceWriterTests.
- `30e1032` P3-f: `RealFileTargetRepeatsAMigrationCancelledInsideWriteFile` (Token-Abbruch mitten im Kopieren, Wiederholung ohne Reste).
- `6ba201e`: master 2316a45 (#211, #212) eingearbeitet; Konstruktoren AccountService/FileService tragen beide Seiten (securityDirectory bzw. virtualSource/isRegularFile, tryBeginWrite zuletzt). Hosttests 1289/1289, nativer Build ok.
- P3-g Schritt 1: `CosmosReadOnlyVfs` (Lese-Wrapper aus CosmosReadOnlyExt2FileSystem herausgeloest) und generische `CosmosReadOnlyFileSystem(inner)` fuer die FAT-Quelle. Entwurf (Frage an lead2 20:16, Antwort ausstehend): waehrend der Migration /data aushaengen und dieselbe FAT-Partition ueber `new CosmosReadOnlyFileSystem(new FatFileSystemType())` lesend einhaengen, danach normal zurueck. Noch NICHT verdrahtet, kein Gastlauf.
- Offen: CosmosMigrationService (RO-Pruefung Ziel, /mnt/target RW, RootVolumeState.Open/Close, LayoutMigration.Run mit AppStorageGate.TryLockSource), Kernelflag `-p:ZehOSRootMigration=true` auf Basis von InitializeServices (#212), Vollsuite + Flag-Build, Review peter-review.

## Stand agent-peter-jensen nach der Pause (17.11.2026, ab 21:45)

- P3-g nach Entscheidung lead2 (21:47): `MigrationSourceSwap` haengt /data unter dem Quelllease aus, haengt dieselbe Partition ueber den Lese-Wrapper ein und haengt sie nach dem Lauf normal wieder ein. Scheitert das Zurueckhaengen, bleibt der Lease gehalten (Schreiber gesperrt bis zum Neustart) und das Ergebnis nennt den Zustand. Es gibt 9 Hosttests, einen je Fehlerstelle.
- Audit zu #212: `LayoutMigration.ReadText` liest blockweise und bricht an der Grenze ab; ein Test mit endlosem Marker deckt das ab.
- `CosmosMigrationService` und Flag `-p:ZehOSRootMigration=true`:
  - Prueft das Ziel nur lesend: kein Quelllaufwerk, keine eingehaengte Partition, GPT, ESP-Identitaet `Healthy`, Root-Marker `fresh`/`migrating`.
  - Haengt erst dann Root unter `/mnt/target` beschreibbar ein, setzt `RootVolumeState.Open` und startet `LayoutMigration.Run`.
  - Schliesst danach mit `Close` und Unmount.
  - Nachlaufende Fehler (Close, Unmount, Zurueckhaengen der Quelle) werden zusammen mit dem Kopierergebnis gemeldet.
- Cosmos-Befunde (Reflection/IL, siehe `docs/COSMOS_SUPPORT.md`):
  - Die Ueberladung von `TryMount` mit Quelle und Partition ist nicht oeffentlich.
  - Die Partitions-Ueberladung reicht den Index weiter; `FatFileSystemType(IBlockDevice)` nutzt das injizierte Geraet nur bei leerer Quelle.
  - Loesung: `CosmosSwitchableFileSystem` mit fester Registrierung, das den Index verwirft.
- Kernel (nur unter dem Flag):
  - Schreiber-Leases fuer Konten, Pakete ("Paketinstallation"), Updates und das Anlegen des App-Speichers. Schreibende App-Aufrufe liefen schon vorher ueber `AppStorageGate.Run`.
  - `system migrate`/`system cancel`, Strg+C ueber `InterruptJob`.
  - Energieaktionen lehnen eine laufende Migration ab, statt sie ueber `StopAll` still abzubrechen. Nach gescheitertem Zurueckhaengen ist ein Neustart trotzdem moeglich.
- Pruefungen:
  - Hosttests 1300/1300.
  - Nativer Build ohne Flag, mit Flag und mit `ZehOSRootProfile` + Flag; Installer- und Recovery-Build: alle mit 0 Fehlern.
- Offen:
  - Unabhaengiges Review (peter-review).
  - Gastlauf G4–G6 erst nach dem 4.0-Go.
  - P3-d/e messen.
  - VMware und echte Hardware ungeprueft.
