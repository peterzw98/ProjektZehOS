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
    private readonly FileService files;
    private readonly Dictionary<int, Entry> open = [];
    private int nextHandle = 1;
    private long buffered;

    public WasmSaveStore(string root, Func<bool> writesBlocked, Func<string, bool, ResultErr>? pathGuard = null,
        Func<string, ResultObject<(long TotalBytes, long AvailableBytes)>>? diskSpace = null, WasmSaveLimits? limits = null)
    {
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.writesBlocked = writesBlocked;
        this.pathGuard = pathGuard;
        this.diskSpace = diskSpace;
        this.limits = limits ?? new WasmSaveLimits();
        // Ohne Rechteverwaltung wie die Kontendatei: der Waechter prueft Symlinks und Mountgrenzen.
        files = new FileService(this.root, pathGuard: pathGuard, diskSpace: diskSpace);
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
            if (!Directory.Exists(path)) return File.Exists(path) ? WasiErrno.NotDirectory : WasiErrno.NotFound;
            handle = Add(new Entry(path, true, false));
            return WasiErrno.Success;
        }
        if (Directory.Exists(path)) return WasiErrno.IsDirectory;
        bool exists = File.Exists(path);
        if (exists && create && exclusive) return WasiErrno.Exists;
        if (!exists && !create) return WasiErrno.NotFound;
        if (write && CountWritable() >= limits.WritableFiles) return WasiErrno.TooManyFiles;
        if (!exists && Usage().Entries >= limits.Entries) return WasiErrno.NoSpace;

        byte[] data = [];
        if (exists && !truncate)
        {
            long size = new FileInfo(path).Length;
            if (size > limits.FileBytes) return WasiErrno.FileTooLarge;
            if (buffered + size > limits.BufferBytes) return WasiErrno.NoSpace;
            var read = files.ReadBytes(path, limits.FileBytes);
            if (!read.IsOK()) return Failed(read.Status);
            data = read.Value!;
        }
        var entry = new Entry(path, false, write || create || truncate) { Data = data, Length = data.Length };
        // O_CREAT legt die Datei sofort an, O_TRUNC leert sie sofort, wie unter POSIX erwartet.
        if (!exists || truncate)
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
        if (Directory.Exists(path)) { directory = true; return WasiErrno.Success; }
        if (!File.Exists(path)) return WasiErrno.NotFound;
        size = new FileInfo(path).Length;
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
        if (Directory.Exists(path) || File.Exists(path)) return WasiErrno.Exists;
        if (!Directory.Exists(Path.GetDirectoryName(path)!)) return WasiErrno.NotFound;
        if (Usage().Entries >= limits.Entries) return WasiErrno.NoSpace;
        var created = files.CreateDirectory(path);
        return created.IsOK() ? WasiErrno.Success : Failed(created);
    }

    public int RemoveDirectory(string relative)
    {
        if (writesBlocked()) return WasiErrno.Busy;
        int status = Resolve(relative, out string path);
        if (status != WasiErrno.Success) return status;
        if (path == root) return WasiErrno.Access;
        if (!Directory.Exists(path)) return File.Exists(path) ? WasiErrno.NotDirectory : WasiErrno.NotFound;
        status = List(path, out var children);
        if (status != WasiErrno.Success) return status;
        if (children.Length != 0) return WasiErrno.NotEmpty;
        var removed = files.DeleteDirectory(path);
        return removed.IsOK() ? WasiErrno.Success : Failed(removed);
    }

    public int Unlink(string relative)
    {
        if (writesBlocked()) return WasiErrno.Busy;
        int status = Resolve(relative, out string path);
        if (status != WasiErrno.Success) return status;
        if (Directory.Exists(path)) return WasiErrno.IsDirectory;
        if (!File.Exists(path)) return WasiErrno.NotFound;
        var removed = files.DeleteFile(path);
        return removed.IsOK() ? WasiErrno.Success : Failed(removed);
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
        if (Directory.Exists(from)) return Directory.Exists(target) || File.Exists(target) ? WasiErrno.Exists : MovedDirectory(from, target);
        if (!File.Exists(from)) return WasiErrno.NotFound;
        if (Directory.Exists(target)) return WasiErrno.IsDirectory;
        if (!Directory.Exists(Path.GetDirectoryName(target)!)) return WasiErrno.NotFound;
        if (!File.Exists(target)) return Moved(from, target);
        long size = new FileInfo(from).Length;
        if (size > limits.FileBytes) return WasiErrno.FileTooLarge;
        if (buffered + size > limits.BufferBytes) return WasiErrno.NoSpace;
        var data = files.ReadBytes(from, limits.FileBytes);
        if (!data.IsOK()) return Failed(data.Status);
        if (!HasDiskSpace(data.Value!.Length)) return WasiErrno.NoSpace;
        var replaced = files.WriteBytes(target, data.Value!);
        if (!replaced.IsOK()) return Failed(replaced);
        var removed = files.DeleteFile(from);
        return removed.IsOK() ? WasiErrno.Success : Failed(removed);
    }

    private int Moved(string from, string to)
    {
        var moved = files.MoveFile(from, to);
        return moved.IsOK() ? WasiErrno.Success : Failed(moved);
    }

    // FileService verschiebt nur Dateien; beide Pfade sind ueber Resolve geprueft und bewacht.
    private int MovedDirectory(string from, string to)
    {
        if (!Directory.Exists(Path.GetDirectoryName(to)!)) return WasiErrno.NotFound;
        try
        {
            Directory.Move(from, to);
            return WasiErrno.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Directory.Exists(root) ? WasiErrno.Io : WasiErrno.NotFound;
        }
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
        var data = new byte[capacity];
        entry.Data.AsSpan(0, entry.Length).CopyTo(data);
        buffered += capacity - entry.Data.Length;
        entry.Data = data;
        return WasiErrno.Success;
    }

    private int Persist(Entry entry)
    {
        if (writesBlocked()) return WasiErrno.Busy;
        if (!Directory.Exists(root)) return WasiErrno.NotFound;
        long existing = File.Exists(entry.Path) ? new FileInfo(entry.Path).Length : 0;
        if (Usage().Bytes - existing + entry.Length > limits.TotalBytes) return WasiErrno.NoSpace;
        if (!HasDiskSpace(entry.Length)) return WasiErrno.NoSpace;
        var written = files.WriteBytes(entry.Path, entry.Data.AsSpan(0, entry.Length).ToArray());
        if (!written.IsOK()) return Failed(written);
        entry.Dirty = false;
        return WasiErrno.Success;
    }

    // Vorab statt aus der Fehlermeldung: FileService unterscheidet einen vollen Datentraeger nicht von anderen Fehlern.
    private bool HasDiskSpace(long bytes)
    {
        if (diskSpace == null) return true;
        var space = diskSpace(root);
        return !space.IsOK() || space.Value.AvailableBytes >= bytes + 64 * 1024;
    }

    private (long Bytes, int Entries) Usage()
    {
        long bytes = 0;
        int entries = 0;
        if (!Directory.Exists(root)) return (0, 0);
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path).StartsWith(".zehos-", StringComparison.OrdinalIgnoreCase)) continue;
            entries++;
            if (File.Exists(path)) bytes += new FileInfo(path).Length;
        }
        return (bytes, entries);
    }

    private int List(string path, out (string Name, bool Directory)[] entries)
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
                result.Add((name, Directory.Exists(child)));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            entries = result.ToArray();
            return WasiErrno.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Directory.Exists(root) ? WasiErrno.Io : WasiErrno.NotFound;
        }
    }

    /// <summary>
    /// Relativer WASI-Pfad unter /save; jede Komponente nach den Regeln fuer Nutzerpfade (kein .., kein .zehos-). "." steht
    /// fuer /save selbst, weil die WASI-libc einen Ordner per path_open(".") oeffnet; veraendern laesst er sich nicht.
    /// </summary>
    private int Resolve(string relative, out string path)
    {
        path = root;
        if (relative == ".") return Directory.Exists(root) ? WasiErrno.Success : WasiErrno.NotFound;
        if (relative.Length is 0 or > 255 || relative[0] == '/') return WasiErrno.Access;
        foreach (string part in relative.Split('/'))
            if (!PathComponent.IsValid(part)) return WasiErrno.Access;
        if (!Directory.Exists(root)) return WasiErrno.NotFound;
        path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        var guarded = FileAccessControl.RunGuard(pathGuard, path, true);
        return guarded.IsOK() ? WasiErrno.Success : WasiErrno.Access;
    }

    private int Failed(ResultErr result) => !Directory.Exists(root) ? WasiErrno.NotFound :
        result.Code == ResultCode.NotFound ? WasiErrno.NotFound : WasiErrno.Io;
}
