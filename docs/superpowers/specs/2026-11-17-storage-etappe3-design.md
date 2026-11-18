# Etappe 3: Pfadidentität und Rechte – Entwurf

Stand: 17. November 2026, Entwurf des Integrations-Stellvertreters zur Abstimmung mit Lead (`agent-nweh-storage-resume`) und senior. Grundlage: [Plan Etappe 3](../plans/2026-11-17-storage-linux.md), `ProjektZeh/Security/FileAccessControl.cs` und `AccountService.cs` auf `feature/storage-data-disk`. Keine Root-Aktivierung in dieser Etappe; der Baum bleibt unter `/data`, nur Prüflogik und Tests werden auf einen späteren Root `/` vorbereitet.

## Ist-Zustand (gelesen, nicht im Gast geprüft)

- `FileAccessControl` ist die einzige Rechtequelle: Schlüssel sind absolute Pfade, Vergleich `OrdinalIgnoreCase`, Prüfung textuell bis `RootDirectory` hinauf (Eintrag + jede Elternstufe).
- `IsManagedPath` verlangt das Präfix `RootDirectory + Separator`; für `RootDirectory = "/"` ergibt das `//` und kein Pfad wäre verwaltet.
- Symlinks werden nicht betrachtet; die Prüfung sieht den Pfad, das VFS folgt dem Link (bis acht Schritte, Vertragsinventar).
- ext2 ist groß-/kleinschreibungsgenau, FAT nicht; die ACL behandelt beide wie FAT.

## Entscheidungen (Lead 09:09: 1/2/3/5/7 angenommen, 4 nach API-Klärung, 6 präzisiert)

1. **Eine Wahrheit bleibt die ACL.** ext2-`uid`/`gid`/`mode` werden nicht ausgewertet und nicht als zweite Rechtequelle eingeführt; neue Dateien bekommen feste Werte (Dokumentation, kein Rechtebezug).
2. **Vergleich je Verwaltungswurzel:** `FileAccessControl` erhält den Vergleicher vom Dateisystem seiner Wurzel – `Ordinal` auf ext2, `OrdinalIgnoreCase` auf FAT. Bestands-ACLs auf FAT bleiben unverändert lesbar. Der Vergleicher muss alle Stellen gleich erreichen: Dictionary, `Restore`, `Contains`, Verschieben und jede Präfixprüfung.
3. **Root `/` korrekt:** Präfix- und Elternprüfung behandeln `RootDirectory == "/"` ausdrücklich (kein `//`, Abbruch der Elternschleife an `/`).
4. **Entscheidung auf dem aufgelösten Ziel:** Vor jeder Prüfung wird jede Pfadkomponente per `VfsManager.TryStat` (ohne Folgen) geprüft. Enthält der Pfad einen Symlink, wird auf dem aufgelösten Ziel geprüft, sofern Cosmos die Auflösung sicher liefert (senior klärt die API); sonst wird der Zugriff verweigert (fail-closed) und die API-Lücke dokumentiert. Keine eigene zweite Auflösung mit TOCTOU-Versprechen. Linkschleifen und Ziele außerhalb der Verwaltungswurzel: verweigert.
5. **Mountgrenzen:** Verwaltet ist nur, was im Dateisystem der Wurzel liegt. Pfade in anderen Mounts (`/proc`, `/sys`, `/dev`, Fremdplatten) gelten als nicht verwaltet und werden verweigert, bis Etappe 5/6 eigene Regeln bringt.
6. **Rechte für Systempfade** (erst wirksam mit dem Root-Baum in Etappe 4, hier nur als Tabelle + Tests): `/etc` und Kontendaten nur Admin schreibend, Kontohashes nie lesbar für Nutzer; Kontohashes und Admin-Daten vertraulich; `/var/log` Admin schreibend, Nutzer lesen nur einen eigenen bereinigten virtuellen Logpfad, keine pauschale Lesefreigabe der Logdateien; `/home/<name>` Eigentümer, das Durchqueren der Elternverzeichnisse wird getrennt geprüft; Paketdaten Admin.
7. **Wasm** erreicht weiterhin nur das eigene Paket; `/etc`, `/proc`, `/sys`, `/dev` bleiben unerreichbar (Test).

