# Cosmos 3.0.97 – VFS-Verträge (Etappe 1, Inventar)

Stand: 17. November 2026. Nur Lesen, kein Code geändert.

**Quelle und Grenze.** Gelesen wurde der Quellbaum `src/Cosmos.Kernel.System` im Cosmos-Commit `efcf20cc62884b042a0adadffe9fc4540491fd89` ("Bump version to 3.0.97"), der laut [Storage-Spezifikation](2026-11-17-storage-linux.md) dem NuGet-Paket `Cosmos.Kernel.System` 3.0.97 entspricht. Das Paket-Binary selbst wurde hier **nicht** dekompiliert; dass Quelle und Binary übereinstimmen, ist nicht neu belegt. Namespace überall `Cosmos.Kernel.System.FileSystem`, `Storage` für GPT.

## 1. Treibereintritt: `IVfsFileSystemType`

| Member | Vertrag |
|---|---|
| `bool TryMount(ReadOnlySpan<char> source, MountFlags flags, out IVfsSuperblock? sb)` | Erkennt und mountet; `source` ist treiberspezifisch (Partitionsindex als Text, leer = injiziertes Gerät). |
| `bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options)` | Legt ein neues Dateisystem an; `options == null` = Treiberstandard; Treiber castet auf eigenen Typ. `IVfsFormatOptions` ist ein leeres Marker-Interface. Die Quelle darf nicht eingehängt sein (das erzwingt `VfsManager`, nicht der Treiber). |
| `bool TryDestroy(ReadOnlySpan<char> source)` | Löscht nur die Signatur, nullt das Gerät nicht. |

Registrierung: `VfsManager.RegisterFileSystem(string name, IVfsFileSystemType)`; `false` bei leerem Namen, `null` oder bereits vergebenem Namen (kein Überschreiben, kein Deregistrieren). Mitgelieferte Typen: FAT, ISO9660 (`IsReadOnly`), `Ext2.Ext2FileSystemType` (Konstruktor ohne Argumente oder mit `IBlockDevice`).

## 2. Superblock: `IVfsSuperblock` / `ISuperblockOperations`

- `IVfsInode Root`, `ISuperblockOperations SuperOperations`, `long BlockSize` (0 = n. a.), `ulong MaxNameLength`.
- `bool IsReadOnly => false` (Default-Interface-Member). `true` erzwingt Read-only: `VfsManager` ORt `MountFlags.ReadOnly` in die Mount-Flags, alle Mutationen werden vom VFS verweigert.
- `ISuperblockOperations`: `bool Sync(sb)` (Flush, POSIX `sync`), `bool StatFs(sb, out VfsStatFs)`, **`void Drop(sb)`** (Unmount, kein Rückgabewert).
- `VfsStatFs`: `Type, BlockSize, Blocks, FreeBlocks, AvailableBlocks, Inodes, FreeInodes, MaxNameLength, FragmentSize` (alle `ulong`).

## 3. Inode und Operationen

`IVfsInode`: `IInodeOperations InodeOperations`, `IFileOperations? FileOperations` (`null` bei Verzeichnissen), `string Name`.

`IInodeOperations` (alle `bool`, Ergebnis über `out`):
`Lookup(dir, name, out child)`, `ReadDir(dir, out IReadOnlyList<IVfsInode>)`, `Create(dir, name, VfsMode, out inode)`, `Mkdir(...)`, `Symlink(dir, name, target, out inode)`, `Unlink(dir, name)`, `Rmdir(dir, name)`, `Rename(oldParent, oldName, newParent, newName)`, `GetAttr(inode, out VfsStat)`, `SetAttr(inode, SetAttrFlags, in VfsStat)`, `TryReadLink(symlink, out string? target)` (Default: `false`).

`IFileOperations`: `long Read(IVfsOpenFile, Span<byte>)`, `long Write(IVfsOpenFile, ReadOnlySpan<byte>)`, `bool Seek(file, offset, SeekWhence, out newPosition)`, `bool Fsync(file)`, `void Release(file)`. `IVfsOpenFile` trägt `Inode`, `Operations`, `long Position {get;set;}`; die Position hält das VFS, nicht der Treiber.

Typen: `VfsStat` (`InodeNumber, Mode, LinkCount, Uid, Gid, DeviceId, Size, PreferredBlockSize, Blocks, Access/Modification/ChangeTime`; Helfer `IsDirectory/IsRegularFile/IsSymbolicLink`), `VfsTimespec(Seconds, Nanoseconds)` (je `long`), `SetAttrFlags` (`Mode, Uid, Gid, Size, AccessTime, ModificationTime, ChangeTime`), `SeekWhence` (`Set/Cur/End`), `MountFlags` (`ReadOnly, NoSuid, NoDev, NoExec`), `VfsMode` (Unix-Rechte, Sticky/SGID/SUID, Dateityp-Bits `0x1..0xC << 12`, `PermissionMask`, `FileTypeMask`).

## 4. `VfsManager` (statisch) und Handles

