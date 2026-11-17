# Etappe 4.4: `LayoutMigration` – Umsetzungsentwurf

Stand: 17. November 2026, Basis `origin/master` (Etappe-4-Entwurf #145 gemergt). Konkretisiert [Etappe 4 §3](2026-11-17-storage-etappe4-design.md#3-migration-legacy--root) für das Paket 4.4: **reine Hostlogik mit Tests**, keine Cosmos-Anbindung, kein Shellbefehl (das ist 4.5). Bezüge auf Code: `origin/master`, `SystemPaths` aus `task/storage-e4-1` (`318867c`), `InstallMarker` aus `task/storage-7a-identity`. Nichts davon ist gebaut oder getestet.

## 1. Schnitt

| Baustein | Aufgabe | Abhängigkeit |
|---|---|---|
| `MigrationInventory` | Quelle lesen: alle Dateien und Verzeichnisse unter `SystemPaths.Legacy.AdminRoot` mit tatsächlichem Namen, Größe, SHA-256 | `System.IO`, `SHA256` (im Kernel schon genutzt, `CosmosSecurityProbe`, `TlsTransport`) |
| `MigrationPlan.Create(inventory, legacy, root)` | **rein**: Zuordnung jedes Eintrags, Ausschlüsse, Konflikte, Platzbedarf | keine |
| `MigrationAcl.Rewrite(securityText, plan)` | **rein**: Kontendatei umschreiben (Konten unverändert, ACL-Pfade zugeordnet) | `FileAccessControl` |
| `MigrationJournal` | **rein** Parse/Format; Schreiben über das Ziel | keine |
| `LayoutMarker` | Root-Marker `state=fresh/migrating/ready` | Format wie `InstallMarker` (gleiche crc32-Regel, eigene Felder) |
| `LayoutMigration.Run(...)` | Ablauf Schritte 1–10 aus Etappe 4 §3 | `IMigrationTarget` |
| `IMigrationTarget` | Schreibseite: `CreateDirectory`, `WriteFile` (Temp + Rename), `AppendJournal` (mit Flush), `Delete`, `ReadBack`, `FreeBytes`, `FreeInodes`, `Sync` | Hostfake in Tests; Cosmos-Umsetzung erst 4.5 |

Die Quelle wird nur über `System.IO` gelesen (Hosttests mit Temp-Verzeichnis). Für die Schreibseite ist eine Schnittstelle nötig, weil ENOSPC, Inode-Mangel, Abbruch nach jedem Schritt und Absturz zwischen Datei und Journal auf dem Host nicht anders reproduzierbar sind; sie bekommt genau die oben genannten Methoden, keine weiteren.

## 2. Zuordnungsregeln (`MigrationPlan`)

Relativ zu `/data`, Vergleich der Quelle **OrdinalIgnoreCase** (FAT), Ziel **Ordinal**; Zielnamen aus dem tatsächlichen Namen der Verzeichnisauflistung.

| Quelle | Ziel | Bemerkung |
|---|---|---|
| `.zehos-security.db` | `/etc/zehos/.zehos-security.db` | Inhalt über `MigrationAcl` umgeschrieben (§3) |
| `.zehos-commands.db` | `/etc/zehos/.zehos-commands.db` | unverändert (enthält nur Befehlsnamen) |
| `.zehos-security-recovery-*.db` | `/etc/zehos/` gleicher Name | unverändert; alte ACL-Pfade darin bleiben historische Daten |
| `.zehos-update.conf` | `/etc/zehos/update.conf` | unverändert |
| `.zehos-packages/**` | `/var/lib/zehos/packages/**` | 1:1; `PackageStore` speichert nur relative Pfade (`Path.Combine(root, …)`, `PackageStore.cs:37-39`), geprüft per Test (§5, P3 d) |
| `.zehos-apps/**` | `/var/lib/zehos/apps/**` | 1:1 (`SystemPaths.AppState`) |
| `home/<name>/zehos-desktop.conf` | `/home/<name>/.config/zehos/desktop.conf` | `.config/zehos` wird angelegt |
| `home/<name>/**` sonst | `/home/<name>/**` | |
| `boot/**` | – | ausgeschlossen, protokolliert (alte ESP-Artefakte) |
| `.zehos-*.tmp`, `.zehos-*.bak` (in jedem Verzeichnis) | – | ausgeschlossen, protokolliert |
| jede andere `.zehos-*`-Datei | – | **Konflikt** (unbekannte Systemdatei, fail-closed) |
| alles andere | `/srv/legacy-data/<relativer Pfad>` | Lead-Entscheid 11:23 |

**Konflikte (stoppen vor jedem Schreibzugriff, mit Liste):** zwei Quellen auf dasselbe Ziel (Ordinal), unbekannte `.zehos-*`-Datei, Zielpfad besteht `PathGuard.Check` nicht (Komponentenregeln Etappe 3), Konto-Name, der keine gültige Pfadkomponente ist, `home/<name>` ohne Konto (wird nach `/srv/legacy-data/home/<name>` gelegt, **kein** Konflikt, aber protokolliert).

**Platzbedarf:** je Datei `ceil(size / 4096) * 4096`, je Verzeichnis 4096; Inodes = Dateien + Verzeichnisse; jeweils plus 10 % Reserve, mindestens 1 MiB bzw. 64 Inodes (Etappe 4 §3 Schritt 4).

## 3. ACL-Umschreibung (`MigrationAcl`)

Eingabe: Text von `.zehos-security.db` (`ZEHOS-ACCOUNTS-1`, Kontozeilen, `#ACL\n`, `ZEHOS-SECURITY-1`-Block; `AccountService.cs:13-14,105-119`).

1. Kontozeilen **byteidentisch** übernehmen (Id, Name, Rolle, Hash).
2. ACL-Block mit `new FileAccessControl("/data", OrdinalIgnoreCase).Restore` laden; Fehler ⇒ Abbruch.
3. Jeden Eintrag über die Planzuordnung abbilden. Gibt es zum Eintrag keine Datei (Groß-/Kleinschreibung über OrdinalIgnoreCase verglichen), wird er protokolliert und weggelassen.
4. Wurzelrechte des neuen Baums nach dem Muster von `SetupWork` (`AccountService.cs:282-287`): `/` und `/home` erhalten Eigentümer und `*`-Rechte des bisherigen `/data`- bzw. `/data/home`-Eintrags; `/srv` und `/srv/legacy-data` bekommen die Rechte des bisherigen `/data`-Eintrags. `/etc`, `/var` usw. bekommen **keinen** Eintrag (für `FileService` ohnehin gesperrt, Paket 4.2).
5. Neu serialisieren mit `new FileAccessControl("/", Ordinal)`; das Ergebnis muss mit derselben Instanzart `Restore` bestehen (Rücklesen), sonst Abbruch.

Benötigt eine kleine interne Aufzählung der Einträge in `FileAccessControl` (`internal IEnumerable<(string Path, FilePermissions Rights)> Entries`) – keine neue öffentliche API.

## 4. Journal und Root-Marker

**Journal** `/etc/zehos/.zehos-migration-journal`, eine Zeile je Ereignis, Pfade Base64 (wie `FileAccessControl.Encode`), jede Zeile sofort geflusht:

```
ZEHOS-MIGRATION-1 <quellinventar-sha256>
D <pfad>                    Verzeichnis angelegt
W <pfad>                    Absicht: Datei wird geschrieben
F <pfad> <sha256>           Datei geschrieben und zurückgelesen
```

`Parse` akzeptiert eine abgeschnittene **letzte** Zeile (Absturz beim Anhängen) und behandelt sie als nicht vorhanden; jede andere ungültige Zeile ⇒ Journal ungültig ⇒ Wiederholung verweigert.

**Wiederholung:** erlaubt, wenn Root-Marker `state=migrating` und jede Datei/jedes Verzeichnis auf Root entweder vom Installer stammt (`/boot/efi`, `/mnt`, `/proc`, `/sys`, `/dev`, `/etc/zehos`, Root-Marker – Liste aus 7a S4) oder im Journal als `D`, `W` oder `F` steht. Dann: Dateien aus `W`/`F` löschen, Verzeichnisse aus `D` von unten nach oben löschen, Journal löschen, neu beginnen. Abweichender Quellinventar-Hash ist kein Hindernis (die Quelle darf sich zwischen zwei Läufen geändert haben), wird aber protokolliert.

**Root-Marker** (`LayoutMarker`, `/etc/zehos/.zehos-layout`): Zeilen `format=1`, `state=fresh|migrating|ready`, `disk=<GUID>`, bei `ready` nach Migration zusätzlich `source=<sha256>`, `files=<zahl>`; letzte Zeile `crc32=<hex>` wie `InstallMarker`. ≤ 512 Byte. Gemeinsame CRC-/Zeilenlogik mit `InstallMarker` wiederverwenden statt kopieren.

## 5. Tests (zuerst, `ProjektZeh.Tests/LayoutMigrationTests.cs`)

**Plan (rein):**
1. Jede Zeile der Tabelle §2 einzeln; Desktopdatei landet in `.config/zehos/desktop.conf`.
2. `boot/**`, `.tmp`, `.bak` ausgeschlossen und im Protokoll.
3. Unbekannte `.zehos-foo` ⇒ Konflikt, kein Schreibzugriff.
4. `home/x` ohne Konto ⇒ `/srv/legacy-data/home/x`.
5. Zwei Quellen auf dasselbe Ziel ⇒ Konflikt (konstruierter Fall: `zehos-desktop.conf` und vorhandenes `.config/zehos/desktop.conf` im selben Home).
6. Platz: viele 1-Byte-Dateien ⇒ Bedarf je 4 KiB; Inodebedarf; Reserve; `FreeBytes`/`FreeInodes` knapp darunter ⇒ verweigert, 0 Schreibzugriffe.
7. Ungültige Pfadkomponente im Zielpfad ⇒ Konflikt.

**ACL (rein):**
8. Kontozeilen byteidentisch; Hash unverändert.
9. Eintrag `/data/Home/Anna/Bild.BMP` bei tatsächlichem Namen `home/anna/bild.bmp` ⇒ Ziel `/home/anna/bild.bmp`.
10. Eintrag ohne Datei ⇒ protokolliert, fehlt im Ergebnis.
11. Wurzelrechte `/`, `/home`, `/srv`, `/srv/legacy-data` wie §3.4; kein Eintrag unter `/etc` oder `/var`.
12. Ergebnis besteht `Restore` mit Wurzel `/`, Ordinal; beschädigter Eingabetext ⇒ Abbruch.

**Journal und Marker (rein):**
13. Format/Parse rund; abgeschnittene letzte Zeile toleriert, beschädigte mittlere Zeile ⇒ ungültig.
14. Marker: jeder `state`, CRC falsch, > 512 Byte, unbekanntes Feld ⇒ ungültig.

**Ablauf (`Run` mit Hostfake-Ziel):**
15. Erfolg: alle Dateien mit gleichem Hash auf dem Ziel, Marker `ready` als **letzter** Schreibzugriff, danach Sync zweifach.
16. Reihenfolge je Datei: `W` geflusht vor dem ersten Byte der Datei, `F` danach.
17. Abbruch nach jedem Schritt 1–9 und nach jeder einzelnen Datei (Fake wirft) ⇒ nie `state=ready`; Schreibsperre wieder frei (`finally`).
18. ENOSPC und Inodemangel mitten im Kopieren ⇒ Abbruch, Marker `migrating`.
19. Absturz zwischen Datei und `F` sowie zwischen `W` und Datei ⇒ Wiederholung erlaubt, Endergebnis gleich Test 15.
20. Fremde Datei auf dem Ziel ⇒ Wiederholung verweigert.
21. Quelle ändert sich (Fake ändert eine Quelldatei nach Schritt 2) ⇒ Inventarvergleich Schritt 8 bricht ab.
22. Quelle unverändert: Temp-Verzeichnis vor/nach byteidentisch (Hash über alle Dateien), keine neuen Dateien.
23. Paketbestand: installiertes Testpaket nach der Migration mit `PackageStore` (Wurzel `/var/lib/zehos/packages` im Fake-Baum) lesbar, Manifest/Signatur gültig.

## 6. Nicht in 4.4

Schreibsperre selbst (Kennzeichen in `FileAccessControl`/`PackageStore`/`UpdateController`, 4.5 zusammen mit dem Befehl), Cosmos-`IMigrationTarget` (ext2 unter `/mnt/target`, `FreeInodes` aus dem Superblock), Fortschrittsanzeige und Tastenabbruch, Gasttests G4–G6.

## 7. Offene Punkte

1. 7a S4: endgültige Liste der vom Installer auf Root angelegten Einträge (§4, Wiederholungsregel).
2. Rechte für `/srv` und `/srv/legacy-data` (§3.4): Vorschlag „wie bisher `/data`“ – Leitung bestätigen.
3. `FreeInodes` ist in Cosmos 3.0.97 nur über den Superblock lesbar (`s_free_inodes_count`); ob `VfsManager`/`VfsStatFs` ihn liefert, prüft 4.5.
