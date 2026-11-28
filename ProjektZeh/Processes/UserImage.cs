using System;
using System.Collections.Generic;
using System.Globalization;
using ProjektZeh.Shared;

namespace ProjektZeh.Processes;

/// <summary>Ein belegter Bereich [Start, End) des Benutzeradressraums, seitenausgerichtet.</summary>
internal readonly record struct UserRegion(ulong Start, ulong End, MapAccess Access);

/// <summary>Ein ladbares Segment nach Verschiebung: Dateibytes [FileOffset, FileOffset + FileSize) liegen ab Address. Mit
/// <paramref name="Interpreter"/> stammen sie aus dem Interpreter (V25), sonst aus dem Programm.</summary>
internal readonly record struct UserSegment(ulong Address, ulong MemorySize, ulong FileOffset, ulong FileSize, MapAccess Access, bool Interpreter = false);

/// <summary>
/// Ergebnis von <see cref="ElfLoader.Load(ReadOnlySpan{byte}, string[], string[], ElfLoadOptions)"/>: wo das Programm liegt, wo es startet und was
/// in seinen Seiten steht. Das Abbild bildet selbst nichts ab, damit es auf dem Host prüfbar ist. Wer den Prozess anlegt (V3),
/// belegt <see cref="Regions"/> (etwa mit <see cref="Reserve"/>), holt für jede Seite eines Bereichs mit Zugriff einen Frame,
/// füllt ihn mit <see cref="FillPage"/> und bildet ihn mit dem Zugriff des Bereichs ab. Seiten mit <see cref="MapAccess.None"/>
/// (Schutzseite unter dem Stack) bleiben unabgebildet.
/// </summary>
internal sealed class UserImage
{
    private readonly UserSegment[] _segments;
    private readonly byte[] _stackContent;
    private readonly int _elfLength;

    internal UserImage(
        ulong entry,
        ulong stackPointer,
        ulong loadBias,
        ulong programHeaderAddress,
        ulong breakStart,
        ulong stackBottom,
        ulong stackTop,
        UserSegment[] segments,
        UserRegion[] regions,
        byte[] stackContent,
        int elfLength,
        ulong uid = 1000,
        ulong gid = 1000)
    {
        Entry = entry;
        StackPointer = stackPointer;
        LoadBias = loadBias;
        ProgramHeaderAddress = programHeaderAddress;
        BreakStart = breakStart;
        StackBottom = stackBottom;
        StackTop = stackTop;
        _segments = segments;
        Regions = regions;
        _stackContent = stackContent;
        _elfLength = elfLength;
        Uid = uid;
        Gid = gid;
    }

    /// <summary>V25: Inhalt des Interpreters (PT_INTERP) oder null bei statischen Programmen. Das Abbild hält ihn, damit
    /// <see cref="FillPage"/> wie bisher nur die Programmdatei übergeben bekommt.</summary>
    public byte[]? Interpreter { get; init; }

    /// <summary>N2-Z: <see cref="Interpreter"/> ist nur der Kopfteil; die Segmente des Interpreters kommen beim Zugriff aus der Datei (<see cref="FillPage"/> lässt sie aus).</summary>
    public bool InterpreterLazy { get; init; }

    /// <summary>V25: Verschiebung des Interpreters (AT_BASE), 0 ohne Interpreter.</summary>
    public ulong InterpreterBias { get; init; }

    /// <summary>Benutzer- und Gruppennummer wie im Hilfsvektor (AT_UID/AT_GID); getuid und getgid liefern dieselben Werte.</summary>
    public ulong Uid { get; }

    public ulong Gid { get; }

    /// <summary>Einsprung (RIP) im Ring 3, Verschiebung schon eingerechnet.</summary>
    public ulong Entry { get; }

    /// <summary>Anfangswert von RSP: zeigt auf argc, 16-Byte-ausgerichtet wie im System-V-ABI verlangt.</summary>
    public ulong StackPointer { get; }

    /// <summary>Verschiebung gegenüber den Adressen der Datei; 0 bei ET_EXEC.</summary>
    public ulong LoadBias { get; }

    /// <summary>Adresse der Programmköpfe im Speicher (AT_PHDR); musl findet darüber PT_TLS.</summary>
    public ulong ProgramHeaderAddress { get; }

