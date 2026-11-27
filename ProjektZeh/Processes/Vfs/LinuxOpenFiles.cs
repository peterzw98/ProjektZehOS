using System;
using System.Collections.Generic;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Storage;

namespace ProjektZeh.Processes.Vfs;

/// <summary>Gemeinsames der offenen Dateien: Zugriffsart, einmaliges Schließen, Budget beim Dateisystem.</summary>
internal abstract class LinuxOpenFile : ILinuxFile
{
    private bool _closed;

    protected LinuxOpenFile(LinuxFileSystem fs, string path, bool read, bool write)
    {
        FileSystem = fs;
        Path = path;
        CanRead = read;
        CanWrite = write;
    }

    protected LinuxFileSystem FileSystem { get; }

    protected bool CanRead { get; }

    protected bool CanWrite { get; }

    protected bool IsClosed => _closed;

    public string Path { get; }

    public virtual bool IsDirectory => false;

    public long DirIndex { get; set; }

    public long Read(Span<byte> dst)
    {
        if (_closed || !CanRead)
        {
            return -LinuxErrno.EBADF;
        }

        return ReadCore(dst);
    }

    public long Write(ReadOnlySpan<byte> data)
    {
        if (_closed || !CanWrite)
        {
            return -LinuxErrno.EBADF;
        }

        return WriteCore(data);
    }

    public long Seek(long offset, int whence) => _closed ? -LinuxErrno.EBADF : SeekCore(offset, whence);

    // Linux prüft bei ftruncate die Schreibberechtigung des Deskriptors und meldet sie wie einen falschen Typ als -EINVAL.
    public long Truncate(long length) => _closed ? -LinuxErrno.EBADF : !CanWrite ? -LinuxErrno.EINVAL : TruncateCore(length);

    public long Sync() => _closed ? -LinuxErrno.EBADF : SyncCore();

    public long Stat(out LinuxStat st)
    {
        st = default;
        return _closed ? -LinuxErrno.EBADF : StatCore(out st);
    }

    public virtual long ReadDir(long index, out LinuxDirEntry entry)
    {
        entry = default;
        return _closed ? -LinuxErrno.EBADF : -LinuxErrno.ENOTDIR;
    }

    public long Close() => CloseChecked();

    /// <summary>
    /// Wie <see cref="Close"/>, liefert aber den Fehler beim Schließen (0 oder -errno, etwa -EIO beim Zurückschreiben auf die Platte).
    /// Die Datei ist danach in jedem Fall zu und zählt nicht mehr zum Budget, wie <c>close</c> unter Linux; ein zweiter Aufruf liefert 0.
    /// </summary>
    public long CloseChecked()
    {
        if (_closed)
        {
            return 0;
        }

        _closed = true;
        try
        {
            return Release();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            // Ein Speicher, der beim Schließen wirft statt -errno zu liefern: trotzdem zu, Fehler als -errno.
            return DirectoryFileStore.Errno(exception);
        }
        finally
        {
            // Auch wenn Release unerwartet wirft: Budget und Zustand bleiben stimmig, der Aufrufer kann die übrigen Dateien schließen.
            FileSystem.Closed();
        }
    }

    protected abstract long ReadCore(Span<byte> dst);

    protected abstract long WriteCore(ReadOnlySpan<byte> data);

    protected abstract long SeekCore(long offset, int whence);

    protected abstract long StatCore(out LinuxStat st);

    /// <summary>Gibt Speicherressourcen frei; 0 oder -errno.</summary>
    protected virtual long Release() => 0;

    /// <summary>Nur reguläre Dateien lassen sich kürzen oder verlängern; alles andere ist -EINVAL wie bei Linux.</summary>
    protected virtual long TruncateCore(long length) => -LinuxErrno.EINVAL;

    /// <summary>
    /// Die Speicher schreiben ungepuffert durch (<c>DirectoryFileStore</c> öffnet mit Puffergröße 1, Cosmos-FAT schreibt jeden Cluster sofort):
    /// Bei Rückkehr von write liegen die Daten schon beim Laufwerk, fsync hat nichts nachzuholen.
    /// </summary>
    protected virtual long SyncCore() => 0;

