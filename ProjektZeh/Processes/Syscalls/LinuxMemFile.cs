using System;
using System.Collections.Generic;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Optional neben <see cref="ILinuxFiles"/> (V17): geteilter Speicher über memfd. Ein Prozess legt eine <see cref="LinuxMemFile"/> an und
/// bildet sie mit MAP_SHARED ab; alle Abbildungen derselben Datei, auch in anderen Prozessen (Weitergabe über SCM_RIGHTS oder Vererbung bei
/// posix_spawn), sehen dieselben Frames.
/// </summary>
internal interface ILinuxSharedMemory
{
    /// <summary>Neue memfd; <paramref name="sealing"/> wie MFD_ALLOW_SEALING (sonst ist F_SEAL_SEAL von Anfang an gesetzt).</summary>
    LinuxMemFile CreateMemFile(string name, bool sealing);

    /// <summary>
    /// V24 (V21-Review P3 3): die namenlose Datei hinter MAP_SHARED|MAP_ANONYMOUS. Ihre Länge ist nicht an den Speicher des Prozesses
    /// gebunden: Wie bei privaten anonymen Abbildungen kommen Frames erst beim Zugriff, und dort gilt die Grenze des Prozesses. Sonst
    /// lieferte ein großer, nur teilweise benutzter Bereich (MAP_NORESERVE, Arenen) -ENOMEM, privat aber Erfolg.
    /// </summary>
    LinuxMemFile CreateAnonymousSharedFile();

    /// <summary>
    /// Bildet <paramref name="len"/> Bytes (seitenweise) der Datei ab Seite <paramref name="pageOffset"/> geteilt ab; Adresse oder -errno.
    /// Frames kommen erst beim Zugriff (Seitenfehler oder Kernelzugriff) und gehören dann Datei und Abbildung gemeinsam.
    /// </summary>
    long MapShared(LinuxMemFile file, ulong len, int prot, long pageOffset);

    /// <summary>mprotect mit Fehlernummer: 0, -EACCES (PROT_WRITE auf eine nach F_SEAL_WRITE entstandene Abbildung) oder -ENOMEM.</summary>
    long ProtectShared(ulong addr, ulong len, int prot);
}

/// <summary>
/// Ein Frame einer memfd mit Referenzzählung (V17): ein Verweis für die Seitentabelle der Datei, einer je Prozessseite, die ihn abbildet.
/// Der letzte Verweis gibt ihn frei; so überlebt der Inhalt ftruncate auf 0, close und das Ende des Erzeugers, solange ihn eine Abbildung
/// hält. Geschützt durch die Sperre der besitzenden <see cref="LinuxMemFile"/>.
/// </summary>
internal sealed class LinuxSharedFrame
{
    public LinuxSharedFrame(LinuxMemFile owner, ulong frame)
    {
        Owner = owner;
        Frame = frame;
    }

    public LinuxMemFile Owner { get; }

    public ulong Frame { get; }

    /// <summary>Anzahl der Verweise; nur unter der Sperre von <see cref="Owner"/>.</summary>
    public int Refs { get; set; } = 1;
}