    /// <summary>Erste Seite hinter dem höchsten Segment: Anfang von brk.</summary>
    public ulong BreakStart { get; }

    /// <summary>Unterste abgebildete Stackseite; die Seite darunter ist die Schutzseite.</summary>
    public ulong StackBottom { get; }

    /// <summary>Erste Adresse über dem Stack (exklusiv).</summary>
    public ulong StackTop { get; }

    /// <summary>Die geladenen Segmente nach Verschiebung, in der Reihenfolge der Programmköpfe.</summary>
    public IReadOnlyList<UserSegment> Segments => _segments;

    /// <summary>Belegte Bereiche, sortiert und überlappungsfrei: Segmente (seitenweise vereinigt), Schutzseite, Stack.</summary>
    public IReadOnlyList<UserRegion> Regions { get; }

    /// <summary>
    /// Schreibt den Anfangsinhalt der Seite ab <paramref name="pageAddress"/> nach <paramref name="page"/>: Dateibytes der Segmente,
    /// Nullen für .bss und Lücken, den vorbereiteten Teil des Stacks. <paramref name="elf"/> muss dieselbe Datei wie beim Laden sein.
    /// V30: Mit <paramref name="skipProgram"/> bleiben die Dateibytes des Programms aus (sie kommen beim Zugriff aus der Datei);
    /// <paramref name="elf"/> ist dann nur der Kopfteil, den der Lader las.
    /// </summary>
    public void FillPage(ReadOnlySpan<byte> elf, ulong pageAddress, Span<byte> page, bool skipProgram = false)
    {
        if (elf.Length != _elfLength)
        {
            throw new ArgumentException("Die Datei ist nicht die geladene.", nameof(elf));
        }

        if (page.Length != (int)AddressSpaceMap.PageSize || (pageAddress & (AddressSpaceMap.PageSize - 1)) != 0)
        {
            throw new ArgumentException("Eine ganze, ausgerichtete Seite wird erwartet.", nameof(page));
        }

        page.Clear();
        ulong pageEnd = pageAddress + AddressSpaceMap.PageSize;
        foreach (UserSegment segment in _segments)
        {
            if ((skipProgram && !segment.Interpreter) || (InterpreterLazy && segment.Interpreter))
            {
                continue;
            }

            // Nur der Dateiteil wird kopiert; der Rest bis MemorySize bleibt null (.bss).
            ulong from = Math.Max(segment.Address, pageAddress);
            ulong to = Math.Min(segment.Address + segment.FileSize, pageEnd);
            if (from < to)
            {
                int length = (int)(to - from);
                int source = checked((int)(segment.FileOffset + (from - segment.Address)));
                ReadOnlySpan<byte> file = segment.Interpreter ? Interpreter : elf;
                file.Slice(source, length).CopyTo(page.Slice((int)(from - pageAddress), length));
            }
        }

        ulong stackStart = StackTop - (ulong)_stackContent.Length;
        ulong sFrom = Math.Max(stackStart, pageAddress);
        ulong sTo = Math.Min(StackTop, pageEnd);
        if (sFrom < sTo)
        {
            _stackContent.AsSpan((int)(sFrom - stackStart), (int)(sTo - sFrom)).CopyTo(page.Slice((int)(sFrom - pageAddress)));
        }
    }

    /// <summary>Belegt alle Bereiche fest (ohne Ersetzen). Scheitert einer, sind die schon belegten wieder frei.</summary>
    public ResultErr Reserve(AddressSpaceMap map)
    {
        for (int i = 0; i < Regions.Count; i++)
        {
            UserRegion region = Regions[i];
            ResultObject<ulong> r = map.Reserve(region.Start, region.End - region.Start, region.Access, Placement.FixedNoReplace);
            if (!r.IsOK())
            {
                for (int j = 0; j < i; j++)
                {
                    map.Release(Regions[j].Start, Regions[j].End - Regions[j].Start);
                }

                return ResultErr.CreateFailure(ResultCode.Failure, "Bereich 0x" + region.Start.ToString("X", CultureInfo.InvariantCulture) + " nicht belegbar: " + r.Status.Message);
            }
        }

        return ResultErr.CreateSuccess();
    }
}
