# Etappe 5b/6b: Cosmos-VFS-Treiber für `/proc`, `/sys`, `/dev` – Entwurf

Stand: 17. November 2026, Basis `origin/master` @ `0a5547e` (E5a/E6a-Hostlogik #147 gemergt: `SystemInfoSnapshot`, `SystemInfoFile`, `DeviceFileSystem`). **Nur Entwurf**, kein Code, nichts im Gast. Cosmos-Aussagen aus der Quelle `efcf20c` (= 3.0.97). Grundlage: [Matrix](2026-11-17-system-files-inventory.md), [Spezifikation „Alles als Datei“](2026-11-17-storage-linux.md), [Etappe 4](2026-11-17-storage-etappe4-design.md).

## 0. Entscheidungen (Vorschlag; Review huang zu `0633a37` eingearbeitet)

1. **Ein eigener Dateisystemtyp je Baum** (`zehos-proc`, `zehos-sys`, `zehos-dev`), registriert per `VfsManager.RegisterFileSystem`, eingehängt auf `/proc`, `/sys` (beide `MountFlags.ReadOnly`) und `/dev` (schreibbar nur für `/dev/null`). Kein Overlay, kein zweites VFS.
2. **In beiden Profilen.** `TryMount` verlangt kein Verzeichnis am Mountpunkt (`VfsManager.cs:146`), die Bäume funktionieren deshalb auch im `Legacy`-Profil ohne `/` – E5b/E6b müssen nicht auf das Root-Profil warten. Im `Root`-Profil legt der Installer die Mountpunktverzeichnisse an (Etappe 4 §2), damit `ls /` sie zeigt.
3. **Snapshot je Open beim ersten Lesezugriff, ohne Tabelle.** Cosmos kennt keinen Open-Hook im Treiber, `VfsManager.TryOpenFile` löst den Pfad aber je Open neu auf und baut ein eigenes `VfsOpenFile` (`VfsManager.cs:432`). Deshalb liefert `Lookup` je Aufruf ein **frisches Inode-Objekt mit eigener `IFileOperations`-Instanz**, die den Snapshot beim ersten `Read`/`Seek` erzeugt und hält (huang P3 a). Keine Tabelle je `IVfsOpenFile`, keine Abhängigkeit von `Release`, keine Obergrenze offener Snapshots.
4. **`GetAttr` meldet Größe 0** für `/proc`/`/sys` (wie Linux, kein Snapshot beim `stat`). Alle ZehOS-Leser lesen deshalb bis EOF mit Grenze (`FileService.ReadText` tut das schon; `ReadBytes` wird für Größe 0 auf begrenztes Lesen bis EOF umgestellt).
5. **`FileService` bekommt drei schmale Einstiege, die Pipeline bleibt unverändert** (huang P2-1): `ReadVirtual(path, session)`, `ListVirtual(path)`, `WriteNull(path)`. Sie lesen **direkt aus den Erzeugern** (`SystemInfoSnapshot`, `DeviceFileSystem`), nicht über `System.IO`, und werden nur von `cat`, Umleitung, Textbetrachter und `ls` aufgerufen. `ResolvePath`/`Authorize` und alle übrigen Methoden bleiben wie heute: für sie sind `/proc`, `/sys`, `/dev` fremde Mounts und werden in beiden Profilen abgelehnt. `cp /dev/zero`, `pkg install-local /dev/…`, Suche, Prüfsumme usw. kommen damit strukturell nie dorthin; §3 ist zusätzliche Absicherung. Direktes Lesen aus dem Erzeuger schließt auch aus, dass bei fehlgeschlagenem Mount eine darunterliegende Datei auf Root gelesen wird (huang P3 d).
6. **Wasm bekommt nichts.** Paket-Apps sehen nur ihr Paketverzeichnis über `PREOPEN_FD` (`zehos_wasm.c:325-345`, `is_safe_path`); daran ändert sich nichts. Test belegt, dass `/proc`, `/sys`, `/dev` aus einer App nicht erreichbar sind.
7. **Warum trotzdem ein Cosmos-Treiber** (huang P2-2, zur Bestätigung durch die Leitung): die Spezifikation verlangt „alles als Datei“ – Verzeichnisse und `stat` über dieselbe Dateischnittstelle wie bei normalen Dateien, `/proc/mounts` mit den virtuellen Mounts selbst, `ls /` im `Root`-Profil, und spätere Verbraucher außerhalb von `FileService` (Dateimanager über `System.IO`, Etappe 6 Geräte). Der Treiber liefert diese Sichtbarkeit; der Inhalt für ZehOS-Bedienung kommt über die Einstiege aus §0.5. Damit ist E5b.1/E5b.2 allein schon nutzbar, E5b.3/E6b.1 machen die Bäume im VFS sichtbar. Alternative der Leitung: E5b.3/E6b.1 zurückstellen, bis ein Verbraucher außerhalb `FileService` sie braucht.

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
  - `Seek`: jede nichtnegative Position wird angenommen, auch hinter EOF (`Read` liefert dort 0, wie die Schnittstelle es erlaubt); `End` erzeugt bei Bedarf den Snapshot. Für `/dev/*` ebenso, die Position hat dort keine Bedeutung (huang P3 b).
  - `Write`: nur `/dev/null` (Länge zurück), sonst −1/Fehler.
  - `Fsync`: `true` ohne Wirkung. `Release`: ohne Wirkung (der Snapshot hängt an der Open-Instanz, §0.3).
  - `ISuperblockOperations.Sync`/`Drop`: ohne Wirkung; `StatFs` mit 0 Blöcken; `CosmosSystemInfo.DiskSpace` und `df` prüfen 0 Blöcke vor jeder Division (huang P3 e).
- Erzeugerfehler (`SystemInfoFile.Open` liefert Fehler) ⇒ `Read` liefert −1; `FileService` meldet „Systemdaten nicht verfügbar“ statt einer Ausnahme.

## 3. Leser ohne EOF und Geräte in ZehOS-Abläufen

- `ReadText`: liest schon bis EOF mit Grenze 64 Ki Zeichen und meldet Überschreitung als Fehler – `/dev/zero` endet so begrenzt mit Fehler, nicht in einer Endlosschleife.
- `ReadBytes`: bei gemeldeter Länge 0 begrenzt bis EOF lesen (Grenze = `maximumLength`), sonst unverändert. Das betrifft alle Pfade; eine echte leere Datei ergibt weiter 0 Bytes (Test, huang P3 f).
- **Kopieren, Prüfsumme, Suche, Paketinstallation** erreichen die Bäume nicht (§0.5); zusätzlich prüfen sie vor dem Lesen den Typ: Zeichengerät nur über `(mode & FileTypeMask) == CharacterDevice` bzw. die `VfsStat`-Helfer, nie mit `HasFlag` (Typwerte überlappen, `VfsMode.cs:15-21`), ⇒ Ablehnung „Gerätedatei“ (huang P3 c). Die Suche steigt nicht in `/proc`, `/sys`, `/dev` ab (heute schon fremde Mounts, bleibt so). `cp /dev/zero x` darf nie die Platte füllen.
- Dateimanager: Größe 0 wird für diese Bäume als „–“ angezeigt; Öffnen im Textbetrachter nutzt `ReadText`.

## 4. Arbeitspakete

| Paket | Inhalt | Prüfung |
|---|---|---|
| E5b.1 | `VirtualTree`, `VirtualAccess` (rein) + Tests | Hosttests |
| E5b.2 | `FileService`: Einstiege `ReadVirtual`/`ListVirtual`/`WriteNull` (direkt aus den Erzeugern, Rechte per `VirtualAccess`), Aufrufe in `cat`, Umleitung, Textbetrachter, `ls`; `ReadBytes` bis EOF bei Länge 0 | Hosttests |
| E5b.3 | Cosmos-Adapter `zehos-proc`/`zehos-sys`, Registrierung und Mount beim Boot (beide Profile), Mountfehler nur protokolliert, Boot läuft weiter | nativer Build, Gast G1–G4 |
| E6b.1 | `zehos-dev` mit `null`/`zero`, Gerätetyp-Ablehnung in Kopieren/Prüfsumme/Paketinstallation | Hosttests, Gast G5–G6 |
| E6b.2 | Wasm-Gegenprobe | Gast G7 |

## 5. Tests

**Hosttests:**
1. `VirtualTree`: jede Zeile §1 auffindbar, unbekannter Name ⇒ fehlt, `List` sortiert, Groß-/Kleinschreibung Ordinal (`/proc/Version` fehlt).
2. `VirtualAccess`: Konto liest öffentliche Dateien; `/proc/log` nur Admin; ohne Konto nichts; jede Schreib-/Umbenenn-/Löschabsicht ⇒ abgelehnt, außer Schreiben auf `/dev/null`.
3. Snapshot je Open: zwei Opens mit wechselndem Erzeuger liefern je eigene, in sich gleiche Bytes; Teilreads 1 Byte/7 Byte/4096 Byte ergeben zusammen denselben Snapshot; nach EOF bleibt EOF; Seek `End` = Länge; Seek hinter EOF erlaubt, `Read` dort 0.
4. Erzeuger wirft ⇒ Fehlerergebnis, keine Ausnahme bis `FileService`.
5. `ReadBytes` mit gemeldeter Länge 0 und 10 KiB Inhalt ⇒ 10 KiB; mit endlosem Strom ⇒ Fehler bei Grenze; echte leere Datei ⇒ 0 Bytes.
6. Kopieren/Prüfsumme/Suche/`pkg install-local` mit Pfaden unter `/proc`, `/sys`, `/dev` ⇒ abgelehnt (fremder Mount), 0 Bytes geschrieben; Typprüfung über `FileTypeMask` erkennt Zeichengeräte, nicht Symlinks (Überlappung).
7. `ReadVirtual` liest aus dem Erzeuger auch dann, wenn unter Root eine gleichnamige Datei `/proc/version` liegt (Mount fehlgeschlagen).

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

1. **Leitung:** Rechte §1 bestätigen (huang: einverstanden) und §0.7 entscheiden: Treiber jetzt (Vorschlag, Spezifikation „alles als Datei“) oder zurückstellen.
2. Ob die System.IO-Schicht von Cosmos (`InteropSysFilePlug`) für Zeichengeräte und Größe 0 etwas anderes tut als für Dateien (Länge, `pread`), ist ungeprüft; E5b.3/E6b.1 prüfen das im Gast zuerst.
3. *Entfällt:* keine Snapshot-Tabelle mehr (§0.3).
4. Verzeichnisse je Gerät unter `/sys/devices/<id>/` und `/dev/<block-id>` sind nicht Teil dieser Stufe (Spezifikation: später, Schreibzugriff nur mit eigener Freigabe).
