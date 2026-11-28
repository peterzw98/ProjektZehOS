using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests.Processes;

[TestClass]
public sealed class ElfLoaderTests
{
    private const ulong Page = 4096;
    private const uint R = 4, W = 2, X = 1;
    private static readonly byte[] FixedRandom = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    /// <summary>Kleiner ELF64-Bauer für die Tests: Kopf, Programmköpfe direkt dahinter, danach frei gesetzte Bytes.</summary>
    private sealed class ElfBuilder
    {
        public ushort Type = ElfFile.TypeExecutable;
        public ushort Machine = ElfFile.MachineX86_64;
        public ulong Entry = 0x401000;
        public byte Class = 2, Data = 1, OsAbi;
        public ushort PhEntSize = ElfFile.ProgramHeaderSize;
        public ulong? PhOffOverride;
        public ushort? PhNumOverride;
        public readonly List<(uint Type, uint Flags, ulong Offset, ulong Vaddr, ulong FileSize, ulong MemSize)> Headers = new();
        public byte[] Body = new byte[3 * Page];

        public ElfBuilder Add(uint type, uint flags, ulong offset, ulong vaddr, ulong fileSize, ulong memSize)
        {
            Headers.Add((type, flags, offset, vaddr, fileSize, memSize));
            return this;
        }