/// <summary>
/// memfd_create (V17, Ladybirds AnonymousBuffer für Bitmaps): eine namenlose, im Arbeitsspeicher liegende reguläre Datei aus Frames.
/// read/write/lseek/ftruncate/fstat wirken wie bei tmpfs; MAP_SHARED bildet dieselben Frames in jeden abbildenden Prozess ab
/// (<see cref="ILinuxSharedMemory"/>). MAP_PRIVATE bleibt eine Kopie wie bei anderen Dateien.
/// <para>
/// Sperrreihenfolge: <c>_gate</c> steht unter FileGate und unter der Speichersperre des Prozesses (Seitenfehler holen hier einen Frame),
/// über der Sperre des PageAllocator. Darin keine andere Sperre und kein Zugriff auf Benutzerspeicher.
/// </para>
/// <para>
/// Lebensdauer: Die Datei lebt, solange ein Deskriptor oder eine Abbildung sie hält. Nach dem letzten close und der letzten Abbildung gibt
/// sie ihre Seitentabelle frei; Frames, die eine Prozessseite noch abbildet, bleiben bis zu deren Freigabe (Refzählung je Frame).
/// Abweichung: Schrumpft ftruncate die Datei, behalten schon abgebildete Seiten ihren alten Frame (Linux entfernt sie, ein Zugriff gibt
/// SIGBUS); neue Zugriffe hinter dem Ende scheitern wie bei Linux.
/// </para>
/// </summary>
internal sealed class LinuxMemFile : ILinuxFile
{
    public const int SealSeal = 1, SealShrink = 2, SealGrow = 4, SealWrite = 8, SealFutureWrite = 0x10, SealExec = 0x20;
    private const int KnownSeals = SealSeal | SealShrink | SealGrow | SealWrite | SealFutureWrite | SealExec;
    private const long Page = 4096;
    private const long EPERM = 1, ENOTDIR = 20, EINVAL = 22, EFBIG = 27, ENOSPC = 28, EBUSY = 16;

    private readonly object _gate = new();
    private readonly IUserPages _pages;
    private readonly long _maxBytes;
    private readonly Dictionary<long, LinuxSharedFrame> _table = new();
    private readonly ulong _ino;
    private long _size;
    private long _position;
    private int _seals;
    private int _mappings;
    private int _writableMappings;
    private bool _closed;

    /// <param name="pages">Frames kommen von hier; im Kernel gemeinsam für alle Prozesse (PageAllocator), daher nicht an den Erzeuger gebunden.</param>
    /// <param name="maxBytes">Obergrenze der Länge (Speicher des Erzeugers); darüber -EFBIG wie RLIMIT_FSIZE.</param>
    /// <param name="ino">Inodenummer für fstat; vergibt der Erzeuger (ohne statischen Zähler, der auf mehreren CPUs geteilt wäre).</param>
    public LinuxMemFile(IUserPages pages, string name, bool sealing, long maxBytes, ulong ino)
    {
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _maxBytes = maxBytes;
        _seals = sealing ? 0 : SealSeal;
        _ino = ino;
        Path = "/memfd:" + name + " (deleted)"; // so zeigt Linux sie in /proc/self/fd
    }

    public string Path { get; }

    public bool IsDirectory => false;

    public long DirIndex { get; set; }

    /// <summary>Gesetzte Siegel (F_GET_SEALS).</summary>
    public int Seals
    {
        get
        {
            lock (_gate)
            {
                return _seals;
            }
        }
    }

    /// <summary>Frames in der Seitentabelle der Datei (für Tests und Messung).</summary>
    public int FrameCount
    {
        get
        {
            lock (_gate)
            {
                return _table.Count;
            }
        }
    }

    /// <summary>F_ADD_SEALS: 0 oder -errno (-EPERM bei F_SEAL_SEAL, -EBUSY für F_SEAL_WRITE bei beschreibbarer Abbildung, wie Linux).</summary>
    public long AddSeals(int seals)
    {
        if ((seals & ~KnownSeals) != 0)
        {
            return -EINVAL;
        }

        lock (_gate)
        {
            if ((_seals & SealSeal) != 0)
            {
                return -EPERM;
            }

            if ((seals & SealWrite) != 0 && _writableMappings > 0)
            {
                return -EBUSY;
            }

            _seals |= seals;
            return 0;
        }
    }

    public long Read(Span<byte> dst)
    {
        lock (_gate)
        {
            long n = CopyLocked(_position, dst, default, write: false);
            _position += n;
            return n;
        }
    }

    public long Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if ((_seals & (SealWrite | SealFutureWrite)) != 0)
            {
                return -EPERM;
            }

            if (data.Length == 0)
            {
                return 0;
            }

