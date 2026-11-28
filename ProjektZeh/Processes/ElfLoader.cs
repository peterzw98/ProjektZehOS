using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProjektZeh.Processes;

/// <summary>Einstellungen des Laders, die nicht aus der Datei kommen. Voreinstellungen passen für die Hosttests und den ersten Gastlauf.</summary>
internal sealed class ElfLoadOptions
{
    /// <summary>16 Bytes für AT_RANDOM (Stack-Kanarie, Zeigerschutz in musl). Pflicht: Es gibt keinen stillen Ersatz aus einem
    /// nicht kryptografischen Generator; ohne Wert wirft der Lader <see cref="ElfLoadException"/> (<see cref="ElfLoadError.NoRandom"/>).
    /// Der Prozessstart übergibt Bytes aus der Hardware, Hosttests eine feste Testquelle.</summary>
    public byte[]? Random { get; init; }

    /// <summary>Pfad für AT_EXECFN; fehlt er, gilt argv[0] (bei leerem argv eine leere Zeichenkette).</summary>
    public string? ExecFn { get; init; }

    /// <summary>Benutzer- und Gruppennummer im Hilfsvektor. ZehOS kennt im Ring 3 noch keine Benutzer; 1000 statt 0, damit
    /// Programme sich nicht für root halten. Muss zu getuid/getgid (V2) passen.</summary>
    public ulong Uid { get; init; } = 1000;

    public ulong Gid { get; init; } = 1000;

    /// <summary>AT_HWCAP: unter x86-64 CPUID(1).EDX. V3 soll den echten Wert übergeben; die Voreinstellung nennt nur die Merkmale,
    /// die jede x86-64-CPU hat (FPU, CX8, CMOV, MMX, FXSR, SSE, SSE2), weil Programme 0 als „unbekannt“ lesen.</summary>
    public ulong HwCap { get; init; } = (1UL << 0) | (1UL << 8) | (1UL << 15) | (1UL << 23) | (1UL << 24) | (1UL << 25) | (1UL << 26);

    /// <summary>Größe des Stacks (Vielfaches der Seitengröße).</summary>
    public ulong StackSize { get; init; } = 8UL << 20;

    /// <summary>Obergrenze für die Summe der Segmentgrößen im Speicher (Programm und Interpreter zusammen), damit ein Kopf mit
    /// riesigem .bss den Prozessstart nicht den ganzen Speicher anfordern lässt.</summary>
    public ulong MaxImageSize { get; init; } = 1UL << 30;

    /// <summary>V25: liest den Interpreter aus PT_INTERP (absoluter Pfad in der Prozesswurzel). Ohne Leser lehnt der Lader dynamisch
    /// gelinkte Programme mit ENOENT ab, wie Linux bei fehlendem Interpreter.</summary>
    public ElfFileReader? ReadInterpreter { get; init; }

    /// <summary>
    /// N2-Z: wie <see cref="ReadInterpreter"/>, aber mit Vorrang und ohne den ganzen Interpreter in einen Puffer zu lesen: Liefert der Leser
    /// <c>lazy</c>, ist <c>head</c> nur der Kopfteil (<see cref="ElfFile.HeadLength"/>) und die Segmente kommen später aus der Datei
    /// (der Aufrufer hält die Quelle, <see cref="UserImage.InterpreterLazy"/>); sonst steht der ganze ladbare Teil in <c>head</c>.
    /// </summary>
    public ElfInterpreterReader? ReadInterpreterHead { get; init; }

    /// <summary>
    /// V30: Länge der ganzen Programmdatei, wenn <c>elf</c> nur ihr Kopfteil ist (<see cref="ElfFile.HeadLength"/>): Die Segmente werden
    /// gegen diese Länge geprüft und später aus der Datei gelesen. null: <c>elf</c> ist der ganze ladbare Teil (bisheriger Weg).
    /// </summary>
    public long? FileLength { get; init; }
}

/// <summary>Liest eine Datei ganz: 0 oder -errno (etwa -ENOENT), bei Erfolg der Inhalt in <paramref name="content"/>.</summary>
internal delegate long ElfFileReader(string path, out byte[] content);

