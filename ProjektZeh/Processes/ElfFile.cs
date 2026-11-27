using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ProjektZeh.Processes;

/// <summary>Fehler im ELF-Abbild oder ein Abbild, das der Lader (noch) nicht unterstützt.</summary>
internal sealed class ElfLoadException : FormatException
{
    public ElfLoadException(string message) : this(message, ElfLoadError.Invalid) { }

    public ElfLoadException(string message, ElfLoadError kind) : base(message)
    {
        Kind = kind;
    }

    /// <summary>Art des Fehlers, damit Aufrufer ihn ohne Textvergleich unterscheiden können (E2BIG als -E2BIG melden).</summary>
    public ElfLoadError Kind { get; }

    /// <summary>Fehlernummer (positiv) bei <see cref="ElfLoadError.InterpreterUnavailable"/>, sonst 0.</summary>
    public long Errno { get; init; }
}

/// <summary>Grobe Fehlerart des Laders.</summary>
internal enum ElfLoadError
{
    /// <summary>Abbild ungültig oder nicht unterstützt (ENOEXEC).</summary>
    Invalid,

    /// <summary>Argumente und Umgebung passen nicht in den Platz für die Startinformationen (E2BIG).</summary>
    ArgumentsTooLong,

    /// <summary>Ein Eintrag in argv, envp oder execFn ist ungültig: null oder mit eingebettetem Nullzeichen (EINVAL).</summary>
    InvalidArgument,

    /// <summary>Keine Zufallsquelle für AT_RANDOM übergeben.</summary>
    NoRandom,

    /// <summary>V25: Der Interpreter aus PT_INTERP ließ sich nicht lesen; die Fehlernummer steht in <see cref="ElfLoadException.Errno"/> (meist ENOENT).</summary>
    InterpreterUnavailable,
}

/// <summary>Ein Programmkopf (ELF64 Phdr), unverändert aus der Datei.</summary>
internal readonly record struct ElfProgramHeader(uint Type, uint Flags, ulong Offset, ulong VirtualAddress, ulong FileSize, ulong MemorySize, ulong Align)
{
    public const uint Load = 1;
    public const uint Dynamic = 2;
    public const uint Interpreter = 3;
    public const uint ProgramHeaders = 6;
    public const uint Tls = 7;
    public const uint GnuStack = 0x6474_E551;

    public const uint FlagExecute = 1;
    public const uint FlagWrite = 2;
    public const uint FlagRead = 4;
}

/// <summary>
/// Liest Dateikopf und Programmköpfe eines ELF64-Abbilds für x86-64 (Little Endian). Prüft jede Länge und jeden Versatz
/// überlaufsicher gegen die Dateigröße, denn das Abbild kommt vom Benutzer. Abschnittsköpfe braucht der Lader nicht und
/// liest sie deshalb nicht. Plattformunabhängig, damit die Prüfungen auf dem Host testbar sind.
/// </summary>
/// <summary>Liest ab <paramref name="offset"/> höchstens <paramref name="dst"/>.Length Bytes; Anzahl (0 am Ende) oder -errno.</summary>
internal delegate long ElfRangeReader(long offset, Span<byte> dst);

internal sealed class ElfFile
{
    public const ushort TypeExecutable = 2;
    public const ushort TypeShared = 3;
    public const ushort MachineX86_64 = 62;
    public const int HeaderSize = 64;
    public const int ProgramHeaderSize = 56;

    // Linux begrenzt die Programmköpfe auf 64 KiB; mehr als 1024 Köpfe hat kein vernünftiges Programm.
    public const int MaxProgramHeaders = 1024;

    public ushort Type { get; }
    public ulong Entry { get; }
    public ulong ProgramHeaderOffset { get; }
    public int ProgramHeaderCount { get; }
    public IReadOnlyList<ElfProgramHeader> ProgramHeaders { get; }

    private ElfFile(ushort type, ulong entry, ulong phoff, ElfProgramHeader[] headers)
    {
        Type = type;
        Entry = entry;
        ProgramHeaderOffset = phoff;
        ProgramHeaderCount = headers.Length;
        ProgramHeaders = headers;
    }

    public static ElfFile Parse(ReadOnlySpan<byte> elf)
    {
        if (elf.Length < HeaderSize)
        {
            throw new ElfLoadException("Die Datei ist kürzer als ein ELF-Kopf.");
        }

        if (elf[0] != 0x7F || elf[1] != (byte)'E' || elf[2] != (byte)'L' || elf[3] != (byte)'F')
        {
            throw new ElfLoadException("Keine ELF-Datei.");
        }

        if (elf[4] != 2)
        {
            throw new ElfLoadException("Nur ELF64 wird unterstützt.");
        }

        if (elf[5] != 1)
        {
            throw new ElfLoadException("Nur Little Endian wird unterstützt.");
        }

        if (elf[6] != 1)
        {
            throw new ElfLoadException("Unbekannte ELF-Version.");
        }

        // System V (0) und GNU/Linux (3); andere ABIs erwarten andere Systemaufrufe.
        if (elf[7] != 0 && elf[7] != 3)
        {
            throw new ElfLoadException("Nicht unterstützte OS-ABI " + elf[7] + ".");
        }

        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(elf.Slice(16));
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(elf.Slice(18));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(elf.Slice(20));
        ulong entry = BinaryPrimitives.ReadUInt64LittleEndian(elf.Slice(24));
        ulong phoff = BinaryPrimitives.ReadUInt64LittleEndian(elf.Slice(32));
        ushort ehsize = BinaryPrimitives.ReadUInt16LittleEndian(elf.Slice(52));
        ushort phentsize = BinaryPrimitives.ReadUInt16LittleEndian(elf.Slice(54));
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf.Slice(56));

