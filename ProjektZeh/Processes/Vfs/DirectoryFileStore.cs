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
    /// <summary>
    /// Art des Eintrags <paramref name="name"/> in <paramref name="parent"/> ohne Folgen. <paramref name="found"/> ist der Name, unter
    /// dem das Laufwerk den Eintrag führt: FAT findet einen Eintrag auch über seinen 8.3-Kurznamen ("_ZEHOS~1.DB" für
    /// ".zehos-security.db"), dann unterscheidet sich <paramref name="found"/> vom angefragten Namen.
    /// </summary>
    public LinuxNodeKind KindStorageLocked(string parent, string name, out string found);

    public string ReadLinkStorageLocked(string full);

    /// <summary>false, wenn das Laufwerk keine Symlinks kann (FAT); das Dateisystem meldet dann -EPERM wie Linux.</summary>
    public bool CreateSymlinkStorageLocked(string full, string target);
}

/// <summary>Host-Fassung über .NET (lstat-Semantik von <see cref="FileSystemInfo"/> unter Unix). Nur für Hosttests belegt.</summary>
internal sealed class HostLinuxDiskLinks : ILinuxDiskLinks
{
    public LinuxNodeKind KindStorageLocked(string parent, string name, out string found)
    {
        found = name; // Linux-Hosts suchen exakt; es gibt keinen zweiten Namen
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
/// lehnt sie mit -EINVAL ab, Listen lassen sie weg. Ein Treffer über einen anderen Namen als den angefragten (FAT-Kurzname, siehe
/// <see cref="ILinuxDiskLinks.KindStorageLocked"/>) gilt als fehlend; Anlegen und Umbenennen auf einen solchen Namen sind -EEXIST,
/// damit nie ein vorhandener Eintrag über seinen Zweitnamen überschrieben wird.
/// <para>
/// Wachstum: Eine Datei wächst in Schritten von höchstens <see cref="StorageLock.ChunkBytes"/>, jeder unter einer eigenen Sperre
/// (Cosmos-FAT nullt Lücken sofort, ohne Sparse), und alles, was dieser Speicher wachsen lässt, zählt gegen
/// <see cref="QuotaBytes"/> (-ENOSPC).
/// </para>
/// <para>
/// Rechte: Ordner 0755, Dateien 0644 (schreibgeschützte 0444); FAT kennt keine Unix-Rechte, ein Ausführungsbit fehlt deshalb.
/// Umbenennen über ein vorhandenes Verzeichnis entfernt es erst und ist damit nicht atomar.
/// </para>
/// </summary>
internal sealed class DirectoryFileStore : ILinuxFileStore
{
    public const long DefaultMaxFileBytes = 1L << 30;

    /// <summary>
    /// Kontingent je Wurzel: so viele Bytes darf ein Prozess die Platte über diesen Speicher netto wachsen lassen. 64 MiB reichen für
    /// die Testprogramme und js (schreibt nichts) und halten ein bösartiges Programm unter einem kleinen Teil der Datenplatte;
    /// vorhandene Dateien (Pakete) zählen nicht, Löschen und Kürzen geben frei.
    /// </summary>
    public const long DefaultQuotaBytes = 64L << 20;

    private const uint DirectoryMode = 0x1ED, FileMode = 0x1A4, ReadOnlyFileMode = 0x124;
    private readonly string _root;
    private readonly ILinuxDiskLinks _links;

    private DirectoryFileStore(string root, ILinuxDiskLinks links, StringComparison nameComparison, long maxFileBytes, long quotaBytes)
    {
        _root = root;
        _links = links;
        NameComparison = nameComparison;
        MaxFileBytes = maxFileBytes;
        QuotaBytes = quotaBytes;
    }

    /// <summary>Kontingent für das Nettowachstum (siehe <see cref="DefaultQuotaBytes"/>).</summary>
    public long QuotaBytes { get; }

    /// <summary>Bisheriges Nettowachstum; kann durch Löschen vorhandener Dateien negativ werden.</summary>
    public long GrownBytes { get; private set; }

    /// <summary>Anzahl der Wachstumsschritte (je höchstens <see cref="StorageLock.ChunkBytes"/> unter einer Sperre); Diagnose und Tests.</summary>
    public int GrowSteps { get; private set; }

    public StringComparison NameComparison { get; }

    /// <summary>Größte Datei; ein Schreiben dahinter ist -EFBIG (sonst legte ein lseek weit nach vorn die Platte voll).</summary>
    public long MaxFileBytes { get; }

    /// <summary>
    /// Öffnet <paramref name="root"/> (absolut, ohne abschließendes '/') als Prozesswurzel und legt den Ordner bei Bedarf an.
    /// Der Pfad kommt vom Kernel, nicht vom Programm.
    /// </summary>
    public static ResultObject<DirectoryFileStore> Open(string root, ILinuxDiskLinks links, StringComparison nameComparison,
        long maxFileBytes = DefaultMaxFileBytes, long quotaBytes = DefaultQuotaBytes)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (string.IsNullOrEmpty(root) || root[0] != '/' || root.EndsWith('/') || root.Contains("/../", StringComparison.Ordinal) ||
            root.EndsWith("/..", StringComparison.Ordinal))
        {
            return ResultObjectFactory.CreateFailure<DirectoryFileStore>(ResultCode.InvalidArgument, "Die Prozesswurzel ist kein absoluter Ordnerpfad.");
        }

        try
        {
            StorageLock.Run(() => Directory.CreateDirectory(root));
            return ResultObjectFactory.CreateSuccess(new DirectoryFileStore(root, links, nameComparison, maxFileBytes, quotaBytes));
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<DirectoryFileStore>(exception);
        }
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

    /// <summary>Art des Eintrags unter genau diesem Namen; ein Treffer über einen Zweitnamen (8.3) gilt als fehlend.</summary>
    private LinuxNodeKind KindStorageLocked(string path)
    {
        LinuxNodeKind kind = RawKindStorageLocked(path, out bool alias);
        return alias ? LinuxNodeKind.Missing : kind;
    }

    /// <summary>Art des Eintrags, wie das Laufwerk ihn findet; <paramref name="alias"/>, wenn es ihn unter einem anderen Namen führt.</summary>
    private LinuxNodeKind RawKindStorageLocked(string path, out bool alias)
    {
        int slash = path.LastIndexOf('/');
        string parent = slash < 0 ? _root : string.Concat(_root, "/", path.AsSpan(0, slash));
        string name = path.Substring(slash + 1);
        LinuxNodeKind kind = _links.KindStorageLocked(parent, name, out string found);
        alias = kind != LinuxNodeKind.Missing && !SameName(found, name, NameComparison);
        return kind;
    }

    /// <summary>
    /// Gleicher Name im Sinn des Laufwerks: ordinal, bei einem Laufwerk ohne Groß-/Kleinschreibung ordinal ohne sie. Ein anderer
    /// Name (Kurzname statt Langname) ist nie gleich.
    /// </summary>
    internal static bool SameName(string? found, string requested, StringComparison comparison) =>
        string.Equals(found, requested, comparison == StringComparison.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

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
                if (KindStorageLocked(path) != LinuxNodeKind.Symlink)
                {
                    return -LinuxErrno.ENOENT; // auch ein Treffer über einen Zweitnamen
                }

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
                file = new DiskFile(this, stream);
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
                // CreateNew meldet einen belegten Namen nur als allgemeine IOException; vorher ohne Folgen nachsehen. Auch ein
                // Zweitname zählt als belegt: sonst landete das Anlegen auf dem vorhandenen Eintrag.
                if (RawKindStorageLocked(path, out _) != LinuxNodeKind.Missing)
                {
                    return -LinuxErrno.EEXIST;
                }

                var stream = new FileStream(Full(path), System.IO.FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1);
                file = new DiskFile(this, stream);
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
                if (RawKindStorageLocked(path, out _) != LinuxNodeKind.Missing)
                {
                    return -LinuxErrno.EEXIST; // auch ein Zweitname (8.3) eines vorhandenen Eintrags
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
                if (RawKindStorageLocked(path, out _) != LinuxNodeKind.Missing)
                {
                    return -LinuxErrno.EEXIST; // auch ein Zweitname (8.3) eines vorhandenen Eintrags
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

                long freed = kind == LinuxNodeKind.File ? new FileInfo(Full(path)).Length : 0;
                File.Delete(Full(path));
                GrownBytes -= freed; // eine noch offene Datei belegt unter Cosmos/FAT weiter; Näherung zugunsten des Programms
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
                LinuxNodeKind kind = KindStorageLocked(path);
                if (kind != LinuxNodeKind.Directory)
                {
                    return kind == LinuxNodeKind.Missing ? -LinuxErrno.ENOENT : -LinuxErrno.ENOTDIR;
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

        // Ziel nur unter seinem eigenen Namen: rename("/langername", "/LANGER~1") träfe denselben Eintrag über seinen Kurznamen,
        // File.Move mit Überschreiben könnte ihn dann verlieren.
        LinuxNodeKind existing = RawKindStorageLocked(to, out bool targetAlias);
        if (targetAlias)
        {
            return -LinuxErrno.EEXIST;
        }

        long replaced = existing == LinuxNodeKind.File ? new FileInfo(target).Length : 0;

        if (kind == LinuxNodeKind.Symlink)
        {
            // File.Move prüft unter .NET das Linkziel und scheitert an hängenden Links: Link neu anlegen, dann den alten entfernen
            // (nicht atomar, wie das Ersetzen eines Verzeichnisses).
            string linkTarget = _links.ReadLinkStorageLocked(source);
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
            GrownBytes -= replaced;
            return 0;
        }

        if (kind != LinuxNodeKind.Directory)
        {
            File.Move(source, target, true);
            GrownBytes -= replaced;
            return 0;
        }

        if (existing == LinuxNodeKind.Directory)
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
        // Platte voll: .NET unter Linux trägt errno 28 als HResult, Windows ERROR_DISK_FULL/ERROR_HANDLE_DISK_FULL (0x70/0x27, auch als
        // 0x8007xxxx). Cosmos (Pin 5790fa74) setzt ConvertErrorPalToPlatform als Identität ein (Cosmos.Kernel.Plugs/System/IO/
        // InteropSysFilePlug.cs:336), der HResult ist dort also der PAL-Wert selbst: PalError.ENOSPC = 0x10034 und EFBIG = 0x10016
        // (Interop.Error.cs:37 und :23), gemeldet von FileDescriptorTable.cs:248. Unbekannte Werte bleiben -EIO.
        IOException { HResult: 28 or 0x10034 or 0x70 or 0x27 or unchecked((int)0x80070070) or unchecked((int)0x80070027) } => -LinuxErrno.ENOSPC,
        IOException { HResult: 27 or 0x10016 } => -LinuxErrno.EFBIG,
        FileNotFoundException or DirectoryNotFoundException => -LinuxErrno.ENOENT,
        UnauthorizedAccessException => -LinuxErrno.EACCES,
        PathTooLongException => -LinuxErrno.ENAMETOOLONG,
        IOException => -LinuxErrno.EIO,
        _ => -LinuxErrno.EINVAL,
    };

    /// <summary>
    /// Offene Datei auf dem Laufwerk; jede Stream-Operation unter der Sperre, höchstens <see cref="StorageLock.ChunkBytes"/> je
    /// Abschnitt. Wachsen (Lücke vor einem Schreiben, ftruncate nach oben) läuft in Schritten über <see cref="GrowStorage"/>.
    /// </summary>
    private sealed class DiskFile : ILinuxStoreFile
    {
        private readonly DirectoryFileStore _store;
        private FileStream? _stream;

        public DiskFile(DirectoryFileStore store, FileStream stream)
        {
            _store = store;
            _stream = stream;
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

            if (offset >= _store.MaxFileBytes)
            {
                return -LinuxErrno.EFBIG;
            }

            long length = Length();
            if (length < 0)
            {
                return length;
            }

            if (offset > length)
            {
                // Lücke erst in Schritten anlegen (Cosmos-FAT nullt sie sofort), dann schreiben.
                long grown = Grow(length, offset);
                if (grown < 0)
                {
                    return grown;
                }

                length = offset;
            }

            int count = (int)Math.Min(Math.Min(src.Length, _store.MaxFileBytes - offset), StorageLock.ChunkBytes);
            long growth = Math.Max(0, offset + count - length);
            long room = _store.QuotaBytes - _store.GrownBytes;
            if (growth > room)
            {
                // Wie bei voller Platte: kürzen, ganz ohne Platz -ENOSPC.
                count -= (int)(growth - Math.Max(0, room));
                if (count <= 0)
                {
                    return -LinuxErrno.ENOSPC;
                }

                growth = Math.Max(0, offset + count - length);
            }

            ReadOnlySpan<byte> part = src.Slice(0, count);
            try
            {
                using (StorageLock.Enter())
                {
                    _stream.Position = offset;
                    _stream.Write(part);
                    _store.GrownBytes += growth;
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

            if (length > _store.MaxFileBytes)
            {
                return -LinuxErrno.EFBIG;
            }

            long current = Length();
            if (current < 0)
            {
                return current;
            }

            if (length > current)
            {
                long grown = Grow(current, length);
                return grown < 0 ? grown : 0;
            }

            try
            {
                using (StorageLock.Enter())
                {
                    _stream.SetLength(length);
                    _store.GrownBytes -= current - length;
                    return 0;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }

        /// <summary>
        /// Verlängert von <paramref name="from"/> auf <paramref name="to"/> in Schritten von höchstens <see cref="StorageLock.ChunkBytes"/>,
        /// jeder Schritt unter einer eigenen Sperre, damit andere Plattenzugriffe dazwischen drankommen. Prüft vorher das Kontingent
        /// für das ganze Wachstum (-ENOSPC, nichts geschrieben). Ein Fehler mitten drin lässt die bis dahin gewachsene Länge stehen.
        /// </summary>
        private long Grow(long from, long to) => _store.GrowStorage(_stream!, from, to);

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

    /// <summary>Siehe <see cref="DiskFile"/>: Wachstum in Schritten mit Kontingent.</summary>
    private long GrowStorage(FileStream stream, long from, long to) => GrowInSteps(stream.SetLength, from, to);

    /// <summary>
    /// Verlängert über <paramref name="setLength"/> von <paramref name="from"/> auf <paramref name="to"/>, je Schritt höchstens
    /// <see cref="StorageLock.ChunkBytes"/> unter einer eigenen Sperre. Vorher Prüfung des Kontingents für das ganze Wachstum (-ENOSPC,
    /// nichts verändert). Scheitert ein Schritt (etwa Platte voll), wird die Datei auf <paramref name="from"/> zurückgesetzt und das
    /// Kontingent zurückgebucht; maßgeblich bleibt der Fehler des Schritts.
    /// </summary>
    internal long GrowInSteps(Action<long> setLength, long from, long to)
    {
        if (to - from > QuotaBytes - GrownBytes)
        {
            return -LinuxErrno.ENOSPC;
        }

        long current = from;
        while (current < to)
        {
            long next = Math.Min(to, current + StorageLock.ChunkBytes);
            try
            {
                using (StorageLock.Enter())
                {
                    setLength(next);
                    GrownBytes += next - current;
                    GrowSteps++;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                long error = Errno(exception);
                if (current > from)
                {
                    _ = RollBack(setLength, from, current);
                }

                return error;
            }

            current = next;
        }

        return 0;
    }

    /// <summary>
    /// Setzt nach einem gescheiterten Wachstumsschritt auf <paramref name="from"/> zurück. Scheitert auch das, bleibt die Datei bei
    /// <paramref name="reached"/> und das Kontingent dafür gebucht (stimmt mit der Platte); der Aufrufer meldet den Fehler des
    /// Wachstumsschritts, nicht diesen. false in diesem Fall (Tests).
    /// </summary>
    private bool RollBack(Action<long> setLength, long from, long reached)
    {
        try
        {
            using (StorageLock.Enter())
            {
                setLength(from);
                GrownBytes -= reached - from;
                return true;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return false;
        }
    }
}
