using System;
using System.Collections.Generic;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shared;

namespace ProjektZeh.Processes;

/// <summary>
/// Seiten eines Benutzeradressraums, wie sie <see cref="LinuxUserProcess"/> braucht. Im Kernel steckt dahinter eine
/// <c>UserPageTable</c> (<c>CosmosUserPages</c>), in den Hosttests ein Wörterbuch. Ein Frame gehört dem Prozess, bis er ihn
/// zurückgibt; eine Seite mit <see cref="MapAccess.None"/> wird nie abgebildet.
/// </summary>
internal interface IUserPages
{
    /// <summary>Ein genullter Frame oder 0 ohne Speicher.</summary>
    ulong AllocateFrame();

    /// <summary>Gibt einen Frame zurück, den keine Seite mehr abbildet.</summary>
    void FreeFrame(ulong frame);

    /// <summary>Die 4096 Bytes des Frames, für den Kernel lesbar und schreibbar.</summary>
    Span<byte> Frame(ulong frame);

    /// <summary>Bildet die Seite mit dem Zugriff ab (nicht <see cref="MapAccess.None"/>); false, wenn schon abgebildet oder abgelehnt.</summary>
    bool Map(ulong page, ulong frame, MapAccess access);

    /// <summary>Hebt die Abbildung auf; der Frame bleibt beim Prozess. true, wenn die Seite danach nicht (mehr) abgebildet ist.</summary>
    bool Unmap(ulong page);

    /// <summary>Gibt die Tabelle selbst frei, nicht die Frames; danach bildet nichts mehr ab.</summary>
    void DestroyTable();
}

/// <summary>
/// Ein Linux-Prozess mit einem Thread (V3 des Plans <c>2026-11-26-ladybird-vorstellung.md</c>): Speicher, brk, anonyme
/// Abbildungen und Deskriptoren 0 bis 2 auf das Terminal. Die Bereiche führt <see cref="AddressSpaceMap"/>; Frames kommen erst bei
/// Bedarf (<see cref="HandlePageFault"/> oder ein Kernelzugriff über <see cref="IUserMemory"/>), nur die Seiten mit Dateiinhalt
/// und der vorbereitete Teil des Stacks werden beim Laden gefüllt. Plattformunabhängig und auf dem Host geprüft.
/// <para>
/// Nicht threadsicher: Laden, Aufrufe und Fehler laufen alle auf dem einen Thread des Prozesses (die Handler von <c>UserMode</c>
/// laufen auf seinem Kernelstack), das Aufräumen danach auf demselben Thread.
/// </para>
/// </summary>
internal sealed class LinuxUserProcess : ILinuxProcess, IUserMemory, ILinuxHost
{
    private const ulong Page = AddressSpaceMap.PageSize;
    private const int ProtRead = 1, ProtWrite = 2, ProtExec = 4;
    private const ulong FaultPresent = 1, FaultWrite = 2, FaultFetch = 0x10;

    /// <summary>Wunschlage anonymer Abbildungen (16 TiB): weit über brk eines Programms bei 4 MiB und unter statisch-PIE.</summary>
    public const ulong MmapBase = 0x0000_1000_0000_0000;

    private readonly IUserPages _pages;
    private readonly AddressSpaceMap _map = new(ElfLoader.MinAddress, ElfLoader.UserEnd);
    private readonly Dictionary<ulong, ulong> _frames = new();
    private readonly ILinuxDescriptor _terminal;
    private readonly ILinuxHost? _host;
    private readonly ulong _maxFrames;
    private ulong _brkStart;
    private ulong _brk;
    private ulong _fsBase;

    /// <param name="maxBytes">Obergrenze für den Speicher des Prozesses; darüber endet ein Seitenfehler mit Abbruch.</param>
    /// <param name="host">Zeit, Zufall und Schlafen des Kernels (V2b); ohne Host liefern Uhren 0 und getrandom scheitert.</param>
    public LinuxUserProcess(IUserPages pages, ILinuxDescriptor terminal, int pid, ulong maxBytes, ILinuxHost? host = null)
    {
        _host = host;
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        Pid = pid;
        _maxFrames = maxBytes / Page;
    }