/// <summary>N2-Z: liest den Kopfteil einer Datei und nennt ihre ganze Länge; <paramref name="lazy"/>: der Rest kommt beim Zugriff aus der Datei.</summary>
internal delegate long ElfInterpreterReader(string path, out byte[] head, out long fileLength, out bool lazy);

/// <summary>
/// Lader für ELF64-Programme (x86-64, Linux-ABI): ET_EXEC an den Adressen der Datei, ET_DYN (PIE) ab <see cref="PieBase"/>.
/// Statisch-PIE verschiebt sich selbst (musl <c>_dlstart_c</c> findet die Basis über AT_PHDR und PT_DYNAMIC, AT_BASE ist 0
/// wie bei Linux ohne Interpreter); der Lader wendet deshalb keine Relokationen an.
/// V25: Mit PT_INTERP (dynamisch gelinkt) lädt er zusätzlich den Interpreter (musl: <c>/lib/ld-musl-x86_64.so.1</c>, über
/// <see cref="ElfLoadOptions.ReadInterpreter"/>) ab <see cref="InterpreterBase"/> und springt dort hinein; AT_BASE nennt dessen
/// Verschiebung, AT_PHDR, AT_PHNUM und AT_ENTRY beschreiben weiter das Programm. Den Rest (Bibliotheken, Relokationen) erledigt der
/// Interpreter im Ring 3 über open, mmap und mprotect.
/// Der Startstack folgt dem System-V-ABI für x86-64: argc, argv, NULL, envp, NULL, Hilfsvektor (Satz S1 des Entwurfs), darüber die
/// Zeichenketten und die 16 Zufallsbytes.
/// </summary>
internal static class ElfLoader
{
    private const ulong PageSize = AddressSpaceMap.PageSize;

    /// <summary>Unterste Adresse für Segmente, wie <c>vm.mmap_min_addr</c> bei Linux: Nullzeiger und kleine Versätze bleiben unabgebildet.</summary>
    public const ulong MinAddress = 0x1_0000;

    /// <summary>Erste Adresse hinter dem Benutzerraum (wie <c>UserPageTable.UserEnd</c> im Cosmos-Patch user-cr3); der Stack endet hier.</summary>
    public const ulong UserEnd = 0x0000_7FFF_FFFF_F000;

    /// <summary>Lage von statisch-PIE, dieselbe wie Linux ohne Zufall (ELF_ET_DYN_BASE). Feste Lage, bis es Zufall im Adressraum gibt.</summary>
    public const ulong PieBase = 0x0000_5555_5555_4000;

    /// <summary>V25: Lage des Interpreters. Linux legt ihn unter die mmap-Basis nahe dem Stack (mit Zufall); ZehOS nimmt eine feste Adresse
    /// weit über PIE, Heap und der mmap-Basis von <see cref="LinuxUserProcess"/>, unter dem Stack.</summary>
    public const ulong InterpreterBase = 0x0000_7F00_0000_0000;

    /// <summary>Längster Interpreterpfad samt Null, wie PATH_MAX bei Linux (binfmt_elf lehnt längere ab).</summary>
    private const ulong MaxInterpreterPath = 4096;

    private const long ENoEnt = 2;

    /// <summary>Platz für argv, envp und Hilfsvektor höchstens ein Viertel des Stacks, wie bei Linux (ARG_MAX-Regel).</summary>
    private const int StackInfoDivisor = 4;

    private const ulong AtNull = 0, AtPhdr = 3, AtPhent = 4, AtPhnum = 5, AtPagesz = 6, AtBase = 7, AtFlags = 8, AtEntry = 9;
    private const ulong AtUid = 11, AtEuid = 12, AtGid = 13, AtEgid = 14, AtHwcap = 16, AtClktck = 17, AtSecure = 23, AtRandom = 25, AtExecfn = 31;

