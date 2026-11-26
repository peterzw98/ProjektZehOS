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
        if (!_map.TryFind(page, out MapAccess access) || access == MapAccess.None)
        {
            return false;
        }

        if (((error & FaultWrite) != 0 && access != MapAccess.ReadWrite) || ((error & FaultFetch) != 0 && access != MapAccess.ReadExecute))
        {
            return false;
        }

        // Frame da, Seite nicht abgebildet: Protect konnte sie nach dem Aufheben nicht neu abbilden (V3-Review P3 b).
        // Jetzt mit dem Zugriff aus der Karte nachholen; Inhalt und Frame bleiben.
        if (_frames.TryGetValue(page, out ulong held))
        {
            return _pages.Map(page, held, access);
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

    public bool Unmap(ulong addr, ulong len)
    {
        // Erst prüfen, dann Frames freigeben: scheiterte _map.Release erst danach, blieben Bereiche ohne Frames stehen und
        // füllten sich beim nächsten Zugriff genullt (V3-Review P3 d).
        if (len == 0 || ((addr | len) & (Page - 1)) != 0 || !InUserRange(addr, len))
        {
            return false;
        }

        // Unter MinAddress liegt nie ein Bereich; dort ist munmap wie bei Linux erfolgreich und ohne Wirkung.
        ulong start = Math.Max(addr, ElfLoader.MinAddress);
        return start >= addr + len || Release(start, addr + len);
    }

    public bool Protect(ulong addr, ulong len, int prot)
    {
        if (!InUserRange(addr, len) || !TryAccess(prot, out MapAccess access))
        {
            return false;
        }

        // Der alte Zugriff je Seite mit Frame, damit eine Seite, die die Tabelle nicht freigibt, in der Karte so bleibt, wie die
        // Tabelle sie abbildet (V3-Review P3 b). Sonst erlaubte die Karte dem Kernel, was die Tabelle dem Programm verwehrt, oder umgekehrt.
        List<ulong> pages = PagesWithFrames(addr, addr + len);
        var before = new MapAccess[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            _map.TryFind(pages[i], out before[i]);
        }

        if (!_map.Protect(addr, len, access).IsOK())
        {
            return false;
        }

        bool ok = true;
        for (int i = 0; i < pages.Count; i++)
        {
            ulong page = pages[i];

            // Neu abbilden statt umschreiben: IUserPages kennt nur Map und Unmap, der Frame und sein Inhalt bleiben.
            if (!_pages.Unmap(page))
            {
                _map.Protect(page, Page, before[i]);
                ok = false;
                continue;
            }

            // Scheitert das neue Abbilden, bleibt der Frame beim Prozess und die Seite unabgebildet: Karte und Tabelle widersprechen
            // sich nicht, der nächste Zugriff des Programms bildet über HandlePageFault nach, der Kernel liest über den Frame.
            if (access != MapAccess.None && !_pages.Map(page, _frames[page], access))
            {
                ok = false;
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

    /// <summary>Gibt [start, end) frei; start und end ausgerichtet und im Adressraum der Karte.</summary>
    private bool Release(ulong start, ulong end)
    {
        List<ulong> stuck = new();
        foreach (ulong page in PagesWithFrames(start, end))
        {
            // Bleibt die Seite abgebildet (die Tabelle lehnt ab), darf der Frame nicht frei werden: er bleibt beim Prozess,
            // bis Destroy die Tabelle aufgelöst hat.
            if (_pages.Unmap(page))
            {
                _pages.FreeFrame(_frames[page]);
                _frames.Remove(page);
            }
            else
            {
                stuck.Add(page);
            }
        }

        if (!_map.Release(start, end - start).IsOK())
        {
            return false;
        }

        // Eine solche Seite bleibt als gesperrter Bereich (None) belegt (V3-Review P3 a): Sonst legte ein späteres mmap oder brk
        // dort einen neuen Bereich an, und Programm wie Kernel sähen den alten, nicht genullten Frame. Der Kernel greift auf None
        // nie zu, ein Seitenfehler dort bricht ab; ein späteres munmap versucht das Aufheben erneut.
        foreach (ulong page in stuck)
        {
            _map.Reserve(page, Page, MapAccess.None, Placement.FixedNoReplace);
        }

        return true;
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
