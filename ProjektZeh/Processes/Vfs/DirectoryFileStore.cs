using System;
using System.Collections.Generic;
using System.IO;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Processes.Vfs;

/// <summary>
/// Abfragen, die System.IO nicht sicher beantwortet: Art eines Eintrags OHNE Folgen eines Symlinks, Linkziel, Link anlegen.
/// Cosmos folgt Symlinks in jedem Pfadteil (siehe <see cref="PathGuard"/>), deshalb liefert der Kernel eine eigene Fassung
/// (<c>CosmosLinuxDiskLinks</c>), der Host <see cref="HostLinuxDiskLinks"/>. Aufrufe laufen schon unter <see cref="StorageLock"/>
/// und werfen bei Speicherfehlern (<see cref="FileErrors.IsFileError"/>).
/// </summary>
internal interface ILinuxDiskLinks
{
    public LinuxNodeKind KindStorageLocked(string parent, string name);

    public string ReadLinkStorageLocked(string full);

    /// <summary>false, wenn das Laufwerk keine Symlinks kann (FAT); das Dateisystem meldet dann -EPERM wie Linux.</summary>
    public bool CreateSymlinkStorageLocked(string full, string target);
}

/// <summary>Host-Fassung über .NET (lstat-Semantik von <see cref="FileSystemInfo"/> unter Unix). Nur für Hosttests belegt.</summary>
internal sealed class HostLinuxDiskLinks : ILinuxDiskLinks
{
    public LinuxNodeKind KindStorageLocked(string parent, string name)
    {
        var info = new FileInfo(parent + "/" + name);
        FileAttributes attributes = info.Attributes;
        if ((int)attributes == -1)
        {
            return LinuxNodeKind.Missing;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return LinuxNodeKind.Symlink;
        }

        return (attributes & FileAttributes.Directory) != 0 ? LinuxNodeKind.Directory : LinuxNodeKind.File;
    }

    public string ReadLinkStorageLocked(string full) =>
        new FileInfo(full).LinkTarget ?? throw new IOException("Kein Symlink.");

    public bool CreateSymlinkStorageLocked(string full, string target)
    {
        File.CreateSymbolicLink(full, target);
        return true;
    }
}

/// <summary>
/// Prozesswurzel als Ordner eines eingebundenen Laufwerks (Datenplatte). Jeder Zugriff läuft unter <see cref="StorageLock"/>, auch
/// Lesen und Schreiben auf offenen Dateien. Namen, die ein FAT- oder Cosmos-Pfad anders deuten könnte (":", "\\", Steuerzeichen,
/// abschließender Punkt oder Leerzeichen, Systemnamen ".zehos-"), gibt es hier nicht: Abfragen melden sie als fehlend, Anlegen
/// lehnt sie mit -EINVAL ab, Listen lassen sie weg. So entsteht aus einem Linux-Namen nie ein anderer Pfad auf dem Laufwerk.
/// <para>
/// Rechte: Ordner 0755, Dateien 0644 (schreibgeschützte 0444); FAT kennt keine Unix-Rechte, ein Ausführungsbit fehlt deshalb.
/// Umbenennen über ein vorhandenes Verzeichnis entfernt es erst und ist damit nicht atomar.
/// </para>
/// </summary>
internal sealed class DirectoryFileStore : ILinuxFileStore
{
    public const long DefaultMaxFileBytes = 1L << 30;

    private const uint DirectoryMode = 0x1ED, FileMode = 0x1A4, ReadOnlyFileMode = 0x124;
    private readonly string _root;
    private readonly ILinuxDiskLinks _links;

    private DirectoryFileStore(string root, ILinuxDiskLinks links, StringComparison nameComparison, long maxFileBytes)
    {
        _root = root;
        _links = links;
        NameComparison = nameComparison;
        MaxFileBytes = maxFileBytes;
    }

    public StringComparison NameComparison { get; }

    /// <summary>Größte Datei; ein Schreiben dahinter ist -EFBIG (sonst legte ein lseek weit nach vorn die Platte voll).</summary>
    public long MaxFileBytes { get; }