    /// <summary>Wird beim Exit gerufen; im Kernel verlässt das den Ring 3 (<c>UserMode.LeaveCurrent</c>).</summary>
    public Action<int>? OnExit { get; set; }

    /// <summary>Wird beim Setzen der FS-Basis gerufen; im Kernel <c>UserMode.SetFsBase</c>.</summary>
    public Action<ulong>? OnFsBase { get; set; }

    /// <summary>Exitcode, sobald der Prozess exit oder exit_group gerufen hat.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Anzahl der Frames, die der Prozess gerade hält.</summary>
    public int FrameCount => _frames.Count;

    public IUserMemory Memory => this;

    public int Pid { get; }

    public ulong Brk => _brk;

    public ulong ClearChildTid { get; set; }

    public ulong FsBase
    {
        get => _fsBase;
        set
        {
            _fsBase = value;
            OnFsBase?.Invoke(value);
        }
    }

    public LinuxSignalState Signals { get; } = new();

    // ILinuxHost: der Verteiler fragt den Prozess (p as ILinuxHost); weitergereicht an den Host des Kernels.
    public long RealtimeNanos => _host?.RealtimeNanos ?? 0;

    public long MonotonicNanos => _host?.MonotonicNanos ?? 0;

    public bool TryFillRandom(Span<byte> dst) => _host != null && _host.TryFillRandom(dst);

    public void SleepNanos(long ns) => _host?.SleepNanos(ns);

    public ILinuxDescriptor? GetDescriptor(int fd) => fd is >= 0 and <= 2 ? _terminal : null;

    public void Exit(int code)
    {
        ExitCode ??= code;
        OnExit?.Invoke(code);
    }

    /// <summary>
    /// Belegt die Bereiche des Abbilds und füllt die Seiten mit Dateiinhalt und den vorbereiteten Stack; alle übrigen Seiten
    /// (.bss jenseits der Dateibytes, der Rest des Stacks) kommen genullt beim ersten Zugriff.
    /// </summary>
    public ResultErr Load(UserImage image, ReadOnlySpan<byte> elf)
    {
        ArgumentNullException.ThrowIfNull(image);
        ResultErr reserved = image.Reserve(_map);
        if (!reserved.IsOK())
        {
            return reserved;
        }

        ulong stackContent = image.StackPointer & ~(Page - 1);
        foreach (UserRegion region in image.Regions)
        {
            if (region.Access == MapAccess.None)
            {
                continue;
            }

            for (ulong page = region.Start; page < region.End; page += Page)
            {
                bool content = page >= stackContent && page < image.StackTop;
                foreach (UserSegment segment in image.Segments)
                {
                    content |= segment.FileSize > 0 && page < segment.Address + segment.FileSize && page + Page > segment.Address;
                }

                if (!content)
                {
                    continue;
                }

                ulong frame = NewFrame();
                if (frame == 0)
                {
                    return ResultErr.CreateFailure(ResultCode.Failure, "Kein Speicher für das Programmabbild.");
                }

                image.FillPage(elf, page, _pages.Frame(frame));
                if (!_pages.Map(page, frame, region.Access))
                {
                    _pages.FreeFrame(frame);
                    return ResultErr.CreateFailure(ResultCode.Failure, "Seite 0x" + page.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + " nicht abbildbar.");
                }

                _frames[page] = frame;
            }
        }

        _brkStart = image.BreakStart;
        _brk = image.BreakStart;
        return ResultErr.CreateSuccess();
    }

    /// <summary>
    /// Ein Seitenfehler im Ring 3. true: die Seite ist jetzt abgebildet und der Befehl läuft noch einmal. false: Schutzverletzung,
    /// unbelegte Adresse oder kein Speicher mehr; der Prozess wird abgebrochen.
    /// </summary>
    public bool HandlePageFault(ulong address, ulong error)
    {
        if ((error & FaultPresent) != 0 || address >= ElfLoader.UserEnd)
        {
            return false;
        }

        ulong page = address & ~(Page - 1);
        if (_frames.ContainsKey(page) || !_map.TryFind(page, out MapAccess access) || access == MapAccess.None)
        {
            return false;
        }

        if (((error & FaultWrite) != 0 && access != MapAccess.ReadWrite) || ((error & FaultFetch) != 0 && access != MapAccess.ReadExecute))
        {
            return false;
        }

        return Populate(page, access) != 0;
    }