    public static UserImage Load(ReadOnlySpan<byte> elf, string[] argv, string[] envp, ElfLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(envp);
        ArgumentNullException.ThrowIfNull(options);
        if (options.StackSize < 2 * PageSize || (options.StackSize & (PageSize - 1)) != 0 || options.StackSize > (UserEnd - MinAddress) / 2)
        {
            throw new ArgumentException("Die Stackgröße muss seitenausgerichtet und mindestens zwei Seiten groß sein.", nameof(options));
        }

        if (options.Random == null)
        {
            throw new ElfLoadException("Keine Zufallsquelle für AT_RANDOM übergeben.", ElfLoadError.NoRandom);
        }

        if (options.Random.Length != 16)
        {
            throw new ArgumentException("AT_RANDOM braucht genau 16 Bytes.", nameof(options));
        }

        ElfFile file = ElfFile.Parse(elf);
        ulong stackTop = UserEnd;
        ulong stackBottom = stackTop - options.StackSize;
        ulong guard = stackBottom - PageSize;

        ulong total = 0;
        if (options.FileLength is long declared && declared < elf.Length)
        {
            throw new ArgumentException("Die Dateilänge ist kürzer als der gelesene Kopfteil.", nameof(options));
        }

        List<ElfProgramHeader> loads = CollectLoads(file, elf, options.FileLength ?? elf.Length, options, ref total, out ulong lowestPage, out bool hasPhdr,
            out ulong phdrVaddr, out string? interpreterPath);

        // ET_EXEC liegt, wo die Datei sagt; ET_DYN so, dass die niedrigste Seite auf PieBase kommt.
        ulong bias = 0;
        if (file.Type == ElfFile.TypeShared && lowestPage < PieBase)
        {
            bias = PieBase - lowestPage;
        }

        UserSegment[] segments = Place(loads, bias, guard, false, out ulong highest);
        UserRegion[] segmentRegions = BuildRegions(segments);
        // Einsprung und PT_PHDR kommen frei aus der Datei: mit der PIE-Verschiebung kann die Summe überlaufen und auf eine
        // gültige Adresse zurückfallen. Das wird vorher abgelehnt statt sich auf die Bereichsprüfung danach zu verlassen.
        ulong programEntry = AddBias(file.Entry, bias, "Der Einsprung");
        if (!IsExecutable(segmentRegions, programEntry))
        {
            throw new ElfLoadException("Der Einsprung 0x" + file.Entry.ToString("X", CultureInfo.InvariantCulture) + " liegt in keinem ausführbaren Segment.");
        }

        ulong phdrAddress = ProgramHeaderAddress(file, loads, hasPhdr, phdrVaddr, bias);
        if (!IsMapped(segmentRegions, phdrAddress, (ulong)file.ProgramHeaderCount * ElfFile.ProgramHeaderSize))
        {
            throw new ElfLoadException("Die Programmköpfe liegen in keinem geladenen Segment (AT_PHDR).");
        }

        // V25: Interpreter. Der Einsprung wechselt zu ihm; AT_ENTRY bleibt der Einsprung des Programms.
        ulong entry = programEntry;
        ulong interpreterBias = 0;
        byte[]? interpreter = null;
        bool interpreterLazy = false;
        UserSegment[] interpreterSegments = [];
        UserRegion[] interpreterRegions = [];
        if (interpreterPath != null)
        {
            interpreter = ReadInterpreterFile(interpreterPath, options, out long interpreterLength, out interpreterLazy);
            ElfFile interpreterFile = ElfFile.Parse(interpreter);
            if (interpreterFile.Type != ElfFile.TypeShared)
            {
                throw new ElfLoadException("Der Interpreter " + interpreterPath + " ist kein ET_DYN.");
            }

            List<ElfProgramHeader> interpreterLoads = CollectLoads(interpreterFile, interpreter, interpreterLength, options, ref total, out ulong interpreterLowest, out _, out _, out string? nested);
            if (nested != null)
            {
                throw new ElfLoadException("Der Interpreter " + interpreterPath + " verlangt selbst einen Interpreter.");
            }

            if (interpreterLowest > InterpreterBase)
            {
                throw new ElfLoadException("Der Interpreter " + interpreterPath + " liegt zu hoch für seine Basis.");
            }

            interpreterBias = InterpreterBase - interpreterLowest;
            interpreterSegments = Place(interpreterLoads, interpreterBias, guard, true, out _);
            interpreterRegions = BuildRegions(interpreterSegments);
            if (Overlaps(segmentRegions, interpreterRegions))
            {
                throw new ElfLoadException("Programm und Interpreter überschneiden sich im Adressraum.");
            }

            entry = AddBias(interpreterFile.Entry, interpreterBias, "Der Einsprung des Interpreters");
            if (!IsExecutable(interpreterRegions, entry))
            {
                throw new ElfLoadException("Der Einsprung des Interpreters liegt in keinem ausführbaren Segment.");
            }
        }

        byte[] random = options.Random;
        string execFn = options.ExecFn ?? (argv.Length > 0 ? argv[0] : string.Empty);
        ulong[] auxv =
        [
            AtPhdr, phdrAddress,
            AtPhent, ElfFile.ProgramHeaderSize,
            AtPhnum, (ulong)file.ProgramHeaderCount,
            AtPagesz, PageSize,
            AtBase, interpreterBias,
            AtFlags, 0,
            AtEntry, programEntry,
            AtUid, options.Uid,
            AtEuid, options.Uid,
            AtGid, options.Gid,
            AtEgid, options.Gid,
            AtHwcap, options.HwCap,
            AtClktck, 100,
            AtSecure, 0,
            AtRandom, 0, // Adresse folgt beim Aufbau
            AtExecfn, 0, // Adresse folgt beim Aufbau
            AtNull, 0,
        ];

        byte[] stack = BuildStack(stackTop, argv, envp, execFn, random, auxv, options.StackSize / StackInfoDivisor, out ulong stackPointer);

        var regions = new List<UserRegion>(segmentRegions.Length + interpreterRegions.Length + 2);
        regions.AddRange(segmentRegions);
        regions.AddRange(interpreterRegions);
        regions.Sort((a, b) => a.Start.CompareTo(b.Start));
        regions.Add(new UserRegion(guard, stackBottom, MapAccess.None));
        regions.Add(new UserRegion(stackBottom, stackTop, MapAccess.ReadWrite));
        UserSegment[] all = new UserSegment[segments.Length + interpreterSegments.Length];
        segments.CopyTo(all, 0);
        interpreterSegments.CopyTo(all, segments.Length);
        return new UserImage(entry, stackPointer, bias, phdrAddress, highest, stackBottom, stackTop, all, regions.ToArray(), stack, elf.Length, options.Uid, options.Gid)
        {
            Interpreter = interpreter,
            InterpreterLazy = interpreterLazy,
            InterpreterBias = interpreterBias,
        };
    }