## Cosmos-Befunde (senior, efcf20c, gelesen 09:12)

- **Keine sichere Auflösung:** `VfsManager.TryResolve` (VfsManager.cs:508ff) ist privat, folgt Symlinks in jedem Segment inklusive des letzten (:562-596, höchstens 8: :518/:569) und liefert nur Inode und Blattname, keinen kanonischen Pfad; `..` wird abgelehnt (:551). Öffentlich folgen `TryStat` (VfsManager.Paths.cs:120), `TryOpenFile` und `TryOpenDirectory` immer; ein `lstat` ohne Folgen gibt es nicht, nur `IInodeOperations.TryReadLink` je Inode.
- **System.IO folgt immer:** `InteropSysFilePlug.cs:207-221` setzt `LStat = Stat`; `SymLink` → EPERM (:280), `ReadLink` → null/EINVAL (:283). ZehOS kann keine Links anlegen; Links kommen nur aus Host-Images. `Unlink`/`Rename` (VfsManager.Paths.cs:210/265) lösen den Elternpfad auf und entfernen das Blatt selbst.
- **Pfad → Mount:** `FindMount` privat (VfsManager.cs:671, längster Treffer), `MountCovers` intern (:645, Ordinal, Segmentgrenze, `/` deckt alles), `TryGetMount` (:372) nur exakter Einhängepunkt. Öffentlich nur `VfsManager.Mounts` (:55). Absolute Linkziele starten die Auflösung bei `FindMount` neu und wechseln dabei Mounts (:581-596).
- **RO-Flag wird auf dem unaufgelösten Text geprüft:** `IsReadOnlyPath` in FileDescriptorTable.cs:98 (Open) und VfsManager.Paths.cs:212 (Unlink). Ein Symlink im Elternpfad aus einem RW- in einen RO-Mount umgeht das Flag; dann schützt nur die Inode-Ebene. `CosmosReadOnlyExt2FileSystem` verweigert dort, der native Cosmos-RO-Mount nicht.

## Folgerungen für Entscheidung 4 und 5

- **4 ist fail-closed:** Erkennung ohne Folgen je Komponente (`TryOpenDirectory(Eltern).Inode` → `Lookup` → `GetAttr`, `IsSymbolicLink`); enthält der Pfad irgendeinen Symlink, wird verweigert. Das ist Erkennung, keine zweite Auflösung. Pfade mit `..` werden vorher selbst verworfen. Restrisiko TOCTOU gering, weil ZehOS keine Links anlegen kann (EPERM); so dokumentieren.
- **5:** Die Mount-Zuordnung bildet die VFS-Regel über `VfsManager.Mounts` nach (Ordinal, Segmentgrenze, längster Treffer, `/` deckt alles). Der Mount-Präfix wird immer `Ordinal` verglichen wie im VFS; der Vergleicher je Dateisystem gilt nur innerhalb der Wurzel.
- **Fremde ext2-Platten** nur über `CosmosReadOnlyExt2FileSystem` einhängen, nie über den nativen RO-Mount.

## Arbeitspakete

| Paket | Inhalt | Prüfung |
|---|---|---|
| 3.1 | Tests zuerst (Plan Etappe 3): `A`/`a` auf ext2 getrennt, FAT-Alias gleich, relative Pfade, Präfixnachbarn `/home/a` und `/home/ab`, Links in Zwischenverzeichnissen, Linkschleife, mountübergreifend, Root `/` mit `/etc`, `/home` und Elternprüfung bis `/` | Hosttests rot |
| 3.2 | Vergleicher je Wurzel + Root-`/`-Fix in `FileAccessControl` | Hosttests grün |
| 3.3 | Symlink-/Mount-Prüfung nach Antwort von senior; bei fehlender API: verweigern + Doku | Hosttests, Gast mit auf dem Host erzeugten Symlinks |
| 3.4 | Rechtetabelle Systempfade als Daten + Tests (ohne Aktivierung) | Hosttests |
| 3.5 | Gastabnahme auf ext2 (RO-Mount aus `task/storage-ext2-mount-ro` für Symlink-Lesefälle) | Tester, QEMU; VMware offen |