    /// <summary>Neue Position nach lseek; -EINVAL für negative Ergebnisse und unbekanntes whence, -EOVERFLOW bei Überlauf.</summary>
    protected static long NewPosition(long position, long length, long offset, int whence)
    {
        long baseValue;
        switch (whence)
        {
            case 0:
                baseValue = 0;
                break;
            case 1:
                baseValue = position;
                break;
            case 2:
                baseValue = length;
                break;
            default:
                return -LinuxErrno.EINVAL;
        }

        if (offset > 0 && baseValue > long.MaxValue - offset)
        {
            return -LinuxErrno.EOVERFLOW;
        }

        long result = baseValue + offset;
        return result < 0 ? -LinuxErrno.EINVAL : result;
    }
}

/// <summary>Eine reguläre Datei des Speichers mit eigener Position (lseek, O_APPEND).</summary>
internal sealed class LinuxRegularFile : LinuxOpenFile, ILinuxPageSourceFile
{
    private readonly ILinuxStoreFile _file;

    // V30: Abbildungen (mmap, PT_LOAD) teilen die Speicherdatei; geschlossen wird sie mit dem letzten Verweis, nicht mit dem Deskriptor.
    private readonly LinuxSharedStoreFile _shared;
    private readonly LinuxStoreNode _node;
    private readonly ulong _ino;

    // V32-P2: offene Datei des Laufs (Seitenablage für MAP_SHARED), beim Öffnen festgehalten und über rename/unlink hinweg dieselbe.
    private readonly LinuxFileNode _fileNode;
    private readonly bool _append;
    private long _position;

    public LinuxRegularFile(LinuxFileSystem fs, ILinuxStoreFile file, string storePath, LinuxStoreNode node, bool read, bool write, bool append)
        : base(fs, "/" + storePath, read, write)
    {
        _file = file;
        _shared = new LinuxSharedStoreFile(file);
        _node = node;
        _append = append;
        // Am Deskriptor festhalten: fstat nach unlink oder rename meldet weiter dieselbe Datei.
        _ino = fs.InodeOf(storePath);
        _fileNode = fs.AttachNode(storePath);
    }

    /// <summary>V32: Zugriffsrechte des Deskriptors für mmap mit MAP_SHARED.</summary>
    internal bool Readable => CanRead;

    internal bool Writable => CanWrite;

    /// <summary>V32: lebende Seitenablage dieser Datei (MAP_SHARED in irgendeinem Prozess des Laufs) oder null.</summary>
    private LinuxMemFile? SharedPages => FileSystem.LivePages(_fileNode);

    /// <summary>
    /// V32: Seitenablage für MAP_SHARED, gehalten (der Aufrufer gibt den Halt mit ReleaseHold zurück); entsteht, wenn keine lebt. Sie hält
    /// die Speicherdatei dieses Deskriptors, damit Zurückschreiben auch nach close geht; ein schreibbarer Deskriptor ersetzt eine nur lesende.
    /// Unter FileGate; null nach close.
    /// </summary>
    internal LinuxMemFile? HoldSharedPages(ILinuxSharedMemory shared)
    {
        if (IsClosed)
        {
            return null;
        }

        LinuxMemFile? pages = FileSystem.HoldSharedPages(_fileNode, _ino, shared, null, false, Path);
        if (pages == null)
        {
            _shared.AddRef();
            return FileSystem.HoldSharedPages(_fileNode, _ino, shared, _shared, CanWrite, Path);
        }

        if (CanWrite)
        {
            _shared.AddRef();
            pages.OfferWritableBacking(_shared);
        }

        return pages;
    }

    protected override long ReadCore(Span<byte> dst)
    {
        LinuxMemFile? pages = SharedPages;
        long n = pages != null ? pages.ReadThrough(_file, _position, dst) : _file.ReadAt(_position, dst);
        if (n > 0)
        {
            _position += n;
        }

        return n;
    }

    protected override long WriteCore(ReadOnlySpan<byte> data)
    {
        if (_append)
        {
            long length = _file.Length();
            if (length < 0)
            {
                return length;
            }

            _position = length;
        }

        LinuxMemFile? pages = SharedPages;
        long n = pages != null ? pages.WriteThrough(_file, _position, data) : _file.WriteAt(_position, data);
        if (n > 0)
        {
            _position += n;
        }

        return n;
    }

    protected override long SeekCore(long offset, int whence)
    {
        long length = 0;
        if (whence == 2)
        {
            length = _file.Length();
            if (length < 0)
            {
                return length;
            }
        }

        long r = NewPosition(_position, length, offset, whence);
        if (r >= 0)
        {
            _position = r;
        }

        return r;
    }

    /// <summary>ioctl FIONREAD (V33): Größe minus Position wie Linux (<c>do_vfs_ioctl</c>), nie negativ; -EBADF nach close.</summary>
    public long Readable()
    {
        if (IsClosed)
        {
            return -LinuxErrno.EBADF;
        }

        long length = _file.Length();
        return length < 0 ? length : Math.Max(0, length - _position);
    }

