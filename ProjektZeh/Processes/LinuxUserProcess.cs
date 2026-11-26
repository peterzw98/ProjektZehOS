using System;
using System.Text;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Processes;

/// <summary>
/// Seitenebene unter einem Linux-Benutzerprozess (V3). Der Kernel setzt sie auf <c>UserPageTable</c> und Frames; Tests auf Arrays.
/// Adressen sind seitenausgerichtete Benutzeradressen. Frames werden beim ersten Anlegen mit Nullen gefüllt.
/// </summary>
internal interface IUserPages
{
    /// <summary>Legt die Seite an (mit Nullen), falls sie fehlt, und setzt den Zugriff; false ohne Speicher.</summary>
    bool Map(ulong page, MapAccess access);

    /// <summary>Ist die Seite schon angelegt?</summary>
    bool IsMapped(ulong page);

    /// <summary>Ändert den Zugriff einer angelegten Seite (fehlende Seiten bleiben fehlend und kommen beim Zugriff mit dem Zugriff der Karte).</summary>
    void Protect(ulong page, MapAccess access);

    /// <summary>Gibt die Seite samt Frame frei (fehlende Seiten sind erlaubt).</summary>
    void Unmap(ulong page);

    /// <summary>Kopiert aus einer angelegten Seite (ohne Rücksicht auf den Zugriff des Programms); [offset, offset+Länge) liegt in der Seite.</summary>
    void Read(ulong page, int offset, Span<byte> dst);

    /// <summary>Schreibt in eine angelegte Seite (ohne Rücksicht auf den Zugriff des Programms).</summary>
    void Write(ulong page, int offset, ReadOnlySpan<byte> src);
}

/// <summary>
/// <see cref="IUserMemory"/> über Karte und Seiten: prüft jede Seite gegen die Karte (Lesen: nicht None; Schreiben: nur ReadWrite),
/// legt fehlende Seiten wie ein Seitenfehler an und kopiert seitenweise. Ein Fehler mittendrin heißt false (nichts oder alles gilt
/// nur für die Prüfung; schon kopierte Bytes bleiben, wie bei Linux).
/// </summary>
internal sealed class UserPagesMemory : IUserMemory
{
    private const ulong PageSize = AddressSpaceMap.PageSize;
    private readonly AddressSpaceMap _map;
    private readonly IUserPages _pages;

    public UserPagesMemory(AddressSpaceMap map, IUserPages pages)
    {
        _map = map;
        _pages = pages;
    }

    public bool TryRead(ulong addr, Span<byte> dst) => Copy(addr, dst, default, write: false);

    public bool TryWrite(ulong addr, ReadOnlySpan<byte> src) => Copy(addr, default, src, write: true);

    public bool TryReadCString(ulong addr, int max, out string s)
    {
        s = string.Empty;
        byte[] buffer = new byte[Math.Min(max, 4096) + 1];
        int length = 0;
        while (true)
        {
            if (length > max || length >= buffer.Length)
            {
                return false;
            }

            Span<byte> one = buffer.AsSpan(length, 1);
            if (!TryRead(addr + (ulong)length, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                break;
            }

            length++;
        }

        s = Encoding.UTF8.GetString(buffer, 0, length);
        return true;
    }

    private bool Copy(ulong addr, Span<byte> dst, ReadOnlySpan<byte> src, bool write)
    {
        int length = write ? src.Length : dst.Length;
        if (length == 0)
        {
            return true;
        }

        if (!UserRange.Contains(0, ElfLoader.UserEnd, addr, (ulong)length))
        {
            return false;
        }

        int done = 0;
        while (done < length)
        {
            ulong current = addr + (ulong)done;
            ulong page = current & ~(PageSize - 1);
            int offset = (int)(current - page);
            int n = Math.Min(length - done, (int)PageSize - offset);
            if (!_map.TryFind(page, out MapAccess access) || access == MapAccess.None || (write && access != MapAccess.ReadWrite))
            {
                return false;
            }

            if (!_pages.IsMapped(page) && !_pages.Map(page, access))
            {
                return false;
            }

            if (write)
            {
                _pages.Write(page, offset, src.Slice(done, n));
            }
            else
            {
                _pages.Read(page, offset, dst.Slice(done, n));
            }

            done += n;
        }

        return true;
    }
}

/// <summary>Deskriptor 0–2: schreibt Bytes als Zeilen ins Terminal (UTF-8, Zeilenumbruch beendet eine Zeile; Rest beim Ende).</summary>
internal sealed class TerminalDescriptor : ILinuxDescriptor
{
    private readonly ITextOutput _terminal;
    private readonly StringBuilder _line = new();
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();