- **Mounts:** `TryMount(name, source, flags, mountPoint, out VfsMount)` oder `TryMount(name, Partition, ...)` (reicht den Index in `StorageManager.Partitions` als Text weiter). Mountpunkt wird normalisiert. `Mounts` ist ein Snapshot-Array. `VfsMount`: `Name, Source, MountPoint, Superblock, Partition?, Flags, IsReadOnly`.
- **Unmount:** `TryUnmount(mountPoint)` entfernt den Mount erst aus der Tabelle, ruft dann `Drop`. Fehler von `Drop` werden nicht gemeldet; wirft der Flush, ist der Mount bereits entfernt. Offene Handles werden nicht abgewiesen (Doku von `DetachMounts`: offene Dateien scheitern beim nächsten I/O).
- **Format/Destroy:** `TryFormat(name, source|Partition, options)` und `TryDestroy(name, source)` liefern `false`, wenn die Quelle (bzw. Partition) eingehängt ist, der Treiber fehlt oder verweigert.
- **Pfad-API:** `TryStat`, `TryCreateFile(path, VfsMode)`, `TryCreateDirectory`, `TryUnlink`, `TryRemoveDirectory`, `TryRename`, `TryStatFs(mountPoint)`, `TryGetMount`. `TryRename` scheitert über Mountgrenzen, auf Mountpunkten, in den eigenen Teilbaum, bei Typunterschied, nicht-leerem Zielverzeichnis und read-only.
- **Handles:** `TryOpenFile -> IVfsFileHandle` (`Read/Write/TrySeek/TryFlush/TrySetAttr`, `Position`), `TryOpenDirectory -> IVfsDirectoryHandle` (`TryReadDir/TryLookup/TryCreateFile/TryCreateDirectory/TrySymlink/TryUnlink/TryRemoveDirectory/TryRename/TrySetAttr`). Beide erben `IVfsNodeHandle : IDisposable` (`Name, Inode, TryStat`).
- **Symlinks:** Das VFS löst vorhandene Symlinks auf, Verfolgung auf acht Schritte begrenzt (siehe Storage-Spezifikation).

## 5. GPT (`Cosmos.Kernel.System.Storage.Gpt`)

Statisch, arbeitet auf `IBlockDevice`: `IsGpt`, `Parse -> List<GptPartitionEntry>`, `Create(device)`, `AddPartition(device, startSector, sectorCount, Guid type)`, `RemovePartition(device, index)`, `ResizePartition(device, index, newSectorCount)`, `MovePartition(device, index, newStartSector)`. Konstanten `PrimaryHeaderLba = 1`, `FirstUsableLba = 34`, `BasicDataPartitionType`. `GptPartitionEntry`: `PartitionType, PartitionGuid, StartSector, SectorCount`. `Partition : IBlockDevice` (`Host, StartSector, BlockCount, BlockSize, Name`).

Quellenzitate: `Create` schreibt Schutz-MBR, Primärheader und 32 leere Eintragssektoren, den Backup-Header/-Array ausdrücklich **nicht**; `ResizePartition`/`MovePartition` sind „table-level only" (weder Dateisystem noch Daten werden angepasst).

## 6. Offene API-Lücken (für Linux-VFS-Plan)

Aus dem Lesen der Verträge abgeleitet, nicht im Gast geprüft:

1. **Keine Hardlinks** (`Link` fehlt in `IInodeOperations`/Handles), kein `mknod`/Gerätedateien/FIFOs, keine xattr/ACLs.
2. **`Drop` ist `void`** und `TryUnmount` meldet Flush-Fehler nicht; Unmount mit offenen Handles wird nicht verweigert. Für ein sicheres Aushängen ist ein eigener Vorab-Check (offene Handles, `Sync`) nötig.
3. **`Seek`-Zugriff auf die Position** liegt im VFS; ein Treiber kann keinen Fehler für Seek hinter EOF o. ä. melden außer `false`.
4. **Zeit-/Rechtepfade:** `SetAttr` bietet Mode/Uid/Gid/Size/Zeiten, ob die ext2-Implementierung sie für `System.IO` zuverlässig setzt, ist laut Storage-Spezifikation ungeprüft. Ob das VFS Rechte/Uid selbst durchsetzt, wurde hier nicht untersucht (offen).
5. **GPT:** kein Backup-Header, keine CRC-Pflege, nur `BasicDataPartitionType`-Konstante (Linux-Filesystem-GUID `0FC63DAF-8483-4772-8E79-3D69D8477DE4` fehlt, `AddPartition` nimmt aber beliebige GUID); Resize/Move ändern nur die Tabelle.
6. **`FormatOptions`** ist ein leeres Marker-Interface; ext2-Parameter (Blockgröße, `sparse_super`, Gruppengröße) stehen nur im Treiber-Typ, nicht im allgemeinen Vertrag.
7. **Keine Deregistrierung** von Treibern, keine Mountpropagation/Bind-Mounts, keine Dateisperren, kein `mmap`.
8. **`MountFlags.NoSuid/NoDev/NoExec`** existieren; ob das VFS sie auswertet, ist nicht geprüft.