    /// <summary>Liest die ladbaren Segmente einer Datei, zählt sie gegen <see cref="ElfLoadOptions.MaxImageSize"/> und findet PT_PHDR und PT_INTERP.</summary>
    private static List<ElfProgramHeader> CollectLoads(ElfFile file, ReadOnlySpan<byte> bytes, long fileLength, ElfLoadOptions options, ref ulong total, out ulong lowestPage,
        out bool hasPhdr, out ulong phdrVaddr, out string? interpreterPath)
    {
        List<ElfProgramHeader> loads = new();
        lowestPage = ulong.MaxValue;
        hasPhdr = false;
        phdrVaddr = 0;
        interpreterPath = null;
        foreach (ElfProgramHeader ph in file.ProgramHeaders)
        {
            switch (ph.Type)
            {
                case ElfProgramHeader.Interpreter:
                    // Wie binfmt_elf: der erste PT_INTERP zählt.
                    interpreterPath ??= ReadInterpreterPath(bytes, ph);
                    break;
                case ElfProgramHeader.ProgramHeaders:
                    hasPhdr = true;
                    phdrVaddr = ph.VirtualAddress;
                    break;
                case ElfProgramHeader.Load:
                    ValidateLoad(ph, fileLength);
                    if (ph.MemorySize == 0)
                    {
                        break;
                    }

                    loads.Add(ph);
                    lowestPage = Math.Min(lowestPage, ph.VirtualAddress & ~(PageSize - 1));
                    ulong pages = PageUp(ph.VirtualAddress + ph.MemorySize) - (ph.VirtualAddress & ~(PageSize - 1));
                    // total bleibt <= MaxImageSize, die Subtraktion läuft deshalb nie unter null.
                    if (pages > options.MaxImageSize - total)
                    {
                        throw new ElfLoadException("Die Segmente sind zusammen größer als " + options.MaxImageSize + " Bytes.");
                    }

                    total += pages;
                    break;
            }
        }

        if (loads.Count == 0)
        {
            throw new ElfLoadException("Die Datei hat kein ladbares Segment.");
        }

        RejectOverlaps(loads);
        return loads;
    }