    public TerminalDescriptor(ITextOutput terminal) => _terminal = terminal;

    public long Write(ReadOnlySpan<byte> data)
    {
        char[] chars = new char[data.Length + 1];
        int count = _decoder.GetChars(data, chars, flush: false);
        for (int i = 0; i < count; i++)
        {
            char c = chars[i];
            if (c == '\n')
            {
                Flush();
            }
            else if (c != '\r')
            {
                _line.Append(c);
            }
        }

        return data.Length;
    }

    /// <summary>Gibt eine angefangene Zeile aus (am Prozessende).</summary>
    public void Flush()
    {
        _terminal.WriteLine(_line.ToString());
        _line.Clear();
    }

    public bool HasPartialLine => _line.Length > 0;
}

/// <summary>
/// Ein Linux-Benutzerprozess (V3): Karte des Adressraums, Seiten, Deskriptoren 0–2 auf dem Terminal, brk, mmap, Ende.
/// Plattformunabhängig; die Brücke im Kernel (<c>CosmosLinuxRun</c>) liefert <see cref="IUserPages"/> und ruft
/// <see cref="Syscall"/> aus dem Systemaufruf-Handler. Nicht threadsicher (ein Thread je Prozess).
/// </summary>
internal sealed class LinuxUserProcess : ILinuxProcess
{
    private const ulong PageSize = AddressSpaceMap.PageSize;

    /// <summary>Hinweisadresse für mmap: weit über Programm und brk, unter dem Stack.</summary>
    public const ulong MmapBase = 0x0000_7000_0000_0000;

    /// <summary>Obergrenze für brk über dem Programmende.</summary>
    public const ulong MaxBrkSize = 1UL << 30;

    private const int ProtRead = 1, ProtWrite = 2, ProtExec = 4;

    private readonly AddressSpaceMap _map = new(ElfLoader.MinAddress, ElfLoader.UserEnd);
    private readonly IUserPages _pages;
    private readonly TerminalDescriptor _terminal;
    private readonly UserImage _image;
    private readonly Action<int> _onExit;
    private ulong _brk;
    private ulong _brkMapped;

    public LinuxUserProcess(UserImage image, IUserPages pages, ITextOutput terminal, int pid, Action<int> onExit)
    {
        _image = image;
        _pages = pages;
        _terminal = new TerminalDescriptor(terminal);
        Pid = pid;
        _onExit = onExit;
        Memory = new UserPagesMemory(_map, pages);
        _brk = image.BreakStart;
        _brkMapped = image.BreakStart;
    }

    public AddressSpaceMap Map => _map;

    public IUserMemory Memory { get; }

    public int Pid { get; }

    public ulong FsBase { get; set; }

    public ulong ClearChildTid { get; set; }

    public ulong Brk => _brk;

    public bool Exited { get; private set; }

    public int ExitCode { get; private set; }

