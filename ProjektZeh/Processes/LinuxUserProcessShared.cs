using System.Collections.Generic;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shared;

namespace ProjektZeh.Processes;

/// <summary>
/// V17: geteilte Abbildungen einer <see cref="LinuxMemFile"/> (MAP_SHARED). Die Karte (<c>_map</c>) führt Bereich und Zugriff wie bei
/// anonymen Abbildungen; zusätzlich merkt sich der Prozess je geteiltem Bereich Datei und erste Dateiseite (<c>_regions</c>) und je
/// abgebildeter Seite den geteilten Frame (<c>_shared</c>). Die Seite steht auch in <c>_frames</c>, damit Kopieren, mprotect und
/// Seitenfehler unverändert laufen; nur das Freigeben unterscheidet sich (<see cref="DropFrame"/>: Verweis zurückgeben statt Frame freigeben).
/// Alles unter <c>_memGate</c>; die Datei nimmt darin ihre eigene Sperre (Reihenfolge an <see cref="LinuxMemFile"/>).
/// </summary>
internal sealed partial class LinuxUserProcess : ILinuxSharedMemory
{
    private readonly Dictionary<ulong, LinuxSharedFrame> _shared = new();
    private readonly List<SharedRegion> _regions = new();

    /// <summary>
    /// Ein geteilter Bereich [Start, End) ab Dateiseite FirstIndex. Writable heißt „darf schreibbar werden“ (Linux: VM_MAYWRITE) und nicht
    /// „ist gerade schreibbar“: Linux 6.12 zählt jede geteilte Abbildung ohne Schreibsiegel als schreibbar, auch PROT_READ und PROT_NONE
    /// (F_SEAL_WRITE gibt dann -EBUSY), und eine nach dem Siegel entstandene bleibt für immer lesend (mprotect mit PROT_WRITE gibt -EACCES).
    /// So bleibt der Zähler der Datei bei mprotect gültig, ohne neu zu zählen (V17-Review P2, auf dem Host gegen Linux 6.12 geprüft).
    /// </summary>
    private sealed class SharedRegion(LinuxMemFile file, ulong start, ulong end, long firstIndex, bool writable)
    {
        public LinuxMemFile Mem { get; } = file;
        public ulong Start { get; } = start;
        public ulong End { get; } = end;
        public long FirstIndex { get; } = firstIndex;
        public bool Writable { get; } = writable;
    }

    /// <summary>Anzahl geteilter Bereiche (für Tests).</summary>
    public int SharedRegionCount => _vforkOf?.SharedRegionCount ?? _regions.Count;

    private int _memFiles;

    // Inode: Prozessnummer und laufende Nummer, eindeutig unter den lebenden Prozessen eines Laufs; Threads zählen atomar hoch.
    public LinuxMemFile CreateMemFile(string name, bool sealing) => new(_vforkOf?._pages ?? _pages, name, sealing, (long)(_maxFrames * Page),
        0x6d65_6d00_0000_0000UL | ((ulong)(uint)Pid << 24) | (uint)System.Threading.Interlocked.Increment(ref _memFiles));

    // Länge bis zum Ende des Benutzeradressraums: Ohne Deskriptor gibt es kein write und kein ftruncate, jeder Frame kommt über einen
    // Seitenfehler oder Kernelzugriff dieses Prozesses (PopulateShared) und zählt dort gegen _maxFrames.
    public LinuxMemFile CreateAnonymousSharedFile() => new(_vforkOf?._pages ?? _pages, "dev/zero", false, 1L << 47,
        0x6d65_6d00_0000_0000UL | ((ulong)(uint)Pid << 24) | (uint)System.Threading.Interlocked.Increment(ref _memFiles));