    /// <summary>Verschiebt die Segmente um <paramref name="bias"/> und prüft, dass sie zwischen MinAddress und der Schutzseite liegen.</summary>
    private static UserSegment[] Place(List<ElfProgramHeader> loads, ulong bias, ulong guard, bool interpreter, out ulong highest)
    {
        var segments = new UserSegment[loads.Count];
        highest = 0;
        for (int i = 0; i < loads.Count; i++)
        {
            ElfProgramHeader ph = loads[i];
            ulong start = ph.VirtualAddress & ~(PageSize - 1);
            ulong end = PageUp(ph.VirtualAddress + ph.MemorySize);
            if (bias > guard || start > guard - bias || end > guard - bias || start + bias < MinAddress)
            {
                throw new ElfLoadException("Segment bei 0x" + ph.VirtualAddress.ToString("X", CultureInfo.InvariantCulture) + " liegt außerhalb des nutzbaren Benutzerraums.");
            }

            segments[i] = new UserSegment(ph.VirtualAddress + bias, ph.MemorySize, ph.Offset, ph.FileSize, AccessOf(ph.Flags), interpreter);
            highest = Math.Max(highest, end + bias);
        }

        return segments;
    }

    /// <summary>
    /// Pfad aus PT_INTERP wie binfmt_elf: 2 bis PATH_MAX Bytes, das letzte eine Null; gilt bis zur ersten Null. ZehOS verlangt
    /// zusätzlich einen absoluten Pfad: einen relativen löste Linux gegen das Arbeitsverzeichnis auf, das hat kein Programm.
    /// </summary>
    private static string ReadInterpreterPath(ReadOnlySpan<byte> bytes, ElfProgramHeader ph)
    {
        if (ph.FileSize < 2 || ph.FileSize > MaxInterpreterPath || !ElfFile.FitsInFile(ph.Offset, ph.FileSize, bytes.Length))
        {
            throw new ElfLoadException("PT_INTERP hat eine ungültige Länge oder liegt außerhalb der Datei.");
        }

        ReadOnlySpan<byte> raw = bytes.Slice((int)ph.Offset, (int)ph.FileSize);
        if (raw[^1] != 0)
        {
            throw new ElfLoadException("PT_INTERP endet nicht mit einer Null.");
        }

        raw = raw.Slice(0, raw.IndexOf((byte)0));
        if (raw.Length == 0 || raw[0] != (byte)'/')
        {
            throw new ElfLoadException("PT_INTERP nennt keinen absoluten Pfad.");
        }

        return Encoding.UTF8.GetString(raw);
    }

    private static byte[] ReadInterpreterFile(string path, ElfLoadOptions options, out long length, out bool lazy)
    {
        lazy = false;
        if (options.ReadInterpreterHead != null)
        {
            long h = options.ReadInterpreterHead(path, out byte[] head, out length, out lazy);
            if (h < 0)
            {
                throw new ElfLoadException("Interpreter " + path + " nicht lesbar (errno " + (-h).ToString(CultureInfo.InvariantCulture) + ").", ElfLoadError.InterpreterUnavailable) { Errno = -h };
            }

            if (head == null || length < head.Length)
            {
                throw new ElfLoadException("Interpreter " + path + " ohne Inhalt.");
            }

            return head;
        }

        if (options.ReadInterpreter == null)
        {
            throw new ElfLoadException("Interpreter " + path + " nicht erreichbar (kein Dateisystem).", ElfLoadError.InterpreterUnavailable) { Errno = ENoEnt };
        }

        long r = options.ReadInterpreter(path, out byte[] content);
        if (r < 0)
        {
            throw new ElfLoadException("Interpreter " + path + " nicht lesbar (errno " + (-r).ToString(CultureInfo.InvariantCulture) + ").", ElfLoadError.InterpreterUnavailable) { Errno = -r };
        }

        length = content?.Length ?? 0;
        return content ?? throw new ElfLoadException("Interpreter " + path + " ohne Inhalt.");
    }

