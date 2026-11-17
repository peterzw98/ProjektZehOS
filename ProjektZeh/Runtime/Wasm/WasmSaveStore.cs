using System;
using System.Collections.Generic;
using System.IO;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>WASI-Fehlernummern (wasi_snapshot_preview1), die der App-Speicher meldet.</summary>
internal static class WasiErrno
{
    public const int Success = 0, Access = 2, BadFile = 8, Busy = 10, Exists = 20, FileTooLarge = 22, Invalid = 28, Io = 29,
        IsDirectory = 31, TooManyFiles = 33, NotFound = 44, NoSpace = 51, NotDirectory = 54, NotEmpty = 55;
}

/// <summary>
/// Ordnereintraege im WASI-dirent-Format: d_next (u64, Cookie des naechsten Eintrags), d_ino (u64), d_namlen (u32),
/// d_type (u8), 3 Byte Fuellung, danach der Name. Ein am Pufferende abgeschnittener Eintrag ist erlaubt; die WASI-libc
/// erkennt den vollen Puffer und fragt mit groesserem Puffer erneut.
/// </summary>
internal static class WasiDirectoryEntries
{
    private const int HeaderBytes = 24;

    public static int Write((string Name, bool Directory)[] entries, ulong cookie, Span<byte> buffer)
    {
        int used = 0;
        Span<byte> header = stackalloc byte[HeaderBytes];
        for (ulong index = cookie; index < (ulong)entries.Length && used < buffer.Length; index++)
        {
            byte[] name = System.Text.Encoding.UTF8.GetBytes(entries[index].Name);
            header.Clear();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header, index + 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header[8..], index + 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)name.Length);
            header[20] = entries[index].Directory ? (byte)3 : (byte)4;
            int count = Math.Min(HeaderBytes, buffer.Length - used);
            header[..count].CopyTo(buffer[used..]);
            used += count;
            count = Math.Min(name.Length, buffer.Length - used);
            name.AsSpan(0, count).CopyTo(buffer[used..]);
            used += count;
        }
        return used;
    }
}

/// <summary>Grenzen je App und Konto (Spec 2026-11-17-app-storage-13-4.md §1).</summary>
internal sealed record WasmSaveLimits(long TotalBytes = 64L * 1024 * 1024, int Entries = 1024, int FileBytes = 16 * 1024 * 1024,
    int BufferBytes = 16 * 1024 * 1024, int WritableFiles = 8);

/// <summary>
/// Schreibender App-Speicher /save einer App-Instanz (Etappe 13.4). Plattformunabhaengig; der Kernel bindet ihn an die
/// WASI-Hostfunktionen. Jede geoeffnete Datei liegt vollstaendig im Speicher und wird bei Sync bzw. Close als Ganzes ueber
/// <see cref="FileService.WriteBytes"/> (Temp + Rename mit Sicherung) ersetzt; so bleibt nach einem Abbruch die alte oder
/// die neue Fassung, nie eine halbe. Alle Ergebnisse sind WASI-Fehlernummern, nie Ausnahmen.
/// </summary>
internal sealed class WasmSaveStore
{
    private sealed class Entry(string path, bool directory, bool writable)
    {
        public string Path { get; } = path;
        public bool Directory { get; } = directory;
        public bool Writable { get; } = writable;
        public byte[] Data { get; set; } = [];
        public int Length { get; set; }
        public bool Dirty { get; set; }
    }

    private readonly string root;
    private readonly Func<bool> writesBlocked;
    private readonly Func<string, bool, ResultErr>? pathGuard;
    private readonly Func<string, ResultObject<(long TotalBytes, long AvailableBytes)>>? diskSpace;
    private readonly WasmSaveLimits limits;
    private readonly Func<int, byte[]?> allocate;
    private readonly Func<Func<int>, int> disk;
    private readonly StringComparison comparison;
    private readonly FileService files;
    // Ordner belegen auf FAT mindestens einen Cluster; ohne diesen Anteil liessen sich ueber leere Ordner rund 32 MiB
    // jenseits der Quote belegen (Review).
    private const long DirectoryBytes = 32 * 1024;
    private readonly Dictionary<int, Entry> open = [];
    private int nextHandle = 1;
    private long buffered;

