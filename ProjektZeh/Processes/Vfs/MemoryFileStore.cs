using System;
using System.Collections.Generic;

namespace ProjektZeh.Processes.Vfs;

/// <summary>
/// Speicher im Arbeitsspeicher (Hosttests, später etwa <c>/tmp</c>). Namen unterscheiden Groß- und Kleinschreibung. Grenzen gegen
/// bösartige Programme: <see cref="MaxFileBytes"/> je Datei (-EFBIG), <see cref="MaxTotalBytes"/> und <see cref="MaxNodes"/>
/// zusammen (-ENOSPC). Eine entfernte, noch offene Datei behält ihren Inhalt bis zum letzten Schließen, wie bei Linux.
/// </summary>
internal sealed class MemoryFileStore : ILinuxFileStore
{
    private sealed class Node
    {
        public LinuxNodeKind Kind;
        public uint Mode;
        public long Mtime;
        public string Target = string.Empty;
        public byte[] Data = Array.Empty<byte>();
        public long Length;
        public SortedSet<string>? Children;
        public bool Linked = true;
        public int OpenCount;
    }

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private long _totalBytes;
    private long _clock;

    /// <summary>
    /// Änderungszeit: Wanduhr in Nanosekunden seit 1970, nie rückwärts und je Änderung mindestens eins weiter. Ein bloßer Zähler (1, 2, ...)
    /// ergab st_mtime 0: Fontconfig nimmt eine Cache-Datei nur, wenn sie strikt neuer als 0 s ist (FcDirCacheMapHelper), und verwarf
    /// deshalb den Cache unter /tmp in jedem Prozess.
    /// </summary>
    private long Stamp()
    {
        long now = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
        _clock = now > _clock ? now : _clock + 1;
        return _clock;
    }

    public MemoryFileStore(long maxFileBytes = 16L << 20, long maxTotalBytes = 64L << 20, int maxNodes = 65536)
    {
        MaxFileBytes = maxFileBytes;
        MaxTotalBytes = maxTotalBytes;
        MaxNodes = maxNodes;
        _nodes[string.Empty] = new Node { Kind = LinuxNodeKind.Directory, Mode = 0x1ED, Children = new SortedSet<string>(StringComparer.Ordinal) };
    }

    public long MaxFileBytes { get; }

    public long MaxTotalBytes { get; }

    public int MaxNodes { get; }

    /// <summary>Belegte Inhaltsbytes, auch von entfernten, noch offenen Dateien.</summary>
    public long TotalBytes => _totalBytes;

    public int NodeCount => _nodes.Count;

    public StringComparison NameComparison => StringComparison.Ordinal;

    /// <summary>Setzt die Rechtebits eines Eintrags (Tests für -EACCES); false, wenn er fehlt.</summary>
    public bool SetMode(string path, uint mode)
    {
        if (!_nodes.TryGetValue(path, out Node? node))
        {
            return false;
        }

        node.Mode = mode & LinuxMode.PermissionMask;
        return true;
    }

    public long Lookup(string path, out LinuxStoreNode node)
    {
        if (!_nodes.TryGetValue(path, out Node? n))
        {
            node = new LinuxStoreNode(LinuxNodeKind.Missing, 0, 0, 0);
            return 0;
        }

        long size = n.Kind switch
        {
            LinuxNodeKind.File => n.Length,
            LinuxNodeKind.Symlink => System.Text.Encoding.UTF8.GetByteCount(n.Target),
            _ => 4096,
        };
        node = new LinuxStoreNode(n.Kind, size, n.Mode, n.Mtime);
        return 0;
    }

    public long ReadLink(string path, out string target)
    {
        target = string.Empty;
        if (!_nodes.TryGetValue(path, out Node? n))
        {
            return -LinuxErrno.ENOENT;
        }

        if (n.Kind != LinuxNodeKind.Symlink)
        {
            return -LinuxErrno.EINVAL;
        }

        target = n.Target;
        return 0;
    }