    public long MapShared(LinuxMemFile file, ulong len, int prot, long pageOffset)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.MapShared(file, len, prot, pageOffset);
        }

        lock (_memGate)
        {
            if (len == 0 || (len & (Page - 1)) != 0 || pageOffset < 0 || !TryAccess(prot, out MapAccess access))
            {
                return -LinuxSyscalls.EINVAL;
            }

            long r = file.AddMapping((prot & ProtWrite) != 0, out bool mayWrite);
            if (r < 0)
            {
                return r;
            }

            ResultObject<ulong> placed = _map.Reserve(MmapBase, len, access, Placement.Hint);
            if (!placed.IsOK())
            {
                file.RemoveMapping(mayWrite);
                return -LinuxSyscalls.ENOMEM;
            }

            _regions.Add(new SharedRegion(file, placed.Value, placed.Value + len, pageOffset, mayWrite));
            return unchecked((long)placed.Value);
        }
    }

    /// <summary>
    /// mprotect mit Fehlernummer (V17): -EACCES, wenn PROT_WRITE einen geteilten Bereich trifft, der nicht schreibbar werden darf
    /// (abgebildet nach F_SEAL_WRITE), wie Linux 6.12; sonst wie <see cref="Protect"/> (0 oder -ENOMEM).
    /// </summary>
    public long ProtectShared(ulong addr, ulong len, int prot)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.ProtectShared(addr, len, prot);
        }

        lock (_memGate)
        {
            if ((prot & ProtWrite) != 0 && DeniesWrite(addr, addr + len))
            {
                return -LinuxSyscalls.EACCES;
            }

            return ProtectLocked(addr, len, prot) ? 0 : -LinuxSyscalls.ENOMEM;
        }
    }

    /// <summary>Ob [start, end) einen geteilten Bereich schneidet, der nicht schreibbar werden darf; unter <c>_memGate</c>.</summary>
    private bool DeniesWrite(ulong start, ulong end)
    {
        foreach (SharedRegion r in _regions)
        {
            if (!r.Writable && r.Start < end && start < r.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Holt den geteilten Frame, wenn <paramref name="page"/> in einem geteilten Bereich liegt; 0 sonst oder ohne Frame.</summary>
    private ulong PopulateShared(ulong page, MapAccess access, out bool isShared)
    {
        isShared = false;
        foreach (SharedRegion r in _regions)
        {
            if (page < r.Start || page >= r.End)
            {
                continue;
            }

            isShared = true;
            if ((ulong)_frames.Count >= _maxFrames)
            {
                return 0;
            }

            LinuxSharedFrame? f = r.Mem.Acquire(r.FirstIndex + (long)((page - r.Start) / Page));
            if (f == null)
            {
                return 0; // hinter dem Dateiende: Linux gibt SIGBUS, hier endet der Zugriff wie ein Seitenfehler ohne Bereich
            }

            if (!_pages.Map(page, f.Frame, access))
            {
                r.Mem.Release(f);
                return 0;
            }

            _frames[page] = f.Frame;
            _shared[page] = f;
            return f.Frame;
        }

        return 0;
    }

    /// <summary>Gibt den Frame einer Seite frei, deren Abbildung schon aufgehoben ist: geteilt nur den Verweis, sonst den Frame.</summary>
    private void DropFrame(ulong page)
    {
        ulong frame = _frames[page];
        _frames.Remove(page);
        if (_shared.Remove(page, out LinuxSharedFrame? f))
        {
            f.Owner.Release(f);
        }
        else
        {
            _pages.FreeFrame(frame);
        }
    }

    /// <summary>Kürzt geteilte Bereiche um [start, end); Reststücke links und rechts bleiben eigene Bereiche derselben Datei.</summary>
    private void TrimRegions(ulong start, ulong end)
    {
        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            SharedRegion r = _regions[i];
            if (r.End <= start || r.Start >= end)
            {
                continue;
            }

            _regions.RemoveAt(i);
            if (r.Start < start)
            {
                _regions.Add(new SharedRegion(r.Mem, r.Start, start, r.FirstIndex, r.Writable));
                r.Mem.AddSplit(r.Writable);
            }

            if (r.End > end)
            {
                _regions.Add(new SharedRegion(r.Mem, end, r.End, r.FirstIndex + (long)((end - r.Start) / Page), r.Writable));
                r.Mem.AddSplit(r.Writable);
            }

            r.Mem.RemoveMapping(r.Writable);
        }
    }

    /// <summary>Beim Prozessende: alle geteilten Bereiche enden (die Frames gab <see cref="DestroyMemory"/> schon zurück).</summary>
    private void DropAllRegions()
    {
        foreach (SharedRegion r in _regions)
        {
            r.Mem.RemoveMapping(r.Writable);
        }

        _regions.Clear();
    }
}