    // Beide Listen sind sortiert und in sich überlappungsfrei.
    private static bool Overlaps(UserRegion[] a, UserRegion[] b)
    {
        foreach (UserRegion x in a)
        {
            foreach (UserRegion y in b)
            {
                if (x.Start < y.End && y.Start < x.End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void ValidateLoad(ElfProgramHeader ph, long fileLength)
    {
        if (ph.FileSize > ph.MemorySize)
        {
            throw new ElfLoadException("Segment mit mehr Dateibytes als Speicher.");
        }

        if (!ElfFile.FitsInFile(ph.Offset, ph.FileSize, fileLength))
        {
            throw new ElfLoadException("Segment reicht über das Dateiende.");
        }

        if (ph.VirtualAddress > UserEnd || ph.MemorySize > UserEnd - ph.VirtualAddress)
        {
            throw new ElfLoadException("Segment bei 0x" + ph.VirtualAddress.ToString("X", CultureInfo.InvariantCulture) + " liegt außerhalb des Benutzerraums.");
        }

        if ((ph.Flags & ElfProgramHeader.FlagWrite) != 0 && (ph.Flags & ElfProgramHeader.FlagExecute) != 0)
        {
            throw new ElfLoadException("Segment zugleich beschreibbar und ausführbar (W^X).");
        }
    }

    // x86-64 kennt kein Ausführen ohne Lesen: X wird zu ReadExecute.
    private static MapAccess AccessOf(uint flags)
    {
        if ((flags & ElfProgramHeader.FlagExecute) != 0)
        {
            return MapAccess.ReadExecute;
        }

        if ((flags & ElfProgramHeader.FlagWrite) != 0)
        {
            return MapAccess.ReadWrite;
        }

        return (flags & ElfProgramHeader.FlagRead) != 0 ? MapAccess.Read : MapAccess.None;
    }

    /// <summary>
    /// Seitenweise Vereinigung der Segmente. Teilen sich zwei Segmente eine Seite (Ende von .text und Anfang von .rodata ohne
    /// <c>-z separate-code</c>), bekommt die Seite die Rechte beider; würde sie dadurch beschreibbar und ausführbar, lehnt der Lader ab.
    /// </summary>
    private static UserRegion[] BuildRegions(UserSegment[] segments)
    {
        var bounds = new SortedSet<ulong>();
        foreach (UserSegment s in segments)
        {
            bounds.Add(s.Address & ~(PageSize - 1));
            bounds.Add(PageUp(s.Address + s.MemorySize));
        }

        var regions = new List<UserRegion>();
        ulong? previous = null;
        foreach (ulong bound in bounds)
        {
            if (previous is ulong from)
            {
                bool any = false, read = false, write = false, execute = false;
                foreach (UserSegment s in segments)
                {
                    ulong sStart = s.Address & ~(PageSize - 1);
                    ulong sEnd = PageUp(s.Address + s.MemorySize);
                    if (sStart < bound && sEnd > from)
                    {
                        any = true;
                        read |= s.Access != MapAccess.None;
                        write |= s.Access == MapAccess.ReadWrite;
                        execute |= s.Access == MapAccess.ReadExecute;
                    }
                }

                if (any)
                {
                    if (write && execute)
                    {
                        throw new ElfLoadException("Ein beschreibbares und ein ausführbares Segment teilen sich die Seite 0x" + from.ToString("X", CultureInfo.InvariantCulture) + " (W^X).");
                    }

                    MapAccess access = execute ? MapAccess.ReadExecute : write ? MapAccess.ReadWrite : read ? MapAccess.Read : MapAccess.None;
                    if (regions.Count > 0 && regions[^1].End == from && regions[^1].Access == access)
                    {
                        regions[^1] = regions[^1] with { End = bound };
                    }
                    else
                    {
                        regions.Add(new UserRegion(from, bound, access));
                    }
                }
            }

            previous = bound;
        }

        return regions.ToArray();
    }

    // Linux nimmt PT_PHDR, sonst das Segment, das die Programmköpfe aus der Datei enthält.
    private static ulong ProgramHeaderAddress(ElfFile file, List<ElfProgramHeader> loads, bool hasPhdr, ulong phdrVaddr, ulong bias)
    {
        if (hasPhdr)
        {
            return AddBias(phdrVaddr, bias, "PT_PHDR");
        }

        ulong size = (ulong)file.ProgramHeaderCount * ElfFile.ProgramHeaderSize;
        foreach (ElfProgramHeader ph in loads)
        {
            if (file.ProgramHeaderOffset >= ph.Offset && file.ProgramHeaderOffset - ph.Offset <= ph.FileSize
                && size <= ph.FileSize - (file.ProgramHeaderOffset - ph.Offset))
            {
                return ph.VirtualAddress + (file.ProgramHeaderOffset - ph.Offset) + bias;
            }
        }

        throw new ElfLoadException("Die Programmköpfe liegen in keinem geladenen Segment (AT_PHDR).");
    }

    private static ulong AddBias(ulong address, ulong bias, string what)
    {
        if (address > UserEnd - bias)
        {
            throw new ElfLoadException(what + " 0x" + address.ToString("X", CultureInfo.InvariantCulture) + " liegt nach der Verschiebung außerhalb des Benutzerraums.");
        }

        return address + bias;
    }

    /// <summary>
    /// Lehnt PT_LOAD ab, deren Speicherbereiche sich bytegenau überschneiden: sonst überschriebe ein späteres Segment beim Füllen
    /// das frühere, und die gemeinsame Seite bekäme die Rechte beider (eine nur lesbare Seite würde beschreibbar). Eine gemeinsame
    /// Seite ohne Überschneidung (Ende von .text, Anfang von .rodata) bleibt erlaubt; ValidateLoad hat Überläufe schon ausgeschlossen.
    /// </summary>
    private static void RejectOverlaps(List<ElfProgramHeader> loads)
    {
        for (int i = 0; i < loads.Count; i++)
        {
            for (int j = i + 1; j < loads.Count; j++)
            {
                ElfProgramHeader a = loads[i], b = loads[j];
                if (a.VirtualAddress < b.VirtualAddress + b.MemorySize && b.VirtualAddress < a.VirtualAddress + a.MemorySize)
                {
                    throw new ElfLoadException("Segmente bei 0x" + a.VirtualAddress.ToString("X", CultureInfo.InvariantCulture) + " und 0x"
                        + b.VirtualAddress.ToString("X", CultureInfo.InvariantCulture) + " überschneiden sich.");
                }
            }
        }
    }

    private static bool IsExecutable(UserRegion[] regions, ulong address)
    {
        foreach (UserRegion r in regions)
        {
            if (address >= r.Start && address < r.End)
            {
                return r.Access == MapAccess.ReadExecute;
            }
        }

        return false;
    }

    // [address, address + length) liegt in lesbaren, lückenlos aufeinanderfolgenden Bereichen.
    private static bool IsMapped(UserRegion[] regions, ulong address, ulong length)
    {
        if (address > ulong.MaxValue - length)
        {
            return false;
        }

        ulong next = address;
        ulong end = address + length;
        foreach (UserRegion r in regions)
        {
            if (r.End <= next)
            {
                continue;
            }

            if (r.Start > next || r.Access == MapAccess.None)
            {
                return false;
            }

            next = r.End;
            if (next >= end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Baut den Stackinhalt [StackPointer, top). Von oben: 16 Zufallsbytes, AT_EXECFN, die envp- und argv-Zeichenketten; darunter auf
    /// 16 Bytes ausgerichtet Hilfsvektor, envp, argv und argc, sodass RSP auf argc zeigt und durch 16 teilbar ist.
    /// </summary>
    private static byte[] BuildStack(ulong top, string[] argv, string[] envp, string execFn, byte[] random, ulong[] auxv, ulong limit, out ulong stackPointer)
    {
        // Gemeinsames Budget über argv, envp und execFn: Die Eingabe wird schon beim Umwandeln gegen die Grenze gezählt, damit
        // ein riesiges Argument oder Millionen leerer Einträge nicht erst vollständig umgewandelt werden.
        ulong used = 0;
        byte[][] args = Encode(argv, "argv", limit, ref used);
        byte[][] envs = Encode(envp, "envp", limit, ref used);
        byte[] exec = Encode([execFn], "execFn", limit, ref used)[0];

        ulong strings = 16 + (ulong)exec.Length;
        foreach (byte[] a in args)
        {
            strings += (ulong)a.Length;
        }

        foreach (byte[] e in envs)
        {
            strings += (ulong)e.Length;
        }

        ulong pointerCount = 1 + (ulong)args.Length + 1 + (ulong)envs.Length + 1 + (ulong)auxv.Length;
        ulong infoStart = (top - strings) & ~15UL;
        ulong pointerBytes = pointerCount * 8;
        stackPointer = (infoStart - pointerBytes) & ~15UL;
        ulong size = top - stackPointer;
        if (size > limit)
        {
            throw TooBig();
        }

        byte[] content = new byte[size];
        ulong cursor = top;

        cursor -= 16;
        random.CopyTo(content, (int)(cursor - stackPointer));
        ulong randomAddress = cursor;

        cursor = Place(content, stackPointer, cursor, exec);
        ulong execAddress = cursor;

        // envp und argv werden von oben nach unten abgelegt; die Zeiger behalten die Reihenfolge der Eingabe.
        var envAddresses = new ulong[envs.Length];
        for (int i = envs.Length - 1; i >= 0; i--)
        {
            cursor = Place(content, stackPointer, cursor, envs[i]);
            envAddresses[i] = cursor;
        }

        var argAddresses = new ulong[args.Length];
        for (int i = args.Length - 1; i >= 0; i--)
        {
            cursor = Place(content, stackPointer, cursor, args[i]);
            argAddresses[i] = cursor;
        }

        for (int i = 0; i < auxv.Length; i += 2)
        {
            if (auxv[i] == AtRandom)
            {
                auxv[i + 1] = randomAddress;
            }
            else if (auxv[i] == AtExecfn)
            {
                auxv[i + 1] = execAddress;
            }
        }

        Span<byte> words = content.AsSpan(0, (int)pointerBytes);
        int w = 0;
        Put(words, ref w, (ulong)args.Length);
        foreach (ulong a in argAddresses)
        {
            Put(words, ref w, a);
        }

        Put(words, ref w, 0);
        foreach (ulong e in envAddresses)
        {
            Put(words, ref w, e);
        }

        Put(words, ref w, 0);
        foreach (ulong v in auxv)
        {
            Put(words, ref w, v);
        }

        return content;
    }

    private static ulong Place(byte[] content, ulong baseAddress, ulong cursor, byte[] value)
    {
        cursor -= (ulong)value.Length;
        value.CopyTo(content, (int)(cursor - baseAddress));
        return cursor;
    }

    private static void Put(Span<byte> words, ref int index, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(words.Slice(index * 8), value);
        index++;
    }

    private static ElfLoadException TooBig()
        => new("Argumente und Umgebung sind zu groß für den Stack (E2BIG).", ElfLoadError.ArgumentsTooLong);

    // UTF-8 mit abschließender Null; eine eingebettete Null würde die Zeichenkette für das Programm stillschweigend kürzen.
    // Fehler in der Eingabe sind ElfLoadException (nie ArgumentException), die Meldung nennt Liste und Nummer, nie den Text.
    // Das Budget wird vor jeder Allokation und vor jedem Durchlauf über die Zeichenkette geprüft: Jeder Eintrag braucht mindestens
    // 9 Bytes (Null plus Zeiger), jedes UTF-16-Zeichen mindestens ein UTF-8-Byte.
    private static byte[][] Encode(string[] values, string name, ulong limit, ref ulong used)
    {
        if (used > limit || (ulong)values.Length * 9 > limit - used)
        {
            throw TooBig();
        }

        var result = new byte[values.Length][];
        for (int i = 0; i < values.Length; i++)
        {
            string value = values[i] ?? throw new ElfLoadException(name + "[" + i.ToString(CultureInfo.InvariantCulture) + "] ist null.", ElfLoadError.InvalidArgument);
            if ((ulong)value.Length + 9 > limit - used)
            {
                throw TooBig();
            }

            if (value.Contains('\0'))
            {
                throw new ElfLoadException(name + "[" + i.ToString(CultureInfo.InvariantCulture) + "] enthält ein Nullzeichen.", ElfLoadError.InvalidArgument);
            }

            // Zeichenkette mit Null plus Zeiger; die endgültige Größe (Ausrichtung, Hilfsvektor) prüft BuildStack danach.
            ulong length = (ulong)Encoding.UTF8.GetByteCount(value) + 1;
            used += length + 8;
            if (used > limit)
            {
                throw TooBig();
            }

            byte[] bytes = new byte[length];
            Encoding.UTF8.GetBytes(value, bytes);
            result[i] = bytes;
        }

        return result;
    }

    private static ulong PageUp(ulong value) => (value + PageSize - 1) & ~(PageSize - 1);
}