        public byte[] Build()
        {
            byte[] b = (byte[])Body.Clone();
            b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F';
            b[4] = Class; b[5] = Data; b[6] = 1; b[7] = OsAbi;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), Type);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), Machine);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), Entry);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), PhOffOverride ?? 64);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(52), 64);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(54), PhEntSize);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), PhNumOverride ?? (ushort)Headers.Count);
            for (int i = 0; i < Headers.Count; i++)
            {
                Span<byte> p = b.AsSpan(64 + (i * 56), 56);
                var h = Headers[i];
                BinaryPrimitives.WriteUInt32LittleEndian(p, h.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(4), h.Flags);
                BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(8), h.Offset);
                BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(16), h.Vaddr);
                BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(24), h.Vaddr);
                BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(32), h.FileSize);
                BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(40), h.MemSize);
                BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(48), Page);
            }

            return b;
        }
    }

    // Wie ein statisches musl-Programm (ld -z separate-code): Kopf lesbar, Code, Nur-Lesen, Daten mit .bss.
    private static ElfBuilder Static()
    {
        var b = new ElfBuilder { Body = new byte[4 * Page] };
        for (int i = 0; i < b.Body.Length; i++)
        {
            b.Body[i] = (byte)(i * 7 + 3);
        }

        b.Add(ElfProgramHeader.Load, R, 0, 0x400000, 0x200, 0x200);
        b.Add(ElfProgramHeader.Load, R | X, Page, 0x401000, Page, Page);
        b.Add(ElfProgramHeader.Load, R, 2 * Page, 0x402000, 0x300, 0x300);
        b.Add(ElfProgramHeader.Load, R | W, 3 * Page + 0x10, 0x403010, 0x100, 0x2000);
        b.Add(ElfProgramHeader.Tls, R, 3 * Page + 0x10, 0x403010, 0x20, 0x40);
        b.Add(ElfProgramHeader.GnuStack, R | W, 0, 0, 0, 0);
        return b;
    }

    // Statisch-PIE: Adressen ab 0, PT_PHDR und PT_DYNAMIC wie bei musl rcrt1.
    private static ElfBuilder Pie()
    {
        ElfBuilder b = Static();
        b.Type = ElfFile.TypeShared;
        b.Entry = 0x1000;
        b.Headers.Clear();
        b.Add(ElfProgramHeader.ProgramHeaders, R, 64, 64, 4 * 56, 4 * 56);
        b.Add(ElfProgramHeader.Load, R, 0, 0, 0x200, 0x200);
        b.Add(ElfProgramHeader.Load, R | X, Page, 0x1000, Page, Page);
        b.Add(ElfProgramHeader.Load, R | W, 3 * Page, 0x3000, 0x100, 0x1100);
        return b;
    }

    private static ElfLoadOptions Options(ulong stackSize = 64 * 1024) => new() { Random = FixedRandom, StackSize = stackSize };

    private static UserImage Load(byte[] elf, string[]? argv = null, string[]? envp = null, ElfLoadOptions? options = null)
        => ElfLoader.Load(elf, argv ?? ["/bin/prog"], envp ?? [], options ?? Options());

    /// <summary>Füllt alle zugänglichen Seiten wie der Prozessstart und liefert sie als Speicherabbild.</summary>
    private static Dictionary<ulong, byte[]> Materialize(UserImage image, byte[] elf)
    {
        var pages = new Dictionary<ulong, byte[]>();
        foreach (UserRegion region in image.Regions)
        {
            if (region.Access == MapAccess.None)
            {
                continue;
            }

            for (ulong a = region.Start; a < region.End; a += Page)
            {
                byte[] page = new byte[Page];
                image.FillPage(elf, a, page);
                pages[a] = page;
            }
        }

        return pages;
    }

    private static byte ReadByte(Dictionary<ulong, byte[]> mem, ulong address)
    {
        Assert.IsTrue(mem.TryGetValue(address & ~(Page - 1), out byte[]? page), $"Adresse {address:X} nicht abgebildet");
        return page![address & (Page - 1)];
    }

    private static ulong ReadU64(Dictionary<ulong, byte[]> mem, ulong address)
    {
        Span<byte> b = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            b[i] = ReadByte(mem, address + (ulong)i);
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    private static string ReadString(Dictionary<ulong, byte[]> mem, ulong address)
    {
        var bytes = new List<byte>();
        for (byte c; (c = ReadByte(mem, address)) != 0; address++)
        {
            bytes.Add(c);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record StartStack(string[] Argv, string[] Envp, Dictionary<ulong, ulong> Aux);

    private static StartStack ParseStack(Dictionary<ulong, byte[]> mem, ulong sp)
    {
        ulong argc = ReadU64(mem, sp);
        ulong p = sp + 8;
        var argv = new List<string>();
        for (ulong i = 0; i < argc; i++, p += 8)
        {
            argv.Add(ReadString(mem, ReadU64(mem, p)));
        }

        Assert.AreEqual(0UL, ReadU64(mem, p), "argv endet mit NULL");
        p += 8;
        var envp = new List<string>();
        for (ulong e; (e = ReadU64(mem, p)) != 0; p += 8)
        {
            envp.Add(ReadString(mem, e));
        }

        p += 8;
        var aux = new Dictionary<ulong, ulong>();
        for (ulong type; (type = ReadU64(mem, p)) != 0; p += 16)
        {
            Assert.IsFalse(aux.ContainsKey(type), $"AT {type} doppelt");
            aux[type] = ReadU64(mem, p + 8);
        }

        return new StartStack(argv.ToArray(), envp.ToArray(), aux);
    }

    private static void AssertRejected(byte[] elf, ElfLoadOptions? options = null)
        => Assert.ThrowsException<ElfLoadException>(() => Load(elf, options: options));

    [TestMethod]
    public void StaticLoadsAtFileAddresses()
    {
        byte[] elf = Static().Build();
        UserImage image = Load(elf);

        Assert.AreEqual(0UL, image.LoadBias);
        Assert.AreEqual(0x401000UL, image.Entry);
        Assert.AreEqual(0x400040UL, image.ProgramHeaderAddress, "ohne PT_PHDR aus dem Segment mit Versatz 0");
        Assert.AreEqual(0x406000UL, image.BreakStart);
        CollectionAssert.AreEqual(
            new[]
            {
                new UserRegion(0x400000, 0x401000, MapAccess.Read),
                new UserRegion(0x401000, 0x402000, MapAccess.ReadExecute),
                new UserRegion(0x402000, 0x403000, MapAccess.Read),
                new UserRegion(0x403000, 0x406000, MapAccess.ReadWrite),
                new UserRegion(image.StackBottom - Page, image.StackBottom, MapAccess.None),
                new UserRegion(image.StackBottom, ElfLoader.UserEnd, MapAccess.ReadWrite),
            },
            new List<UserRegion>(image.Regions));
        Assert.AreEqual(ElfLoader.UserEnd, image.StackTop);
        Assert.AreEqual(ElfLoader.UserEnd - (64 * 1024), image.StackBottom);
    }

    [TestMethod]
    public void FillPageCopiesFileBytesAndZeroesBss()
    {
        byte[] elf = Static().Build();
        UserImage image = Load(elf);
        Dictionary<ulong, byte[]> mem = Materialize(image, elf);

        // Code: ganze Seite aus der Datei.
        CollectionAssert.AreEqual(elf.AsSpan((int)Page, (int)Page).ToArray(), mem[0x401000]);
        // Kopfseite: nur 0x200 Dateibytes, der Rest der Seite ist null.
        CollectionAssert.AreEqual(elf.AsSpan(0, 0x200).ToArray(), mem[0x400000].AsSpan(0, 0x200).ToArray());
        Assert.IsTrue(mem[0x400000].AsSpan(0x200).IndexOfAnyExcept((byte)0) < 0);
        // Daten: 0x10 Bytes Lücke, 0x100 Dateibytes, danach .bss bis über die nächste Seite.
        Assert.IsTrue(mem[0x403000].AsSpan(0, 0x10).IndexOfAnyExcept((byte)0) < 0);
        CollectionAssert.AreEqual(elf.AsSpan((int)(3 * Page) + 0x10, 0x100).ToArray(), mem[0x403000].AsSpan(0x10, 0x100).ToArray());
        Assert.IsTrue(mem[0x403000].AsSpan(0x110).IndexOfAnyExcept((byte)0) < 0);
        Assert.IsTrue(mem[0x404000].AsSpan().IndexOfAnyExcept((byte)0) < 0);
        Assert.IsTrue(mem[0x405000].AsSpan().IndexOfAnyExcept((byte)0) < 0);
        // AT_PHDR zeigt auf echte Programmköpfe: erster Eintrag ist PT_LOAD mit Adresse 0x400000.
        Assert.AreEqual(ElfProgramHeader.Load, (uint)ReadU64(mem, image.ProgramHeaderAddress));
        Assert.AreEqual(0x400000UL, ReadU64(mem, image.ProgramHeaderAddress + 16));
    }

    [TestMethod]
    public void StartStackFollowsSysVAbi()
    {
        byte[] elf = Static().Build();
        string[] argv = ["/bin/args_env_auxv", "a1", "b 2", "ü€"];
        string[] envp = ["ZEHOS_ABI=1", "LANG=C", "PATH=/usr/bin:/bin"];
        UserImage image = Load(elf, argv, envp);
        Dictionary<ulong, byte[]> mem = Materialize(image, elf);

        Assert.AreEqual(0UL, image.StackPointer % 16, "RSP beim Einsprung durch 16 teilbar");
        Assert.IsTrue(image.StackPointer >= image.StackBottom && image.StackPointer < image.StackTop);
        StartStack s = ParseStack(mem, image.StackPointer);
        CollectionAssert.AreEqual(argv, s.Argv);
        CollectionAssert.AreEqual(envp, s.Envp);

        Assert.AreEqual(image.ProgramHeaderAddress, s.Aux[3], "AT_PHDR");
        Assert.AreEqual(56UL, s.Aux[4], "AT_PHENT");
        Assert.AreEqual(6UL, s.Aux[5], "AT_PHNUM");
        Assert.AreEqual(4096UL, s.Aux[6], "AT_PAGESZ");
        Assert.AreEqual(0UL, s.Aux[7], "AT_BASE");
        Assert.AreEqual(image.Entry, s.Aux[9], "AT_ENTRY");
        Assert.AreEqual(1000UL, s.Aux[11], "AT_UID");
        Assert.AreEqual(1000UL, s.Aux[12], "AT_EUID");
        Assert.AreEqual(1000UL, s.Aux[13], "AT_GID");
        Assert.AreEqual(1000UL, s.Aux[14], "AT_EGID");
        Assert.AreNotEqual(0UL, s.Aux[16], "AT_HWCAP");
        Assert.AreEqual(0UL, s.Aux[23], "AT_SECURE");
        Assert.AreEqual("/bin/args_env_auxv", ReadString(mem, s.Aux[31]), "AT_EXECFN");
        ulong random = s.Aux[25];
        for (int i = 0; i < 16; i++)
        {
            Assert.AreEqual(FixedRandom[i], ReadByte(mem, random + (ulong)i), "AT_RANDOM");
        }

        Assert.IsTrue(random + 16 <= image.StackTop);
    }

    [TestMethod]
    public void StartStackOptionsAndEmptyArgv()
    {
        byte[] elf = Static().Build();
        var options = new ElfLoadOptions { Random = FixedRandom, StackSize = 64 * 1024, ExecFn = "/x/y", Uid = 7, Gid = 9, HwCap = 0x178BFBFF };
        UserImage image = ElfLoader.Load(elf, [], [], options);
        StartStack s = ParseStack(Materialize(image, elf), image.StackPointer);
        Assert.AreEqual(0, s.Argv.Length);
        Assert.AreEqual(0, s.Envp.Length);
        Assert.AreEqual(7UL, s.Aux[11]);
        Assert.AreEqual(9UL, s.Aux[14]);
        Assert.AreEqual(0x178BFBFFUL, s.Aux[16]);
        Assert.AreEqual(0UL, image.StackPointer % 16);

        // Ohne Zufallsquelle kein stiller Ersatz; die Voreinstellung der Stackgröße (8 MiB) gilt sonst.
        UserImage plain = ElfLoader.Load(elf, ["p"], [], new ElfLoadOptions { Random = FixedRandom });
        Assert.AreEqual(ElfLoader.UserEnd - (8UL << 20), plain.StackBottom);
    }

    [TestMethod]
    public void StaticPieIsRelocatedToPieBase()
    {
        byte[] elf = Pie().Build();
        UserImage image = Load(elf);
        Dictionary<ulong, byte[]> mem = Materialize(image, elf);

        Assert.AreEqual(ElfLoader.PieBase, image.LoadBias);
        Assert.AreEqual(ElfLoader.PieBase + 0x1000, image.Entry);
        Assert.AreEqual(ElfLoader.PieBase + 64, image.ProgramHeaderAddress, "AT_PHDR aus PT_PHDR plus Verschiebung");
        Assert.AreEqual(ElfLoader.PieBase + 0x5000, image.BreakStart);
        StartStack s = ParseStack(mem, image.StackPointer);
        Assert.AreEqual(0UL, s.Aux[7], "AT_BASE bleibt 0: musl rechnet die Basis aus AT_PHDR und PT_DYNAMIC");
        Assert.AreEqual(image.Entry, s.Aux[9]);
        Assert.AreEqual(ElfProgramHeader.ProgramHeaders, (uint)ReadU64(mem, s.Aux[3]));
        CollectionAssert.AreEqual(elf.AsSpan((int)Page, (int)Page).ToArray(), mem[ElfLoader.PieBase + 0x1000]);
    }

    [TestMethod]
    public void PieAboveBaseIsNotMoved()
    {
        ElfBuilder b = Pie();
        b.Entry = 0x6000_0000_1000;
        b.Headers.Clear();
        b.Add(ElfProgramHeader.Load, R | X, 0, 0x6000_0000_0000, 2 * Page, 2 * Page);
        UserImage image = Load(b.Build());
        Assert.AreEqual(0UL, image.LoadBias);
        Assert.AreEqual(0x6000_0000_0040UL, image.ProgramHeaderAddress);
    }

    [TestMethod]
    public void SharedPageGetsUnionOfRights()
    {
        var b = new ElfBuilder { Entry = 0x400100 };
        b.Add(ElfProgramHeader.Load, R | X, 0, 0x400000, 0x800, 0x800);
        b.Add(ElfProgramHeader.Load, R, 0x800, 0x400800, 0x900, 0x900);
        UserImage image = Load(b.Build());
        Assert.AreEqual(new UserRegion(0x400000, 0x401000, MapAccess.ReadExecute), image.Regions[0]);
        Assert.AreEqual(new UserRegion(0x401000, 0x402000, MapAccess.Read), image.Regions[1]);

        var conflict = new ElfBuilder { Entry = 0x400100 };
        conflict.Add(ElfProgramHeader.Load, R | X, 0, 0x400000, 0x800, 0x800);
        conflict.Add(ElfProgramHeader.Load, R | W, 0x800, 0x400800, 0x100, 0x100);
        AssertRejected(conflict.Build());
    }

    [TestMethod]
    public void OverlappingLoadsAreRejected()
    {
        // Zweites Segment beginnt innerhalb des ersten: abgelehnt, auch wenn beide nur lesbar sind.
        var b = new ElfBuilder { Entry = 0x400100 };
        b.Add(ElfProgramHeader.Load, R | X, 0, 0x400000, 0x800, 0x800);
        b.Add(ElfProgramHeader.Load, R, 0x800, 0x4007F0, 0x100, 0x100);
        AssertRejected(b.Build());

        // .bss des ersten reicht in das zweite hinein.
        var bss = new ElfBuilder { Entry = 0x401100 };
        bss.Add(ElfProgramHeader.Load, R | W, 0, 0x400000, 0x100, 0x2000);
        bss.Add(ElfProgramHeader.Load, R | X, Page, 0x401000, Page, Page);
        AssertRejected(bss.Build());

        // Unmittelbar aneinander (gemeinsame Seite ohne Überschneidung) bleibt erlaubt.
        var touching = new ElfBuilder { Entry = 0x400100 };
        touching.Add(ElfProgramHeader.Load, R | X, 0, 0x400000, 0x800, 0x800);
        touching.Add(ElfProgramHeader.Load, R, 0x800, 0x400800, 0x100, 0x100);
        Assert.AreEqual(2, Load(touching.Build()).Segments.Count);
    }

    [TestMethod]
    public void PieAddressesNearTopAreRejectedBeforeBias()
    {
        ElfBuilder entry = Pie();
        entry.Entry = ulong.MaxValue - 0xFFF;
        AssertRejected(entry.Build());

        ElfBuilder phdr = Pie();
        phdr.Headers[0] = (ElfProgramHeader.ProgramHeaders, R, 64, ulong.MaxValue - ElfLoader.PieBase + 0x41, 4 * 56, 4 * 56);
        AssertRejected(phdr.Build());
    }

    [TestMethod]
    public void ReserveBelegtAlleBereicheOderKeinen()
    {
        byte[] elf = Static().Build();
        UserImage image = Load(elf);
        var map = new AddressSpaceMap(Page, ElfLoader.UserEnd);
        Assert.IsTrue(image.Reserve(map).IsOK());
        Assert.IsTrue(map.TryFind(0x401000, out MapAccess code));
        Assert.AreEqual(MapAccess.ReadExecute, code);
        Assert.IsTrue(map.TryFind(image.StackBottom - 1, out MapAccess guard));
        Assert.AreEqual(MapAccess.None, guard);

        // Der Stack ist schon belegt: nichts aus dem Abbild darf belegt bleiben.
        var busy = new AddressSpaceMap(Page, ElfLoader.UserEnd);
        Assert.IsTrue(busy.Reserve(ElfLoader.UserEnd - Page, Page, MapAccess.Read, Placement.FixedNoReplace).IsOK());
        Assert.IsFalse(image.Reserve(busy).IsOK());
        Assert.IsFalse(busy.TryFind(0x400000, out _));
        Assert.IsFalse(busy.TryFind(image.StackBottom - 1, out _));
    }

    [TestMethod]
    public void RejectsMalformedHeaders()
    {
        Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(new byte[63], ["p"], [], Options()));
        AssertRejected(Mutate(Static().Build(), b => b[1] = (byte)'X'));
        AssertRejected(Build(b => b.Class = 1));
        AssertRejected(Build(b => b.Data = 2));
        AssertRejected(Build(b => b.OsAbi = 9));
        AssertRejected(Build(b => b.Machine = 3));
        AssertRejected(Build(b => b.Type = 1));
        AssertRejected(Build(b => b.Type = 4));
        AssertRejected(Build(b => b.PhEntSize = 64));
        AssertRejected(Build(b => b.PhNumOverride = 0));
        AssertRejected(Build(b => b.PhNumOverride = 0xFFFF));
        AssertRejected(Build(b => b.PhOffOverride = (ulong)b.Body.Length - 100));
        AssertRejected(Build(b => b.PhOffOverride = ulong.MaxValue - 10));
        AssertRejected(Mutate(Static().Build(), b => b[20] = 2));
    }

    [TestMethod]
    public void RejectsBadSegments()
    {
        AssertRejected(Seg(R | X, 0, 0x401000, 0x2000, 0x1000));                 // Datei größer als Speicher
        AssertRejected(Seg(R | X, 3 * Page, 0x401000, 2 * Page, 2 * Page));      // über das Dateiende
        AssertRejected(Seg(R | X, ulong.MaxValue - 5, 0x401000, 0x10, 0x10));    // Versatz läuft über
        AssertRejected(Seg(R | X, 0, ulong.MaxValue - 0x100, 0, 0x1000));        // Adresse plus Größe läuft über
        AssertRejected(Seg(R | W | X, 0, 0x401000, 0x10, 0x10));                 // W^X
        AssertRejected(Seg(R | X, 0, 0x1000, 0x10, 0x10, entry: 0x1000));        // unter MinAddress
        AssertRejected(Seg(R | X, 0, ElfLoader.UserEnd - Page, 0x10, 0x10, entry: ElfLoader.UserEnd - Page)); // im Stack
        AssertRejected(Seg(R | X, 0, 0x8000_0000_0000, 0x10, 0x10, entry: 0x8000_0000_0000)); // Kernelhälfte
        AssertRejected(Seg(R, 0, 0x401000, 0x10, 0x10));                         // Einsprung nicht ausführbar
        AssertRejected(Seg(R | X, 0, 0x402000, 0x10, 0x10));                     // Einsprung außerhalb
        AssertRejected(Build(b => b.Add(ElfProgramHeader.Interpreter, R, 0, 0, 0, 0)));
        AssertRejected(Build(b =>
        {
            b.Headers.Clear();
            b.Add(ElfProgramHeader.GnuStack, R | W, 0, 0, 0, 0);
        }));
        AssertRejected(Seg(R | X, 0, 0x400000, 0x1000, 0x10_0000_1000, entry: 0x400000)); // größer als MaxImageSize

        // Programmköpfe weder per PT_PHDR noch aus einem Segment erreichbar.
        var noPhdr = new ElfBuilder();
        noPhdr.Add(ElfProgramHeader.Load, R | X, Page, 0x401000, Page, Page);
        AssertRejected(noPhdr.Build());
        AssertRejected(Build(b => b.Add(ElfProgramHeader.ProgramHeaders, R, 64, 0x900000, 56, 56)));
    }

    // V25: dynamisch gelinktes PIE wie bei Alpine: PT_INTERP im ersten Segment, Interpreter ist ein ET_DYN (hier dasselbe Muster wie Pie()).
    private const string Ldso = "/lib/ld-musl-x86_64.so.1";
    private const ulong AtPhdr = 3, AtPhnum = 5, AtBase = 7, AtEntry = 9;

    private static ElfBuilder Dynamic(string interp = Ldso, bool terminate = true)
    {
        ElfBuilder b = Pie();
        byte[] path = Encoding.UTF8.GetBytes(interp);
        path.CopyTo(b.Body, 0x180);
        b.Body[0x180 + path.Length] = 0;
        b.Add(ElfProgramHeader.Interpreter, R, 0x180, 0x180, (ulong)path.Length + (terminate ? 1UL : 0UL), (ulong)path.Length + 1);
        return b;
    }

    private static byte[] Interp()
    {
        ElfBuilder b = Pie();
        b.Entry = 0x1010;
        for (int i = 0; i < b.Body.Length; i++)
        {
            b.Body[i] = (byte)(i * 13 + 5); // anderes Muster als das Programm
        }

        return b.Build();
    }

    private static ElfLoadOptions DynOptions(byte[]? interp, long error = 0, List<string>? asked = null) => new()
    {
        Random = FixedRandom,
        StackSize = 64 * 1024,
        ReadInterpreter = (string path, out byte[] content) =>
        {
            asked?.Add(path);
            content = interp ?? [];
            return error;
        },
    };

    [TestMethod]
    public void DynamicLazyInterpreterReadsOnlyHeadAndSkipsItsBytes()
    {
        byte[] elf = Dynamic().Build();
        byte[] interp = Interp();
        byte[] head = interp.AsSpan(0, 64 + 56 * 5).ToArray();
        ElfLoadOptions options = new()
        {
            Random = FixedRandom,
            StackSize = 64 * 1024,
            ReadInterpreterHead = (string path, out byte[] h, out long length, out bool lazy) =>
            {
                h = head;
                length = interp.Length;
                lazy = true;
                return 0;
            },
        };
        UserImage image = ElfLoader.Load(elf, ["p"], [], options);

        Assert.IsTrue(image.InterpreterLazy);
        Assert.AreSame(head, image.Interpreter);
        Assert.AreEqual(ElfLoader.InterpreterBase + 0x1010, image.Entry);
        Assert.IsTrue(image.Segments.Any(s => s.Interpreter && s.FileSize > 0));
        UserSegment seg = image.Segments.First(s => s.Interpreter && s.FileSize > 0);
        byte[] page = new byte[4096];
        image.FillPage(elf, seg.Address & ~4095UL, page);
        CollectionAssert.AreEqual(new byte[4096], page, "Seiten des Interpreters kommen beim Zugriff aus der Datei, nicht aus dem Kopfteil");
    }

    [TestMethod]
    public void DynamicLoadsInterpreterAndAuxv()
    {
        byte[] elf = Dynamic().Build();
        byte[] interp = Interp();
        var asked = new List<string>();
        UserImage image = ElfLoader.Load(elf, ["p"], [], DynOptions(interp, asked: asked));

        CollectionAssert.AreEqual(new[] { Ldso }, asked);
        Assert.AreEqual(ElfLoader.InterpreterBase, image.InterpreterBias);
        Assert.AreEqual(ElfLoader.InterpreterBase + 0x1010, image.Entry, "Einsprung beim Interpreter");
        Assert.AreEqual(ElfLoader.PieBase, image.LoadBias);
        Assert.AreEqual(ElfLoader.PieBase + 0x5000, image.BreakStart, "brk hinter dem Programm, nicht hinter dem Interpreter");
        Assert.AreSame(interp, image.Interpreter);

        Dictionary<ulong, byte[]> mem = Materialize(image, elf);
        StartStack stack = ParseStack(mem, image.StackPointer);
        Assert.AreEqual(ElfLoader.InterpreterBase, stack.Aux[AtBase]);
        Assert.AreEqual(ElfLoader.PieBase + 0x1000, stack.Aux[AtEntry], "AT_ENTRY ist das Programm");
        Assert.AreEqual(ElfLoader.PieBase + 64, stack.Aux[AtPhdr]);
        Assert.AreEqual(5UL, stack.Aux[AtPhnum]);

        // Seiten: Programm mit Programmbytes, Interpreter mit Interpreterbytes, .bss beider null.
        Assert.AreEqual(elf[Page + 5], ReadByte(mem, ElfLoader.PieBase + 0x1005));
        Assert.AreEqual(interp[Page + 5], ReadByte(mem, ElfLoader.InterpreterBase + 0x1005));
        Assert.AreEqual(interp[3 * Page + 0x20], ReadByte(mem, ElfLoader.InterpreterBase + 0x3020));
        Assert.AreEqual(0, ReadByte(mem, ElfLoader.InterpreterBase + 0x3100), ".bss des Interpreters");
        Assert.AreNotEqual(elf[Page + 5], interp[Page + 5], "Muster unterscheiden sich");

        // Bereiche sortiert, überlappungsfrei, Interpreter ausführbar an seinem Einsprung.
        for (int i = 1; i < image.Regions.Count; i++)
        {
            Assert.IsTrue(image.Regions[i - 1].End <= image.Regions[i].Start, "sortiert bei " + i);
        }

        Assert.IsTrue(image.Regions.Any(r => r.Start <= image.Entry && image.Entry < r.End && r.Access == MapAccess.ReadExecute));
        Assert.AreEqual(3, image.Segments.Count(s => s.Interpreter));
    }

    [TestMethod]
    public void StaticHasNoInterpreter()
    {
        UserImage image = Load(Pie().Build());
        Assert.IsNull(image.Interpreter);
        Assert.AreEqual(0UL, image.InterpreterBias);
        StartStack stack = ParseStack(Materialize(image, Pie().Build()), image.StackPointer);
        Assert.AreEqual(0UL, stack.Aux[AtBase]);
        Assert.AreEqual(image.Entry, stack.Aux[AtEntry]);
    }

    [TestMethod]
    public void DynamicInterpreterMissingGivesErrno()
    {
        byte[] elf = Dynamic().Build();
        var none = Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], [], Options()));
        Assert.AreEqual(ElfLoadError.InterpreterUnavailable, none.Kind);
        Assert.AreEqual(2L, none.Errno, "ohne Dateisystem ENOENT");
        var denied = Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], [], DynOptions(null, -13)));
        Assert.AreEqual(ElfLoadError.InterpreterUnavailable, denied.Kind);
        Assert.AreEqual(13L, denied.Errno);
    }

    [TestMethod]
    public void DynamicRejectsBadInterpreter()
    {
        byte[] interp = Interp();
        void Rejected(byte[] elf, byte[] i)
        {
            var e = Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], [], DynOptions(i)));
            Assert.AreEqual(ElfLoadError.Invalid, e.Kind);
        }

        Rejected(Dynamic(terminate: false).Build(), interp);       // ohne abschließende Null
        Rejected(Dynamic("lib/ld.so").Build(), interp);           // relativ
        Rejected(Dynamic("").Build(), interp);                    // leer (nur die Null): zu kurz
        Rejected(Dynamic().Build(), Static().Build());            // Interpreter ET_EXEC
        Rejected(Dynamic().Build(), Dynamic().Build());           // Interpreter mit eigenem PT_INTERP
        Rejected(Dynamic().Build(), [1, 2, 3]);                   // kein ELF
        Rejected(Dynamic().Build(), []);                          // leere Datei: ungültiges Abbild (ENOEXEC), kein Speicherfehler
        ElfBuilder outside = Dynamic();
        outside.Headers[^1] = (ElfProgramHeader.Interpreter, R, 0x10_0000, 0, 10, 10);
        Rejected(outside.Build(), interp);                        // außerhalb der Datei

        // W^X im Interpreter gilt wie im Programm.
        ElfBuilder wx = Pie();
        wx.Headers[2] = (ElfProgramHeader.Load, R | W | X, Page, 0x1000, Page, Page);
        Rejected(Dynamic().Build(), wx.Build());

        // Programm und Interpreter zusammen über MaxImageSize.
        var small = new ElfLoadOptions { Random = FixedRandom, StackSize = 64 * 1024, MaxImageSize = 5 * Page, ReadInterpreter = DynOptions(interp).ReadInterpreter };
        Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(Dynamic().Build(), ["p"], [], small));
    }

    [TestMethod]
    public void DynamicExecutableOverlappingInterpreterBaseIsRejected()
    {
        // ET_EXEC mit PT_INTERP, dessen Segment auf der festen Interpreter-Basis 0x7F00_0000_0000 liegt: Überschneidung, kein stilles Überlagern.
        ElfBuilder b = Static();
        byte[] path = Encoding.UTF8.GetBytes(Ldso);
        path.CopyTo(b.Body, 0x300);
        b.Body[0x300 + path.Length] = 0;
        b.Add(ElfProgramHeader.Interpreter, R, 0x300, 0x300, (ulong)path.Length + 1, (ulong)path.Length + 1);
        b.Add(ElfProgramHeader.Load, R, 0, ElfLoader.InterpreterBase + 0x1000, 0x10, 0x10);
        var e = Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(b.Build(), ["p"], [], DynOptions(Interp())));
        Assert.AreEqual(ElfLoadError.Invalid, e.Kind);
        Assert.IsTrue(e.Message.Contains("überschneiden", StringComparison.Ordinal), e.Message);

        // Dasselbe Programm ohne das Segment auf der Basis lädt.
        ElfBuilder ok = Static();
        path.CopyTo(ok.Body, 0x300);
        ok.Body[0x300 + path.Length] = 0;
        ok.Add(ElfProgramHeader.Interpreter, R, 0x300, 0x300, (ulong)path.Length + 1, (ulong)path.Length + 1);
        UserImage image = ElfLoader.Load(ok.Build(), ["p"], [], DynOptions(Interp()));
        Assert.AreEqual(ElfLoader.InterpreterBase, image.InterpreterBias);
    }

    [TestMethod]
    public void RejectsBadArguments()
    {
        byte[] elf = Static().Build();
        // Eingabefehler aus argv/envp sind ElfLoadException mit Art, nie ArgumentException (Prepare fängt nur diese).
        Assert.AreEqual(ElfLoadError.InvalidArgument, Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["a\0b"], [], Options())).Kind);
        Assert.AreEqual(ElfLoadError.InvalidArgument, Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["a"], [null!], Options())).Kind);
        Assert.AreEqual(ElfLoadError.ArgumentsTooLong, Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, [new string('x', 20000)], [], Options())).Kind);
        Assert.ThrowsException<ArgumentException>(() => ElfLoader.Load(elf, ["a"], [], new ElfLoadOptions { Random = new byte[8] }));
        Assert.ThrowsException<ArgumentException>(() => ElfLoader.Load(elf, ["a"], [], new ElfLoadOptions { StackSize = Page + 1 }));
        UserImage image = Load(elf);
        Assert.ThrowsException<ArgumentException>(() => image.FillPage(new byte[elf.Length + 1], 0x400000, new byte[Page]));
        Assert.ThrowsException<ArgumentException>(() => image.FillPage(elf, 0x400001, new byte[Page]));
    }

    [TestMethod]
    public void ArgumentErrorsAreLoadExceptionsWithKind()
    {
        byte[] elf = Static().Build();
        ElfLoadException big = Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p", new string('x', 3 << 20)], [], Options(8UL << 20)));
        Assert.AreEqual(ElfLoadError.ArgumentsTooLong, big.Kind);
        Assert.IsTrue(big.Message.Contains("E2BIG", StringComparison.Ordinal));
        Assert.IsFalse(big.Message.Contains("xxxx", StringComparison.Ordinal), "keine Eingabe in der Meldung");

        string[] empties = new string[300_000];
        Array.Fill(empties, string.Empty);
        Assert.AreEqual(ElfLoadError.ArgumentsTooLong, Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, empties, [], Options(8UL << 20))).Kind);
        Assert.AreEqual(ElfLoadError.ArgumentsTooLong, Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], empties, Options(8UL << 20))).Kind);
        Assert.AreEqual(ElfLoadError.ArgumentsTooLong, Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], [new string('y', 3 << 20)], Options(8UL << 20))).Kind);

        // Auch execFn zählt zum Budget.
        Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], [], new ElfLoadOptions { Random = FixedRandom, StackSize = 64 * 1024, ExecFn = new string('z', 1 << 20) }));

        // NUL und null in argv und envp; der Text taucht nicht in der Meldung auf.
        ElfLoadException nul = Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["ge\0heim"], [], Options()));
        Assert.IsFalse(nul.Message.Contains("geheim", StringComparison.Ordinal));
        Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], ["A=1\0B=2"], Options()));
        Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, [null!], [], Options()));
        Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], ["A=1", null!], Options()));

        // Echter Grenzfall: größtes noch passendes Argument suchen (64 KiB Stack, Viertel = 16 KiB), das nächstlängere scheitert.
        int fit = 0;
        for (int step = 1 << 14; step > 0; step >>= 1)
        {
            try
            {
                Load(elf, ["p", new string('x', fit + step)]);
                fit += step;
            }
            catch (ElfLoadException)
            {
            }
        }

        Assert.IsTrue(fit > 8000 && fit < 16384, "Grenze " + fit);
        Assert.IsNotNull(Load(elf, ["p", new string('x', fit)]));
        Assert.AreEqual(ElfLoadError.ArgumentsTooLong, Assert.ThrowsException<ElfLoadException>(() => Load(elf, ["p", new string('x', fit + 1)])).Kind);
    }

    [TestMethod]
    public void ArgumentBudgetIsCheckedBeforeAllocating()
    {
        // Das Budget greift beim Umwandeln: Eine riesige Zeichenkette und Millionen leerer Einträge werden abgelehnt, ohne dass ihre
        // UTF-8-Form oder die Zeigertabelle angelegt wird (ohne frühe Prüfung wären das 64 MiB und 2,4 MB).
        byte[] elf = Static().Build();
        string huge = new('x', 64 << 20);
        string[] empties = new string[300_000];
        Array.Fill(empties, string.Empty);
        ElfLoadOptions options = Options();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p", huge], [], options));
        Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], empties, options));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < (1 << 20), "angelegt: " + allocated);
    }

    [TestMethod]
    public void SegmentEndsAtGuardPageBoundary()
    {
        // Grenze der Prüfung in Load: Segmentende höchstens an der Schutzseite unter dem Stack (die Mutationsprobe erreicht das nie).
        ulong guard = ElfLoader.UserEnd - (64 * 1024) - Page;
        Assert.IsNotNull(Load(Build(b => b.Add(ElfProgramHeader.Load, R | W, 0, guard - Page, 0x10, Page))));
        AssertRejected(Build(b => b.Add(ElfProgramHeader.Load, R | W, 0, guard - Page, 0x10, Page + 1)));
        AssertRejected(Build(b => b.Add(ElfProgramHeader.Load, R | W, 0, guard, 0x10, 1)));

        // Mit PIE-Verschiebung: Das Ende zählt nach der Verschiebung (bias = PieBase - niedrigste Seite).
        ulong limit = guard - ElfLoader.PieBase;
        Assert.IsNotNull(Load(PieWith(b => b.Add(ElfProgramHeader.Load, R | W, 0, limit - Page, 0x10, Page))));
        AssertRejected(PieWith(b => b.Add(ElfProgramHeader.Load, R | W, 0, limit - Page, 0x10, Page + 1)));
    }

    private static byte[] PieWith(Action<ElfBuilder> change)
    {
        ElfBuilder b = Pie();
        change(b);
        return b.Build();
    }

    [TestMethod]
    public void MissingRandomSourceIsRejected()
    {
        byte[] elf = Static().Build();
        ElfLoadException e = Assert.ThrowsException<ElfLoadException>(() => ElfLoader.Load(elf, ["p"], [], new ElfLoadOptions { StackSize = 64 * 1024 }));
        Assert.AreEqual(ElfLoadError.NoRandom, e.Kind);
    }

    // Eine Tabelle aus 1024 oder 1025 Köpfen: erster Kopf PT_LOAD für die ganze Tabelle, dann Auffüllung mit PT_NULL, Code dahinter.
    private static byte[] ManyHeaders(int count)
    {
        var b = new ElfBuilder { Body = new byte[0x12000] };
        b.Add(ElfProgramHeader.Load, R, 0, 0x400000, 0xF000, 0xF000);
        b.Add(ElfProgramHeader.Load, R | X, 0xF000, 0x410000, 0x1000, 0x1000);
        for (int i = 2; i < count; i++)
        {
            b.Add(0, 0, 0, 0, 0, 0);
        }

        b.Entry = 0x410000;
        return b.Build();
    }

    [TestMethod]
    public void ProgramHeaderCountLimitIs1024()
    {
        UserImage image = Load(ManyHeaders(1024));
        Assert.AreEqual(0x400000UL + 64, image.ProgramHeaderAddress);
        AssertRejected(ManyHeaders(1025));
    }

    [TestMethod]
    public void UnalignedProgramHeaderOffsetIsAccepted()
    {
        // Linux prüft e_phoff nicht auf Ausrichtung; die Köpfe werden byteweise gelesen. Der Lader lehnt deshalb nicht ab, die Adresse
        // in AT_PHDR folgt dem tatsächlichen Versatz.
        byte[] elf = Static().Build();
        int count = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(56));
        Array.Copy(elf, 64, elf, 67, count * 56);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(32), 67);
        UserImage image = Load(elf);
        Assert.AreEqual(0x400000UL + 67, image.ProgramHeaderAddress);
    }

    [TestMethod]
    public void ZeroSizedLoadIsIgnoredAndShortHeaderRejected()
    {
        UserImage image = Load(Build(b => b.Add(ElfProgramHeader.Load, R | W, 0, 0x7000_0000, 0, 0)));
        foreach (UserRegion r in image.Regions)
        {
            Assert.IsFalse(r.Start <= 0x7000_0000 && 0x7000_0000 < r.End);
        }

        // Nur Segmente mit memsz 0: nichts zu laden.
        AssertRejected(Build(b =>
        {
            b.Headers.Clear();
            b.Add(ElfProgramHeader.Load, R | X, 0, 0x401000, 0, 0);
        }));

        byte[] shortHeader = Static().Build();
        BinaryPrimitives.WriteUInt16LittleEndian(shortHeader.AsSpan(52), 63);
        AssertRejected(shortHeader);
    }

    [TestMethod]
    public void ProgramHeaderEntryNotPointingAtHeadersIsTrustedLikeLinux()
    {
        // Linux übernimmt p_vaddr von PT_PHDR ungeprüft in AT_PHDR; der Lader verlangt nur, dass die Köpfe lückenlos lesbar abgebildet sind.
        // Zeigt PT_PHDR woandershin (hier in die .bss), lädt das Programm; musl fände dort keine Köpfe, das schadet nur dem Programm selbst.
        UserImage image = Load(Build(b => b.Add(ElfProgramHeader.ProgramHeaders, R, 64, 0x404000, 56, 56)));
        Assert.AreEqual(0x404000UL, image.ProgramHeaderAddress);

        // Nicht abgebildet oder nicht ganz abgebildet: abgelehnt.
        AssertRejected(Build(b => b.Add(ElfProgramHeader.ProgramHeaders, R, 64, 0x500000, 56, 56)));
        AssertRejected(Build(b => b.Add(ElfProgramHeader.ProgramHeaders, R, 64, 0x405FF0, 56, 56)));
    }

    [TestMethod]
    public void SegmentWithoutFlagsAndSharedPageRights()
    {
        // flags 0: ein Bereich ohne Zugriff (wie PROT_NONE), nicht lesbar.
        UserImage none = Load(Build(b => b.Add(ElfProgramHeader.Load, 0, 0, 0x600000, 0x10, 0x1000)));
        Assert.IsTrue(none.Regions.Any(r => r.Start == 0x600000 && r.Access == MapAccess.None));

        // R- und RX-Segment in derselben Seite: Die Seite wird ausführbar (wie Linux ohne -z separate-code), also auch der Einsprung
        // ins Nur-Lesen-Segment. Dokumentiertes Verhalten (V1-Review P3-1); W bleibt nie in einer RX-Seite.
        var b2 = new ElfBuilder { Entry = 0x701900 };
        b2.Add(ElfProgramHeader.Load, R, 0, 0x400000, 0x200, 0x200);
        b2.Add(ElfProgramHeader.Load, R | X, Page, 0x701000, 0x800, 0x800);
        b2.Add(ElfProgramHeader.Load, R, 2 * Page, 0x701800, 0x300, 0x300);
        b2.Add(ElfProgramHeader.ProgramHeaders, R, 64, 64 + 0x400000, 4 * 56, 4 * 56);
        UserImage shared = Load(b2.Build());
        Assert.AreEqual(MapAccess.ReadExecute, shared.Regions.First(r => r.Start == 0x701000).Access);
        AssertRejected(Build(b => b.Add(ElfProgramHeader.Load, R | W, 0, 0x401800, 0x10, 0x10)));
    }

    [TestMethod]
    public void WorstCaseManySegmentsAndBigBssStaysFast()
    {
        // 1022 winzige Segmente je eine Seite auseinander plus ein RW-Segment fast in Höhe des Budgets (1 GiB). Der Lader braucht
        // keine zeitlich nennenswerte Arbeit (O(n^2) nur über die höchstens 1024 Köpfe). Das Abbilden pro Seite im Aufrufer
        // (LinuxUserProcess.Load) läuft über Seiten x Segmente und ist hier nicht Teil der Messung (offen im PR).
        var b = new ElfBuilder { Body = new byte[0x12000] };
        b.Add(ElfProgramHeader.Load, R, 0, 0x400000, 0xF000, 0xF000);
        b.Add(ElfProgramHeader.Load, R | X, 0xF000, 0x410000, 0x10, 0x10);
        b.Entry = 0x410000;
        for (int i = 0; i < 1020; i++)
        {
            b.Add(ElfProgramHeader.Load, R, 0x2000, 0x500000 + ((ulong)i * Page), 1, 1);
        }

        b.Add(ElfProgramHeader.Load, R | W, 0x10000, 0x1000_0000, 0x10, (1UL << 30) - (64UL << 20));
        byte[] elf = b.Build();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        UserImage image = ElfLoader.Load(elf, ["p"], [], Options());
        Assert.IsTrue(image.Reserve(new AddressSpaceMap(Page, ElfLoader.UserEnd)).IsOK());
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), "Laden dauerte " + watch.Elapsed);
    }

    private static readonly ulong[] Edge =
    [
        0, 1, 2, 7, 8, 0x7F, 0x80, 0xFF, 0xFFF, 0x1000, 0x1001, 0xFFFF, 0x1_0000, 0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFF,
        0x1_0000_0000, 0x1000_0000_0000, 0x5555_5555_4000, 0x5555_5555_5000, 0x7FFF_FFFF_E000, 0x7FFF_FFFF_F000, 0x7FFF_FFFF_FFFF,
        0x8000_0000_0000, 0x7FFF_FFFF_FFFF_FFFF, 0x8000_0000_0000_0000, 0x8000_0000_0000_0001, 0xFFFF_FFFF_FFFF_F000,
        0xFFFF_FFFF_FFFF_FFFE, 0xFFFF_FFFF_FFFF_FFFF, 0xFFFF_AAAA_AAAB_C000, 0x4000_0000,
    ];

    /// <summary>Prüft alle Invarianten eines geladenen Abbilds (siehe MutatedImagesKeepInvariants).</summary>
    private static void CheckInvariants(UserImage image, byte[] elf)
    {
        ulong guard = image.StackBottom - Page;
        UserRegion prev = default;
        bool first = true;
        foreach (UserRegion r in image.Regions)
        {
            Assert.IsTrue(r.Start % Page == 0 && r.End % Page == 0 && r.Start < r.End, "Bereich ausgerichtet");
            Assert.IsTrue(r.Start >= ElfLoader.MinAddress && r.End <= ElfLoader.UserEnd, "Bereich im Benutzerraum");
            if (!first)
            {
                Assert.IsTrue(prev.End <= r.Start, "sortiert und überlappungsfrei");
            }

            prev = r;
            first = false;
        }

        // W nie in einer RX-Seite: kein RW-Segment berührt einen RX-Bereich und umgekehrt.
        UserRegion[] regions = image.Regions.ToArray();
        ulong segmentsEnd = 0;
        foreach (UserSegment s in image.Segments)
        {
            Assert.IsTrue(s.Address >= ElfLoader.MinAddress && s.MemorySize <= ElfLoader.UserEnd - s.Address, "Segment im Benutzerraum");
            Assert.IsTrue(ElfFile.FitsInFile(s.FileOffset, s.FileSize, elf.Length) && s.FileSize <= s.MemorySize);
            ulong sStart = s.Address & ~(Page - 1), sEnd = (s.Address + s.MemorySize + Page - 1) & ~(Page - 1);
            segmentsEnd = Math.Max(segmentsEnd, sEnd);
            foreach (UserRegion r in regions)
            {
                if (r.Start < sEnd && r.End > sStart)
                {
                    Assert.IsFalse(s.Access == MapAccess.ReadWrite && r.Access == MapAccess.ReadExecute, "W in RX-Seite");
                    Assert.IsFalse(s.Access == MapAccess.ReadExecute && r.Access == MapAccess.ReadWrite, "X in RW-Seite");
                }
            }
        }

        UserRegion? entryRegion = regions.Cast<UserRegion?>().FirstOrDefault(r => image.Entry >= r!.Value.Start && image.Entry < r.Value.End);
        Assert.IsNotNull(entryRegion, "Einsprung abgebildet");
        Assert.AreEqual(MapAccess.ReadExecute, entryRegion!.Value.Access, "Einsprung in RX");

        Assert.IsTrue(image.BreakStart <= guard && image.BreakStart >= segmentsEnd && image.BreakStart % Page == 0, "brk vor der Schutzseite");
        Assert.AreEqual(0UL, image.StackPointer % 16);
        Assert.IsTrue(image.StackPointer > image.StackBottom && image.StackPointer < image.StackTop);

        var map = new AddressSpaceMap(Page, ElfLoader.UserEnd);
        Assert.IsTrue(image.Reserve(map).IsOK(), "Reserve in frischer Karte");

        // FillPage: Anfang, Ende und einige Seiten jedes Bereichs mit Zugriff (ein Bereich kann bis zu 1 GiB groß sein).
        byte[] page = new byte[Page];
        foreach (UserRegion r in regions)
        {
            if (r.Access == MapAccess.None)
            {
                continue;
            }

            ulong pages = (r.End - r.Start) / Page;
            for (ulong i = 0; i < Math.Min(pages, 6); i++)
            {
                image.FillPage(elf, r.Start + (i * Page), page);
                image.FillPage(elf, r.End - ((i + 1) * Page), page);
            }
        }
    }

    // Eine Mutation auf einer Kopie: Zufallsbyte, Feld mit Grenzwert, Typ/Flags, Kopffelder.
    private static void MutateOnce(byte[] elf, Random random)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(56));
        int phdr = 64 + (random.Next(Math.Min(count, 8)) * 56);
        switch (random.Next(5))
        {
            case 0:
                elf[random.Next(0, Math.Min(elf.Length, 64 + (count * 56)))] = (byte)random.Next(256);
                break;
            case 1:
                {
                    int[] fields = [8, 16, 24, 32, 40, 48];
                    BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(phdr + fields[random.Next(fields.Length)]), Edge[random.Next(Edge.Length)]);
                    break;
                }

            case 2:
                {
                    uint[] types = [0, 1, 2, 3, 4, 6, 7, 0x6474_E551, 0x6474_E550, 0x7FFF_FFFF, 0xFFFF_FFFF];
                    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(phdr), types[random.Next(types.Length)]);
                    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(phdr + 4), random.Next(3) == 0 ? (uint)random.Next() : (uint)random.Next(8));
                    break;
                }

            case 3:
                BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(random.Next(2) == 0 ? 24 : 32), Edge[random.Next(Edge.Length)]);
                break;
            default:
                if (random.Next(2) == 0)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(56), (ushort)random.Next(0, 80));
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16), (ushort)random.Next(0, 5));
                }

                break;
        }
    }

    /// <summary>
    /// Mutationsprobe mit fester Saat auf einer statischen und einer PIE-Grunddatei: Zufallsbytes, Felder mit Grenzwerten, Typ/Flags und
    /// Kopffelder. Das Ergebnis ist nie eine andere Ausnahme als <see cref="ElfLoadException"/>; was lädt, erfüllt alle Invarianten.
    /// </summary>
    [TestMethod]
    public void MutatedImagesKeepInvariants()
    {
        byte[][] bases = [Static().Build(), Pie().Build()];
        var random = new Random(20261126);
        int loaded = 0;
        for (int round = 0; round < 10_000; round++)
        {
            byte[] elf = (byte[])bases[round % 2].Clone();
            int changes = random.Next(1, 5);
            for (int c = 0; c < changes; c++)
            {
                MutateOnce(elf, random);
            }

            UserImage image;
            try
            {
                image = ElfLoader.Load(elf, ["p"], ["A=1"], Options());
            }
            catch (ElfLoadException)
            {
                continue;
            }

            loaded++;
            CheckInvariants(image, elf);
        }

        Assert.IsTrue(loaded > 100, "einige Verfälschungen bleiben ladbar: " + loaded);
    }

    private static ElfRangeReader Reader(byte[] file) => (offset, dst) =>
    {
        if (offset >= file.Length)
        {
            return 0;
        }

        int n = Math.Min(dst.Length, file.Length - (int)offset);
        file.AsSpan((int)offset, n).CopyTo(dst);
        return n;
    };

    [TestMethod]
    public void LoadedLengthEndsAtLastSegmentAndSkipsTrailingSections()
    {
        // V28: Debugabschnitte hinter dem letzten Segment (WebContent: 58 von 175 MB) werden nicht gelesen.
        ElfBuilder b = Static();
        byte[] full = b.Build();
        byte[] withDebug = new byte[full.Length + (8 * (int)Page)];
        full.CopyTo(withDebug, 0);
        Assert.AreEqual(0L, ElfFile.LoadedLength(withDebug.Length, Reader(withDebug), out long length));
        Assert.AreEqual((long)(3 * Page + 0x110), length);

        byte[] prefix = withDebug.AsSpan(0, (int)length).ToArray();
        UserImage fromPrefix = Load(prefix);
        UserImage fromFull = Load(withDebug);
        Dictionary<ulong, byte[]> a = Materialize(fromPrefix, prefix);
        Dictionary<ulong, byte[]> c = Materialize(fromFull, withDebug);
        CollectionAssert.AreEquivalent(c.Keys.ToList(), a.Keys.ToList());
        foreach (ulong page in c.Keys)
        {
            CollectionAssert.AreEqual(c[page], a[page], "Seite 0x" + page.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [TestMethod]
    public void LoadedLengthCoversInterpreterPathAndProgramHeaders()
    {
        ElfBuilder b = Static();
        b.Body = new byte[6 * Page];
        b.Add(ElfProgramHeader.Interpreter, R, 5 * Page, 0, 0x20, 0x20);
        b.PhOffOverride = null;
        byte[] elf = b.Build();
        Assert.AreEqual(0L, ElfFile.LoadedLength(elf.Length, Reader(elf), out long length));
        Assert.AreEqual((long)(5 * Page + 0x20), length);

        // Programmköpfe hinter allen Segmenten: die Tabelle gehört dazu.
        var late = new ElfBuilder { Body = new byte[4 * Page] };
        late.Add(ElfProgramHeader.Load, R | X, 0, 0x400000, 0x100, 0x100);
        byte[] lateElf = late.Build();
        byte[] moved = new byte[lateElf.Length];
        lateElf.CopyTo(moved, 0);
        lateElf.AsSpan(64, 56).CopyTo(moved.AsSpan(3 * (int)Page));
        BinaryPrimitives.WriteUInt64LittleEndian(moved.AsSpan(32), 3 * Page);
        Assert.AreEqual(0L, ElfFile.LoadedLength(moved.Length, Reader(moved), out long lateLength));
        Assert.AreEqual((long)(3 * Page + 56), lateLength);
    }

    [TestMethod]
    public void LoadedLengthFallsBackToWholeFileForBrokenHeaders()
    {
        // Was der Lader ablehnen muss, bekommt er ganz, damit seine Meldung (ENOEXEC) unverändert bleibt.
        byte[] text = Encoding.ASCII.GetBytes(new string('x', 200));
        Assert.AreEqual(0L, ElfFile.LoadedLength(text.Length, Reader(text), out long l1));
        Assert.AreEqual(200L, l1);

        byte[] overEnd = Seg(R | X, Page, 0x401000, 100 * Page, 100 * Page);
        Assert.AreEqual(0L, ElfFile.LoadedLength(overEnd.Length, Reader(overEnd), out long l2));
        Assert.AreEqual((long)overEnd.Length, l2);
        Assert.ThrowsException<ElfLoadException>(() => Load(overEnd));

        byte[] badTable = Build(b => b.PhOffOverride = ulong.MaxValue - 8);
        Assert.AreEqual(0L, ElfFile.LoadedLength(badTable.Length, Reader(badTable), out long l3));
        Assert.AreEqual((long)badTable.Length, l3);

        Assert.AreEqual(0L, ElfFile.LoadedLength(10, Reader(new byte[10]), out long l4));
        Assert.AreEqual(10L, l4);
    }

    [TestMethod]
    public void LoadedLengthPassesReadErrors()
    {
        byte[] elf = Static().Build();
        Assert.AreEqual(-5L, ElfFile.LoadedLength(elf.Length, (_, _) => -5, out _));
        int calls = 0;
        ElfRangeReader secondFails = (offset, dst) => ++calls == 1 ? Reader(elf)(offset, dst) : -5;
        Assert.AreEqual(-5L, ElfFile.LoadedLength(elf.Length, secondFails, out _));
    }

    private static byte[] Build(Action<ElfBuilder> change)
    {
        ElfBuilder b = Static();
        change(b);
        return b.Build();
    }

    private static byte[] Mutate(byte[] elf, Action<byte[]> change)
    {
        change(elf);
        return elf;
    }

    private static byte[] Seg(uint flags, ulong offset, ulong vaddr, ulong fileSize, ulong memSize, ulong entry = 0x401000)
    {
        var b = new ElfBuilder { Entry = entry };
        b.Add(ElfProgramHeader.Load, R, 0, 0x300000, 0x200, 0x200);
        b.Add(ElfProgramHeader.Load, flags, offset, vaddr, fileSize, memSize);
        return b.Build();
    }
}
