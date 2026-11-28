using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ProjektZeh.Packages;
using ProjektZeh.Storage;

namespace ProjektZeh.Processes.Vfs;

/// <summary>
/// Prozesswurzel eines Paketstarts (Plan N3-B, Schritt B2): Der gebundene <see cref="PackageIndex2"/> liefert die Paketpfade
/// schreibgeschuetzt, alles andere (etwa <c>/tmp</c>, <c>/.cache</c>) liegt in einem beschreibbaren Speicher darunter (im Kernel ein
/// <see cref="MemoryFileStore"/>, das mit dem Lauf endet).
/// </summary>
/// <remarks>
/// <para>Aufteilung nach dem ersten Pfadteil: Ist er ein Eintrag der Indexwurzel (<c>usr</c>, <c>lib</c>, <c>etc</c> …, ohne
/// Gross-/Kleinschreibung wie der Index), gehoert der ganze Unterbaum dem Index und ist <c>-EROFS</c> fuer jede Aenderung; ein
/// gleichnamiger Eintrag im Speicher darunter ist verdeckt. Sonst entscheidet der Speicher darunter. So kann ein Programm keine Datei
/// neben eine Paketdatei legen, die ein spaeterer Lader statt der Paketversion faende.</para>
/// <para>Inhalt: Beim ersten Oeffnen einer Paketdatei in dieser Wurzel prueft der Speicher Groesse und SHA-256 gegen das signierte
/// Manifest, auf demselben geoeffneten Strom, der danach gelesen wird. Weitere Oeffnungen derselben Datei pruefen nicht erneut: Die
/// Ablage <c>v2/&lt;id&gt;/&lt;version&gt;/files/</c> liegt unter <c>.zehos</c>, schreibt nur <c>PackageStore2</c> und wird fuer eine
/// gestufte Version nie ueberschrieben (Grenze: ein Schreiber am Dateidienst vorbei, etwa ein anderes Betriebssystem, waere nach der
/// ersten Pruefung unbemerkt).</para>
/// <para>Platte: Oeffnen, jeder Leseblock und Schliessen sperren <see cref="StorageLock"/> einzeln, hoechstens
/// <see cref="StorageLock.ChunkBytes"/> je Sperre. Der Speicher wird von allen Prozessen eines Laufs geteilt; die Pruefliste hat deshalb
/// eine eigene Sperre (Blatt, nie unter <see cref="StorageLock"/> genommen).</para>
/// </remarks>
internal sealed class PackageRootStore : ILinuxFileStore
{
    /// <summary>Aenderungszeit aller Paketeintraege: eine Sekunde nach 1970 wie die Untergrenze von <see cref="MemoryFileStore"/> (nie 0, siehe dort).</summary>
    internal const long PackageMtimeNanos = MemoryFileStore.MinStampNanos;

    // Uebliche Modi (0644, 0755, 0755, 0777) statt ohne Schreibbit: Der Schutz ist -EROFS des Speichers wie bei einem schreibgeschuetzt
    // eingehaengten Dateisystem; ohne Schreibbit meldete das Dateisystem schon vorher -EACCES, und Programme deuteten das als Rechtefehler.
    private const uint FileMode = 0x1A4, ExecMode = 0x1ED, DirMode = 0x1ED, LinkMode = 0x1FF;

    private readonly PackageIndex2 index;
    private readonly ILinuxFileStore below;
    private readonly HashSet<string> verified = new(StringComparer.OrdinalIgnoreCase);
    private readonly object verifiedGate = new();