    public long List(string path, out string[] names)
    {
        names = Array.Empty<string>();
        if (!_nodes.TryGetValue(path, out Node? n))
        {
            return -LinuxErrno.ENOENT;
        }

        if (n.Children is null)
        {
            return -LinuxErrno.ENOTDIR;
        }

        names = new string[n.Children.Count];
        n.Children.CopyTo(names);
        return 0;
    }

    public long OpenFile(string path, bool write, out ILinuxStoreFile? file)
    {
        file = null;
        if (!_nodes.TryGetValue(path, out Node? n))
        {
            return -LinuxErrno.ENOENT;
        }

        if (n.Kind != LinuxNodeKind.File)
        {
            return n.Kind == LinuxNodeKind.Directory ? -LinuxErrno.EISDIR : -LinuxErrno.EINVAL;
        }

        file = new Handle(this, n);
        return 0;
    }

    public long CreateFile(string path, uint mode, out ILinuxStoreFile? file)
    {
        file = null;
        long r = Add(path, new Node { Kind = LinuxNodeKind.File, Mode = mode & LinuxMode.PermissionMask }, out Node? node);
        if (r < 0)
        {
            return r;
        }

        file = new Handle(this, node!);
        return 0;
    }

    public long CreateDirectory(string path, uint mode) =>
        Add(path, new Node { Kind = LinuxNodeKind.Directory, Mode = mode & LinuxMode.PermissionMask, Children = new SortedSet<string>(StringComparer.Ordinal) }, out _);

    public long CreateSymlink(string path, string target) =>
        Add(path, new Node { Kind = LinuxNodeKind.Symlink, Mode = LinuxMode.PermissionMask, Target = target }, out _);

    public long DeleteFile(string path)
    {
        if (!_nodes.TryGetValue(path, out Node? n) || path.Length == 0)
        {
            return -LinuxErrno.ENOENT;
        }

        if (n.Kind == LinuxNodeKind.Directory)
        {
            return -LinuxErrno.EISDIR;
        }

        Remove(path, n);
        return 0;
    }

    public long DeleteDirectory(string path)
    {
        if (!_nodes.TryGetValue(path, out Node? n) || path.Length == 0)
        {
            return -LinuxErrno.ENOENT;
        }

        if (n.Children is null)
        {
            return -LinuxErrno.ENOTDIR;
        }

        if (n.Children.Count > 0)
        {
            return -LinuxErrno.ENOTEMPTY;
        }

        Remove(path, n);
        return 0;
    }

    public long Rename(string from, string to)
    {
        if (from.Length == 0 || to.Length == 0 || !_nodes.TryGetValue(from, out Node? n))
        {
            return -LinuxErrno.ENOENT;
        }

        if (from == to)
        {
            return 0;
        }

        if (!_nodes.TryGetValue(Parent(to), out Node? newParent) || newParent.Children is null)
        {
            return -LinuxErrno.ENOENT;
        }

        if (n.Children != null && to.StartsWith(from + "/", StringComparison.Ordinal))
        {
            return -LinuxErrno.EINVAL;
        }

        if (_nodes.TryGetValue(to, out Node? old))
        {
            if (old.Children is { Count: > 0 })
            {
                return -LinuxErrno.ENOTEMPTY;
            }

            Remove(to, old);
        }

        _nodes[Parent(from)].Children!.Remove(Name(from));
        newParent.Children.Add(Name(to));
        n.Mtime = Stamp();
        if (n.Children is null)
        {
            _nodes.Remove(from);
            _nodes[to] = n;
            return 0;
        }

        // Einträge unter einem Ordner neu einordnen: linear in der Zahl aller Einträge, nur bei rename eines Ordners.
        var moved = new List<KeyValuePair<string, Node>>();
        string prefix = from + "/";
        foreach (KeyValuePair<string, Node> entry in _nodes)
        {
            if (entry.Key == from || entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                moved.Add(entry);
            }
        }

        foreach (KeyValuePair<string, Node> entry in moved)
        {
            _nodes.Remove(entry.Key);
        }

        foreach (KeyValuePair<string, Node> entry in moved)
        {
            _nodes[string.Concat(to, entry.Key.AsSpan(from.Length))] = entry.Value;
        }

        return 0;
    }