    /// <summary>Belegt die Bereiche des Abbilds, legt alle Seiten mit Zugriff an und füllt sie (Segmente, Stack).</summary>
    public ResultErr Load(ReadOnlySpan<byte> elf)
    {
        ResultErr reserved = _image.Reserve(_map);
        if (!reserved.IsOK())
        {
            return reserved;
        }

        byte[] page = new byte[PageSize];
        foreach (UserRegion region in _image.Regions)
        {
            if (region.Access == MapAccess.None)
            {
                continue;
            }

            for (ulong address = region.Start; address < region.End; address += PageSize)
            {
                if (!_pages.Map(address, region.Access))
                {
                    return ResultErr.CreateFailure(ResultCode.Failure, "Kein Speicher für Seite 0x" + address.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                }

                _image.FillPage(elf, address, page);
                _pages.Write(address, 0, page);
            }
        }

        return ResultErr.CreateSuccess();
    }

    /// <summary>Führt einen Systemaufruf aus (Nummer und Argumente aus den Registern); Rückgabe ist der Wert für rax.</summary>
    public long Syscall(long number, ulong a0, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5)
    {
        var args = new SyscallArgs { Number = number, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
        return LinuxSyscalls.Dispatch(ref args, this);
    }

    /// <summary>Seitenfehler: legt eine fehlende Seite eines belegten Bereichs an. false: echter Fehler (nichts belegt, zu wenig Zugriff).</summary>
    public bool HandlePageFault(ulong address, bool write, bool instructionFetch)
    {
        ulong page = address & ~(PageSize - 1);
        if (!_map.TryFind(page, out MapAccess access) || access == MapAccess.None || _pages.IsMapped(page))
        {
            return false;
        }

        if ((write && access != MapAccess.ReadWrite) || (instructionFetch && access != MapAccess.ReadExecute))
        {
            return false;
        }

        return _pages.Map(page, access);
    }

    public ILinuxDescriptor? GetDescriptor(int fd) => fd is >= 0 and <= 2 ? _terminal : null;

    public bool TrySetBrk(ulong newBrk)
    {
        if (newBrk < _image.BreakStart || newBrk - _image.BreakStart > MaxBrkSize)
        {
            return false;
        }

        ulong wanted = AlignUp(newBrk);
        if (wanted > _brkMapped)
        {
            if (!_map.Reserve(_brkMapped, wanted - _brkMapped, MapAccess.ReadWrite, Placement.FixedNoReplace).IsOK())
            {
                return false;
            }
        }
        else if (wanted < _brkMapped)
        {
            ReleasePages(wanted, _brkMapped - wanted);
        }

        _brkMapped = wanted;
        _brk = newBrk;
        return true;
    }

    public ulong MapAnonymous(ulong len, int prot)
    {
        if (!TryAccess(prot, out MapAccess access) || len == 0 || (len & (PageSize - 1)) != 0)
        {
            return 0;
        }

        ResultObject<ulong> r = _map.Reserve(MmapBase, len, access, Placement.Hint);
        return r.IsOK() ? r.Value : 0;
    }

    public bool Unmap(ulong addr, ulong len)
    {
        if (!_map.Release(addr, len).IsOK())
        {
            return false;
        }

        ReleasePages(addr, len);
        return true;
    }

    public bool Protect(ulong addr, ulong len, int prot)
    {
        if (!TryAccess(prot, out MapAccess access) || !_map.Protect(addr, len, access).IsOK())
        {
            return false;
        }

        for (ulong page = addr; page < addr + len; page += PageSize)
        {
            if (_pages.IsMapped(page))
            {
                _pages.Protect(page, access);
            }
        }

        return true;
    }

    public void Exit(int code)
    {
        if (Exited)
        {
            return;
        }

        Exited = true;
        ExitCode = code;
        if (_terminal.HasPartialLine)
        {
            _terminal.Flush();
        }

        _onExit(code);
    }

    private void ReleasePages(ulong address, ulong length)
    {
        for (ulong page = address; page < address + length; page += PageSize)
        {
            if (_pages.IsMapped(page))
            {
                _pages.Unmap(page);
            }
        }
    }

    private static ulong AlignUp(ulong value) => (value + PageSize - 1) & ~(PageSize - 1);

    // W^X: Schreiben und Ausführen zugleich gibt es nicht (der Seitentabellen-Patch kennt nur writable/executable je Seite).
    private static bool TryAccess(int prot, out MapAccess access)
    {
        access = MapAccess.None;
        if ((prot & ~(ProtRead | ProtWrite | ProtExec)) != 0 || ((prot & ProtWrite) != 0 && (prot & ProtExec) != 0))
        {
            return false;
        }

        access = (prot & ProtWrite) != 0 ? MapAccess.ReadWrite
            : (prot & ProtExec) != 0 ? MapAccess.ReadExecute
            : (prot & ProtRead) != 0 ? MapAccess.Read : MapAccess.None;
        return true;
    }
}