    public PackageRootStore(PackageIndex2 index, ILinuxFileStore below)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
        this.below = below ?? throw new ArgumentNullException(nameof(below));
    }

    /// <summary>Wie der Speicher darunter; Paketpfade findet der Index zusaetzlich ohne Gross-/Kleinschreibung.</summary>
    public StringComparison NameComparison => below.NameComparison;

    /// <summary>Testnaht: wird nach dem Oeffnen und vor der Pruefung einer Paketdatei aufgerufen.</summary>
    internal Action<string>? OnVerify { get; init; }

    private bool InPackages(string path)
    {
        if (path.Length == 0) return false;
        int slash = path.IndexOf('/');
        return index.Lookup(slash < 0 ? path : path[..slash]).Kind != PackageIndexNodeKind.Missing;
    }

    public long Lookup(string path, out LinuxStoreNode node)
    {
        if (!InPackages(path)) return below.Lookup(path, out node);
        var entry = index.Lookup(path);
        node = entry.Kind switch
        {
            PackageIndexNodeKind.Directory => new LinuxStoreNode(LinuxNodeKind.Directory, 0, DirMode, PackageMtimeNanos),
            PackageIndexNodeKind.Link => new LinuxStoreNode(LinuxNodeKind.Symlink, System.Text.Encoding.UTF8.GetByteCount(entry.Target!), LinkMode, PackageMtimeNanos),
            PackageIndexNodeKind.File => new LinuxStoreNode(LinuxNodeKind.File, entry.File!.Size, entry.File.Executable ? ExecMode : FileMode, PackageMtimeNanos),
            _ => new LinuxStoreNode(LinuxNodeKind.Missing, 0, 0, 0),
        };
        return 0;
    }

    public long ReadLink(string path, out string target)
    {
        if (!InPackages(path)) return below.ReadLink(path, out target);
        var entry = index.Lookup(path);
        target = entry.Target ?? string.Empty;
        return entry.Kind switch
        {
            PackageIndexNodeKind.Link => 0,
            PackageIndexNodeKind.Missing => -LinuxErrno.ENOENT,
            _ => -LinuxErrno.EINVAL,
        };
    }

    public long List(string path, out string[] names)
    {
        if (path.Length > 0)
        {
            if (!InPackages(path)) return below.List(path, out names);
            names = index.List(path) ?? [];
            return index.Lookup(path).Kind switch
            {
                PackageIndexNodeKind.Directory => 0,
                PackageIndexNodeKind.Missing => -LinuxErrno.ENOENT,
                _ => -LinuxErrno.ENOTDIR,
            };
        }

        // Wurzel: Indexwurzel und Speicher darunter, verdeckte Namen des Speichers nur einmal (in der Schreibweise des Index).
        long listed = below.List(path, out string[] lower);
        if (listed < 0)
        {
            names = [];
            return listed;
        }

        string[] top = index.List(string.Empty)!;
        var merged = new List<string>(top.Length + lower.Length);
        merged.AddRange(top);
        foreach (string name in lower)
            if (!InPackages(name)) merged.Add(name);
        merged.Sort(StringComparer.Ordinal);
        names = [.. merged];
        return 0;
    }

    public long OpenFile(string path, bool write, out ILinuxStoreFile? file)
    {
        if (!InPackages(path)) return below.OpenFile(path, write, out file);
        file = null;
        var entry = index.Lookup(path);
        switch (entry.Kind)
        {
            case PackageIndexNodeKind.Missing: return -LinuxErrno.ENOENT;
            case PackageIndexNodeKind.Directory: return -LinuxErrno.EISDIR;
            case PackageIndexNodeKind.Link: return -LinuxErrno.EINVAL; // Das Dateisystem loest Links vorher auf.
        }

        if (write) return -LinuxErrno.EROFS;
        FileStream stream;
        try
        {
            stream = StorageLock.Run(() => new FileStream(entry.Location!, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return -LinuxErrno.EIO;
        }

        bool keep = false;
        try
        {
            bool known;
            lock (verifiedGate) known = verified.Contains(entry.Path);
            if (!known)
            {
                OnVerify?.Invoke(entry.Path);
                // Veraenderte oder fehlende Paketdatei: -EIO wie ein unlesbarer Block, der Start eines Laders scheitert damit sichtbar.
                if (!PackageStore2.Matches(stream, entry.File!, new byte[64 * 1024], CancellationToken.None)) return -LinuxErrno.EIO;
                lock (verifiedGate) verified.Add(entry.Path);
            }

            file = new PackageFile(stream, entry.File!.Size);
            keep = true;
            return 0;
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return -LinuxErrno.EIO;
        }
        finally
        {
            if (!keep) StorageLock.Run(() => stream.Dispose());
        }
    }

    public long CreateFile(string path, uint mode, out ILinuxStoreFile? file)
    {
        if (InPackages(path))
        {
            file = null;
            return -LinuxErrno.EROFS;
        }

        return below.CreateFile(path, mode, out file);
    }

    public long CreateDirectory(string path, uint mode) => InPackages(path) ? -LinuxErrno.EROFS : below.CreateDirectory(path, mode);

    public long CreateSymlink(string path, string target) => InPackages(path) ? -LinuxErrno.EROFS : below.CreateSymlink(path, target);

    public long DeleteFile(string path) => InPackages(path) ? -LinuxErrno.EROFS : below.DeleteFile(path);

    public long DeleteDirectory(string path) => InPackages(path) ? -LinuxErrno.EROFS : below.DeleteDirectory(path);

    public long Rename(string from, string to) => InPackages(from) || InPackages(to) ? -LinuxErrno.EROFS : below.Rename(from, to);

    /// <summary>Geoeffnete, gepruefte Paketdatei; Laenge aus dem Manifest (gleich der geprueften Laenge).</summary>
    private sealed class PackageFile(FileStream stream, long length) : ILinuxStoreFile
    {
        private readonly object gate = new();
        private byte[] buffer = [];
        private bool closed;

        public long Length() => length;

        public long ReadAt(long offset, Span<byte> dst)
        {
            if (offset < 0) return -LinuxErrno.EINVAL;
            if (offset >= length || dst.Length == 0) return 0;
            int want = (int)Math.Min(Math.Min(dst.Length, length - offset), StorageLock.ChunkBytes);
            // Mehrere Prozesse eines Laufs teilen die offene Datei; Position und Puffer gehoeren in einen Abschnitt.
            lock (gate)
            {
                if (closed) return -LinuxErrno.EBADF;
                if (buffer.Length < want) buffer = new byte[want];
                int read;
                try
                {
                    byte[] target = buffer;
                    read = StorageLock.Run(() =>
                    {
                        stream.Position = offset;
                        int total = 0;
                        while (total < want)
                        {
                            int got = stream.Read(target, total, want - total);
                            if (got <= 0) break;
                            total += got;
                        }

                        return total;
                    });
                }
                catch (Exception exception) when (FileErrors.IsFileError(exception))
                {
                    return -LinuxErrno.EIO;
                }

                buffer.AsSpan(0, read).CopyTo(dst);
                return read;
            }
        }

        public long WriteAt(long offset, ReadOnlySpan<byte> src) => -LinuxErrno.EBADF;

        public long SetLength(long newLength) => -LinuxErrno.EROFS;

        public long Close()
        {
            lock (gate)
            {
                if (closed) return 0;
                closed = true;
                buffer = [];
            }

            StorageLock.Run(() => stream.Dispose());
            return 0;
        }
    }
}
