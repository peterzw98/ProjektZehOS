# Etappe 5b/6b: Cosmos-VFS-Treiber für `/proc`, `/sys`, `/dev` – Entwurf

Stand: 17. November 2026, Basis `origin/master` @ `0a5547e` (E5a/E6a-Hostlogik #147 gemergt: `SystemInfoSnapshot`, `SystemInfoFile`, `DeviceFileSystem`). **Nur Entwurf**, kein Code, nichts im Gast. Cosmos-Aussagen aus der Quelle `efcf20c` (= 3.0.97). Grundlage: [Matrix](2026-11-17-system-files-inventory.md), [Spezifikation „Alles als Datei“](2026-11-17-storage-linux.md), [Etappe 4](2026-11-17-storage-etappe4-design.md).

## 0. Entscheidungen (Vorschlag)

1. **Ein eigener Dateisystemtyp je Baum** (`zehos-proc`, `zehos-sys`, `zehos-dev`), registriert per `VfsManager.RegisterFileSystem`, eingehängt auf `/proc`, `/sys` (beide `MountFlags.ReadOnly`) und `/dev` (schreibbar nur für `/dev/null`). Kein Overlay, kein zweites VFS.
2. **In beiden Profilen.** `TryMount` verlangt kein Verzeichnis am Mountpunkt (`VfsManager.cs:146`), die Bäume funktionieren deshalb auch im `Legacy`-Profil ohne `/` – E5b/E6b müssen nicht auf das Root-Profil warten. Im `Root`-Profil legt der Installer die Mountpunktverzeichnisse an (Etappe 4 §2), damit `ls /` sie zeigt.
3. **Snapshot je Open beim ersten Lesezugriff.** Cosmos kennt keinen Open-Hook im Treiber: `VfsManager.TryOpenFile` erzeugt selbst ein `VfsOpenFile` (`VfsManager.cs:432`), `Release` kommt beim Schließen (`VfsFileHandle.cs:98`). Der Treiber hält je `IVfsOpenFile` (Referenzschlüssel) ein `SystemInfoFile`, erzeugt beim ersten `Read`/`Seek`, entfernt in `Release`. Folge: dieselbe Semantik wie „beim Öffnen“ für jeden Leser, der nach dem Öffnen liest; zwei Opens sind unabhängig.
4. **`GetAttr` meldet Größe 0** für `/proc`/`/sys` (wie Linux, kein Snapshot beim `stat`). Alle ZehOS-Leser lesen deshalb bis EOF mit Grenze (`FileService.ReadText` tut das schon; `ReadBytes` wird für Größe 0 auf begrenztes Lesen bis EOF umgestellt).
5. **Zugriff über `FileService` ausschließlich lesend und per fester Tabelle**, unabhängig von ACL und Pfadwächter (die Bäume sind fremde Mounts und würden sonst immer abgelehnt). Keine ACL-Einträge, keine UID/GID-Wirkung.
6. **Wasm bekommt nichts.** Paket-Apps sehen nur ihr Paketverzeichnis über `PREOPEN_FD` (`zehos_wasm.c:325-345`, `is_safe_path`); daran ändert sich nichts. Test belegt, dass `/proc`, `/sys`, `/dev` aus einer App nicht erreichbar sind.

## 1. Dateien und Rechte

| Pfad | Quelle (E5a/E6a) | Modus (`VfsMode`) | Lesen | Schreiben |
|---|---|---|---|---|
| `/proc/version` | `SystemInfoSnapshot.Version` | Datei 0444 | jedes angemeldete Konto | – |
| `/proc/uptime` | `.Uptime` | Datei 0444 | jedes Konto | – |
| `/proc/meminfo` | `.Memory` | Datei 0444 | jedes Konto | – |
| `/proc/mounts` | `.Mounts` (aus `VfsManager.Mounts`) | Datei 0444 | jedes Konto | – |
| `/proc/log` | `.Log` (Allowlist-Projektion) | Datei 0400 | **nur Administrator** | – |
| `/sys/kernel/components` | `.Components` | Datei 0444 | jedes Konto | – |
| `/sys/devices` | `.Devices` | Datei 0444 | jedes Konto | – |
| `/sys/block` | `.Blocks` | Datei 0444 | jedes Konto | – |
| `/dev/null` | `DeviceFileSystem` | Zeichengerät 0666 | jedes Konto (sofort EOF) | jedes Konto (verworfen) |
| `/dev/zero` | `DeviceFileSystem` | Zeichengerät 0444 | jedes Konto (Nullbytes, je Read ≤ 64 KiB) | – |

`/sys/devices` und `/sys/block` sind in dieser Stufe **Dateien** mit einem Datensatz je Gerät (so liefert E6a), nicht die Verzeichnisse `/sys/devices/<id>/` der Spezifikation; Verzeichnisse je Gerät sind ein späterer Ausbau. `/proc/threads` fehlt weiter (kein öffentlicher Schedulerzugriff, Matrix).

Ohne Anmeldung: nichts lesbar (wie heute ohne Konto). Alle anderen Operationen (`Create`, `Mkdir`, `Unlink`, `Rmdir`, `Rename`, `SetAttr`, `Symlink`, Schreiben auf andere Dateien) liefern im Treiber `false` und werden in `FileService` schon vorher als „nur lesbar“ abgelehnt.

## 2. Treiberaufbau

Getrennt nach Projektregel Hardware/Logik:

- **Rein, hostgetestet** (`ProjektZeh/Storage/Virtual/`): `VirtualTree` – fester Baum je Typ aus Name, Art (Verzeichnis/Datei/Zeichengerät), Modus und Erzeuger (`Func<ResultObject<byte[]>>` bzw. Geräteart); `Lookup`, `List` mit Ordinal-Namen. `VirtualAccess.Check(path, session, write)` – die Tabelle aus §1. Lese-/Seek-Logik über `SystemInfoFile.Read(offset, …)` (vorhanden).
- **Cosmos-Adapter** (`ProjektZeh/Storage/CosmosVirtualFileSystem.cs`, nur Kernelbuild): `IVfsFileSystemType`, `IVfsSuperblock` (`IsReadOnly` true für proc/sys, `BlockSize` 4096, `MaxNameLength` 255), `IInodeOperations` (nur `Lookup`, `ReadDir`, `GetAttr`; Rest `false`), `IFileOperations`:
  - `Read`: liest ab `openFile.Position` und erhöht sie **nicht** selbst (`VfsFileHandle.Read` erhöht, `VfsFileHandle.cs:41-42`; Matrix-Befund).
  - `Seek`: `Begin`/`Current` innerhalb `[0, Länge]`; `End` erzeugt bei Bedarf den Snapshot. Für `/dev/*` nur Position 0.
  - `Write`: nur `/dev/null` (Länge zurück), sonst −1/Fehler.
  - `Fsync`: `true` ohne Wirkung. `Release`: Snapshot entfernen.
  - `ISuperblockOperations.Sync`/`Drop`: ohne Wirkung; `StatFs` mit 0 Blöcken.
- Erzeugerfehler (`SystemInfoFile.Open` liefert Fehler) ⇒ `Read` liefert −1; `FileService` meldet „Systemdaten nicht verfügbar“ statt einer Ausnahme.

## 3. Leser ohne EOF und Geräte in ZehOS-Abläufen

- `ReadText`: liest schon bis EOF mit Grenze 64 Ki Zeichen und meldet Überschreitung als Fehler – `/dev/zero` endet so begrenzt mit Fehler, nicht in einer Endlosschleife.
- `ReadBytes`: bei gemeldeter Länge 0 begrenzt bis EOF lesen (Grenze = `maximumLength`), sonst unverändert.
- **Kopieren, Prüfsumme, Suche, Paketinstallation** prüfen vor dem Lesen den Typ: Zeichengeräte ⇒ Ablehnung „Gerätedatei“. Die Suche steigt nicht in `/proc`, `/sys`, `/dev` ab (heute schon fremde Mounts, bleibt so). `cp /dev/zero x` darf nie die Platte füllen.
- Dateimanager: Größe 0 wird für diese Bäume als „–“ angezeigt; Öffnen im Textbetrachter nutzt `ReadText`.

## 4. Arbeitspakete

| Paket | Inhalt | Prüfung |
|---|---|---|
| E5b.1 | `VirtualTree`, `VirtualAccess` (rein) + Tests | Hosttests |
| E5b.2 | `FileService`: Zweig für virtuelle Bäume vor ACL/Pfadwächter (Lesen per Tabelle, alles andere abgelehnt), `ReadBytes` bis EOF bei Länge 0 | Hosttests |
| E5b.3 | Cosmos-Adapter `zehos-proc`/`zehos-sys`, Registrierung und Mount beim Boot (beide Profile), Mountfehler nur protokolliert, Boot läuft weiter | nativer Build, Gast G1–G4 |
| E6b.1 | `zehos-dev` mit `null`/`zero`, Gerätetyp-Ablehnung in Kopieren/Prüfsumme/Paketinstallation | Hosttests, Gast G5–G6 |
| E6b.2 | Wasm-Gegenprobe | Gast G7 |

## 5. Tests

**Hosttests:**
1. `VirtualTree`: jede Zeile §1 auffindbar, unbekannter Name ⇒ fehlt, `List` sortiert, Groß-/Kleinschreibung Ordinal (`/proc/Version` fehlt).
2. `VirtualAccess`: Konto liest öffentliche Dateien; `/proc/log` nur Admin; ohne Konto nichts; jede Schreib-/Umbenenn-/Löschabsicht ⇒ abgelehnt, außer Schreiben auf `/dev/null`.
3. Snapshot je Open: zwei Opens mit wechselndem Erzeuger liefern je eigene, in sich gleiche Bytes; Teilreads 1 Byte/7 Byte/4096 Byte ergeben zusammen denselben Snapshot; nach EOF bleibt EOF; Seek `End` = Länge.
4. Erzeuger wirft ⇒ Fehlerergebnis, keine Ausnahme bis `FileService`.
5. `ReadBytes` mit gemeldeter Länge 0 und 10 KiB Inhalt ⇒ 10 KiB; mit endlosem Strom ⇒ Fehler bei Grenze.
6. Kopieren/Prüfsumme von einem Zeichengerät ⇒ abgelehnt, 0 Bytes geschrieben.

**Gasttests (QEMU, dann VMware; Ansage, ein Gast):**
- G1 `ls /proc`, `cat /proc/version` (Version = Build), `cat /proc/meminfo` (frei ≤ gesamt).
- G2 `cat /proc/mounts` zeigt `/data`, `/proc`, `/sys`, `/dev` mit Schreibschutz-Spalte.
- G3 `cat /proc/log` als Benutzer verweigert, als Admin ohne MAC-Adressen, Befehle oder Pfade (Gegenprobe mit Netzwerk aktiv).
- G4 `rm`, `mv`, `mkdir`, Schreiben unter `/proc` und `/sys` ⇒ abgelehnt, Shell bleibt bedienbar.
- G5 `cat /dev/zero` endet mit Grenzfehler, `echo x > /dev/null` gelingt, `cat /dev/null` leer.
- G6 `cp /dev/zero /data/x` abgelehnt, `/data` unverändert (freier Platz gleich).
- G7 Paket-App versucht `/proc/version`, `../proc/version`, `/dev/zero` ⇒ jeweils WASI-Fehler.
- G8 Boot ohne Datenplatte (VMware-Profil): `/proc` lesbar, Boot unverändert.

## 6. Offene Punkte

1. **Leitung:** Rechte §1 bestätigen, insbesondere `/proc/mounts` für alle Konten (zeigt Gerätenamen, keine Pfade aus Diagnosen) und `/proc/log` nur Admin.
2. Ob die System.IO-Schicht von Cosmos (`InteropSysFilePlug`) für Zeichengeräte und Größe 0 etwas anderes tut als für Dateien (Länge, `pread`), ist ungeprüft; E5b.3/E6b.1 prüfen das im Gast zuerst.
3. Lebensdauer der Snapshot-Tabelle: verlässt ein Aufrufer ein Handle ohne `Dispose`, bleibt der Eintrag; Grenze 64 offene Snapshots je Baum, darüber `Read` ⇒ Fehler. `shortcut:` keine Zeitabräumung, nachrüsten, falls G-Tests Lecks zeigen.
4. Verzeichnisse je Gerät unter `/sys/devices/<id>/` und `/dev/<block-id>` sind nicht Teil dieser Stufe (Spezifikation: später, Schreibzugriff nur mit eigener Freigabe).