    private long Add(string path, Node node, out Node? added)
    {
        added = null;
        if (path.Length == 0 || _nodes.ContainsKey(path))
        {
            return -LinuxErrno.EEXIST;
        }

        if (!_nodes.TryGetValue(Parent(path), out Node? parent))
        {
            return -LinuxErrno.ENOENT;
        }

        if (parent.Children is null)
        {
            return -LinuxErrno.ENOTDIR;
        }

        if (_nodes.Count >= MaxNodes)
        {
            return -LinuxErrno.ENOSPC;
        }

        node.Mtime = Stamp();
        _nodes[path] = node;
        parent.Children.Add(Name(path));
        added = node;
        return 0;
    }

    private void Remove(string path, Node node)
    {
        _nodes.Remove(path);
        _nodes[Parent(path)].Children!.Remove(Name(path));
        node.Linked = false;
        if (node.OpenCount == 0)
        {
            _totalBytes -= node.Length;
        }
    }

    private static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path.Substring(0, slash);
    }

    private static string Name(string path) => path.Substring(path.LastIndexOf('/') + 1);

    private sealed class Handle : ILinuxStoreFile
    {
        private readonly MemoryFileStore _store;
        private Node? _node;

        public Handle(MemoryFileStore store, Node node)
        {
            _store = store;
            _node = node;
            node.OpenCount++;
        }

        public long Length() => _node is null ? -LinuxErrno.EBADF : _node.Length;

        public long ReadAt(long offset, Span<byte> dst)
        {
            if (_node is null)
            {
                return -LinuxErrno.EBADF;
            }

            if (offset < 0)
            {
                return -LinuxErrno.EINVAL;
            }

            if (offset >= _node.Length)
            {
                return 0;
            }

            int n = (int)Math.Min(dst.Length, _node.Length - offset);
            _node.Data.AsSpan((int)offset, n).CopyTo(dst);
            return n;
        }

        public long WriteAt(long offset, ReadOnlySpan<byte> src)
        {
            if (_node is null)
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

            // Wie Linux bei RLIMIT_FSIZE: kürzen bis zur Grenze, erst ein Schreiben ganz jenseits davon ist -EFBIG.
            int count = (int)Math.Min(src.Length, _store.MaxFileBytes - offset);
            long end = offset + count;
            if (end > _node.Length)
            {
                long r = Grow(end);
                if (r < 0)
                {
                    return r;
                }
            }

            src.Slice(0, count).CopyTo(_node.Data.AsSpan((int)offset));
            _node.Mtime = _store.Stamp();
            return count;
        }

        public long SetLength(long length)
        {
            if (_node is null)
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

            if (length > _node.Length)
            {
                return Grow(length);
            }

            Array.Clear(_node.Data, (int)length, (int)(_node.Length - length));
            _store._totalBytes -= _node.Length - length;
            _node.Length = length;
            _node.Mtime = _store.Stamp();
            return 0;
        }

        public long Close()
        {
            if (_node is null)
            {
                return 0;
            }

            _node.OpenCount--;
            if (!_node.Linked && _node.OpenCount == 0)
            {
                _store._totalBytes -= _node.Length;
            }

            _node = null;
            return 0;
        }

        /// <summary>Verlängert auf <paramref name="end"/> (genullt); verdoppelt den Puffer, damit viele kleine Schreibvorgänge linear bleiben.</summary>
        private long Grow(long end)
        {
            Node node = _node!;
            long delta = end - node.Length;
            if (_store._totalBytes + delta > _store.MaxTotalBytes)
            {
                return -LinuxErrno.ENOSPC;
            }

            if (end > node.Data.Length)
            {
                long capacity = Math.Max(end, Math.Min(_store.MaxFileBytes, Math.Max(256, (long)node.Data.Length * 2)));
                byte[] data = new byte[capacity];
                node.Data.AsSpan(0, (int)node.Length).CopyTo(data);
                node.Data = data;
            }

            node.Length = end;
            _store._totalBytes += delta;
            return 0;
        }
    }
}