    /// <summary>
    /// Öffnet <paramref name="root"/> (absolut, siehe <see cref="ValidRoot"/>) als Prozesswurzel und legt den Ordner bei Bedarf an.
    /// Der Pfad kommt vom Kernel, nicht vom Programm.
    /// </summary>
    public static ResultObject<DirectoryFileStore> Open(string root, ILinuxDiskLinks links, StringComparison nameComparison,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (!ValidRoot(root))
        {
            return ResultObjectFactory.CreateFailure<DirectoryFileStore>(ResultCode.InvalidArgument, "Die Prozesswurzel ist kein absoluter Ordnerpfad.");
        }

        try
        {
            StorageLock.Run(() => Directory.CreateDirectory(root));
            return ResultObjectFactory.CreateSuccess(new DirectoryFileStore(root, links, nameComparison, maxFileBytes));
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<DirectoryFileStore>(exception);
        }
    }

    /// <summary>
    /// Absolut (im Kernel mit '/', auf einem Windows-Host auch "C:\..."), ohne abschließenden Trenner und ohne "."- oder "..".
    /// Teile. Windows-Pfade nimmt nur ein Windows-Host an (<see cref="Path.IsPathFullyQualified(string)"/>), damit die Hosttests dort
    /// echt laufen; der Kernel und Linux sehen nur '/'-Pfade.
    /// </summary>
    private static bool ValidRoot(string root)
    {
        if (string.IsNullOrEmpty(root) || !(root[0] == '/' || Path.IsPathFullyQualified(root)) || root.EndsWith('/') ||
            root.EndsWith('\\'))
        {
            return false;
        }

        foreach (string part in root.Split('/', '\\'))
        {
            if (part is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    public long Lookup(string path, out LinuxStoreNode node)
    {
        node = new LinuxStoreNode(LinuxNodeKind.Missing, 0, 0, 0);
        if (path.Length == 0)
        {
            node = new LinuxStoreNode(LinuxNodeKind.Directory, 4096, DirectoryMode, 0);
            return 0;
        }

        if (!ValidPath(path))
        {
            return 0; // ein solcher Name kann hier nicht existieren
        }

        try
        {
            using (StorageLock.Enter())
            {
                node = LookupStorageLocked(path);
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    private LinuxStoreNode LookupStorageLocked(string path)
    {
        LinuxNodeKind kind = KindStorageLocked(path);
        switch (kind)
        {
            case LinuxNodeKind.File:
                var info = new FileInfo(Full(path));
                uint mode = (info.Attributes & FileAttributes.ReadOnly) != 0 ? ReadOnlyFileMode : FileMode;
                return new LinuxStoreNode(kind, info.Length, mode, ToNanos(info.LastWriteTimeUtc));
            case LinuxNodeKind.Directory:
                return new LinuxStoreNode(kind, 4096, DirectoryMode, 0);
            case LinuxNodeKind.Symlink:
                return new LinuxStoreNode(kind, 0, LinuxMode.PermissionMask, 0);
            default:
                return new LinuxStoreNode(LinuxNodeKind.Missing, 0, 0, 0);
        }
    }

    private LinuxNodeKind KindStorageLocked(string path)
    {
        int slash = path.LastIndexOf('/');
        string parent = slash < 0 ? _root : string.Concat(_root, "/", path.AsSpan(0, slash));
        return _links.KindStorageLocked(parent, path.Substring(slash + 1));
    }

    public long ReadLink(string path, out string target)
    {
        target = string.Empty;
        if (!ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        try
        {
            using (StorageLock.Enter())
            {
                target = _links.ReadLinkStorageLocked(Full(path));
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long List(string path, out string[] names)
    {
        names = Array.Empty<string>();
        if (path.Length > 0 && !ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        var result = new List<string>();
        try
        {
            using (StorageLock.Enter())
            {
                foreach (string entry in Directory.GetFileSystemEntries(Full(path)))
                {
                    string name = Path.GetFileName(entry);
                    if (ValidName(name))
                    {
                        result.Add(name);
                    }
                }
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }

        result.Sort(StringComparer.Ordinal);
        names = result.ToArray();
        return 0;
    }

    public long OpenFile(string path, bool write, out ILinuxStoreFile? file)
    {
        file = null;
        if (!ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        try
        {
            using (StorageLock.Enter())
            {
                // Erneut ohne Folgen prüfen: zwischen Auflösung und Öffnen könnte ein anderer Weg einen Symlink hingelegt haben.
                if (KindStorageLocked(path) != LinuxNodeKind.File)
                {
                    return -LinuxErrno.ENOENT;
                }

                // Ohne Puffer (1 Byte): Schreibfehler zeigen sich beim write, nicht erst beim Schließen, das keinen Fehler melden kann.
                var stream = new FileStream(Full(path), System.IO.FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1);
                file = new DiskFile(stream, MaxFileBytes);
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long CreateFile(string path, uint mode, out ILinuxStoreFile? file)
    {
        file = null;
        if (!ValidPath(path))
        {
            return -LinuxErrno.EINVAL;
        }

        try
        {
            using (StorageLock.Enter())
            {
                // CreateNew meldet einen belegten Namen nur als allgemeine IOException; vorher ohne Folgen nachsehen.
                if (KindStorageLocked(path) != LinuxNodeKind.Missing)
                {
                    return -LinuxErrno.EEXIST;
                }

                var stream = new FileStream(Full(path), System.IO.FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1);
                file = new DiskFile(stream, MaxFileBytes);
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long CreateDirectory(string path, uint mode)
    {
        if (!ValidPath(path))
        {
            return -LinuxErrno.EINVAL;
        }

        try
        {
            using (StorageLock.Enter())
            {
                if (KindStorageLocked(path) != LinuxNodeKind.Missing)
                {
                    return -LinuxErrno.EEXIST;
                }

                Directory.CreateDirectory(Full(path));
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long CreateSymlink(string path, string target)
    {
        if (!ValidPath(path))
        {
            return -LinuxErrno.EINVAL;
        }

        try
        {
            using (StorageLock.Enter())
            {
                if (KindStorageLocked(path) != LinuxNodeKind.Missing)
                {
                    return -LinuxErrno.EEXIST;
                }

                return _links.CreateSymlinkStorageLocked(Full(path), target) ? 0 : -LinuxErrno.EPERM;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long DeleteFile(string path)
    {
        if (!ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        try
        {
            using (StorageLock.Enter())
            {
                LinuxNodeKind kind = KindStorageLocked(path);
                if (kind == LinuxNodeKind.Missing)
                {
                    return -LinuxErrno.ENOENT; // File.Delete meldet einen fehlenden Eintrag nicht
                }

                if (kind == LinuxNodeKind.Directory)
                {
                    return -LinuxErrno.EISDIR;
                }

                File.Delete(Full(path));
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long DeleteDirectory(string path)
    {
        if (!ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        try
        {
            using (StorageLock.Enter())
            {
                if (KindStorageLocked(path) != LinuxNodeKind.Directory)
                {
                    return -LinuxErrno.ENOTDIR;
                }

                Directory.Delete(Full(path), false);
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long Rename(string from, string to)
    {
        if (!ValidPath(from))
        {
            return -LinuxErrno.ENOENT;
        }

        if (!ValidPath(to))
        {
            return -LinuxErrno.EINVAL;
        }

        try
        {
            using (StorageLock.Enter())
            {
                return RenameStorageLocked(from, to);
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    private long RenameStorageLocked(string from, string to)
    {
        string source = Full(from), target = Full(to);
        LinuxNodeKind kind = KindStorageLocked(from);
        if (kind == LinuxNodeKind.Missing)
        {
            return -LinuxErrno.ENOENT;
        }

        if (kind == LinuxNodeKind.Symlink)
        {
            // File.Move prüft unter .NET das Linkziel und scheitert an hängenden Links: Link neu anlegen, dann den alten entfernen
            // (nicht atomar, wie das Ersetzen eines Verzeichnisses).
            string linkTarget = _links.ReadLinkStorageLocked(source);
            LinuxNodeKind existing = KindStorageLocked(to);
            if (existing == LinuxNodeKind.Directory)
            {
                Directory.Delete(target, false);
            }
            else if (existing != LinuxNodeKind.Missing)
            {
                File.Delete(target);
            }

            if (!_links.CreateSymlinkStorageLocked(target, linkTarget))
            {
                return -LinuxErrno.EPERM; // kann nicht vorkommen, wenn schon ein Link da ist; sonst nichts verändert außer dem ersetzten Ziel
            }

            File.Delete(source);
            return 0;
        }

        if (kind != LinuxNodeKind.Directory)
        {
            File.Move(source, target, true);
            return 0;
        }

        if (KindStorageLocked(to) == LinuxNodeKind.Directory)
        {
            Directory.Delete(target, false); // leer, vom Dateisystem geprüft
        }

        Directory.Move(source, target);
        return 0;
    }

    private string Full(string path) => path.Length == 0 ? _root : _root + "/" + path;

    /// <summary>Jeder Teil muss ein auf dem Laufwerk eindeutiger Name sein.</summary>
    private static bool ValidPath(string path)
    {
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/')
            {
                continue;
            }

            if (!ValidName(path.Substring(start, i - start)))
            {
                return false;
            }

            start = i + 1;
        }

        return true;
    }

    /// <summary><see cref="PathComponent.IsValid"/> (kein ".", "..", ':', '\\', abschließender Punkt, ".zehos-") und keine Steuerzeichen.</summary>
    private static bool ValidName(string name)
    {
        if (!PathComponent.IsValid(name))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c < ' ' || c == '\u007f')
            {
                return false;
            }
        }

        return true;
    }

    private static long ToNanos(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) * 100;

    internal static long Errno(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => -LinuxErrno.ENOENT,
        UnauthorizedAccessException => -LinuxErrno.EACCES,
        PathTooLongException => -LinuxErrno.ENAMETOOLONG,
        IOException => -LinuxErrno.EIO,
        _ => -LinuxErrno.EINVAL,
    };

    /// <summary>Offene Datei auf dem Laufwerk; jede Stream-Operation unter der Sperre, höchstens <see cref="StorageLock.ChunkBytes"/> je Abschnitt.</summary>
    private sealed class DiskFile : ILinuxStoreFile
    {
        private readonly long _maxBytes;
        private FileStream? _stream;

        public DiskFile(FileStream stream, long maxBytes)
        {
            _stream = stream;
            _maxBytes = maxBytes;
        }

        public long Length()
        {
            if (_stream is null)
            {
                return -LinuxErrno.EBADF;
            }

            try
            {
                using (StorageLock.Enter())
                {
                    return _stream.Length;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }

        public long ReadAt(long offset, Span<byte> dst)
        {
            if (_stream is null)
            {
                return -LinuxErrno.EBADF;
            }

            if (offset < 0)
            {
                return -LinuxErrno.EINVAL;
            }

            Span<byte> part = dst.Slice(0, Math.Min(dst.Length, StorageLock.ChunkBytes));
            try
            {
                using (StorageLock.Enter())
                {
                    if (offset >= _stream.Length)
                    {
                        return 0;
                    }

                    _stream.Position = offset;
                    return _stream.Read(part);
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }

        public long WriteAt(long offset, ReadOnlySpan<byte> src)
        {
            if (_stream is null)
            {
                return -LinuxErrno.EBADF;
            }

            if (offset < 0)
            {
                return -LinuxErrno.EINVAL;
            }

            if (src.Length == 0)
            {
                return 0;
            }

            if (offset >= _maxBytes)
            {
                return -LinuxErrno.EFBIG;
            }

            ReadOnlySpan<byte> part = src.Slice(0, (int)Math.Min(Math.Min(src.Length, _maxBytes - offset), StorageLock.ChunkBytes));
            try
            {
                using (StorageLock.Enter())
                {
                    _stream.Position = offset;
                    _stream.Write(part);
                    return part.Length;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }

        public long SetLength(long length)
        {
            if (_stream is null)
            {
                return -LinuxErrno.EBADF;
            }

            if (length < 0)
            {
                return -LinuxErrno.EINVAL;
            }

            if (length > _maxBytes)
            {
                return -LinuxErrno.EFBIG;
            }

            try
            {
                using (StorageLock.Enter())
                {
                    _stream.SetLength(length);
                    return 0;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }

        public long Close()
        {
            FileStream? stream = _stream;
            _stream = null;
            if (stream is null)
            {
                return 0;
            }

            // Schließen kann noch schreiben; scheitert das, ist die Datei trotzdem zu (wie close unter Linux nach -EIO).
            try
            {
                using (StorageLock.Enter())
                {
                    stream.Dispose();
                    return 0;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }
    }
}