            // Erst gegen Überlauf prüfen: nach lseek(LONG_MAX - 1) wäre _position + Length negativ und käme an beiden Grenzen vorbei
            // (V17-Review P2). Linux 6.12 meldet den Überlauf mit -EINVAL (rw_verify_area), die Obergrenze mit -EFBIG.
            if (_position > long.MaxValue - data.Length)
            {
                return -EINVAL;
            }

            if (_position > _maxBytes - data.Length)
            {
                return -EFBIG;
            }

            long end = _position + data.Length;
            if (end > _size && (_seals & SealGrow) != 0)
            {
                return -EPERM;
            }

            long n = CopyLocked(_position, default, data, write: true);
            if (n < 0)
            {
                return n;
            }

            _position += n;
            if (_position > _size)
            {
                _size = _position;
            }

            return n;
        }
    }

    public long Seek(long offset, int whence)
    {
        lock (_gate)
        {
            long basis = whence switch { 0 => 0, 1 => _position, 2 => _size, _ => -1 };
            if (basis < 0 || (offset > 0 && basis > long.MaxValue - offset) || basis + offset < 0)
            {
                return -EINVAL;
            }

            _position = basis + offset;
            return _position;
        }
    }

    public long Stat(out LinuxStat st)
    {
        lock (_gate)
        {
            // Linux: memfd liegt auf tmpfs, Rechte 0777, kein Link.
            st = new LinuxStat
            {
                Dev = 1,
                Ino = _ino,
                Mode = 0x8000 | 0x1ff,
                Nlink = 0, // Linux: clear_nlink, die Datei hat keinen Namen
                Uid = 1000,
                Gid = 1000,
                Size = _size,
                BlockSize = Page,
                Blocks = _table.Count * (Page / 512),
            };
            return 0;
        }
    }

    public long ReadDir(long index, out LinuxDirEntry entry)
    {
        entry = default;
        return -ENOTDIR;
    }

    public long Truncate(long length)
    {
        if (length < 0)
        {
            return -EINVAL;
        }

        lock (_gate)
        {
            if ((length < _size && (_seals & SealShrink) != 0) || (length > _size && (_seals & SealGrow) != 0))
            {
                return -EPERM;
            }

            if (length > _maxBytes)
            {
                return -EFBIG;
            }

            if (length < _size)
            {
                // Ganze Seiten hinter dem Ende verlassen die Datei; die angeschnittene Seite wird ab dem Ende genullt, damit ein späteres
                // Wachsen Nullen liest wie bei Linux.
                long keep = (length + Page - 1) / Page;
                var drop = new List<long>();
                foreach (long index in _table.Keys)
                {
                    if (index >= keep)
                    {
                        drop.Add(index);
                    }
                }

                foreach (long index in drop)
                {
                    UnrefLocked(_table[index]);
                    _table.Remove(index);
                }

                long cut = length % Page;
                if (cut != 0 && _table.TryGetValue(length / Page, out LinuxSharedFrame? last))
                {
                    _pages.Frame(last.Frame).Slice((int)cut).Clear();
                }
            }

            _size = length;
            return 0;
        }
    }

    public long Sync() => 0;

    /// <summary>Letztes close aller Deskriptoren; Abbildungen halten die Datei weiter.</summary>
    public long Close()
    {
        lock (_gate)
        {
            _closed = true;
            ReleaseTableIfDeadLocked();
            return 0;
        }
    }

    /// <summary>
    /// Eine geteilte Abbildung beginnt; -EPERM, wenn <paramref name="writable"/> (PROT_WRITE) und ein Schreibsiegel gesetzt ist.
    /// <paramref name="mayWrite"/>: ob sie später schreibbar werden darf (Linux: VM_MAYWRITE). Ohne Schreibsiegel zählt sie deshalb
    /// immer als schreibbar, auch mit PROT_READ oder PROT_NONE (Linux 6.12: F_SEAL_WRITE gibt dann -EBUSY).
    /// </summary>
    internal long AddMapping(bool writable, out bool mayWrite)
    {
        lock (_gate)
        {
            mayWrite = (_seals & (SealWrite | SealFutureWrite)) == 0;
            if (writable && !mayWrite)
            {
                return -EPERM;
            }

            CountLocked(mayWrite);
            return 0;
        }
    }

    /// <summary>Reststück einer bestehenden Abbildung nach munmap in der Mitte; ohne Siegelprüfung (Linux teilt die VMA ohne Prüfung).</summary>
    internal void AddSplit(bool mayWrite)
    {
        lock (_gate)
        {
            CountLocked(mayWrite);
        }
    }

    private void CountLocked(bool mayWrite)
    {
        _mappings++;
        if (mayWrite)
        {
            _writableMappings++;
        }
    }

    /// <summary>Eine Abbildung endet (munmap, Prozessende); <paramref name="mayWrite"/> wie bei <see cref="AddMapping"/>.</summary>
    internal void RemoveMapping(bool mayWrite)
    {
        lock (_gate)
        {
            _mappings--;
            if (mayWrite)
            {
                _writableMappings--;
            }

            ReleaseTableIfDeadLocked();
        }
    }

    /// <summary>
    /// Frame der Seite <paramref name="index"/> mit einem neuen Verweis für die abbildende Prozessseite; null hinter dem Dateiende (Linux:
    /// SIGBUS) oder ohne Speicher. Fehlt der Frame, kommt ein genullter neu in die Datei.
    /// </summary>
    internal LinuxSharedFrame? Acquire(long index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= (_size + Page - 1) / Page)
            {
                return null;
            }

            LinuxSharedFrame? f = FrameLocked(index);
            if (f != null)
            {
                f.Refs++;
            }

            return f;
        }
    }

    /// <summary>Gibt den Verweis einer Prozessseite zurück; der letzte Verweis gibt den Frame frei.</summary>
    internal void Release(LinuxSharedFrame frame)
    {
        lock (_gate)
        {
            UnrefLocked(frame);
        }
    }

    private LinuxSharedFrame? FrameLocked(long index)
    {
        if (_table.TryGetValue(index, out LinuxSharedFrame? f))
        {
            return f;
        }

        ulong frame = _pages.AllocateFrame(); // genullt
        if (frame == 0)
        {
            return null;
        }

        f = new LinuxSharedFrame(this, frame);
        _table[index] = f;
        return f;
    }

    private void UnrefLocked(LinuxSharedFrame f)
    {
        if (--f.Refs == 0)
        {
            _pages.FreeFrame(f.Frame);
        }
    }

    private void ReleaseTableIfDeadLocked()
    {
        if (!_closed || _mappings > 0)
        {
            return;
        }

        foreach (LinuxSharedFrame f in _table.Values)
        {
            UnrefLocked(f);
        }

        _table.Clear();
        _size = 0;
    }

    /// <summary>Kopiert ab <paramref name="at"/>; Lesen endet am Dateiende, Schreiben legt Frames an. Anzahl oder -ENOSPC.</summary>
    private long CopyLocked(long at, Span<byte> dst, ReadOnlySpan<byte> src, bool write)
    {
        long want = write ? src.Length : Math.Max(0, Math.Min(dst.Length, _size - at));
        long done = 0;
        while (done < want)
        {
            long pos = at + done;
            long index = pos / Page;
            int offset = (int)(pos % Page);
            int chunk = (int)Math.Min(Page - offset, want - done);
            if (write)
            {
                LinuxSharedFrame? f = FrameLocked(index);
                if (f == null)
                {
                    return done > 0 ? done : -ENOSPC;
                }

                src.Slice((int)done, chunk).CopyTo(_pages.Frame(f.Frame).Slice(offset, chunk));
            }
            else if (_table.TryGetValue(index, out LinuxSharedFrame? f))
            {
                _pages.Frame(f.Frame).Slice(offset, chunk).CopyTo(dst.Slice((int)done, chunk));
            }
            else
            {
                dst.Slice((int)done, chunk).Clear(); // Loch: liest sich als Nullen
            }

            done += chunk;
        }

        return done;
    }
}
