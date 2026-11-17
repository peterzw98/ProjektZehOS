# Etappe 4: Root-Baum, feste Systempfade und nichtdestruktive Migration – Entwurf

Stand: 17. November 2026, Basis `origin/master` @ `284e7ee` (0.5.17, Etappe 3 gemergt). **Nur Entwurf**, kein Code, nichts im Gast ausgeführt. Grundlage: [Plan, Etappe 4](../plans/2026-11-17-storage-linux.md#etappe-4-root-baum-konfiguration-und-nichtdestruktive-migration), [Zielstruktur](2026-11-17-storage-linux.md#zielstruktur), [Etappe 3](2026-11-17-storage-etappe3-design.md), [7a-Layout](2026-11-17-storage-7a-installer-layout.md). Cosmos-Aussagen aus der Quelle `efcf20c` (= 3.0.97), nicht aus dem Gast. Code erst nach Lead-OK; Voraussetzung bleibt die Abnahme von 7a.

## 0. Entscheidungen (Leitung 11:23: §0.2 bestätigt, Ablageort `/srv/legacy-data`, neues Paket 4.0; 11:24: Reviewbefunde, `RootUnclean`)

1. **Zwei feste Profile, kein konfigurierbarer Pfadbaum.** `SystemPaths` kennt genau `Legacy` (heutige Pfade unter `/data`, byteidentisches Verhalten) und `Root` (ext2 als `/`). Das Profil wird **einmal beim Boot** aus Identität und Migrationsmarker bestimmt (§2) und danach nicht mehr gewechselt; `disk use/mount` ändern es nicht.
2. **ESP unter `/boot/efi`, nicht unter `/boot`.** Abweichung von der Zielstruktur mit Grund: Kernel und Limine liegen auf der ESP unter `boot/…` (`InstallJob.cs:21`, Limine `boot():/boot/ProjektZeh.elf`). Als `/boot` eingehängt entstünde `/boot/boot/ProjektZeh.elf`; mit `/boot/efi` bleibt der Limine-Pfad unverändert, und `FileUpdateTarget` bekommt nur eine andere Wurzel (`/boot/efi` statt `/data`). Das ist zugleich die übliche Linux-Lage. `/boot` selbst ist ein Verzeichnis auf Root.
3. **Systemdaten sind keine ACL-Dateien.** `/etc/zehos/**` und `/var/lib/zehos/**` sind für `FileService` immer gesperrt, auch für Administratoren – wie heute alles mit Präfix `.zehos-` (`FileAccessControl.IsManagedPath`). Nur die zuständigen Dienste (`AccountService`, `PackageStore`, `UpdateController`) schreiben dort über eigene `FileService`-Instanzen mit Pfadwächter. Keine zweite Rechteverwaltung, keine UID/GID (Etappe 3, Entscheidung 1).
4. **Migration läuft auf dem alten System**, angemeldet als Administrator, auf eine per 7a frisch installierte zweite Platte; die alte Platte wird nur gelesen und bleibt bootfähig. Erst der zuletzt geschriebene Root-Marker `state=ready` (§2) macht die neue Platte zu einem `Root`-System.
5. **`/run` und `/tmp` nicht in Etappe 4.** Cosmos 3.0.97 bringt nur `Fat`, `Ext2`, `Iso9660` mit, kein RAM-Dateisystem. Ein begrenztes RAM-Backend gehört zu den virtuellen Dateisystemen (Etappe 5/6); bis dahin werden beide Pfade nicht angelegt. Ebenso bleiben `/proc`, `/sys`, `/dev` in Etappe 4 nur reservierte Namen.
6. **`/usr/bin` bleibt leer** bis es echte Starter gibt; keine Platzhalter (Plan).

## 1. Pfadzuordnung

| Verantwortung | Heute (`Legacy`) | `Root` | Heutige Fundstelle |
|---|---|---|---|
| Verwaltungswurzel der ACLs, Vergleich | `/data`, OrdinalIgnoreCase | `/`, Ordinal | `Kernel.cs:46` |
| Konten, Befehlsrechte | `/data/.zehos-security.db`, `/data/.zehos-commands.db` | `/etc/zehos/.zehos-security.db`, `/etc/zehos/.zehos-commands.db` | `AccountService.cs:17,41` |
| Recovery-Kopien der Kontendatei | `/data/.zehos-security-recovery-*.db` | `/etc/zehos/.zehos-security-recovery-*.db` | `AccountService.cs:263` |
| Homes | `/data/home/<name>` | `/home/<name>` | `AccountService.Home` |
| Paketbestand | `/data/.zehos-packages` | `/var/lib/zehos/packages` | `Kernel.cs:48` |
| Updatekonfiguration | `/data/.zehos-update.conf` | `/etc/zehos/update.conf` | `UpdateController.cs:16` |
| Kernel, Version, Rückfall (ESP) | `/data/boot/…` | `/boot/efi/boot/…` | `FileUpdateTarget.cs:8-14`, `UpdateController.cs:10` |
| Desktopeinstellungen | `<Home>/zehos-desktop.conf` | `<Home>/.config/zehos/desktop.conf` | `DesktopSettings.cs:62` (relativ zum Arbeitsverzeichnis) |
| Standardhintergrund im Einstellungsfeld | `/data/hintergrund.bmp` | `<Home>/hintergrund.bmp` (nur Vorschlagstext) | `SettingsApp.cs:63` |
| Orte im Dateimanager | Arbeitsverzeichnis, `/data` | Arbeitsverzeichnis, `/home/<name>` | `FileManagerApp.cs:68` |
| Startverzeichnis ohne Konto | `/data` | `/` (nur lesend sichtbar nach ACL) | `Kernel.cs:80,127` |
| App-Zustand (ClassiCube 13.4) | noch keiner | `/var/lib/zehos/apps/<id>/<konto-id>/` (reserviert, §5) | – |
| Übernommene sonstige Dateien | – | `/srv/legacy-data/…` (Verzeichnis auf Root, ACLs übernommen; `/mnt` bleibt reinen Mounts vorbehalten) | – |

Nicht verschoben: Gastproben (`CosmosSecurityProbe`, `CosmosWasmProbe`) bleiben Legacy-Testbuilds unter `/data`; der Installer hängt sein Ziel weiter vorübergehend an `/data` (`CosmosInstallService.cs:66-203`), nur im `Root`-Profil an einem freien Mountpunkt `/mnt/install` (sonst wäre `/data` dort ein unbelegter Name ohne Bedeutung).

`SystemPaths` ist eine kleine unveränderliche Klasse mit diesen Werten je Profil, im Kernel einmal erzeugt und an die Dienste übergeben. Kein globaler Zustand, keine Konfigurationsdatei. Tests bauen beide Profile.

## 2. Boot und Mounts

**Zwei Marker, klar getrennt:** der **ESP-Marker** `boot/.zehos-install.id` (7a §5, Identität von Platte und Root-Partition) und der **Root-Marker** `/etc/zehos/.zehos-layout` auf ext2 (Zustand des Baums). Der Root-Marker hat das Format des ESP-Markers (≤ 512 Byte, CRC32, Temp + Rename) und das Feld `state`:

| `state` | geschrieben von | Bedeutung |
|---|---|---|
| `fresh` | 7a-Installer als letzter Schritt auf Root | leerer Baum, Ersteinrichtung oder Migration möglich |
| `migrating` | `system migrate` vor dem ersten Kopieren | Migration begonnen, nicht abgeschlossen |
| `ready` | Ersteinrichtung bzw. Migration als letzter Schritt | eingerichtet |

Damit unterscheiden sich frische Installation, abgebrochene Einrichtung und abgebrochene Migration eindeutig, ohne eine „Root leer“-Prüfung (senior P3-1).

**Reihenfolge beim Boot:**

1. ESP (FAT) direkt unter `/boot/efi` einhängen und den ESP-Marker dort lesen, `InstalledIdentity.Evaluate` (7a).
2. Bei `Healthy`: Root über `CosmosReadOnlyExt2FileSystem` (#137) öffnen, `Ext2FeatureGate` auswerten, Root-Marker und Kontendatei lesen. Nichts wird dabei geschrieben (Cosmos schreibt bei Release/Sync/Drop auch auf Lese-Mounts, deshalb die RO-Komponente).
3. RO-Zugriff schließen; nur im Fall „`Root`“ unten ext2 schreibbar auf `/` einhängen, im Fall `RootUnclean` die RO-Komponente auf `/`.
4. Ergibt sich kein `Root`-Fall: die ESP muss für das 7a-Verhalten unter `/data` liegen. Offene Frage an die Leitung (§7.8): Leitung 11:24 sagt „kein Umhängen“ und zugleich „`Unclear` = ESP als `/data`“; beides zusammen geht nur, wenn in diesem Fall `/boot/efi` ausgehängt und `/data` eingehängt wird (einmalig beim Boot, vor dem Laden der Konten) oder wenn der Rückfall die ESP unter `/boot/efi` lässt und `/data` leer bleibt.

| Lage | Profil und Verhalten |
|---|---|
| kein 7a-Layout (MBR-Altplatte, Datenplatte) | `Legacy` wie heute (Etappe-2-Auswahl unverändert) |
| `Healthy` (ESP-Marker), Gate `Ok`, Root-Marker `state=ready` | `Root` |
| `Healthy` (ESP-Marker), Gate `Ok`, Root-Marker `state=fresh`, keine Kontendatei | `Root`, Ersteinrichtung wie heute; sie setzt im Root-Marker `state=ready` als letzten Schritt |
| `Healthy` (ESP-Marker), Gate `Ok`, Root-Marker `state=fresh`, Kontendatei vorhanden (Einrichtung nach dem Speichern abgebrochen) | `Root`, keine Ersteinrichtung (Konten existieren); `state=ready` wird beim nächsten Speichern der Konten nachgezogen |
| `Healthy`, Gate `ReadOnly` (unbekanntes RoCompat oder `s_state` nicht sauber, siehe unten), Root-Marker `state=ready` | **`RootUnclean`** (eigener Zustand, nicht `RootDamaged`): Profil `Root` nur lesend über die RO-Komponente, Anmeldung mit vorhandenen Konten, alle Schreibzugriffe verweigert, Hinweis „Root nicht sauber ausgehängt – offline `e2fsck` nötig“; Einrichtung und `security recover` gesperrt |
| `Healthy` (ESP-Marker), aber Root-Marker `state=migrating`, fehlend oder ungültig, oder Gate `Reject` | `Unclear` |
| ESP-Marker ergibt `RootMissing`/`RootDamaged`/`Unclear` | wie 7a: ESP als `/data`, Profil `Legacy`, Einrichtung/Recovery gesperrt |

`Unclear` hat nur noch diese eine Bedeutung (7a).

**Sauber-Kennzeichen (senior P2-2):** Der Cosmos-ext2-Treiber setzt `s_state` beim schreibenden Mount nie zurück (nur der Formatter schreibt es, `Ext2Formatter.cs:161`); nach einem Absturz sähe ein halb geschriebenes Root sauber aus. ZehOS setzt deshalb direkt nach dem schreibenden Root-Mount selbst `s_state = 0` (nicht sauber) und erst nach dem Sync beim Herunterfahren wieder `1`. Ein harter Abbruch führt dadurch zu Gate `ReadOnly` und zu `RootUnclean`, nicht zu verlorenen Konten. `shortcut:` eigener Superblock-Schreibzugriff, entfällt, wenn Cosmos das Kennzeichen selbst pflegt.

**Absturz während eines Schreibvorgangs (huang P2-3):** ext2 hat kein Journal, und Konten und ACLs liegen nur auf Root. Gegenmaßnahmen: Konten-, ACL- und Root-Marker-Dateien schreiben in der Reihenfolge Temp-Datei, Flush, Rename, Verzeichnis-Flush, doppelter Sync (bestehendes `WriteData`-Muster plus Flush/Sync); Test „harter Abbruch während des Speicherns ⇒ alte oder neue Fassung, nie keine“. Das ersetzt kein fsck; beschädigte ext2-Metadaten bleiben möglich und enden in `RootUnclean` bzw. `RootDamaged`. `security recover` bleibt bei `RootDamaged` und `RootUnclean` gesperrt (7a Entscheidung 6, Leitung 11:24); Reparatur offline.

**Cosmos-Befunde (Quelle, kein Gasttest):** `VfsManager.TryMount` prüft nicht, ob der Mountpunkt als Verzeichnis existiert; die Zuordnung ist „längster Treffer“ (`VfsManager.cs:671-690`), verschachtelte Mounts wie `/boot/efi` unter `/` sind damit möglich. Ein Mountpunkt ohne Verzeichnis taucht in der Elternauflistung nicht auf (RO-Probe: `/mnt` fehlte). Deshalb legt der Installer (7a) `/boot/efi`, `/mnt`, `/proc`, `/sys`, `/dev`, `/etc/zehos` als leere Verzeichnisse auf Root an (mit dem Root-Marker `state=fresh`). Gasttest G2 prüft das verschachtelte Einhängen tatsächlich.

**Pfadwächter:** Etappe 3 greift unverändert. Mit Wurzel `/` ist `/boot/efi` ein fremder Mount und für `FileService` gesperrt (in `/boot`-Listen ausgeblendet, `cd` verweigert); das ist gewollt. `FileUpdateTarget`/`UpdateController` bekommen eine eigene `FileService`-Instanz mit `CosmosPathGuard.For("/boot/efi")`, die Migration eine mit `For("/mnt/target")` (senior P3-2). Der Wächter muss mit Wurzel `/` funktionieren (Etappe 3 hat `/` ohne `//` bereits abgedeckt).

**Energieaktionen:** heute wird nur `/data` ausgehängt (`Kernel.cs:323`). Im `Root`-Profil: erst `/boot/efi` aushängen, dann `/` synchronisieren (aushängen geht nicht, solange der Kernel läuft) und `s_state = 1` setzen. Cosmos `Ext2Superblock.Flush` ruft erst `Device.Flush()` und schreibt danach `UpdateSuperblock()` (`Ext2Superblock.cs:767-771`); die Zähler wären nach einem Sync nur im Cache. Deshalb **zweimal** `mount.Superblock.SuperOperations.Sync` (öffentlich über `VfsManager.Mounts`), `shortcut:` bis Cosmos die Reihenfolge korrigiert; Upstream-Hinweis als Entwurf (senior P2-1). Fehler brechen ab, Shell bleibt bedienbar. Die vollständige Reihenfolge ist 7b; Etappe 4 liefert nur diesen Mindestumfang, damit ein `Root`-System nicht ohne Sync herunterfährt.

## 3. Migration `Legacy` → `Root`

**Bedienung:** `system migrate <Ziel> --yes`, nur Administrator, nur im `Legacy`-Profil. Ziel ist eine Platte mit 7a-Layout, Identität `Healthy`, Gate `Ok`, Root-Marker `state=fresh` (oder `migrating` mit Journal, siehe Wiederholung). Ziel-ESP und Root werden dafür nur über Lese-Mounts bzw. die RO-Komponente geprüft; ob ein FAT-Lese-Mount in Cosmos schreibt, ist ungeprüft (§7). Alles andere wird vor dem ersten Schreibzugriff verweigert. Danach wird Root des Ziels unter `/mnt/target` (ext2) schreibbar eingehängt.

**Ablauf (reine Logik `LayoutMigration`, Dateizugriff über injiziertes Dateisystem):**

1. **Schreibsperre der Quelle (huang P2-2):** für die Dauer ist `/data` für alle Schreiber gesperrt – `FileService` (über `FileAccessControl`, auch Admin), `PackageStore`, `UpdateController`, Desktopeinstellungen. Eigenes Sperrkennzeichen, **nicht** `Access.Lock` (das sperrte auch den aufrufenden Admin, `IsAdmin = IsReady && …`). Lesen und die laufende Sitzung bleiben möglich.
2. **Inventar (nur lesen):** alle Einträge unter `/data` mit Größe und SHA-256, ACL-Einträge, Konten. Schlägt das Lesen eines Eintrags fehl: Abbruch.
3. **Zuordnung** nach §1. Einträge ohne Zuordnung → `/srv/legacy-data/<relativer Pfad>`. Ausgenommen und protokolliert: `/data/boot/**` (gehört zur alten ESP; der neue Kernel kommt aus 7a), temporäre `.zehos-*.tmp`/`.bak`. Paketbestand und App-Zustand 1:1; ob `PackageStore`-Metadaten absolute `/data`-Pfade enthalten, wird vor 4.4 geprüft und getestet.
4. **Prüfungen vor dem Schreiben:** freier Platz und freie Inodes für alle Dateien und Verzeichnisse, jede Datei auf 4 KiB aufgerundet, plus 10 % Reserve; keine zwei Quellen auf dasselbe Ziel (Ordinal); Zielpfade bestehen den Pfadwächter. Ein Konflikt stoppt mit Liste, nichts geschrieben.
5. Root-Marker des Ziels auf `state=migrating`.
6. **Kopieren** jeder Datei, Journal-Absicht **vor** der Datei: Zeile „schreibe <Pfad>“ in `/etc/zehos/.zehos-migration-journal`, dann Datei schreiben, Prüfsumme zurücklesen, Zeile „fertig <Pfad> <Hash>“ (huang P3 a).
7. **Metadaten:** ACL-Pfade mit derselben Zuordnung umschreiben; Konten und Befehlsrechte unverändert übernehmen; Ergebnis zurücklesen und mit `FileAccessControl` (Ordinal, Wurzel `/`) laden – Ladefehler ⇒ Abbruch.
8. **Inventar erneut** mit dem aus Schritt 2 vergleichen; jede Abweichung ⇒ Abbruch.
9. Root-Marker `state=ready` mit Ziel-Disk-GUID, Quellinventar-Hash und Dateizahl als letzter Schreibzugriff, danach Sync (zweifach, §2).
10. Schreibsperre der Quelle aufheben; die alte Platte ist unverändert.

**Groß-/Kleinschreibung:** FAT ist case-insensitiv, ACL-Einträge können anders geschrieben sein als der Name auf der Platte. Die Migration nimmt den **tatsächlichen Namen aus der Verzeichnisauflistung** und ordnet ACL-Einträge per OrdinalIgnoreCase zu; ein ACL-Eintrag ohne Datei wird protokolliert und nicht übernommen.

**Abbruch und Wiederholung:** Abbruch an jeder Stelle hinterlässt `state=migrating` (oder noch `fresh`) ⇒ das Ziel bootet nach §2 als `Unclear` (keine Einrichtung). Abbruch per Taste folgt derselben Regel wie ein harter Abbruch (huang P3 f). Ein erneutes `system migrate` auf dasselbe Ziel ist erlaubt, wenn **jede** Datei auf Root entweder von 7a angelegt oder im Journal mit „schreibe“ oder „fertig“ steht; dann werden genau diese Dateien entfernt und neu begonnen. Jede fremde Datei ⇒ Verweigerung (dann nur Neuinstallation mit `--erase`). Die Quelle wird nie geschrieben; Test: Image-Prüfsumme vor/nach.

## 4. Arbeitspakete

| Paket | Inhalt | Prüfung | Vorschlag |
|---|---|---|---|
| 4.0 | **RW-Qualifikation ext2 als `/` im Gast** auf Basis der Etappe-1-Probe: CRUD, 10 000 Dateien, Volllaufen/ENOSPC, Rename/Delete, Neustart, `e2fsck -fn` offline Exit 0, je 32 und 128 GiB. **Ohne Go kein `Root`-Profil** | Tester (Gast), Auswertung Leitung | senior (Probe), aot-probe/tripleclerk |
| 4.1 | `SystemPaths` (zwei Profile) und Umstellung aller Aufrufer aus §1; `Legacy` erzeugt exakt die heutigen Pfade | Hosttests beide Profile; bestehende Tests unverändert grün; nativer Build, Normalboot, Datenplatte A–F (Regression) | senior |
| 4.2 | Gesperrte Systempfade `/etc/zehos`, `/var/lib/zehos` in `FileAccessControl` (auch Admin) | Hosttests: Lesen/Schreiben/Listen/Suche/Umbenennen hinein und heraus verweigert | huang oder jensen |
| 4.3 | Profilwahl beim Boot (§2) als reine Funktion `LayoutProfile.Decide(identity, gate, rootMarker, hasAccounts)` plus Cosmos-Anbindung (ESP-Umhängen, RO-Prüfung, RW-Mount, `s_state`, doppelter Sync bei Energieaktion) | Hosttests jede Tabellenzeile; Gast G1–G3, G7 | senior |
| 4.4 | `LayoutMigration` als reine Logik (Inventar, Zuordnung, Konflikte, Journal, Marker, Wiederholung) | Hosttests §5 | jensen |
| 4.5 | Shellbefehl `system migrate` und Cosmos-Anbindung | Gast G4–G6 | senior |
| 4.6 | App-Pfad mit ClassiCube 13.4 abstimmen (§5), Doku | Review cc-lead | orchestrator-junior |

4.1 und 4.2 sind unabhängig von 7a und nach Review ohne P1/P2 freigegeben (Leitung 11:23). 4.0 läuft vor 4.3; 4.3–4.5 brauchen das Go aus 4.0 und 7a S2–S4 (Writer, Identität, Installer).

## 5. App-Speicher (Abstimmung mit ClassiCube 13.4)

Vorschlag an den App-Speicher-Auftrag: `/var/lib/zehos/apps/<paket-id>/<konto-id>/`, darunter nur die App selbst über ihre Hostfunktionen; für `FileService` gesperrt (§0.3). Im `Legacy`-Profil derselbe Aufbau unter `/data/.zehos-apps/<paket-id>/<konto-id>/` (`.zehos-` sperrt ihn wie den Paketbestand). Die Migration übernimmt das Verzeichnis 1:1. Etappe 4 vergibt **keine** Wasm-Schreibrechte; Größen- und Dateizahlgrenzen, Löschen mit dem Paket und das Verhalten ohne Datenlaufwerk bleiben beim App-Auftrag. Abzustimmen: Paket-ID-Zeichensatz (Pfadkomponente, Etappe-3-Regeln) und ob Konto-ID oder Benutzername.

## 6. Testspezifikation (Tests zuerst)

**Hosttests:**
1. `SystemPaths`: jede Zeile aus §1 für beide Profile; `Legacy` gleich den heutigen Literalen (Regressionstabelle).
2. Gesperrte Systempfade: Lesen, Schreiben, Listen, Suche, `mv` hinein/heraus, `cd`, auch als Admin und in Groß-/Kleinvarianten ⇒ verweigert; Dienste mit eigenem `FileService` dürfen schreiben.
3. `LayoutProfile.Decide`: jede Zeile aus §2; insbesondere `state=migrating` oder fehlender Root-Marker ⇒ `Unclear`, nie Ersteinrichtung; `fresh` mit Kontendatei ⇒ keine Ersteinrichtung; Gate `ReadOnly` ⇒ `RootUnclean` (nur lesend mit Anmeldung, nicht `RootDamaged`).
4. `LayoutMigration`: Zuordnung je Kategorie; Datei ohne Zuordnung nach `/srv/legacy-data`; Zielkonflikt ⇒ nichts geschrieben; zu wenig Platz ⇒ nichts geschrieben; ACL in anderer Schreibweise ⇒ Zieleintrag mit tatsächlichem Namen; ACL ohne Datei ⇒ protokolliert; zu wenig Inodes bzw. viele kleine Dateien (4-KiB-Rundung) ⇒ nichts geschrieben; ENOSPC beim Kopieren; Quelle ändert sich während des Laufs (Schreibversuch gesperrt, Inventarvergleich Schritt 8 schlägt bei manipuliertem Fake an); Abbruch nach jedem der Schritte 1–9 ⇒ nie `state=ready`; Absturz zwischen Datei und Journal ⇒ Wiederholung möglich; Wiederholung nach jedem Abbruch ⇒ gleiches Endergebnis wie ohne Abbruch; fremde Datei auf Ziel ⇒ Verweigerung; Quelle byteidentisch (Fake zählt Schreibzugriffe: 0).
5. Root-Marker: CRC, Größe, Formatversion, `state`-Werte, Temp + Rename; beschädigt ⇒ `Unclear`.
6. Energieaktion im `Root`-Profil: Sync-Fehler bricht ab; zwei Syncs, danach `s_state = 1`.
7. Konten-/ACL-Speichern: Abbruch nach jedem Schritt (Temp, Flush, Rename, Verzeichnis-Flush, Sync) ⇒ alte oder neue Fassung lesbar.
8. Schreibsperre der Quelle: `FileService` als Admin, `PackageStore`, Update, Desktopeinstellungen verweigern; Anmeldung und Lesen gehen weiter.

**Gasttests (QEMU+OVMF, danach VMware; nur auf Ansage, ein Gast):**
- G1 frische 7a-Installation 32 GiB → Ersteinrichtung → Profil `Root`, Home unter `/home/<name>`, Desktopeinstellungen in `.config/zehos`, Neustart, `e2fsck -fn` offline Exit 0.
- G2 `/boot/efi` verschachtelt eingehängt, `update install` (lokal) schreibt nach `/boot/efi/boot/…`, Rückfalleintrag `.bak` startet.
- G3 Marker offline gelöscht ⇒ keine Einrichtung, `security recover` verweigert, Root lesbar.
- G4 Migration 64-MiB-Bestandsplatte → neue 32-GiB-Platte: Anmeldung mit altem Kennwort, Dateien und ACLs gleich (Hashvergleich), Pakete starten (NetSurf), alte Platte Prüfsumme unverändert und weiter bootfähig.
- G5 Migration mit vollem Ziel (kleine Root-Partition per Testlayout) und mit Abbruch (Gast hart beenden) ⇒ Ziel `Unclear`, `e2fsck -fn` nach dem Abbruch, Wiederholung erfolgreich, `e2fsck -fn` danach (Wiederholung löscht Dateien; Unlink/Rmdir hatte ext2-Befunde, huang P3 e).
- G7 Root-System hart beenden während Schreiben ⇒ nächster Boot `RootUnclean`, Anmeldung möglich, Kontendatei alt oder neu; nach offline `e2fsck -fy` wieder schreibbar. ESP-Umhängen `/boot/efi` → `/data` bei `RootMissing`.
- G6 64 GiB und 128 GiB: Migration, Neustart, `e2fsck -fn`.

## 7. Offene Punkte

1. *Entschieden 11:23:* ESP unter `/boot/efi` (weicht bewusst von der Zielstruktur ab).
2. *Entschieden 11:23:* übernommene sonstige Dateien unter `/srv/legacy-data`, ACLs übernommen.
3. **7a:** Installer legt Mountpunktverzeichnisse und Root-Marker `state=fresh` an (§2) – in 7a S3/S4 aufnehmen.
4. **Cosmos:** Schreibendes ext2 als `/` ist bisher nur in der isolierten Probe belegt (Etappe 1: CRUD, Inode-Wiederverwendung); Paket 4.0 schließt diese Lücke vor jedem `Root`-Profil. G1/G6 prüfen danach den Produktpfad.
5. *Geklärt (senior):* Sync über `VfsManager.Mounts` → `SuperOperations.Sync`, zweifach wegen der Flush-Reihenfolge (§2). Offen: ob ein FAT-Lese-Mount in Cosmos schreibt (Ziel-ESP-Prüfung in §3).
6. **App-Speicher:** §5 mit dem ClassiCube-Auftrag abstimmen, bevor 13.4 einen `/data`-Pfad festschreibt.
7. VMware: eigene AHCI-Platte wie in Etappe 2. Zeitbudget: Quelle kann eine FAT-Datenplatte bis 128 GiB sein (Etappe 2); bei ≈ 100 MB/s unter KVM dauert Lesen plus Rücklesen einer vollen Platte über eine Stunde. Fortschrittsanzeige und Abbruch per Taste gehören deshalb in 4.5.
8. **Leitung:** ESP-Lage im Rückfall (§2 Schritt 4): einmalig beim Boot nach `/data` umhängen (Vorschlag, dann gilt 7a unverändert) oder unter `/boot/efi` lassen.