        if (type != TypeExecutable && type != TypeShared)
        {
            throw new ElfLoadException("Nur ausführbare Dateien (ET_EXEC, ET_DYN) lassen sich laden, nicht Typ " + type + ".");
        }

        if (machine != MachineX86_64)
        {
            throw new ElfLoadException("Nur x86-64 wird unterstützt, nicht Maschine " + machine + ".");
        }

        if (version != 1)
        {
            throw new ElfLoadException("Unbekannte ELF-Version im Kopf.");
        }

        if (ehsize < HeaderSize)
        {
            throw new ElfLoadException("Der ELF-Kopf ist zu kurz angegeben.");
        }

        if (phentsize != ProgramHeaderSize)
        {
            throw new ElfLoadException("Unerwartete Größe der Programmköpfe: " + phentsize + ".");
        }

        // 0xFFFF (PN_XNUM) verlangt den Abschnittskopf 0; das hat kein ausführbares Programm, Linux lehnt es ebenso ab.
        if (phnum == 0 || phnum > MaxProgramHeaders)
        {
            throw new ElfLoadException("Ungültige Anzahl Programmköpfe: " + phnum + ".");
        }

        ulong tableSize = (ulong)phnum * ProgramHeaderSize;
        if (!FitsInFile(phoff, tableSize, elf.Length))
        {
            throw new ElfLoadException("Die Programmköpfe liegen außerhalb der Datei.");
        }

        var headers = new ElfProgramHeader[phnum];
        for (int i = 0; i < phnum; i++)
        {
            ReadOnlySpan<byte> p = elf.Slice((int)phoff + (i * ProgramHeaderSize), ProgramHeaderSize);
            headers[i] = new ElfProgramHeader(
                BinaryPrimitives.ReadUInt32LittleEndian(p),
                BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(4)),
                BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(8)),
                BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(16)),
                BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(32)),
                BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(40)),
                BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(48)));
        }

        return new ElfFile(type, entry, phoff, headers);
    }

    /// <summary>
    /// V28: Länge des Dateianfangs, den der Lader braucht: ELF-Kopf, Programmköpfe und die Dateibytes von PT_LOAD und PT_INTERP.
    /// Abschnitte dahinter (Debuginformation, Symboltabellen) liest niemand; Ladybirds WebContent ist 175 MB groß, ladbar sind davon
    /// rund 117 MB. Kopf und Programmköpfe kommen über <paramref name="read"/>; was sich nicht als ELF64 deuten lässt, bekommt die
    /// ganze Datei (höchstens <paramref name="fileLength"/>), damit <see cref="Parse"/> den Fehler wie bisher meldet.
    /// Rückgabe 0 oder -errno des Lesers.
    /// </summary>
    public static long LoadedLength(long fileLength, ElfRangeReader read, out long length)
    {
        ArgumentNullException.ThrowIfNull(read);
        length = fileLength;
        if (fileLength < HeaderSize)
        {
            return 0;
        }

        Span<byte> head = stackalloc byte[HeaderSize];
        long r = ReadFull(read, 0, head);
        if (r < 0)
        {
            return r;
        }

        if (r < HeaderSize || head[0] != 0x7F || head[1] != (byte)'E' || head[2] != (byte)'L' || head[3] != (byte)'F' || head[4] != 2 || head[5] != 1)
        {
            return 0;
        }

        ulong phoff = BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(32));
        ushort phentsize = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(54));
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(56));
        ulong tableSize = (ulong)phnum * ProgramHeaderSize;
        if (phentsize != ProgramHeaderSize || phnum == 0 || phnum > MaxProgramHeaders || phoff > (ulong)fileLength || tableSize > (ulong)fileLength - phoff)
        {
            return 0;
        }

        byte[] table = new byte[(int)tableSize];
        r = ReadFull(read, (long)phoff, table);
        if (r < 0)
        {
            return r;
        }

        if (r < table.Length)
        {
            return 0;
        }

        ulong end = Math.Max((ulong)HeaderSize, phoff + tableSize);
        for (int i = 0; i < phnum; i++)
        {
            ReadOnlySpan<byte> p = table.AsSpan(i * ProgramHeaderSize, ProgramHeaderSize);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(p);
            if (type != ElfProgramHeader.Load && type != ElfProgramHeader.Interpreter)
            {
                continue;
            }

            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(8));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(32));
            if (!FitsInFile(offset, size, fileLength))
            {
                // Segment über das Dateiende: ganze Datei, der Lader lehnt es mit seiner Meldung ab.
                return 0;
            }

            end = Math.Max(end, offset + size);
        }

        length = (long)end;
        return 0;
    }

    /// <summary>Liest ab <paramref name="offset"/> bis <paramref name="dst"/> voll ist oder die Datei endet; Anzahl oder -errno.</summary>
    public static long ReadFull(ElfRangeReader read, long offset, Span<byte> dst)
    {
        ArgumentNullException.ThrowIfNull(read);
        int done = 0;
        while (done < dst.Length)
        {
            long n = read(offset + done, dst.Slice(done));
            if (n < 0)
            {
                return n;
            }

            if (n == 0)
            {
                break;
            }

            done += (int)n;
        }

        return done;
    }

    /// <summary>[offset, offset + length) liegt vollständig in einer Datei der Länge <paramref name="fileLength"/>, ohne Überlauf.</summary>
    public static bool FitsInFile(ulong offset, ulong length, long fileLength)
    {
        ulong size = (ulong)fileLength;
        return offset <= size && length <= size - offset;
    }
}