    public bool TrySetBrk(ulong newBrk)
    {
        if (newBrk < _brkStart || newBrk > ElfLoader.UserEnd)
        {
            return false;
        }

        ulong oldEnd = PageUp(_brk);
        ulong newEnd = PageUp(newBrk);
        if (newEnd > oldEnd)
        {
            if (!_map.Reserve(oldEnd, newEnd - oldEnd, MapAccess.ReadWrite, Placement.FixedNoReplace).IsOK())
            {
                return false;
            }
        }
        else if (newEnd < oldEnd)
        {
            Release(newEnd, oldEnd);
        }

        _brk = newBrk;
        return true;
    }

    public ulong MapAnonymous(ulong len, int prot)
    {
        if (len == 0 || (len & (Page - 1)) != 0 || !TryAccess(prot, out MapAccess access))
        {
            return 0;
        }

        ResultObject<ulong> placed = _map.Reserve(MmapBase, len, access, Placement.Hint);
        return placed.IsOK() ? placed.Value : 0;
    }

    public bool MapFixed(ulong addr, ulong len, int prot)
    {
        if (len == 0 || (len & (Page - 1)) != 0 || (addr & (Page - 1)) != 0 || addr < ElfLoader.MinAddress
            || !InUserRange(addr, len) || !TryAccess(prot, out MapAccess access))
        {
            return false;
        }

        // Erst die Frames des Altbestands freigeben (sonst blieben sie mit altem Inhalt an den Seiten), dann neu belegen.
        Release(addr, addr + len);
        return _map.Reserve(addr, len, access, Placement.FixedNoReplace).IsOK();
    }

    public bool Discard(ulong addr, ulong len)
    {
        if (!InUserRange(addr, len))
        {
            return false;
        }

        bool ok = true;
        foreach (ulong page in PagesWithFrames(addr, addr + len))
        {
            // Frame weg, Bereich bleibt: der nächste Zugriff füllt die Seite über den Seitenfehler mit Nullen.
            if (_pages.Unmap(page))
            {
                _pages.FreeFrame(_frames[page]);
                _frames.Remove(page);
            }
            else
            {
                ok = false;
            }
        }

        return ok;
    }

    public bool Unmap(ulong addr, ulong len)
    {
        if (!InUserRange(addr, len))
        {
            return false;
        }

        Release(addr, addr + len);
        return true;
    }

    public bool Protect(ulong addr, ulong len, int prot)
    {
        if (!InUserRange(addr, len) || !TryAccess(prot, out MapAccess access) || !_map.Protect(addr, len, access).IsOK())
        {
            return false;
        }

        bool ok = true;
        foreach (ulong page in PagesWithFrames(addr, addr + len))
        {
            // Neu abbilden statt umschreiben: IUserPages kennt nur Map und Unmap, der Frame und sein Inhalt bleiben.
            if (!_pages.Unmap(page))
            {
                ok = false;
                continue;
            }

            if (access != MapAccess.None)
            {
                ok &= _pages.Map(page, _frames[page], access);
            }
        }

        return ok;
    }

    public bool TryRead(ulong addr, Span<byte> dst) => Copy(addr, dst, default, write: false);

    public bool TryWrite(ulong addr, ReadOnlySpan<byte> src) => Copy(addr, default, src, write: true);