    protected override long StatCore(out LinuxStat st)
    {
        long length = _file.Length();
        st = default;
        if (length < 0)
        {
            return length;
        }

        st = FileSystem.StoreStat(string.Empty, _node with { Size = length });
        st.Ino = _ino;
        return 0;
    }

    // Bleibt eine Abbildung, schließt erst sie die Datei; ein Fehler beim Zurückschreiben geht dann verloren (Linux: close schreibt auch nicht).
    // V32: Eine lebende Seitenablage schreibt vorher zurück (Linux braucht das nicht, sein Seitencache bleibt; hier geht so auch ein späterer
    // Neustart nicht am Inhalt vorbei).
    protected override long Release()
    {
        _ = SharedPages?.Flush(0, long.MaxValue);
        FileSystem.DetachNode(_fileNode);
        return _shared.ReleaseChecked();
    }

    // V32: fsync schreibt auch, was Abbildungen in die Frames geschrieben haben.
    protected override long SyncCore() => SharedPages?.Flush(0, long.MaxValue) ?? 0;

    /// <summary>V30: Quelle für Seiten aus dieser Datei, unter FileGate geholt; null nach close.</summary>
    public ILinuxPageSource? OpenPageSource()
    {
        if (IsClosed)
        {
            return null;
        }

        _shared.AddRef();
        return _shared;
    }

    protected override long TruncateCore(long length)
    {
        if (length < 0)
        {
            return -LinuxErrno.EINVAL;
        }

        LinuxMemFile? pages = SharedPages;
        return pages != null ? pages.TruncateThrough(_file, length) : _file.SetLength(length);
    }
}

/// <summary>
/// /dev/null (liest EOF, verwirft), /dev/zero (liest Nullbytes in jeder Länge, verwirft) und /dev/tty (schreibt auf das Terminal,
/// liest EOF: Eingabe über /dev/tty gibt es in V7 noch nicht).
/// </summary>
internal sealed class LinuxDeviceFile : LinuxOpenFile
{
    private readonly LinuxFileSystem.LocArea _area;
    private readonly bool _tty;

    public LinuxDeviceFile(LinuxFileSystem fs, LinuxFileSystem.LocArea area, bool read, bool write)
        : base(fs, area switch { LinuxFileSystem.LocArea.DevTty => "/dev/tty", LinuxFileSystem.LocArea.DevZero => "/dev/zero", _ => "/dev/null" }, read, write)
    {
        _area = area;
        _tty = area == LinuxFileSystem.LocArea.DevTty;
    }

    protected override long ReadCore(Span<byte> dst)
    {
        if (_area != LinuxFileSystem.LocArea.DevZero)
        {
            return 0;
        }

        // Der Verteiler liest in Stücken; die Länge begrenzt der Puffer des Aufrufers.
        dst.Clear();
        return dst.Length;
    }

    protected override long WriteCore(ReadOnlySpan<byte> data) => _tty ? FileSystem.Terminal.Write(data) : data.Length;

    // Linux: Zeichengeräte haben kein fsync.
    protected override long SyncCore() => -LinuxErrno.EINVAL;

    // Linux: lseek auf /dev/null liefert 0, auf einem Terminal -ESPIPE.
    protected override long SeekCore(long offset, int whence) => _tty ? -LinuxErrno.ESPIPE : (whence is >= 0 and <= 2 ? 0 : -LinuxErrno.EINVAL);

    protected override long StatCore(out LinuxStat st)
    {
        st = FileSystem.StatOf(new LinuxFileSystem.Loc { Area = _area, Exists = true });
        return 0;
    }
}

/// <summary>
/// Ein Verzeichnis des Speichers oder /dev. Die Namen werden bei Index 0 (erster Aufruf, rewinddir) einmal gelesen; Typ und Inode
/// eines Eintrags erst bei seiner Abfrage, damit große Verzeichnisse nicht vorab einzeln abgefragt werden.
/// </summary>
internal sealed class LinuxDirectoryFile : LinuxOpenFile
{
    private static readonly string[] DevNames = ["null", "tty", "zero"];
    private readonly string? _storePath;
    private readonly bool _dev;
    private readonly uint _mode;
    private string[]? _names;