    /// <param name="disk">
    /// Fuehrt jede Plattenoperation unter der gemeinsamen Schreibsperre aus (<see cref="AppStorageGate.Run"/>), damit
    /// Aushaengen und Energieaktionen nicht mitten in ein Ersetzen fallen; ohne Angabe direkt.
    /// </param>
    public WasmSaveStore(string root, Func<bool> writesBlocked, Func<string, bool, ResultErr>? pathGuard = null,
        Func<string, ResultObject<(long TotalBytes, long AvailableBytes)>>? diskSpace = null, WasmSaveLimits? limits = null,
        Func<Func<int>, int>? disk = null, StringComparison comparison = StringComparison.OrdinalIgnoreCase,
        Func<int, byte[]?>? allocate = null)
    {
        this.allocate = allocate ?? BufferAllocation.TryAllocate;
        // Pfadvergleich des Dateisystems: FAT (Legacy) ohne, ext2 (Root) mit Gross-/Kleinschreibung.
        this.comparison = comparison;
        this.disk = disk ?? (operation => operation());
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.writesBlocked = writesBlocked;
        this.pathGuard = pathGuard;
        this.diskSpace = diskSpace;
        this.limits = limits ?? new WasmSaveLimits();
        // Ohne Rechteverwaltung wie die Kontendatei: der Waechter prueft Symlinks und Mountgrenzen.
        files = new FileService(this.root, pathGuard: pathGuard, diskSpace: diskSpace) { AllocateBuffer = this.allocate };
    }