    public bool TryReadCString(ulong addr, int max, out string s)
    {
        s = string.Empty;
        if (max < 0)
        {
            return false;
        }

        var bytes = new List<byte>(Math.Min(max, 256));
        Span<byte> one = stackalloc byte[1];
        for (ulong i = 0; i <= (ulong)max; i++)
        {
            if (addr > ulong.MaxValue - i || !TryRead(addr + i, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                s = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                return true;
            }

            if (i == (ulong)max)
            {
                return false;
            }

            bytes.Add(one[0]);
        }

        return false;
    }

    /// <summary>Hebt alle Abbildungen auf, gibt die Tabelle und danach alle Frames frei. Danach ist der Prozess unbrauchbar.</summary>
    public void Destroy()
    {
        // Erst die Tabelle: Solange sie besteht, könnte eine CPU über sie noch einen Frame erreichen.
        _pages.DestroyTable();
        foreach (ulong frame in _frames.Values)
        {
            _pages.FreeFrame(frame);
        }

        _frames.Clear();
    }

    private bool Copy(ulong addr, Span<byte> dst, ReadOnlySpan<byte> src, bool write)
    {
        ulong length = (ulong)(write ? src.Length : dst.Length);
        if (length == 0)
        {
            return true;
        }

        if (!InUserRange(addr, length))
        {
            return false;
        }

        // Erst alle Seiten prüfen, dann kopieren: ein Fehler mitten im Bereich lässt nichts halb geschrieben.
        ulong end = addr + length;
        for (ulong page = addr & ~(Page - 1); page < end; page += Page)
        {
            if (!_map.TryFind(page, out MapAccess access) || access == MapAccess.None || (write && access != MapAccess.ReadWrite))
            {
                return false;
            }
        }

        ulong done = 0;
        while (done < length)
        {
            ulong at = addr + done;
            ulong page = at & ~(Page - 1);
            int offset = (int)(at - page);
            int chunk = (int)Math.Min(Page - (ulong)offset, length - done);
            if (!_frames.TryGetValue(page, out ulong frame))
            {
                _map.TryFind(page, out MapAccess access);
                frame = Populate(page, access);
                if (frame == 0)
                {
                    return false;
                }
            }

            Span<byte> memory = _pages.Frame(frame).Slice(offset, chunk);
            if (write)
            {
                src.Slice((int)done, chunk).CopyTo(memory);
            }
            else
            {
                memory.CopyTo(dst.Slice((int)done, chunk));
            }

            done += (ulong)chunk;
        }

        return true;
    }

    private ulong Populate(ulong page, MapAccess access)
    {
        ulong frame = NewFrame();
        if (frame == 0)
        {
            return 0;
        }

        if (!_pages.Map(page, frame, access))
        {
            _pages.FreeFrame(frame);
            return 0;
        }

        _frames[page] = frame;
        return frame;
    }

    private ulong NewFrame() => (ulong)_frames.Count >= _maxFrames ? 0 : _pages.AllocateFrame();

    private void Release(ulong start, ulong end)
    {
        foreach (ulong page in PagesWithFrames(start, end))
        {
            // Bleibt die Seite abgebildet (die Tabelle lehnt ab), darf der Frame nicht frei werden: er bleibt beim Prozess,
            // bis Destroy die Tabelle aufgelöst hat. Ein Zugriff dort bricht das Programm ab (Seite ohne Bereich).
            if (_pages.Unmap(page))
            {
                _pages.FreeFrame(_frames[page]);
                _frames.Remove(page);
            }
        }

        _map.Release(start, end - start);
    }

    /// <summary>Seiten mit Frame in [start, end): über die Frames, nicht über den Bereich, der Terabytes groß sein kann.</summary>
    private List<ulong> PagesWithFrames(ulong start, ulong end)
    {
        var pages = new List<ulong>();
        foreach (ulong page in _frames.Keys)
        {
            if (page >= start && page < end)
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    private static bool InUserRange(ulong addr, ulong length) => UserRange.Contains(0, ElfLoader.UserEnd, addr, length);

    private static ulong PageUp(ulong value) => (value + Page - 1) & ~(Page - 1);

    /// <summary>PROT_* auf den Zugriff. Schreiben und Ausführen zugleich lehnt ZehOS ab (W^X, wie der Lader).</summary>
    private static bool TryAccess(int prot, out MapAccess access)
    {
        bool write = (prot & ProtWrite) != 0, exec = (prot & ProtExec) != 0;
        access = exec ? MapAccess.ReadExecute : write ? MapAccess.ReadWrite : (prot & ProtRead) != 0 ? MapAccess.Read : MapAccess.None;
        return !(write && exec);
    }
}
