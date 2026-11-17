# Etappe 4: Root-Baum, feste Systempfade und nichtdestruktive Migration – Entwurf

Stand: 17. November 2026, Basis `origin/master` @ `284e7ee` (0.5.17, Etappe 3 gemergt). **Nur Entwurf**, kein Code, nichts im Gast ausgeführt. Grundlage: [Plan, Etappe 4](../plans/2026-11-17-storage-linux.md#etappe-4-root-baum-konfiguration-und-nichtdestruktive-migration), [Zielstruktur](2026-11-17-storage-linux.md#zielstruktur), [Etappe 3](2026-11-17-storage-etappe3-design.md), [7a-Layout](2026-11-17-storage-7a-installer-layout.md). Cosmos-Aussagen aus der Quelle `efcf20c` (= 3.0.97), nicht aus dem Gast. Code erst nach Lead-OK; Voraussetzung bleibt die Abnahme von 7a.

## 0. Entscheidungen (Leitung 11:23: §0.2 bestätigt, Ablageort `/srv/legacy-data`, neues Paket 4.0)

1. **Zwei feste Profile, kein konfigurierbarer Pfadbaum.** `SystemPaths` kennt genau `Legacy` (heutige Pfade unter `/data`, byteidentisches Verhalten) und `Root` (ext2 als `/`). Das Profil wird **einmal beim Boot** aus Identität und Migrationsmarker bestimmt (§2) und danach nicht mehr gewechselt; `disk use/mount` ändern es nicht.
2. **ESP unter `/boot/efi`, nicht unter `/boot`.** Abweichung von der Zielstruktur mit Grund: Kernel und Limine liegen auf der ESP unter `boot/…` (`InstallJob.cs:21`, Limine `boot():/boot/ProjektZeh.elf`). Als `/boot` eingehängt entstünde `/boot/boot/ProjektZeh.elf`; mit `/boot/efi` bleibt der Limine-Pfad unverändert, und `FileUpdateTarget` bekommt nur eine andere Wurzel (`/boot/efi` statt `/data`). Das ist zugleich die übliche Linux-Lage. `/boot` selbst ist ein Verzeichnis auf Root.
3. **Systemdaten sind keine ACL-Dateien.** `/etc/zehos/**` und `/var/lib/zehos/**` sind für `FileService` immer gesperrt, auch für Administratoren – wie heute alles mit Präfix `.zehos-` (`FileAccessControl.IsManagedPath`). Nur die zuständigen Dienste (`AccountService`, `PackageStore`, `UpdateController`) schreiben dort über eigene `FileService`-Instanzen mit Pfadwächter. Keine zweite Rechteverwaltung, keine UID/GID (Etappe 3, Entscheidung 1).
4. **Migration läuft auf dem alten System**, angemeldet als Administrator, auf eine per 7a frisch installierte zweite Platte; die alte Platte wird nur gelesen und bleibt bootfähig. Erst der zuletzt geschriebene, geprüfte Abschlussmarker macht die neue Platte zu einem `Root`-System.
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

Reihenfolge im `Root`-Profil (alles vor dem Laden der Konten):

1. 7a-Klassifikation (`InstalledIdentity.Evaluate`) auf `sata0` liefert `Healthy`.
2. `Ext2FeatureGate` auf der Root-Partition liefert `Ok` (bei `ReadOnly`/`Reject` → Fall unten).
3. ext2 schreibbar auf `/` einhängen; ESP (FAT) auf `/boot/efi`.
4. Migrations-Abschlussmarker `/etc/zehos/.zehos-layout` lesen und prüfen (§3). Nur dann Profil `Root`.

| Lage | Profil und Verhalten |
|---|---|
| kein 7a-Layout (MBR-Altplatte, Datenplatte) | `Legacy` wie heute (Etappe-2-Auswahl unverändert) |
| `Healthy`, Gate `Ok`, Marker gültig | `Root` |
| `Healthy`, Gate `Ok`, **kein Marker, Root leer** (frische 7a-Installation) | `Root`, Ersteinrichtung wie heute angeboten; sie schreibt den Marker als letzten Schritt |
| `Healthy`, kein/ungültiger Marker, **Root nicht leer** (abgebrochene Migration, gelöschter Marker) | Root nur lesend unter `/`, ESP unter `/boot/efi`; Ersteinrichtung und `security recover` gesperrt (`Unclear`, Mechanik 7a §5) |
| `RootMissing`/`RootDamaged`/`Unclear`, Gate nicht `Ok` | wie 7a: ESP als `/data`, Profil `Legacy`, Einrichtung/Recovery gesperrt |

„Root leer“ heißt: nichts außer dem, was der Installer anlegt (der korrigierte Formatter legt kein `lost+found` an); die genaue Liste kommt aus 7a S3.

**Cosmos-Befunde (Quelle, kein Gasttest):** `VfsManager.TryMount` prüft nicht, ob der Mountpunkt als Verzeichnis existiert; die Zuordnung ist „längster Treffer“ (`VfsManager.cs:671-690`), verschachtelte Mounts wie `/boot/efi` unter `/` sind damit möglich. Ein Mountpunkt ohne Verzeichnis taucht in der Elternauflistung nicht auf (RO-Probe: `/mnt` fehlte). Deshalb legt der Installer (7a) bzw. die Migration `/boot/efi`, `/mnt`, `/proc`, `/sys`, `/dev` als leere Verzeichnisse auf Root an. Gasttest G2 prüft das verschachtelte Einhängen tatsächlich.

**Pfadwächter:** Etappe 3 greift unverändert. Mit Wurzel `/` ist `/boot/efi` ein fremder Mount und für `FileService` gesperrt; das ist gewollt (Bootartefakte nur über `UpdateController`). Der Wächter muss mit Wurzel `/` funktionieren (Etappe 3 hat `/` ohne `//` bereits abgedeckt).

**Energieaktionen:** heute wird nur `/data` ausgehängt (`Kernel.cs:323`). Im `Root`-Profil: erst `/boot/efi`, dann `/` synchronisieren (ausgehängt werden kann `/` nicht, solange der Kernel läuft; Sync + Flush genügt). Fehler brechen ab, Shell bleibt bedienbar. Die vollständige Reihenfolge ist 7b; Etappe 4 liefert nur diesen Mindestumfang, damit ein `Root`-System nicht ohne Sync herunterfährt.

## 3. Migration `Legacy` → `Root`

**Bedienung:** `system migrate <Ziel> --yes`, nur Administrator, nur im `Legacy`-Profil. Ziel ist eine Platte mit 7a-Layout, Identität `Healthy`, Gate `Ok`, Root leer (§2). Alles andere wird vor dem ersten Schreibzugriff verweigert. Das Ziel wird für die Dauer unter `/mnt/target` (ext2) eingehängt, seine ESP wird nicht angefasst.

**Ablauf (reine Logik `LayoutMigration`, Dateizugriff über injiziertes Dateisystem):**

1. **Inventar (nur lesen):** alle Einträge unter `/data` mit Größe und SHA-256, ACL-Einträge, Konten. Schlägt das Lesen eines Eintrags fehl: Abbruch.
2. **Zuordnung** nach §1. Einträge ohne Zuordnung → `/srv/legacy-data/<relativer Pfad>`. Ausgenommen und protokolliert: `/data/boot/**` (gehört zur alten ESP; der neue Kernel kommt aus 7a), temporäre `.zehos-*.tmp`/`.bak`.
3. **Prüfungen vor dem Schreiben:** freier Platz ≥ Summe + 10 % Reserve; keine zwei Quellen auf dasselbe Ziel (Ordinal); Zielpfade bestehen den Pfadwächter. Ein Konflikt stoppt mit Liste, nichts geschrieben.
4. **Konten sperren:** `AccountService` der Quelle geht für die Dauer in den gesperrten Zustand (keine Anmeldung, keine Rechteänderung), damit sich das Inventar nicht ändert.
5. **Kopieren** jeder Datei auf das Ziel und Prüfsumme zurücklesen. Fortschritt in `/etc/zehos/.zehos-migration-journal` auf dem Ziel (jede geschriebene Zieldatei mit Hash).
6. **Metadaten:** ACL-Pfade mit derselben Zuordnung umschreiben; Konten und Befehlsrechte unverändert übernehmen; Ergebnis zurücklesen und mit `FileAccessControl` (Ordinal, Wurzel `/`) laden – Ladefehler ⇒ Abbruch.
7. **Abschlussmarker** `/etc/zehos/.zehos-layout` als letzter Schritt (Temp + Rename): Formatversion, Ziel-Disk-GUID, Quellinventar-Hash, Anzahl Dateien, CRC32 – Format wie der 7a-ESP-Marker.
8. Quelle entsperren; die alte Platte ist unverändert.

**Groß-/Kleinschreibung:** FAT ist case-insensitiv, ACL-Einträge können anders geschrieben sein als der Name auf der Platte. Die Migration nimmt den **tatsächlichen Namen aus der Verzeichnisauflistung** und ordnet ACL-Einträge per OrdinalIgnoreCase zu; ein ACL-Eintrag ohne Datei wird protokolliert und nicht übernommen.

**Abbruch und Wiederholung:** Abbruch an jeder Stelle hinterlässt kein Markerfile ⇒ das Ziel bootet nach §2 als `Unclear` (keine Einrichtung). Ein erneutes `system migrate` auf dasselbe Ziel ist erlaubt, wenn **jede** Datei auf Root entweder von 7a angelegt oder im Journal mit passendem Hash steht; dann werden genau diese Journaldateien entfernt und neu begonnen. Jede fremde Datei ⇒ Verweigerung (dann nur Neuinstallation mit `--erase`). Die Quelle wird nie geschrieben; Test: Image-Prüfsumme vor/nach.

## 4. Arbeitspakete

| Paket | Inhalt | Prüfung | Vorschlag |
|---|---|---|---|
| 4.0 | **RW-Qualifikation ext2 als `/` im Gast** auf Basis der Etappe-1-Probe: CRUD, 10 000 Dateien, Volllaufen/ENOSPC, Rename/Delete, Neustart, `e2fsck -fn` offline Exit 0, je 32 und 128 GiB. **Ohne Go kein `Root`-Profil** | Tester (Gast), Auswertung Leitung | senior (Probe), aot-probe/tripleclerk |
| 4.1 | `SystemPaths` (zwei Profile) und Umstellung aller Aufrufer aus §1; `Legacy` erzeugt exakt die heutigen Pfade | Hosttests beide Profile; bestehende Tests unverändert grün; nativer Build, Normalboot, Datenplatte A–F (Regression) | senior |
| 4.2 | Gesperrte Systempfade `/etc/zehos`, `/var/lib/zehos` in `FileAccessControl` (auch Admin) | Hosttests: Lesen/Schreiben/Listen/Suche/Umbenennen hinein und heraus verweigert | huang oder jensen |
| 4.3 | Profilwahl beim Boot (§2) als reine Funktion `LayoutProfile.Decide(identity, gate, marker, rootEmpty)` plus Cosmos-Anbindung (Root-Mount, `/boot/efi`, Sync bei Energieaktion) | Hosttests jede Tabellenzeile; Gast G1–G3 | senior |
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
3. `LayoutProfile.Decide`: jede Zeile aus §2; insbesondere „kein Marker, Root nicht leer“ ⇒ gesperrt, nie Ersteinrichtung.
4. `LayoutMigration`: Zuordnung je Kategorie; Datei ohne Zuordnung nach `/srv/legacy-data`; Zielkonflikt ⇒ nichts geschrieben; zu wenig Platz ⇒ nichts geschrieben; ACL in anderer Schreibweise ⇒ Zieleintrag mit tatsächlichem Namen; ACL ohne Datei ⇒ protokolliert; ENOSPC beim Kopieren; Abbruch nach jedem der Schritte 1–7 ⇒ kein Marker; Wiederholung nach jedem Abbruch ⇒ gleiches Endergebnis wie ohne Abbruch; fremde Datei auf Ziel ⇒ Verweigerung; Quelle byteidentisch (Fake zählt Schreibzugriffe: 0).
5. Marker: CRC, Größe, Formatversion, Temp + Rename; beschädigt ⇒ `Unclear`.
6. Energieaktion im `Root`-Profil: Sync-Fehler bricht ab.

**Gasttests (QEMU+OVMF, danach VMware; nur auf Ansage, ein Gast):**
- G1 frische 7a-Installation 32 GiB → Ersteinrichtung → Profil `Root`, Home unter `/home/<name>`, Desktopeinstellungen in `.config/zehos`, Neustart, `e2fsck -fn` offline Exit 0.
- G2 `/boot/efi` verschachtelt eingehängt, `update install` (lokal) schreibt nach `/boot/efi/boot/…`, Rückfalleintrag `.bak` startet.
- G3 Marker offline gelöscht ⇒ keine Einrichtung, `security recover` verweigert, Root lesbar.
- G4 Migration 64-MiB-Bestandsplatte → neue 32-GiB-Platte: Anmeldung mit altem Kennwort, Dateien und ACLs gleich (Hashvergleich), Pakete starten (NetSurf), alte Platte Prüfsumme unverändert und weiter bootfähig.
- G5 Migration mit vollem Ziel (kleine Root-Partition per Testlayout) und mit Abbruch (Gast hart beenden) ⇒ Ziel `Unclear`, Wiederholung erfolgreich.
- G6 64 GiB und 128 GiB: Migration, Neustart, `e2fsck -fn`.

## 7. Offene Punkte

1. *Entschieden 11:23:* ESP unter `/boot/efi` (weicht bewusst von der Zielstruktur ab).
2. *Entschieden 11:23:* übernommene sonstige Dateien unter `/srv/legacy-data`, ACLs übernommen.
3. **7a:** Liste der vom Installer auf Root angelegten Einträge („Root leer“, §2) und ob der Installer die Mountpunktverzeichnisse anlegt.
4. **Cosmos:** Schreibendes ext2 als `/` ist bisher nur in der isolierten Probe belegt (Etappe 1: CRUD, Inode-Wiederverwendung); Paket 4.0 schließt diese Lücke vor jedem `Root`-Profil. G1/G6 prüfen danach den Produktpfad.
5. **Cosmos:** Sync/Flush eines nicht aushängbaren `/` beim Herunterfahren – API in 3.0.97 prüfen (`IVfsSuperblock`-Sync).
6. **App-Speicher:** §5 mit dem ClassiCube-Auftrag abstimmen, bevor 13.4 einen `/data`-Pfad festschreibt.
7. VMware: eigene AHCI-Platte wie in Etappe 2. Zeitbudget: Quelle kann eine FAT-Datenplatte bis 128 GiB sein (Etappe 2); bei ≈ 100 MB/s unter KVM dauert Lesen plus Rücklesen einer vollen Platte über eine Stunde. Fortschrittsanzeige und Abbruch per Taste gehören deshalb in 4.5.
