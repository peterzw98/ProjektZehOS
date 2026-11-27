using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Processes.Vfs;

/// <summary>
/// Dateisystem eines Linux-Prozesses (V7 des Plans <c>2026-11-26-ladybird-vorstellung.md</c>): Die Prozesswurzel "/" liegt auf
/// einem <see cref="ILinuxFileStore"/> (Ordner der Datenplatte oder Arbeitsspeicher), darüber ein virtuelles <c>/dev</c> mit
/// <c>null</c> (liest EOF, verwirft Geschriebenes), <c>zero</c> (liest Nullbytes, verwirft) und <c>tty</c> (das Terminal des Prozesses).
/// <para>
/// Ausbruchschutz: Der Verteiler übergibt absolute, normalisierte Pfade (<see cref="ILinuxFileSystem"/>); ein ".." darin lehnt
/// dieses Dateisystem mit -EINVAL ab, statt es selbst aufzulösen. Symlinks löst es Komponente für Komponente selbst auf, der
/// Speicher folgt nie einem Link. Ein absolutes Linkziel beginnt an der Prozesswurzel, ".." im Linkziel endet an ihr (wie
/// <c>chroot</c>); so gibt es keinen Weg zu einem Speicherpfad außerhalb der Wurzel. Jede Auflösung hat ein festes Budget
/// (<see cref="MaxSymlinks"/>, <see cref="MaxSteps"/>, <see cref="PathMax"/>), damit bösartige Linkketten in begrenzter Zeit mit
/// -ELOOP oder -ENAMETOOLONG enden.
/// </para>
/// <para>
/// Rechte: Der Prozess gilt als Eigentümer jeder Datei, geprüft werden die Eigentümerbits (r, w, x) der Speichereinträge.
/// Nicht threadsicher, wie <see cref="LinuxUserProcess"/>: alle Aufrufe kommen vom einen Thread des Prozesses. Fehler sind
/// -errno (<see cref="LinuxErrno"/>) und tragen keinen Text, also auch keine Pfade aus dem Programm.
/// </para>
/// </summary>
internal sealed class LinuxFileSystem : ILinuxFileSystem, ILinuxFsStats
{
    /// <summary>Wie Linux PATH_MAX: ein Pfad hat höchstens 4095 Bytes (UTF-8) ohne NUL.</summary>
    public const int PathMax = 4096;

    /// <summary>Wie Linux NAME_MAX.</summary>
    public const int NameMax = 255;

    /// <summary>Wie Linux (MAXSYMLINKS): mehr gefolgte Links in einer Auflösung ergeben -ELOOP.</summary>
    public const int MaxSymlinks = 40;

    /// <summary>Höchstens so viele Pfadteile prüft eine Auflösung (Eingabe und Linkziele zusammen), sonst -ELOOP.</summary>
    public const int MaxSteps = 4096;

    public const int DefaultMaxOpenFiles = 1024;

    public const int ORdOnly = 0, OWrOnly = 1, ORdWr = 2, OAccMode = 3, OCreat = 0x40, OExcl = 0x80, ONoCtty = 0x100,
        OTrunc = 0x200, OAppend = 0x400, ONonBlock = 0x800, ODSync = 0x1000, OLargeFile = 0x8000, ODirectory = 0x10000,
        ONoFollow = 0x20000, ONoAtime = 0x40000, OCloExec = 0x80000, OSync = 0x101000, OTmpFileFlag = 0x400000;

    private const int Supported = OAccMode | OCreat | OExcl | ONoCtty | OTrunc | OAppend | ONonBlock | ODSync | OLargeFile |
        ODirectory | ONoFollow | ONoAtime | OCloExec | OSync;

    /// <summary>Gerätenummern für st_dev: Speicher der Wurzel und virtuelles /dev.</summary>
    public const ulong StoreDevice = 0x801, DevDevice = 0x5;

    /// <summary>/dev: 0555, niemand legt dort etwas an (der Prozess ist sonst Eigentümer jeder Datei).</summary>
    internal const uint DevDirMode = 0x16D;
    private const uint DevNodeMode = 0x1B6; // 0666

    private readonly ILinuxFileStore _store;
    private readonly ILinuxDescriptor _terminal;
    private readonly int _maxOpenFiles;
    private readonly List<string> _pending = new();
    private readonly List<(string Path, LinuxStoreNode Node)> _parents = new();

    // V32-P2: offene Dateien je Name (Speicherpfad, Schreibweise wie der Speicher vergleicht) für alle Prozesse des Laufs. Jeder Deskriptor
    // hängt beim Öffnen an seinem Knoten und behält ihn über rename und unlink hinweg; die Seitenablage (MAP_SHARED) hängt am Knoten. So
    // findet ein alter Deskriptor nach rename/unlink weiter seine Ablage, und eine neue Datei gleichen Namens bekommt einen neuen Knoten
    // (V32-Review P2: vorher Suche per Pfad-Hash). Die Sperre steht unter FileGate und über der Sperre einer Ablage; sie wird nie unter der
    // Speichersperre eines Prozesses genommen.
    private readonly object _sharedGate = new();
    private readonly Dictionary<string, LinuxFileNode> _nodes;
    private int _nodeSweepAt = 64;

    // V40: Lese-Cache für Dateiseiten aller Prozesse des Laufs; null ohne Cache (Speicher im Arbeitsspeicher braucht keinen).
    private readonly LinuxReadCache? _readCache;

