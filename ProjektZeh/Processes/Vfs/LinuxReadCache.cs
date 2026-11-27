using System;
using System.Collections.Generic;
using System.Threading;

namespace ProjektZeh.Processes.Vfs;

/// <summary>
/// V40: Lese-Cache für Dateiseiten eines Laufs (Seitenfehler von PT_LOAD und mmap, auch MAP_SHARED-Ablagen), geteilt von allen Prozessen.
/// Startet derselbe Prozess mehrfach (Ladybird: drei WebContent), liest nur der erste von der Platte; jeder Fehlschlag liest gleich einen
/// ganzen Block von <see cref="BlockBytes"/> (Vorauslesen), statt 4 KiB je Seitenfehler. Messung V39: 4-KiB-Lesungen kosten im Mittel 179 µs,
/// unabhängig von der Länge überwiegend Aufwand je Aufruf (Sperre, Position, FAT-Kette).
/// <para>
/// Der Cache kopiert Daten in die Frames der Prozesse, er teilt keine Frames: Jeder Prozess behält eigene, beschreibbare Seiten wie bisher
/// (MAP_PRIVATE braucht so kein Copy-on-Write). Der Preis ist doppelter Speicher für zwischengespeicherte Blöcke, begrenzt durch
/// <c>maxBytes</c>; die ältesten Blöcke gehen zuerst.
/// </para>
/// <para>
/// Gültigkeit: Schlüssel ist Knoten (offene Datei des Laufs, <see cref="LinuxFileNode"/>), Generation des Knotens und Blocknummer. Jede Änderung
/// über einen Deskriptor oder eine Ablage des Laufs (write, ftruncate, O_TRUNC, Zurückschreiben von MAP_SHARED) erhöht nach dem Schreiben die
/// Generation (<see cref="LinuxCachedStoreFile"/>); ältere Blöcke werden nicht mehr gefunden. Ein Leser fügt einen Block nur ein, wenn die
/// Generation während des Lesens gleich blieb. Nicht gesehen werden Änderungen außerhalb des Laufs (andere App auf derselben Platte); Hartlinks
/// gibt es im Speicher nicht.
/// </para>
/// <para>
/// Sperre <c>_gate</c>: nur kurz um Suchen und Einfügen, nie beim Lesen von der Platte. Platz in der Reihenfolge: nach <c>_memGate</c> eines
/// Prozesses und nach FileGate, vor <c>StorageLock</c> (wird darin nie gehalten); innerhalb keine weitere Sperre.
/// </para>
/// </summary>
internal sealed class LinuxReadCache
{
    /// <summary>Blockgröße: 16 Seiten je Plattenzugriff.</summary>
    public const int BlockBytes = 64 * 1024;

    /// <summary>Standardgrenze im Kernel (Gast mit 512 MiB): 512 Blöcke.</summary>
    public const long DefaultMaxBytes = 32L << 20;

    private readonly record struct Key(LinuxFileNode Node, long Generation, long Block);

    private readonly object _gate = new();
    private readonly Dictionary<Key, byte[]> _blocks = new();
    private readonly Queue<Key> _order = new();
    private readonly long _maxBytes;
    private long _bytes;

