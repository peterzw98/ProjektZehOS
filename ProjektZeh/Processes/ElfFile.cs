using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ProjektZeh.Processes;

/// <summary>Fehler im ELF-Abbild oder ein Abbild, das der Lader (noch) nicht unterstützt.</summary>
internal sealed class ElfLoadException : FormatException
{
    public ElfLoadException(string message) : base(message) { }
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

    /// <summary>[offset, offset + length) liegt vollständig in einer Datei der Länge <paramref name="fileLength"/>, ohne Überlauf.</summary>
    public static bool FitsInFile(ulong offset, ulong length, int fileLength)
    {
        ulong size = (ulong)fileLength;
        return offset <= size && length <= size - offset;
    }
}