    /// <param name="store">Speicher der Prozesswurzel.</param>
    /// <param name="terminal">Ziel von <c>/dev/tty</c>; sinnvoll derselbe <see cref="TerminalDescriptor"/> wie für fd 0 bis 2,
    /// damit Zeilen aus beiden Wegen in der richtigen Reihenfolge erscheinen.</param>
    public LinuxFileSystem(ILinuxFileStore store, ILinuxDescriptor terminal, int maxOpenFiles = DefaultMaxOpenFiles, uint uid = 0, uint gid = 0,
        long readCacheBytes = 0)
    {
        _readCache = readCacheBytes > 0 ? new LinuxReadCache(readCacheBytes) : null;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOpenFiles);
        _maxOpenFiles = maxOpenFiles;
        Uid = uid;
        Gid = gid;
        _nodes = new Dictionary<string, LinuxFileNode>(StringComparer.Ordinal); // Schlüssel schon gefaltet (NodeKey)
    }

    public uint Uid { get; }

    public uint Gid { get; }

    /// <summary>Anzahl der offenen Dateien dieses Dateisystems (Budget <c>maxOpenFiles</c>, darüber -EMFILE).</summary>
    public int OpenFiles { get; private set; }

    internal ILinuxFileStore Store => _store;

    /// <summary>V40: Lese-Cache für Dateiseiten oder null.</summary>
    internal LinuxReadCache? ReadCache => _readCache;

    /// <summary>
    /// statfs (V20): Platte als vfat mit dem Kontingent als Größe (frei = Kontingent minus Nettowachstum), Arbeitsspeicher als tmpfs mit seinen
    /// Grenzen. Das ganze Verzeichnis meldet dieselben Werte, auch das virtuelle /dev; der freie Platz der Platte selbst ist nicht berücksichtigt.
    /// </summary>
    public LinuxFsStats FsStats
    {
        get
        {
            const long Block = 4096;
            switch (_store)
            {
                case DirectoryFileStore disk:
                    long quota = disk.QuotaBytes / Block;
                    return new LinuxFsStats(LinuxSyscalls.MsdosSuperMagic, Block, quota,
                        Math.Clamp((disk.QuotaBytes - disk.GrownBytes) / Block, 0, quota), 0, 0);
                case MemoryFileStore mem:
                    long total = mem.MaxTotalBytes / Block;
                    return new LinuxFsStats(LinuxSyscalls.TmpfsMagic, Block, total, Math.Clamp((mem.MaxTotalBytes - mem.TotalBytes) / Block, 0, total),
                        mem.MaxNodes, Math.Max(0, mem.MaxNodes - mem.NodeCount));
                default:
                    return new LinuxFsStats(LinuxSyscalls.TmpfsMagic, Block, 0, 0, 0, 0);
            }
        }
    }

    internal ILinuxDescriptor Terminal => _terminal;

    public long Open(string path, int flags, uint mode, out ILinuxFile? file)
    {
        file = null;
        if ((flags & OTmpFileFlag) != 0)
        {
            return -LinuxErrno.EOPNOTSUPP;
        }

        int access = flags & OAccMode;
        if ((flags & ~Supported) != 0 || access == OAccMode)
        {
            return -LinuxErrno.EINVAL;
        }

        bool create = (flags & OCreat) != 0, exclusive = create && (flags & OExcl) != 0, directory = (flags & ODirectory) != 0;
        if (create && directory)
        {
            return -LinuxErrno.EINVAL; // wie Linux ab 6.4
        }

        if (OpenFiles >= _maxOpenFiles)
        {
            return -LinuxErrno.EMFILE;
        }

        bool follow = (flags & ONoFollow) == 0 && !exclusive;
        long r = Resolve(path, follow, out Loc loc);
        if (r < 0)
        {
            return r;
        }

        bool read = access != OWrOnly, write = access != ORdOnly, append = (flags & OAppend) != 0;
        if (!loc.Exists)
        {
            if (!create)
            {
                return -LinuxErrno.ENOENT;
            }

            if (loc.MustBeDirectory)
            {
                return -LinuxErrno.EISDIR;
            }

            r = CheckParentWritable(in loc);
            if (r < 0)
            {
                return r;
            }

            r = _store.CreateFile(loc.Path, mode & LinuxMode.PermissionMask, out ILinuxStoreFile? created);
            if (r < 0)
            {
                return r;
            }

            var node = new LinuxStoreNode(LinuxNodeKind.File, 0, mode & LinuxMode.PermissionMask, 0);
            // V40: Ein Knoten gleichen Namens kann aus einer früheren, außerhalb des Laufs gelöschten Datei stammen: neue Generation.
            LinuxFileNode fileNode = AttachNode(loc.Path);
            _ = Interlocked.Increment(ref fileNode.Generation);
            file = new LinuxRegularFile(this, new LinuxCachedStoreFile(created!, fileNode, _readCache), loc.Path, node, fileNode, read, write, append);
            OpenFiles++;
            return 0;
        }

        if (exclusive)
        {
            return -LinuxErrno.EEXIST;
        }

        switch (loc.Area)
        {
            case LocArea.DevDirectory:
                if (write || create)
                {
                    return -LinuxErrno.EISDIR;
                }

                file = new LinuxDirectoryFile(this, null, true, DevDirMode);
                break;
            case LocArea.DevNull:
            case LocArea.DevTty:
            case LocArea.DevZero:
                if (directory || loc.MustBeDirectory)
                {
                    return -LinuxErrno.ENOTDIR;
                }

                file = new LinuxDeviceFile(this, loc.Area, read, write);
                break;
            default:
                r = OpenStore(in loc, flags, read, write, append, out file);
                if (r < 0)
                {
                    return r;
                }

                break;
        }

        OpenFiles++;
        return 0;
    }

    private long OpenStore(in Loc loc, int flags, bool read, bool write, bool append, out ILinuxFile? file)
    {
        file = null;
        switch (loc.Node.Kind)
        {
            case LinuxNodeKind.Symlink:
                return -LinuxErrno.ELOOP; // nur bei O_NOFOLLOW nicht gefolgt
            case LinuxNodeKind.Directory:
                if (write || (flags & OCreat) != 0)
                {
                    return -LinuxErrno.EISDIR;
                }

                if ((loc.Node.Mode & LinuxMode.OwnerRead) == 0)
                {
                    return -LinuxErrno.EACCES;
                }

                file = new LinuxDirectoryFile(this, loc.Path, false, loc.Node.Mode);
                return 0;
        }

        if ((flags & ODirectory) != 0 || loc.MustBeDirectory)
        {
            return -LinuxErrno.ENOTDIR;
        }

        if ((read && (loc.Node.Mode & LinuxMode.OwnerRead) == 0) || (write && (loc.Node.Mode & LinuxMode.OwnerWrite) == 0))
        {
            return -LinuxErrno.EACCES;
        }

        long r = _store.OpenFile(loc.Path, write, out ILinuxStoreFile? opened);
        if (r < 0)
        {
            return r;
        }

        // V40: Knoten vor dem Kürzen anhängen, damit auch O_TRUNC die Generation für den Lese-Cache erhöht.
        LinuxFileNode fileNode = AttachNode(loc.Path);
        var cached = new LinuxCachedStoreFile(opened!, fileNode, _readCache);
        if (write && (flags & OTrunc) != 0)
        {
            // V32: Eine lebende Seitenablage verliert ihre Frames mit (sonst schriebe sie alten Inhalt zurück).
            LinuxMemFile? pages = LivePages(fileNode);
            r = pages != null ? pages.TruncateThrough(cached, 0) : cached.SetLength(0);
            if (r < 0)
            {
                _ = cached.Close(); // der Fehler von SetLength ist der maßgebliche
                DetachNode(fileNode);
                return r;
            }
        }

        file = new LinuxRegularFile(this, cached, loc.Path, loc.Node, fileNode, read, write, append);
        return 0;
    }

    public long Stat(string path, bool follow, out LinuxStat st)
    {
        st = default;
        long r = Resolve(path, follow, out Loc loc);
        if (r < 0)
        {
            return r;
        }

        if (!loc.Exists)
        {
            return -LinuxErrno.ENOENT;
        }

        st = StatOf(in loc);
        return 0;
    }

    public long Access(string path, int mode)
    {
        if ((mode & ~7) != 0)
        {
            return -LinuxErrno.EINVAL;
        }

        long r = Resolve(path, true, out Loc loc);
        if (r < 0)
        {
            return r;
        }

        if (!loc.Exists)
        {
            return -LinuxErrno.ENOENT;
        }

        uint have = StatOf(in loc).Mode;
        bool denied = ((mode & 4) != 0 && (have & LinuxMode.OwnerRead) == 0) || ((mode & 2) != 0 && (have & LinuxMode.OwnerWrite) == 0) ||
            ((mode & 1) != 0 && (have & LinuxMode.OwnerExecute) == 0);
        return denied ? -LinuxErrno.EACCES : 0;
    }

    public long ReadLink(string path, out string target)
    {
        target = string.Empty;
        long r = Resolve(path, false, out Loc loc);
        if (r < 0)
        {
            return r;
        }

        if (!loc.Exists)
        {
            return -LinuxErrno.ENOENT;
        }

        if (loc.Area != LocArea.Store || loc.Node.Kind != LinuxNodeKind.Symlink)
        {
            return -LinuxErrno.EINVAL;
        }

        return _store.ReadLink(loc.Path, out target);
    }

    public long Mkdir(string path, uint mode)
    {
        long r = Resolve(path, false, out Loc loc);
        if (r < 0)
        {
            return r;
        }

        if (loc.Exists)
        {
            return -LinuxErrno.EEXIST;
        }

        r = CheckParentWritable(in loc);
        return r < 0 ? r : _store.CreateDirectory(loc.Path, mode & LinuxMode.PermissionMask);
    }

    public long Unlink(string path)
    {
        long r = Resolve(path, false, out Loc loc);
        if (r < 0)
        {
            return r;
        }

        if (!loc.Exists)
        {
            return -LinuxErrno.ENOENT;
        }

        switch (loc.Area)
        {
            case LocArea.DevDirectory:
                return -LinuxErrno.EISDIR;
            case LocArea.DevNull:
            case LocArea.DevTty:
            case LocArea.DevZero:
                return -LinuxErrno.EACCES;
        }

        if (loc.Node.Kind == LinuxNodeKind.Directory)
        {
            return -LinuxErrno.EISDIR;
        }

        if (loc.MustBeDirectory)
        {
            return -LinuxErrno.ENOTDIR;
        }

        r = CheckParentWritable(in loc);
        r = r < 0 ? r : _store.DeleteFile(loc.Path);
        if (r >= 0)
        {
            ForgetNode(loc.Path);
        }

        return r;
    }

    public long Rmdir(string path)
    {
        long r = Resolve(path, false, out Loc loc);
        if (r < 0)
        {
            return r;
        }

        if (!loc.Exists)
        {
            return -LinuxErrno.ENOENT;
        }

        if (loc.Dotted)
        {
            return -LinuxErrno.EINVAL; // rmdir("x/.") wie Linux
        }

        switch (loc.Area)
        {
            case LocArea.DevDirectory:
                return -LinuxErrno.EBUSY;
            case LocArea.DevNull:
            case LocArea.DevTty:
            case LocArea.DevZero:
                return -LinuxErrno.ENOTDIR;
        }

        if (loc.Node.Kind != LinuxNodeKind.Directory)
        {
            return -LinuxErrno.ENOTDIR;
        }

        if (loc.Path.Length == 0)
        {
            return -LinuxErrno.EBUSY; // die Wurzel
        }

        r = _store.List(loc.Path, out string[] names);
        if (r < 0)
        {
            return r;
        }

        if (names.Length > 0)
        {
            return -LinuxErrno.ENOTEMPTY;
        }

        r = CheckParentWritable(in loc);
        return r < 0 ? r : _store.DeleteDirectory(loc.Path);
    }

    public long Rename(string oldPath, string newPath)
    {
        long r = Resolve(oldPath, false, out Loc from);
        if (r < 0)
        {
            return r;
        }

        r = Resolve(newPath, false, out Loc to);
        if (r < 0)
        {
            return r;
        }

        if (!from.Exists)
        {
            return -LinuxErrno.ENOENT;
        }

        bool fromDev = from.Area != LocArea.Store, toDev = to.Area != LocArea.Store || to.ParentIsDev;
        if (from.Area == LocArea.DevDirectory || (to.Exists && to.Area == LocArea.DevDirectory))
        {
            return -LinuxErrno.EBUSY; // Einhängepunkt
        }

        if (fromDev || toDev)
        {
            return fromDev && toDev ? -LinuxErrno.EACCES : -LinuxErrno.EXDEV;
        }

        if (from.Path.Length == 0 || to.Path.Length == 0)
        {
            return -LinuxErrno.EBUSY;
        }

        if (from.Dotted || to.Dotted)
        {
            return -LinuxErrno.EINVAL;
        }

        StringComparison cmp = _store.NameComparison;
        if (to.Exists && string.Equals(from.Path, to.Path, cmp))
        {
            return 0; // derselbe Eintrag: Linux tut nichts
        }

        bool fromDir = from.Node.Kind == LinuxNodeKind.Directory;
        if (fromDir && to.Path.StartsWith(from.Path + "/", cmp))
        {
            return -LinuxErrno.EINVAL; // Verzeichnis in sich selbst
        }

        if (!fromDir && (from.MustBeDirectory || to.MustBeDirectory))
        {
            return -LinuxErrno.ENOTDIR;
        }

        if (to.Exists)
        {
            bool toDir = to.Node.Kind == LinuxNodeKind.Directory;
            if (fromDir && !toDir)
            {
                return -LinuxErrno.ENOTDIR;
            }

            if (!fromDir && toDir)
            {
                return -LinuxErrno.EISDIR;
            }

            if (toDir)
            {
                r = _store.List(to.Path, out string[] names);
                if (r < 0)
                {
                    return r;
                }

                if (names.Length > 0)
                {
                    return -LinuxErrno.ENOTEMPTY;
                }
            }
        }

        r = CheckParentWritable(in from);
        if (r < 0)
        {
            return r;
        }

        r = CheckParentWritable(in to);
        r = r < 0 ? r : _store.Rename(from.Path, to.Path);
        if (r >= 0)
        {
            MoveNodes(from.Path, to.Path);
        }

        return r;
    }

    public long Symlink(string target, string linkPath)
    {
        if (target is null)
        {
            return -LinuxErrno.EINVAL;
        }

        if (target.Length == 0)
        {
            return -LinuxErrno.ENOENT;
        }

        if (target.Contains('\0', StringComparison.Ordinal))
        {
            return -LinuxErrno.EINVAL;
        }

        if (target.Length >= PathMax || Encoding.UTF8.GetByteCount(target) >= PathMax)
        {
            return -LinuxErrno.ENAMETOOLONG;
        }

        long r = Resolve(linkPath, false, out Loc loc);
        if (r < 0)
        {
            return r;
        }

        if (loc.Exists)
        {
            return -LinuxErrno.EEXIST;
        }

        r = CheckParentWritable(in loc);
        return r < 0 ? r : _store.CreateSymlink(loc.Path, target);
    }

    /// <summary>Legen und Entfernen im Elternverzeichnis brauchen dort w und x; in /dev legt niemand etwas an.</summary>
    private static long CheckParentWritable(in Loc loc)
    {
        if (loc.ParentIsDev)
        {
            return -LinuxErrno.EACCES;
        }

        const uint need = LinuxMode.OwnerWrite | LinuxMode.OwnerExecute;
        return (loc.ParentMode & need) == need ? 0 : -LinuxErrno.EACCES;
    }

    internal void Closed() => OpenFiles--;

    /// <summary>
    /// V32-P2: Knoten der Datei unter <paramref name="storePath"/> für einen neuen Deskriptor (zählt ihn mit); der Deskriptor gibt ihn mit
    /// <see cref="DetachNode"/> zurück.
    /// </summary>
    internal LinuxFileNode AttachNode(string storePath)
    {
        string key = NodeKey(storePath);
        lock (_sharedGate)
        {
            if (!_nodes.TryGetValue(key, out LinuxFileNode? node))
            {
                if (_nodes.Count >= _nodeSweepAt)
                {
                    SweepNodesLocked();
                }

                node = new LinuxFileNode { Key = key };
                _nodes[key] = node;
            }

            node.Descriptors++;
            _ = Interlocked.Increment(ref node.Holders);
            return node;
        }
    }

    /// <summary>V32-P2: Ein Deskriptor schließt; ohne Deskriptor und lebende Ablage verlässt der Knoten das Register.</summary>
    internal void DetachNode(LinuxFileNode node)
    {
        lock (_sharedGate)
        {
            node.Descriptors--;
            if (node.Descriptors == 0 && node.Key != null && Volatile.Read(ref node.Holders) == 0 && LivePagesLocked(node) == null)
            {
                _ = _nodes.Remove(node.Key);
                node.Key = null;
            }
        }
    }

    /// <summary>V32-P2: lebende Seitenablage des Knotens oder null.</summary>
    internal LinuxMemFile? LivePages(LinuxFileNode? node)
    {
        if (node == null)
        {
            return null;
        }

        lock (_sharedGate)
        {
            return LivePagesLocked(node);
        }
    }

    private static LinuxMemFile? LivePagesLocked(LinuxFileNode node) => node.Pages != null && !node.Pages.IsDead ? node.Pages : null;

    /// <summary>V32-P2: Knoten unter dem Namen oder null, ohne ihn anzulegen.</summary>
    private LinuxFileNode? PeekNode(string storePath)
    {
        lock (_sharedGate)
        {
            return _nodes.TryGetValue(NodeKey(storePath), out LinuxFileNode? node) ? node : null;
        }
    }

    /// <summary>
    /// V32: Seitenablage des Knotens, gehalten (<see cref="LinuxMemFile.TryHold"/>; der Aufrufer gibt den Halt mit ReleaseHold zurück).
    /// Fehlt eine lebende und ist <paramref name="backing"/> gesetzt, entsteht eine neue über <paramref name="shared"/> (sie übernimmt den
    /// Verweis auf <paramref name="backing"/>); sonst null und der Verweis bleibt beim Aufrufer.
    /// </summary>
    internal LinuxMemFile? HoldSharedPages(LinuxFileNode node, ulong ino, ILinuxSharedMemory shared, LinuxSharedStoreFile? backing, bool backingWritable, string path)
    {
        lock (_sharedGate)
        {
            LinuxMemFile? pages = node.Pages;
            if (pages != null && pages.TryHold())
            {
                backing?.Release(); // nicht gebraucht: Die lebende Ablage hält ihre eigene Datei
                return pages;
            }

            if (backing == null)
            {
                return null;
            }

            pages = shared.CreateFilePages(backing, backingWritable, path, ino);
            _ = pages.TryHold(); // neu, lebt sicher
            node.Pages = pages;
            return pages;
        }
    }

    /// <summary>V32-P2: Nach unlink gehört der Name keiner Datei mehr; offene Deskriptoren behalten Knoten und Ablage.</summary>
    private void ForgetNode(string storePath)
    {
        lock (_sharedGate)
        {
            DropKeyLocked(NodeKey(storePath));
        }
    }

    private void DropKeyLocked(string key)
    {
        if (_nodes.Remove(key, out LinuxFileNode? node))
        {
            node.Key = null;
        }
    }

    /// <summary>
    /// V32-P2: rename trägt die Knoten unter <paramref name="from"/> (bei einem Verzeichnis auch die darunter) zum neuen Namen; ein ersetztes
    /// Ziel verliert seinen Namen wie bei unlink. So teilen neue Deskriptoren unter dem neuen Namen die Ablage der alten.
    /// </summary>
    private void MoveNodes(string from, string to)
    {
        string fromKey = NodeKey(from), toKey = NodeKey(to);
        lock (_sharedGate)
        {
            DropKeyLocked(toKey);
            if (_nodes.Count == 0)
            {
                return;
            }

            var moved = new List<KeyValuePair<string, LinuxFileNode>>();
            foreach (KeyValuePair<string, LinuxFileNode> e in _nodes)
            {
                if (e.Key == fromKey || e.Key.StartsWith(fromKey + "/", StringComparison.Ordinal))
                {
                    moved.Add(e);
                }
            }

            foreach (KeyValuePair<string, LinuxFileNode> e in moved)
            {
                _ = _nodes.Remove(e.Key);
            }

            foreach (KeyValuePair<string, LinuxFileNode> e in moved)
            {
                string key = string.Concat(toKey, e.Key.AsSpan(fromKey.Length));
                e.Value.Key = key;
                _nodes[key] = e.Value;
            }
        }
    }

    /// <summary>
    /// Entfernt Knoten ohne Deskriptor, deren Ablage inzwischen tot ist (die Ablage stirbt unter der Speichersperre eines Prozesses, wo das
    /// Register nicht gesperrt werden darf). Die Schwelle wächst mit, damit nicht jedes open das ganze Register durchgeht.
    /// </summary>
    private void SweepNodesLocked()
    {
        var dead = new List<string>();
        foreach (KeyValuePair<string, LinuxFileNode> e in _nodes)
        {
            if (e.Value.Descriptors == 0 && Volatile.Read(ref e.Value.Holders) == 0 && LivePagesLocked(e.Value) == null)
            {
                dead.Add(e.Key);
            }
        }

        foreach (string key in dead)
        {
            DropKeyLocked(key);
        }

        _nodeSweepAt = Math.Max(64, _nodes.Count * 2);
    }

    /// <summary>Schlüssel des Registers: bei einem Speicher ohne Groß-/Kleinschreibung in Großbuchstaben wie <see cref="InodeOf"/>.</summary>
    private string NodeKey(string storePath)
    {
        if (_store.NameComparison is not (StringComparison.OrdinalIgnoreCase or StringComparison.InvariantCultureIgnoreCase))
        {
            return storePath;
        }

        char[] folded = new char[storePath.Length];
        for (int i = 0; i < folded.Length; i++)
        {
            folded[i] = char.ToUpperInvariant(storePath[i]);
        }

        return new string(folded);
    }

    internal LinuxStat StatOf(in Loc loc) => loc.Area switch
    {
        LocArea.DevDirectory => DevStat(LinuxMode.Directory | DevDirMode, 1, 0, 2),
        LocArea.DevNull => DevStat(LinuxMode.CharDevice | DevNodeMode, 2, 0x103, 1), // makedev(1, 3)
        LocArea.DevTty => DevStat(LinuxMode.CharDevice | DevNodeMode, 3, 0x500, 1), // makedev(5, 0)
        LocArea.DevZero => DevStat(LinuxMode.CharDevice | DevNodeMode, 4, 0x105, 1), // makedev(1, 5)
        _ => StoreStat(loc.Path, loc.Node),
    };

    internal LinuxStat DevStat(uint mode, ulong ino, ulong rdev, uint links) => new()
    {
        Dev = DevDevice,
        Ino = ino,
        Mode = mode,
        Nlink = links,
        Uid = Uid,
        Gid = Gid,
        Rdev = rdev,
        BlockSize = 4096,
    };

    internal LinuxStat StoreStat(string storePath, LinuxStoreNode node)
    {
        uint type = node.Kind switch
        {
            LinuxNodeKind.Directory => LinuxMode.Directory,
            LinuxNodeKind.Symlink => LinuxMode.Symlink,
            _ => LinuxMode.Regular,
        };
        long size = node.Size < 0 ? 0 : node.Size;
        return new LinuxStat
        {
            Dev = StoreDevice,
            Ino = InodeOf(storePath),
            Mode = type | (node.Mode & LinuxMode.PermissionMask),
            Nlink = node.Kind == LinuxNodeKind.Directory ? 2u : 1u,
            Uid = Uid,
            Gid = Gid,
            Size = size,
            BlockSize = 4096,
            Blocks = (size + 511) / 512,
            AtimeNanos = node.MtimeNanos,
            MtimeNanos = node.MtimeNanos,
            CtimeNanos = node.MtimeNanos,
        };
    }

    /// <summary>
    /// Inodenummer aus dem Speicherpfad (FNV-1a, 64 Bit). Stabil, solange der Eintrag nicht umbenannt wird; bei einem Speicher ohne
    /// Groß-/Kleinschreibung über die Großbuchstabenform, damit zwei Schreibweisen derselben Datei dieselbe Nummer haben.
    /// </summary>
    internal ulong InodeOf(string storePath)
    {
        bool fold = _store.NameComparison is StringComparison.OrdinalIgnoreCase or StringComparison.InvariantCultureIgnoreCase;
        ulong h = 14695981039346656037UL;
        foreach (char c in storePath)
        {
            h = (h ^ (fold ? char.ToUpperInvariant(c) : c)) * 1099511628211UL;
        }

        // 1 bis 3 gehören /dev, 0 ist kein gültiger Eintrag.
        return h < 16 ? h + 16 : h;
    }

    internal enum LocArea
    {
        Store,
        DevDirectory,
        DevNull,
        DevTty,
        DevZero,
    }

    /// <summary>Ergebnis einer Auflösung: der letzte Eintrag (vorhanden oder fehlend) und sein Elternverzeichnis.</summary>
    internal struct Loc
    {
        public LocArea Area;
        public bool Exists;
        public string Path; // Speicherpfad (Area Store), auch für einen fehlenden Eintrag
        public LinuxStoreNode Node;
        public uint ParentMode;
        public bool ParentIsDev;
        public bool Dotted; // letzter Teil war "." oder ".." (aus einem Linkziel)
        public bool MustBeDirectory; // Pfad endete mit '/'
    }

    /// <summary>
    /// Löst <paramref name="path"/> auf. Fehler: -EINVAL (kein absoluter Pfad, NUL, ".."), -ENAMETOOLONG, -ENOENT (fehlende
    /// Zwischenkomponente), -ENOTDIR, -EACCES (Zwischenverzeichnis ohne x), -ELOOP (Budget), Speicherfehler. Ein fehlender letzter
    /// Eintrag ist Erfolg mit <see cref="Loc.Exists"/> false.
    /// </summary>
    internal long Resolve(string path, bool followFinal, out Loc loc)
    {
        loc = default;
        long r = PushInput(path);
        if (r < 0)
        {
            return r;
        }

        r = _store.Lookup(string.Empty, out LinuxStoreNode rootNode);
        if (r < 0)
        {
            _pending.Clear();
            return r;
        }

        if (rootNode.Kind != LinuxNodeKind.Directory)
        {
            _pending.Clear();
            return -LinuxErrno.EIO; // Wurzel fehlt oder ist keine Ordner: Speicherfehler, kein Programmfehler
        }

        try
        {
            return Walk(rootNode, followFinal, out loc);
        }
        finally
        {
            _pending.Clear();
            _parents.Clear();
        }
    }

    private long Walk(LinuxStoreNode rootNode, bool followFinal, out Loc loc)
    {
        string current = string.Empty;
        LinuxStoreNode currentNode = rootNode;
        bool inDev = false;
        int steps = 0, links = 0;
        loc = DirectoryLoc(current, currentNode, false, rootNode.Mode);
        while (_pending.Count > 0)
        {
            if (++steps > MaxSteps)
            {
                return -LinuxErrno.ELOOP;
            }

            string name = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
            bool slash = _pending.Count == 1 && _pending[0].Length == 0;
            bool last = _pending.Count == 0 || slash;
            if (name.Length == 0)
            {
                // Nur noch als Merker für ein abschließendes '/': das Ergebnis muss ein Verzeichnis sein und ist es hier.
                continue;
            }

            if (name == ".")
            {
                loc = inDev ? DevDirectoryLoc(true) : DirectoryLoc(current, currentNode, true, ParentMode(rootNode));
                continue;
            }

            if (name == "..")
            {
                // Nur aus Linkzielen; an der Wurzel bleibt ".." an der Wurzel (chroot).
                if (inDev)
                {
                    inDev = false;
                }
                else if (_parents.Count > 0)
                {
                    (current, currentNode) = _parents[^1];
                    _parents.RemoveAt(_parents.Count - 1);
                }

                loc = inDev ? DevDirectoryLoc(true) : DirectoryLoc(current, currentNode, true, ParentMode(rootNode));
                continue;
            }

            if (name.Length > NameMax || Encoding.UTF8.GetByteCount(name) > NameMax)
            {
                return -LinuxErrno.ENAMETOOLONG;
            }

            if (inDev)
            {
                LocArea area = name switch
                {
                    "null" => LocArea.DevNull,
                    "tty" => LocArea.DevTty,
                    "zero" => LocArea.DevZero,
                    _ => LocArea.Store,
                };
                if (area == LocArea.Store)
                {
                    if (!last)
                    {
                        return -LinuxErrno.ENOENT;
                    }

                    loc = new Loc { Area = LocArea.Store, Exists = false, Path = string.Empty, ParentIsDev = true, MustBeDirectory = slash };
                    continue;
                }

                if (!last || slash)
                {
                    return -LinuxErrno.ENOTDIR;
                }

                loc = new Loc { Area = area, Exists = true, Path = string.Empty, ParentIsDev = true };
                continue;
            }

            if ((currentNode.Mode & LinuxMode.OwnerExecute) == 0)
            {
                return -LinuxErrno.EACCES;
            }

            if (current.Length == 0 && IsDevName(name))
            {
                // /dev ist virtuell und verdeckt einen gleichnamigen Ordner des Speichers.
                inDev = true;
                loc = DevDirectoryLoc(false);
                continue;
            }

            string child = current.Length == 0 ? name : current + "/" + name;
            if (child.Length >= PathMax || Encoding.UTF8.GetByteCount(child) >= PathMax)
            {
                return -LinuxErrno.ENAMETOOLONG; // auch aufgelöste Pfade bleiben unter PATH_MAX
            }

            long r = _store.Lookup(child, out LinuxStoreNode node);
            if (r < 0)
            {
                return r;
            }

            switch (node.Kind)
            {
                case LinuxNodeKind.Missing:
                    if (!last)
                    {
                        return -LinuxErrno.ENOENT;
                    }

                    loc = new Loc { Area = LocArea.Store, Exists = false, Path = child, ParentMode = currentNode.Mode, MustBeDirectory = slash };
                    _pending.Clear();
                    continue;
                case LinuxNodeKind.Symlink when !last || followFinal || slash:
                    if (++links > MaxSymlinks)
                    {
                        return -LinuxErrno.ELOOP;
                    }

                    r = _store.ReadLink(child, out string target);
                    if (r < 0)
                    {
                        return r;
                    }

                    r = PushTarget(target);
                    if (r < 0)
                    {
                        return r;
                    }

                    if (target[0] == '/')
                    {
                        current = string.Empty;
                        currentNode = rootNode;
                        inDev = false;
                        _parents.Clear();
                    }

                    // Ein Ziel ohne Teile ("/") endet im Verzeichnis selbst.
                    loc = DirectoryLoc(current, currentNode, false, ParentMode(rootNode));
                    continue;
                case LinuxNodeKind.Symlink:
                case LinuxNodeKind.File:
                    if (!last || slash)
                    {
                        return -LinuxErrno.ENOTDIR;
                    }

                    loc = new Loc { Area = LocArea.Store, Exists = true, Path = child, Node = node, ParentMode = currentNode.Mode };
                    continue;
                default:
                    _parents.Add((current, currentNode));
                    loc = new Loc { Area = LocArea.Store, Exists = true, Path = child, Node = node, ParentMode = currentNode.Mode, MustBeDirectory = slash };
                    current = child;
                    currentNode = node;
                    continue;
            }
        }

        return 0;
    }

    /// <summary>"dev" in der Wurzel nach der Namensregel des Speichers: auf FAT verdeckt /dev auch "DEV" und "Dev".</summary>
    internal bool IsDevName(string name) => string.Equals(name, "dev", _store.NameComparison);

    private uint ParentMode(LinuxStoreNode rootNode) => _parents.Count > 0 ? _parents[^1].Node.Mode : rootNode.Mode;

    private static Loc DirectoryLoc(string path, LinuxStoreNode node, bool dotted, uint parentMode) =>
        new() { Area = LocArea.Store, Exists = true, Path = path, Node = node, ParentMode = parentMode, Dotted = dotted };

    private static Loc DevDirectoryLoc(bool dotted) =>
        new() { Area = LocArea.DevDirectory, Exists = true, Path = string.Empty, Node = new LinuxStoreNode(LinuxNodeKind.Directory, 0, DevDirMode, 0), ParentMode = DevDirMode, Dotted = dotted };

    /// <summary>Prüft den Eingabepfad und legt seine Teile (umgekehrt) auf den Stapel.</summary>
    private long PushInput(string path)
    {
        _pending.Clear();
        if (path is null)
        {
            return -LinuxErrno.EINVAL;
        }

        if (path.Length == 0)
        {
            return -LinuxErrno.ENOENT;
        }

        if (path.Length >= PathMax || Encoding.UTF8.GetByteCount(path) >= PathMax)
        {
            return -LinuxErrno.ENAMETOOLONG;
        }

        if (path[0] != '/' || path.Contains('\0', StringComparison.Ordinal))
        {
            return -LinuxErrno.EINVAL;
        }

        long r = Push(path);
        if (r < 0)
        {
            return r;
        }

        // ".." löst der Verteiler auf; kommt es trotzdem an, ist der Pfad nicht normalisiert: ablehnen statt selbst deuten.
        foreach (string part in _pending)
        {
            if (part == "..")
            {
                _pending.Clear();
                return -LinuxErrno.EINVAL;
            }
        }

        return 0;
    }

    private long PushTarget(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return -LinuxErrno.ENOENT;
        }

        if (target.Length >= PathMax || target.Contains('\0', StringComparison.Ordinal))
        {
            return -LinuxErrno.EIO; // der Speicher liefert kein gültiges Linkziel
        }

        return Push(target);
    }

    /// <summary>
    /// Legt die Teile von <paramref name="text"/> so auf den Stapel, dass der erste oben liegt. Leere Teile ("//") fallen weg; ein
    /// abschließendes '/' bleibt als ein leerer Merker unten, und zwar nur, wenn darunter nichts mehr kommt (sonst ist es bedeutungslos).
    /// Der Stapel wächst höchstens um die Länge des Texts; das Schrittbudget begrenzt die Summe.
    /// </summary>
    private long Push(string text)
    {
        if (_pending.Count + (text.Length / 2) + 1 > MaxSteps * 2)
        {
            return -LinuxErrno.ELOOP;
        }

        bool trailing = text.Length > 1 && text[^1] == '/';
        if (trailing && _pending.Count == 0)
        {
            _pending.Add(string.Empty);
        }

        int end = text.Length;
        for (int i = text.Length - 1; i >= -1; i--)
        {
            if (i >= 0 && text[i] != '/')
            {
                continue;
            }

            if (end - i - 1 > 0)
            {
                _pending.Add(text.Substring(i + 1, end - i - 1));
            }

            end = i;
        }

        return 0;
    }
}