    public LinuxReadCache(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, (long)BlockBytes);
        _maxBytes = maxBytes;
    }

    /// <summary>Treffer (Block aus dem Cache).</summary>
    public long Hits { get; private set; }

    /// <summary>Fehlschläge (Block von der Platte gelesen).</summary>
    public long Misses { get; private set; }

    /// <summary>Belegte Bytes aller Blöcke.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Liest ab <paramref name="offset"/> über den Cache; Anzahl (0 am Dateiende) oder -errno wie <see cref="ILinuxStoreFile.ReadAt"/>.</summary>
    public long Read(ILinuxStoreFile file, LinuxFileNode node, long offset, Span<byte> dst)
    {
        if (offset < 0 || dst.Length == 0)
        {
            return file.ReadAt(offset, dst); // Fehler und Länge 0 meldet der Speicher selbst
        }

        int done = 0;
        while (done < dst.Length)
        {
            long at = offset + done;
            long block = at / BlockBytes;
            byte[]? data = Block(file, node, block, out long error);
            if (data == null)
            {
                return done > 0 ? done : error;
            }

            int inBlock = (int)(at - (block * BlockBytes));
            if (inBlock >= data.Length)
            {
                break; // Dateiende
            }

            int n = Math.Min(data.Length - inBlock, dst.Length - done);
            data.AsSpan(inBlock, n).CopyTo(dst.Slice(done));
            done += n;
            if (data.Length < BlockBytes)
            {
                break; // kurzer Block: die Datei endete beim Lesen darin
            }
        }

        return done;
    }

    private byte[]? Block(ILinuxStoreFile file, LinuxFileNode node, long block, out long error)
    {
        error = 0;
        long generation = Volatile.Read(ref node.Generation);
        var key = new Key(node, generation, block);
        lock (_gate)
        {
            if (_blocks.TryGetValue(key, out byte[]? cached))
            {
                Hits++;
                return cached;
            }

            Misses++;
        }

        // Ohne Sperre lesen: Die Platte nimmt StorageLock, und andere Prozesse sollen derweil Treffer bekommen.
        byte[] data = new byte[BlockBytes];
        int got = 0;
        long start = block * BlockBytes;
        while (got < BlockBytes)
        {
            long n = file.ReadAt(start + got, data.AsSpan(got));
            if (n < 0)
            {
                error = n;
                return null;
            }

            if (n == 0)
            {
                break;
            }

            got += (int)n;
        }

        if (got < BlockBytes)
        {
            Array.Resize(ref data, got);
        }

        lock (_gate)
        {
            // Hat jemand während des Lesens geschrieben, ist der Block vielleicht halb alt: liefern (der Leser lief gleichzeitig mit dem
            // Schreiben), aber nicht aufheben.
            if (Volatile.Read(ref node.Generation) == generation && !_blocks.ContainsKey(key))
            {
                InsertLocked(key, data);
            }
        }

        return data;
    }

    private void InsertLocked(Key key, byte[] data)
    {
        while (_bytes + data.Length > _maxBytes && _order.Count > 0)
        {
            if (_blocks.Remove(_order.Dequeue(), out byte[]? old))
            {
                _bytes -= old.Length;
            }
        }

        _blocks[key] = data;
        _order.Enqueue(key);
        _bytes += data.Length;
    }
}

/// <summary>
/// V40: Speicherdatei eines Deskriptors mit Knoten: zählt die Generation des Knotens bei jeder Änderung hoch (nach dem Schreiben, auch bei
/// Fehler, weil ein Teil geschrieben sein kann) und liest Seiten über den <see cref="LinuxReadCache"/> des Laufs. Hält den Knoten im Register
/// (<see cref="LinuxFileNode.Holders"/>), bis die Datei geschlossen ist, also auch nach close des Deskriptors, solange eine Abbildung lebt:
/// Sonst bekäme ein späteres open einen neuen Knoten, und Schreiben darüber machte die Blöcke der Abbildung nicht ungültig.
/// </summary>
internal sealed class LinuxCachedStoreFile : ILinuxStoreFile
{
    private readonly ILinuxStoreFile _file;
    private readonly LinuxFileNode _node;
    private readonly LinuxReadCache? _cache;
    private int _closed;

    /// <summary>Übernimmt einen Halt auf <paramref name="node"/> (<see cref="LinuxFileSystem.AttachNode"/>), den <see cref="Close"/> zurückgibt.</summary>
    public LinuxCachedStoreFile(ILinuxStoreFile file, LinuxFileNode node, LinuxReadCache? cache)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _cache = cache;
    }

    public long Length() => _file.Length();

    /// <summary>Lesen eines Deskriptors (read, pread): direkt, damit große Lesungen den Cache nicht verdrängen.</summary>
    public long ReadAt(long offset, Span<byte> dst) => _file.ReadAt(offset, dst);

    /// <summary>Lesen für Seiten (Seitenfehler, Ablagen): über den Cache, ohne Cache wie <see cref="ReadAt"/>.</summary>
    public long ReadPages(long offset, Span<byte> dst) => _cache != null ? _cache.Read(_file, _node, offset, dst) : _file.ReadAt(offset, dst);

    public long WriteAt(long offset, ReadOnlySpan<byte> src)
    {
        long n = _file.WriteAt(offset, src);
        _ = Interlocked.Increment(ref _node.Generation);
        return n;
    }

    public long SetLength(long length)
    {
        long r = _file.SetLength(length);
        _ = Interlocked.Increment(ref _node.Generation);
        return r;
    }

    public long Close()
    {
        // Der Halt geht ohne Registersperre zurück: Close läuft auch unter der Speichersperre eines Prozesses (munmap des letzten Verweises).
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _ = Interlocked.Decrement(ref _node.Holders);
        }

        return _file.Close();
    }
}