    /// <summary>
    /// Legt den App-Speicher <paramref name="target"/> unterhalb von <paramref name="adminRoot"/> Ebene fuer Ebene an
    /// (.zehos-apps, Paket, Konto). Der Pfadwaechter erlaubt beim Anlegen nur ein fehlendes Blatt; auf einer frischen
    /// Platte fehlte sonst schon .zehos-apps (Befund cc-lead im ClassiCube-Gasttest). Scheitert eine tiefere Ebene, bleiben
    /// die oberen als leere Ordner stehen; das ist harmlos, der naechste Start nutzt sie weiter. Im Root-Profil (4.3) liegt
    /// der App-Zustand unter /var/lib/zehos/apps und braucht dort die passende Wurzel und den System-Dateidienst.
    /// </summary>
    internal static ResultErr CreateRoot(FileService files, string adminRoot, string target)
    {
        string prefix = adminRoot.TrimEnd('/') + "/";
        if (!target.StartsWith(prefix, StringComparison.Ordinal))
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "cause='OutsideRoot' message='App-Speicher liegt nicht unter der Verwaltungswurzel.'");
        string path = adminRoot.TrimEnd('/');
        foreach (string part in target[prefix.Length..].Split('/'))
        {
            path += "/" + part;
            var created = files.CreateDirectory(path);
            if (!created.IsOK()) return created;
        }
        return ResultErr.CreateSuccess();
    }

    /// <summary>Speicherbedarf der offenen Dateien; begrenzt den Kernel-Heap je Instanz.</summary>
    public long BufferedBytes => buffered;

    public int Open(string relative, bool create, bool exclusive, bool truncate, bool directory, bool write, out int handle)
    {
        handle = 0;
        if ((create || truncate || write) && writesBlocked()) return WasiErrno.Busy;
        int status = Resolve(relative, out string path);
        if (status != WasiErrno.Success) return status;
        if (directory)
        {
            if (create || truncate || write) return WasiErrno.Invalid;
            if (!DirectoryExists(path)) return FileExists(path) ? WasiErrno.NotDirectory : WasiErrno.NotFound;
            handle = Add(new Entry(path, true, false));
            return WasiErrno.Success;
        }
        if (DirectoryExists(path)) return WasiErrno.IsDirectory;
        bool exists = FileExists(path);
        if (exists && create && exclusive) return WasiErrno.Exists;
        if (!exists && !create) return WasiErrno.NotFound;
        if (write && CountWritable() >= limits.WritableFiles) return WasiErrno.TooManyFiles;
        if (!exists && Usage().Entries >= limits.Entries) return WasiErrno.NoSpace;

        byte[] data = [];
        if (exists && !truncate)
        {
            long size = FileLength(path);
            if (size > limits.FileBytes) return WasiErrno.FileTooLarge;
            if (buffered + size > limits.BufferBytes) return WasiErrno.NoSpace;
            var read = files.ReadBytes(path, limits.FileBytes);
            if (!read.IsOK()) return Failed(read.Status);
            data = read.Value!;
        }
        var entry = new Entry(path, false, write) { Data = data, Length = data.Length };
        // O_CREAT legt eine fehlende Datei sofort an (sichtbar wie unter POSIX). O_TRUNC leert dagegen nur den Puffer: die
        // alte Fassung bleibt auf der Platte, bis Sync bzw. Close die neue schreibt, damit ein gescheitertes Speichern
        // nichts zerstoert (Review).
        entry.Dirty = truncate && exists;
        if (!exists)
        {
            entry.Dirty = true;
            status = Persist(entry);
            if (status != WasiErrno.Success) return status;
        }
        buffered += data.Length;
        handle = Add(entry);
        return WasiErrno.Success;
    }

    public int Read(int handle, long offset, Span<byte> target, out int read)
    {
        read = 0;
        if (!open.TryGetValue(handle, out var entry) || entry.Directory) return WasiErrno.BadFile;
        if (offset < 0) return WasiErrno.Invalid;
        if (offset >= entry.Length) return WasiErrno.Success;
        read = (int)Math.Min(target.Length, entry.Length - offset);
        entry.Data.AsSpan((int)offset, read).CopyTo(target);
        return WasiErrno.Success;
    }

    public int Write(int handle, long offset, ReadOnlySpan<byte> data, out int written)
    {
        written = 0;
        if (!open.TryGetValue(handle, out var entry) || entry.Directory || !entry.Writable) return WasiErrno.BadFile;
        if (offset < 0) return WasiErrno.Invalid;
        if (writesBlocked()) return WasiErrno.Busy;
        long end = offset + data.Length;
        int status = Grow(entry, Math.Max(end, entry.Length));
        if (status != WasiErrno.Success) return status;
        data.CopyTo(entry.Data.AsSpan((int)offset));
        entry.Length = (int)Math.Max(end, entry.Length);
        entry.Dirty = true;
        written = data.Length;
        return WasiErrno.Success;
    }

    public long Size(int handle) => open.TryGetValue(handle, out var entry) && !entry.Directory ? entry.Length : -1;

    public int SetSize(int handle, long size)
    {
        if (!open.TryGetValue(handle, out var entry) || entry.Directory || !entry.Writable) return WasiErrno.BadFile;
        if (size < 0) return WasiErrno.Invalid;
        if (writesBlocked()) return WasiErrno.Busy;
        int status = Grow(entry, size);
        if (status != WasiErrno.Success) return status;
        if (size < entry.Length) entry.Data.AsSpan((int)size, entry.Length - (int)size).Clear();
        entry.Length = (int)size;
        entry.Dirty = true;
        return WasiErrno.Success;
    }

    public int Sync(int handle)
    {
        if (!open.TryGetValue(handle, out var entry)) return WasiErrno.BadFile;
        return entry.Dirty ? Persist(entry) : WasiErrno.Success;
    }

    /// <summary>Schreibt ausstehende Aenderungen und gibt den Puffer in jedem Fall frei.</summary>
    public int Close(int handle)
    {
        if (!open.TryGetValue(handle, out var entry)) return WasiErrno.BadFile;
        int status = entry.Dirty ? Persist(entry) : WasiErrno.Success;
        buffered -= entry.Data.Length;
        open.Remove(handle);
        return status;
    }

    /// <summary>Gibt alle Puffer frei und versucht ausstehende Aenderungen zu schreiben (Ende des Laufs).</summary>
    public void CloseAll()
    {
        foreach (int handle in new List<int>(open.Keys)) Close(handle);
    }

    public int Stat(string relative, out long size, out bool directory)
    {
        size = 0;
        directory = false;
        int status = Resolve(relative, out string path);
        if (status != WasiErrno.Success) return status;
        if (DirectoryExists(path)) { directory = true; return WasiErrno.Success; }
        if (!FileExists(path)) return WasiErrno.NotFound;
        size = FileLength(path);
        return WasiErrno.Success;
    }

    /// <summary>Eintraege eines geoeffneten Ordners, ohne interne .zehos--Dateien des Dateidienstes.</summary>
    public int List(int handle, out (string Name, bool Directory)[] entries)
    {
        entries = [];
        if (!open.TryGetValue(handle, out var entry) || !entry.Directory) return WasiErrno.BadFile;
        return List(entry.Path, out entries);
    }

    public int CreateDirectory(string relative)
    {
        if (writesBlocked()) return WasiErrno.Busy;
        int status = Resolve(relative, out string path);
        if (status != WasiErrno.Success) return status;
        if (path == root) return WasiErrno.Exists;
        if (DirectoryExists(path) || FileExists(path)) return WasiErrno.Exists;
        if (!DirectoryExists(Path.GetDirectoryName(path)!)) return WasiErrno.NotFound;
        var usage = Usage();
        if (usage.Entries >= limits.Entries || usage.Bytes + DirectoryBytes > limits.TotalBytes) return WasiErrno.NoSpace;
        return disk(() =>
        {
            var created = files.CreateDirectory(path);
            return created.IsOK() ? WasiErrno.Success : Failed(created);
        });
    }

    public int RemoveDirectory(string relative)
    {
        if (writesBlocked()) return WasiErrno.Busy;
        int status = Resolve(relative, out string path);
        if (status != WasiErrno.Success) return status;
        if (path == root) return WasiErrno.Access;
        if (!DirectoryExists(path)) return FileExists(path) ? WasiErrno.NotDirectory : WasiErrno.NotFound;
        status = List(path, out var children);
        if (status != WasiErrno.Success) return status;
        if (children.Length != 0) return WasiErrno.NotEmpty;
        return disk(() =>
        {
            var removed = files.DeleteDirectory(path);
            return removed.IsOK() ? WasiErrno.Success : Failed(removed);
        });
    }

    public int Unlink(string relative)
    {
        if (writesBlocked()) return WasiErrno.Busy;
        int status = Resolve(relative, out string path);
        if (status != WasiErrno.Success) return status;
        if (DirectoryExists(path)) return WasiErrno.IsDirectory;
        if (!FileExists(path)) return WasiErrno.NotFound;
        return disk(() =>
        {
            var removed = files.DeleteFile(path);
            return removed.IsOK() ? WasiErrno.Success : Failed(removed);
        });
    }

    /// <summary>
    /// Benennt innerhalb von /save um. Ein vorhandenes Ziel wird atomar ersetzt (Temp + Rename des Dateidienstes), damit
    /// Apps ihre Spielstaende nach dem Muster "Temp schreiben, dann umbenennen" sichern koennen.
    /// </summary>
    public int Rename(string fromRelative, string toRelative)
    {
        if (writesBlocked()) return WasiErrno.Busy;
        int status = Resolve(fromRelative, out string from);
        if (status != WasiErrno.Success) return status;
        status = Resolve(toRelative, out string target);
        if (status != WasiErrno.Success) return status;
        if (from == root || target == root) return WasiErrno.Access;
        // Gleicher Name (auf FAT auch nur in anderer Schreibweise): nichts zu tun. Sonst loeschte das Ersetzen unten die
        // einzige Fassung (Review). Eine reine Aenderung der Schreibweise bleibt damit ohne Wirkung.
        if (string.Equals(from, target, comparison))
            return FileExists(from) || DirectoryExists(from) ? WasiErrno.Success : WasiErrno.NotFound;
        if (target.StartsWith(from + Path.DirectorySeparatorChar, comparison)) return WasiErrno.Invalid;
        if (DirectoryExists(from)) return DirectoryExists(target) || FileExists(target) ? WasiErrno.Exists : MovedDirectory(from, target);
        if (!FileExists(from)) return WasiErrno.NotFound;
        if (DirectoryExists(target)) return WasiErrno.IsDirectory;
        if (!DirectoryExists(Path.GetDirectoryName(target)!)) return WasiErrno.NotFound;
        if (!FileExists(target)) return Moved(from, target);
        long size = FileLength(from);
        if (size > limits.FileBytes) return WasiErrno.FileTooLarge;
        if (buffered + size > limits.BufferBytes) return WasiErrno.NoSpace;
        var data = files.ReadBytes(from, limits.FileBytes);
        if (!data.IsOK()) return Failed(data.Status);
        if (!HasDiskSpace(data.Value!.Length)) return WasiErrno.NoSpace;
        return disk(() =>
        {
            var replaced = files.WriteBytes(target, data.Value!);
            if (!replaced.IsOK()) return Failed(replaced);
            var removed = files.DeleteFile(from);
            return removed.IsOK() ? WasiErrno.Success : Failed(removed);
        });
    }

    private int Moved(string from, string to) => disk(() =>
    {
        var moved = files.MoveFile(from, to);
        return moved.IsOK() ? WasiErrno.Success : Failed(moved);
    });

    // FileService verschiebt nur Dateien; beide Pfade sind ueber Resolve geprueft und bewacht.
    private int MovedDirectory(string from, string to)
    {
        if (!DirectoryExists(Path.GetDirectoryName(to)!)) return WasiErrno.NotFound;
        return disk(() =>
        {
            try
            {
                StorageLock.Run(() => Directory.Move(from, to));
                return WasiErrno.Success;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return DirectoryExists(root) ? WasiErrno.Io : WasiErrno.NotFound;
            }
        });
    }

    private int Add(Entry entry)
    {
        int handle = nextHandle++;
        open[handle] = entry;
        return handle;
    }

    private int CountWritable()
    {
        int count = 0;
        foreach (var entry in open.Values) if (entry.Writable) count++;
        return count;
    }

    // Prueft Datei-, Puffer- und Quotengrenzen, bevor der Puffer waechst; erst danach wird Speicher angefordert.
    private int Grow(Entry entry, long size)
    {
        if (size > limits.FileBytes) return WasiErrno.FileTooLarge;
        if (size <= entry.Data.Length) return WasiErrno.Success;
        long capacity = Math.Min(limits.FileBytes, Math.Max(size, entry.Data.Length * 2L));
        if (buffered - entry.Data.Length + capacity > limits.BufferBytes)
        {
            capacity = size;
            if (buffered - entry.Data.Length + capacity > limits.BufferBytes) return WasiErrno.NoSpace;
        }
        // Scheitert die Verdopplung, genuegt vielleicht die exakte Groesse. Ohne Puffer vom Kernel-Heap meldet die App ENOSPC wie
        // bei ausgeschoepftem Pufferbudget; Eintrag und Zaehler bleiben.
        byte[]? data = allocate((int)capacity);
        if (data is null && capacity > size) data = allocate((int)(capacity = size));
        if (data is null) return WasiErrno.NoSpace;
        entry.Data.AsSpan(0, entry.Length).CopyTo(data);
        buffered += capacity - entry.Data.Length;
        entry.Data = data;
        return WasiErrno.Success;
    }

    private int Persist(Entry entry)
    {
        if (writesBlocked()) return WasiErrno.Busy;
        if (!DirectoryExists(root)) return WasiErrno.NotFound;
        long existing = FileExists(entry.Path) ? FileLength(entry.Path) : 0;
        if (Usage().Bytes - existing + entry.Length > limits.TotalBytes) return WasiErrno.NoSpace;
        if (!HasDiskSpace(entry.Length)) return WasiErrno.NoSpace;
        // Ohne Kopie (bis 16 MiB, Audit-Befund): der Puffer aendert sich waehrend des synchronen Schreibens nicht.
        ReadOnlyMemory<byte> content = entry.Data.AsMemory(0, entry.Length);
        int status = disk(() =>
        {
            var written = files.WriteBytes(entry.Path, content);
            return written.IsOK() ? WasiErrno.Success : Failed(written);
        });
        if (status == WasiErrno.Success) entry.Dirty = false;
        return status;
    }

    // Vorab statt aus der Fehlermeldung: FileService unterscheidet einen vollen Datentraeger nicht von anderen Fehlern.
    private bool HasDiskSpace(long bytes)
    {
        if (diskSpace == null) return true;
        var space = diskSpace(root);
        return !space.IsOK() || space.Value.AvailableBytes >= bytes + 64 * 1024;
    }

    private (long Bytes, int Entries) Usage() => StorageLock.Run(UsageStorageLocked);

    private (long Bytes, int Entries) UsageStorageLocked()
    {
        long bytes = 0;
        int entries = 0;
        if (!DirectoryExists(root)) return (0, 0);
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path).StartsWith(".zehos-", StringComparison.OrdinalIgnoreCase)) continue;
            entries++;
            bytes += FileExists(path) ? FileLength(path) : DirectoryBytes;
        }
        return (bytes, entries);
    }

    private int List(string path, out (string Name, bool Directory)[] entries)
    {
        (string Name, bool Directory)[] listed = [];
        int status = StorageLock.Run(() => ListStorageLocked(path, out listed));
        entries = listed;
        return status;
    }

    private int ListStorageLocked(string path, out (string Name, bool Directory)[] entries)
    {
        entries = [];
        var guarded = FileAccessControl.RunGuard(pathGuard, path, false);
        if (!guarded.IsOK()) return WasiErrno.Access;
        try
        {
            var result = new List<(string, bool)>();
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                string name = Path.GetFileName(child);
                if (name.StartsWith(".zehos-", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add((name, DirectoryExists(child)));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            entries = result.ToArray();
            return WasiErrno.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DirectoryExists(root) ? WasiErrno.Io : WasiErrno.NotFound;
        }
    }

    /// <summary>
    /// Relativer WASI-Pfad unter /save; jede Komponente nach den Regeln fuer Nutzerpfade (kein .., kein .zehos-). "." steht
    /// fuer /save selbst, weil die WASI-libc einen Ordner per path_open(".") oeffnet; veraendern laesst er sich nicht.
    /// </summary>
    private int Resolve(string relative, out string path)
    {
        path = root;
        if (relative == ".") return DirectoryExists(root) ? WasiErrno.Success : WasiErrno.NotFound;
        if (relative.Length is 0 or > 255 || relative[0] == '/') return WasiErrno.Access;
        foreach (string part in relative.Split('/'))
            if (!PathComponent.IsValid(part)) return WasiErrno.Access;
        if (!DirectoryExists(root)) return WasiErrno.NotFound;
        path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        var guarded = FileAccessControl.RunGuard(pathGuard, path, true);
        return guarded.IsOK() ? WasiErrno.Success : WasiErrno.Access;
    }

    // Einzelpruefungen unter der Speicher-Sperre (Diagnoselauf 13); Pruefen und spaeteres Handeln bleiben wie zuvor zwei
    // Schritte, die Mutation selbst sperrt der Dateidienst.
    private static bool DirectoryExists(string path) => StorageLock.Run(() => Directory.Exists(path));

    private static bool FileExists(string path) => StorageLock.Run(() => File.Exists(path));

    private static long FileLength(string path) => StorageLock.Run(() => new FileInfo(path).Length);

    private int Failed(ResultErr result) => !DirectoryExists(root) ? WasiErrno.NotFound :
        result.Code == ResultCode.NotFound ? WasiErrno.NotFound : WasiErrno.Io;
}