/// <summary>
/// V32-P2: eine offene Datei des Laufs, geteilt von allen Deskriptoren, die sie unter demselben Namen geöffnet haben (Hartlinks unter anderem
/// Namen bekommen einen eigenen Knoten, siehe LINUX_ABI.md). Trägt die Seitenablage für MAP_SHARED. Felder nur unter der Registersperre von
/// <see cref="LinuxFileSystem"/>.
/// </summary>
internal sealed class LinuxFileNode
{
    /// <summary>Aktueller Schlüssel im Register; null, wenn der Name gelöscht oder ersetzt wurde.</summary>
    public string? Key { get; set; }

    public int Descriptors { get; set; }

    public LinuxMemFile? Pages { get; set; }

    /// <summary>
    /// V40: Generation des Inhalts für den Lese-Cache; nur mit Interlocked/Volatile, ohne Registersperre (geändert unter FileGate oder der
    /// Speichersperre eines Prozesses).
    /// </summary>
    public long Generation;

    /// <summary>
    /// V40: offene Speicherdateien (<see cref="LinuxCachedStoreFile"/>) am Knoten, auch nach close eines Deskriptors, solange eine Abbildung
    /// die Datei hält. Erhöht unter der Registersperre (AttachNode), gesenkt mit Interlocked ohne sie; ein Knoten mit Haltern bleibt im Register.
    /// </summary>
    public int Holders;
}