    /// <param name="storePath">Speicherpfad oder null für /dev.</param>
    public LinuxDirectoryFile(LinuxFileSystem fs, string? storePath, bool dev, uint mode)
        : base(fs, dev ? "/dev" : "/" + storePath, true, false)
    {
        _storePath = storePath;
        _dev = dev;
        _mode = mode;
    }

    public override bool IsDirectory => true;

    protected override long ReadCore(Span<byte> dst) => -LinuxErrno.EISDIR;

    protected override long WriteCore(ReadOnlySpan<byte> data) => -LinuxErrno.EBADF;

    // Positionen eines Verzeichnisses sind Eintragsnummern; der Verteiler führt sie in DirIndex.
    protected override long SeekCore(long offset, int whence)
    {
        long r = NewPosition(DirIndex, 0, offset, whence);
        if (whence == 2)
        {
            return -LinuxErrno.EINVAL;
        }

        if (r >= 0)
        {
            DirIndex = r;
        }

        return r;
    }

    protected override long StatCore(out LinuxStat st)
    {
        if (_dev)
        {
            st = FileSystem.DevStat(LinuxMode.Directory | LinuxFileSystem.DevDirMode, 1, 0, 2);
            return 0;
        }

        st = default;
        long r = FileSystem.Store.Lookup(_storePath!, out LinuxStoreNode node);
        if (r < 0)
        {
            return r;
        }

        // Ein entferntes Verzeichnis bleibt über den offenen Deskriptor abfragbar.
        st = FileSystem.StoreStat(_storePath!, node.Kind == LinuxNodeKind.Directory ? node : new LinuxStoreNode(LinuxNodeKind.Directory, 0, _mode, 0));
        return 0;
    }

    public override long ReadDir(long index, out LinuxDirEntry entry)
    {
        entry = default;
        if (IsClosed)
        {
            return -LinuxErrno.EBADF;
        }

        if (index < 0)
        {
            return -LinuxErrno.EINVAL;
        }

        if (index == 0 || _names is null)
        {
            long r = LoadNames();
            if (r < 0)
            {
                return r;
            }
        }

        if (index == 0)
        {
            entry = new LinuxDirEntry(SelfIno(), LinuxMode.DtDirectory, ".");
            return 0;
        }

        if (index == 1)
        {
            entry = new LinuxDirEntry(ParentIno(), LinuxMode.DtDirectory, "..");
            return 0;
        }

        if (index - 2 >= _names!.Length)
        {
            return 1;
        }

        string name = _names[index - 2];
        if (_dev)
        {
            entry = new LinuxDirEntry(name switch { "null" => 2UL, "tty" => 3UL, _ => 4UL }, LinuxMode.DtChar, name);
            return 0;
        }

        if (_storePath!.Length == 0 && name == "dev")
        {
            entry = new LinuxDirEntry(1, LinuxMode.DtDirectory, name);
            return 0;
        }

        string child = _storePath.Length == 0 ? name : _storePath + "/" + name;
        long lookup = FileSystem.Store.Lookup(child, out LinuxStoreNode node);
        byte type = lookup < 0 ? (byte)0 : node.Kind switch
        {
            LinuxNodeKind.Directory => LinuxMode.DtDirectory,
            LinuxNodeKind.Symlink => LinuxMode.DtSymlink,
            LinuxNodeKind.File => LinuxMode.DtRegular,
            _ => (byte)0, // inzwischen entfernt: DT_UNKNOWN, der Name gehört zum gelesenen Stand
        };
        entry = new LinuxDirEntry(FileSystem.InodeOf(child), type, name);
        return 0;
    }

    private long LoadNames()
    {
        if (_dev)
        {
            _names = DevNames;
            return 0;
        }

        long r = FileSystem.Store.List(_storePath!, out string[] names);
        if (r < 0)
        {
            return r;
        }

        if (_storePath!.Length == 0)
        {
            // In der Wurzel ersetzt das virtuelle /dev einen gleichnamigen Ordner des Speichers.
            var list = new List<string>(names.Length + 1) { "dev" };
            foreach (string name in names)
            {
                if (!FileSystem.IsDevName(name))
                {
                    list.Add(name);
                }
            }

            names = list.ToArray();
        }

        _names = names;
        return 0;
    }

    private ulong SelfIno() => _dev ? 1UL : FileSystem.InodeOf(_storePath!);

    private ulong ParentIno()
    {
        if (_dev || _storePath!.Length == 0)
        {
            return FileSystem.InodeOf(string.Empty);
        }

        int slash = _storePath.LastIndexOf('/');
        return FileSystem.InodeOf(slash < 0 ? string.Empty : _storePath.Substring(0, slash));
    }
}
