using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

[TestClass]
public sealed class LinuxUserProcessTests
{
    private const ulong Page = 4096;
    private const ulong Code = 0x401000, Data = 0x403000;
    private const ulong NotPresentRead = 0x4, NotPresentWrite = 0x6, NotPresentFetch = 0x14, PresentWrite = 0x7;

    /// <summary>Seitentabelle als Wörterbuch; zählt, was der Prozess abbildet und freigibt.</summary>
    private sealed class FakePages : IUserPages
    {
        public readonly Dictionary<ulong, byte[]> Frames = new();
        public readonly Dictionary<ulong, (ulong Frame, MapAccess Access)> Mapped = new();
        public readonly List<string> Events = new();
        public ulong Next = 0x100000;
        public int Limit = int.MaxValue;
        public bool RefuseUnmap;
        public bool RefuseMap;

        public ulong AllocateFrame()
        {
            if (Frames.Count >= Limit) return 0;
            ulong frame = Next;
            Next += Page;
            Frames[frame] = new byte[Page];
            return frame;
        }

        public void FreeFrame(ulong frame)
        {
            Assert.IsFalse(Mapped.Values.Any(m => m.Frame == frame), "Frame noch abgebildet");
            Assert.IsTrue(Frames.Remove(frame));
            Events.Add("free");
        }

        public Span<byte> Frame(ulong frame) => Frames[frame];

        public bool Map(ulong page, ulong frame, MapAccess access)
        {
            Assert.AreNotEqual(MapAccess.None, access);
            if (RefuseMap || Mapped.ContainsKey(page)) return false;
            Mapped[page] = (frame, access);
            return true;
        }

        public bool Unmap(ulong page)
        {
            if (RefuseUnmap && Mapped.ContainsKey(page)) return false;
            Mapped.Remove(page);
            return true;
        }

        public void DestroyTable()
        {
            Mapped.Clear();
            Events.Add("table");
        }
    }

    private sealed class Lines : ITextOutput
    {
        public readonly List<string> Text = new();
        public void WriteLine(string text) => Text.Add(text);
        public void Clear() => Text.Clear();
    }

    // Statisches Programm: Code (R X) bei 0x401000, Daten (R W) bei 0x403000 mit 0x10 Dateibytes und zwei Seiten .bss dahinter.
    private static byte[] Elf()
    {
        byte[] b = new byte[3 * (int)Page];
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F'; b[4] = 2; b[5] = 1; b[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), ElfFile.TypeExecutable);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), ElfFile.MachineX86_64);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), Code);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(54), ElfFile.ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), 3);
        Header(b, 0, 4, 0, 0x400000, 0x200, 0x200);              // Kopf, nur lesbar (AT_PHDR)
        Header(b, 1, 5, Page, Code, Page, Page);                  // Code
        Header(b, 2, 6, 2 * Page, Data, 0x10, 0x10 + 2 * Page);   // Daten + .bss
        for (int i = 0; i < (int)Page; i++) b[(int)Page + i] = 0xC3;
        for (int i = 0; i < 0x10; i++) b[2 * (int)Page + i] = (byte)(0xA0 + i);
        b[2 * (int)Page + 0x10] = 0xEE; // hinter FileSize: darf nicht im Speicher stehen
        return b;
    }

    private static void Header(byte[] b, int index, uint flags, ulong offset, ulong vaddr, ulong fileSize, ulong memSize)
    {
        Span<byte> p = b.AsSpan(64 + (index * 56), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(p, ElfProgramHeader.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(4), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(8), offset);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(16), vaddr);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(24), vaddr);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(32), fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(40), memSize);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(48), Page);
    }

    private static (LinuxUserProcess Process, FakePages Pages, UserImage Image, Lines Output) Start(ulong maxBytes = 64 << 20)
    {
        byte[] elf = Elf();
        UserImage image = ElfLoader.Load(elf, ["prog", "a1"], ["X=1"], new ElfLoadOptions { Random = new byte[16], StackSize = 64 * 1024 });
        var pages = new FakePages();
        var output = new Lines();
        var process = new LinuxUserProcess(pages, new TerminalDescriptor(output), 7, maxBytes);
        Assert.IsTrue(process.Load(image, elf).IsOK());
        return (process, pages, image, output);
    }

    [TestMethod]
    public void LoadMapsOnlyPagesWithContent()
    {
        var (process, pages, image, _) = Start();
        Assert.AreEqual(MapAccess.ReadExecute, pages.Mapped[Code].Access);
        Assert.AreEqual(0xC3, pages.Frames[pages.Mapped[Code].Frame][0]);
        Assert.AreEqual(MapAccess.Read, pages.Mapped[0x400000].Access);
        byte[] data = pages.Frames[pages.Mapped[Data].Frame];
        Assert.AreEqual(0xA0, data[0]);
        Assert.AreEqual(0, data[0x10], ".bss hinter den Dateibytes ist null");
        Assert.IsFalse(pages.Mapped.ContainsKey(Data + Page), "reine .bss-Seite erst bei Bedarf");
        Assert.IsTrue(pages.Mapped.ContainsKey(image.StackPointer & ~(Page - 1)));
        Assert.IsFalse(pages.Mapped.ContainsKey(image.StackBottom), "untere Stackseiten erst bei Bedarf");
        Assert.IsFalse(pages.Mapped.ContainsKey(image.StackBottom - Page), "Schutzseite nie");
        Assert.AreEqual(Data + 3 * Page, image.BreakStart);
        Assert.AreEqual(image.BreakStart, process.Brk);

        Span<byte> argc = stackalloc byte[8];
        Assert.IsTrue(process.TryRead(image.StackPointer, argc));
        Assert.AreEqual(2UL, BinaryPrimitives.ReadUInt64LittleEndian(argc));
    }

    [TestMethod]
    public void PageFaultsMapZeroPagesOnlyWithinRights()
    {
        var (process, pages, image, _) = Start();
        Assert.IsTrue(process.HandlePageFault(Data + Page + 8, NotPresentWrite));
        Assert.AreEqual(MapAccess.ReadWrite, pages.Mapped[Data + Page].Access);
        Assert.IsTrue(pages.Frames[pages.Mapped[Data + Page].Frame].All(x => x == 0));

        Assert.IsFalse(process.HandlePageFault(Code, PresentWrite), "Schreiben auf Code: Schutzverletzung");
        Assert.IsFalse(process.HandlePageFault(Data + 2 * Page, NotPresentFetch), "Ausführen in Daten");
        Assert.IsFalse(process.HandlePageFault(image.StackBottom - Page, NotPresentRead), "Schutzseite");
        Assert.IsFalse(process.HandlePageFault(0x10_0000_0000, NotPresentRead), "unbelegt");
        Assert.IsFalse(process.HandlePageFault(ElfLoader.UserEnd + 5, NotPresentRead), "über dem Benutzerraum");
        Assert.IsFalse(process.HandlePageFault(Data + Page, NotPresentWrite), "schon abgebildet");
        Assert.IsTrue(process.HandlePageFault(image.StackBottom, NotPresentWrite), "Stack wächst bei Bedarf");
    }

    [TestMethod]
    public void MemoryLimitStopsFaults()
    {
        int loaded = Start().Process.FrameCount;
        var (process, pages, _, _) = Start(maxBytes: (ulong)loaded * Page);
        Assert.IsFalse(process.HandlePageFault(Data + Page, NotPresentWrite));
        Assert.IsFalse(pages.Mapped.ContainsKey(Data + Page));
    }

    [TestMethod]
    public void UserMemoryChecksRightsAndIsAllOrNothing()
    {
        var (process, pages, image, _) = Start();
        byte[] two = new byte[0x20];
        Assert.IsTrue(process.TryRead(Data + Page - 0x10, two), "über die Seitengrenze in .bss");
        Assert.IsTrue(pages.Mapped.ContainsKey(Data + Page), "Kernelzugriff holt die Seite wie ein Fehler");
        Assert.IsFalse(process.TryWrite(Code, new byte[] { 1 }), "Code nicht schreibbar");
        Assert.IsTrue(process.TryRead(Code, new byte[1]));

        // Bereich endet im Unbelegten: nichts geschrieben.
        ulong end = Data + 3 * Page;
        Assert.IsFalse(process.TryWrite(end - 4, new byte[8] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        byte[] check = new byte[4];
        Assert.IsTrue(process.TryRead(end - 4, check));
        CollectionAssert.AreEqual(new byte[4], check);

        Assert.IsFalse(process.TryRead(ulong.MaxValue - 2, new byte[8]), "Überlauf");
        Assert.IsFalse(process.TryRead(image.StackBottom - 8, new byte[16]), "in die Schutzseite");
        Assert.IsTrue(process.TryRead(0, Span<byte>.Empty), "Länge 0");

        Assert.IsTrue(process.TryWrite(Data + 0x100, Encoding.UTF8.GetBytes("hallo\0")));
        Assert.IsTrue(process.TryReadCString(Data + 0x100, 16, out string s));
        Assert.AreEqual("hallo", s);
        Assert.IsFalse(process.TryReadCString(Data + 0x100, 3, out _), "zu lang");
        Assert.IsTrue(process.TryWrite(end - 2, "ab"u8));
        Assert.IsFalse(process.TryReadCString(end - 2, 16, out _), "ohne NUL ins Unbelegte");
    }

    [TestMethod]
    public void BrkGrowsShrinksAndZeroesOnRegrow()
    {
        var (process, pages, image, _) = Start();
        ulong start = process.Brk;
        Assert.IsFalse(process.TrySetBrk(start - 1));
        Assert.IsFalse(process.TrySetBrk(ElfLoader.UserEnd + Page));
        Assert.IsTrue(process.TrySetBrk(start + 3 * Page + 5));
        Assert.AreEqual(start + 3 * Page + 5, process.Brk);
        Assert.IsTrue(process.TryWrite(start + 3 * Page, new byte[] { 9 }));
        int frames = process.FrameCount;
        Assert.IsTrue(process.TrySetBrk(start + Page));
        Assert.AreEqual(frames - 1, process.FrameCount);
        Assert.IsFalse(pages.Mapped.ContainsKey(start + 3 * Page));
        Assert.IsFalse(process.TryRead(start + 3 * Page, new byte[1]));
        Assert.IsTrue(process.TrySetBrk(start + 4 * Page));
        byte[] one = new byte[1];
        Assert.IsTrue(process.TryRead(start + 3 * Page, one));
        Assert.AreEqual(0, one[0], "neu gewachsen: genullt");
        Assert.IsTrue(process.TrySetBrk(start));
        Assert.AreEqual(start, process.Brk);

        // brk stößt an den Stack: abgelehnt, der Wert bleibt.
        Assert.IsFalse(process.TrySetBrk(image.StackBottom));
        Assert.AreEqual(start, process.Brk);
    }

    [TestMethod]
    public void AnonymousMappingsProtectAndUnmap()
    {
        var (process, pages, _, _) = Start();
        ulong a = process.MapAnonymous(2 * Page, 3);
        Assert.AreEqual(LinuxUserProcess.MmapBase, a);
        Assert.AreEqual(0UL, process.MapAnonymous(Page, 7), "W^X");
        Assert.AreEqual(0UL, process.MapAnonymous(100, 3), "Länge nicht ausgerichtet");
        Assert.AreEqual(0UL, process.MapAnonymous(0, 3));
        Assert.IsTrue(process.TryWrite(a + Page - 1, new byte[] { 1, 2 }));

        Assert.IsTrue(process.Protect(a, 2 * Page, 1));
        Assert.AreEqual(MapAccess.Read, pages.Mapped[a].Access);
        Assert.IsFalse(process.TryWrite(a, new byte[] { 1 }));
        Assert.IsFalse(process.HandlePageFault(a, PresentWrite));

        Assert.IsTrue(process.Protect(a, Page, 0));
        Assert.IsFalse(pages.Mapped.ContainsKey(a), "PROT_NONE: nicht abgebildet");
        Assert.IsFalse(process.TryRead(a, new byte[1]));
        Assert.IsFalse(process.HandlePageFault(a, NotPresentRead), "PROT_NONE bleibt gesperrt");

        Assert.IsTrue(process.Protect(a, 2 * Page, 3));
        byte[] back = new byte[2];
        Assert.IsTrue(process.TryRead(a + Page - 1, back));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, back, "Inhalt über die Schutzwechsel erhalten");

        Assert.IsFalse(process.Protect(a, 2 * Page, 6), "W^X");
        Assert.IsFalse(process.Protect(a, 3 * Page, 1), "Lücke");
        Assert.IsFalse(process.Protect(ulong.MaxValue & ~(Page - 1), Page, 1));

        int frames = process.FrameCount;
        Assert.IsTrue(process.Unmap(a, 2 * Page));
        Assert.AreEqual(frames - 2, process.FrameCount);
        Assert.IsFalse(process.TryRead(a, new byte[1]));
        Assert.IsTrue(process.Unmap(a, 2 * Page), "Unbelegtes freigeben geht wie bei Linux");
        Assert.IsFalse(process.Unmap(ElfLoader.UserEnd, Page));
        Assert.AreEqual(a, process.MapAnonymous(Page, 1), "frei gewordener Platz wird wieder genutzt");
    }

    [TestMethod]
    public void MapFixedErsetztUndDiscardNullt()
    {
        var (process, _, _, _) = Start();
        ulong a = process.MapAnonymous(2 * Page, 3);
        Assert.IsTrue(process.TryWrite(a, new byte[] { 9 }));
        Assert.IsTrue(process.TryWrite(a + Page, new byte[] { 8 }));

        Assert.IsTrue(process.Discard(a, Page));
        var b = new byte[1];
        Assert.IsTrue(process.TryRead(a, b));
        Assert.AreEqual(0, b[0], "verworfene Seite liest sich als Null");
        Assert.IsTrue(process.TryRead(a + Page, b));
        Assert.AreEqual(8, b[0], "Nachbarseite bleibt");

        // MAP_FIXED über belegtem Bereich: ersetzt, Inhalt weg; auch über Freiem.
        Assert.IsTrue(process.MapFixed(a + Page, Page, 3));
        Assert.IsTrue(process.TryRead(a + Page, b));
        Assert.AreEqual(0, b[0]);
        ulong free = a + 16 * Page;
        Assert.IsTrue(process.MapFixed(free, Page, 3));
        Assert.IsTrue(process.TryWrite(free, new byte[] { 1 }));
        Assert.IsFalse(process.MapFixed(free + 1, Page, 3), "nicht ausgerichtet");
        Assert.IsFalse(process.MapFixed(ElfLoader.UserEnd, Page, 3), "außerhalb");
        Assert.IsFalse(process.MapFixed(free, Page, 7), "W^X");
        Assert.IsFalse(process.Discard(ElfLoader.UserEnd, Page));
    }

    [TestMethod]
    public void RefusedUnmapKeepsTheFrame()
    {
        var (process, pages, _, _) = Start();
        ulong a = process.MapAnonymous(Page, 3);
        Assert.IsTrue(process.TryWrite(a, new byte[] { 1 }));
        pages.RefuseUnmap = true;
        int frames = process.FrameCount;
        Assert.IsTrue(process.Unmap(a, Page));
        Assert.AreEqual(frames, process.FrameCount, "abgebildet: nicht frei");

        // Die Seite bleibt gesperrt: kein neuer Bereich mit dem alten Frame, kein Kernelzugriff, Fehler brechen ab.
        ulong b = process.MapAnonymous(Page, 3);
        Assert.AreNotEqual(a, b, "gesperrte Seite nicht wieder vergeben");
        Assert.IsFalse(process.TryRead(a, new byte[1]));
        Assert.IsFalse(process.HandlePageFault(a, NotPresentRead));
        Assert.IsFalse(process.Protect(a, Page, 1), "Tabelle lehnt weiter ab");
        Assert.IsFalse(process.TryRead(a, new byte[1]), "Karte bleibt bei None, wie die Tabelle sie sieht");

        // Gibt die Tabelle die Seite später frei, gehen Frame und Sperre mit dem nächsten munmap.
        pages.RefuseUnmap = false;
        Assert.IsTrue(process.Unmap(a, Page));
        Assert.AreEqual(frames - 1, process.FrameCount);
        Assert.AreEqual(a, process.MapAnonymous(Page, 3));
        byte[] fresh = new byte[1];
        Assert.IsTrue(process.TryRead(a, fresh));
        Assert.AreEqual(0, fresh[0], "neuer Bereich: genullter Frame");
        process.Destroy();
        Assert.AreEqual(0, pages.Frames.Count);
    }

    [TestMethod]
    public void ProtectKeepsMapAndTableInStep()
    {
        var (process, pages, _, _) = Start();
        ulong a = process.MapAnonymous(Page, 3);
        Assert.IsTrue(process.TryWrite(a, new byte[] { 9 }));

        // Tabelle gibt nicht frei: die Karte behält den alten Zugriff, der Kernel darf weiter schreiben wie das Programm.
        pages.RefuseUnmap = true;
        Assert.IsFalse(process.Protect(a, Page, 1));
        Assert.AreEqual(MapAccess.ReadWrite, pages.Mapped[a].Access);
        Assert.IsTrue(process.TryWrite(a, new byte[] { 8 }));
        pages.RefuseUnmap = false;

        // Neu abbilden scheitert: Seite unabgebildet, Frame bleibt; der nächste Fehler bildet mit dem neuen Zugriff nach.
        pages.RefuseMap = true;
        Assert.IsFalse(process.Protect(a, Page, 1));
        Assert.IsFalse(pages.Mapped.ContainsKey(a));
        Assert.IsFalse(process.TryWrite(a, new byte[] { 7 }), "Karte sagt nur lesen");
        pages.RefuseMap = false;
        Assert.IsFalse(process.HandlePageFault(a, NotPresentWrite), "Schreiben bleibt verboten");
        Assert.IsTrue(process.HandlePageFault(a, NotPresentRead));
        Assert.AreEqual(MapAccess.Read, pages.Mapped[a].Access);
        byte[] back = new byte[1];
        Assert.IsTrue(process.TryRead(a, back));
        Assert.AreEqual(8, back[0], "Inhalt erhalten");
    }

    [TestMethod]
    public void UnmapChecksBeforeFreeing()
    {
        var (process, _, _, _) = Start();
        ulong a = process.MapAnonymous(2 * Page, 3);
        Assert.IsTrue(process.TryWrite(a, new byte[] { 5 }));
        int frames = process.FrameCount;
        Assert.IsFalse(process.Unmap(a + 1, Page), "unausgerichtet");
        Assert.IsFalse(process.Unmap(a, Page + 1), "Länge unausgerichtet");
        Assert.IsFalse(process.Unmap(a, 0));
        Assert.AreEqual(frames, process.FrameCount, "nichts freigegeben");
        byte[] back = new byte[1];
        Assert.IsTrue(process.TryRead(a, back));
        Assert.AreEqual(5, back[0]);
        Assert.IsTrue(process.Unmap(0, Page), "unter MinAddress: wie Linux ohne Wirkung");
        Assert.IsTrue(process.Unmap(0, a + Page), "über MinAddress hinweg");
        Assert.IsFalse(process.TryRead(a, back), "freigegeben");
        Assert.IsTrue(process.TryRead(a + Page, back), "dahinter unverändert");
    }

    [TestMethod]
    public void DestroyFreesTableBeforeFrames()
    {
        var (process, pages, _, _) = Start();
        Assert.IsTrue(process.HandlePageFault(Data + Page, NotPresentWrite));
        process.Destroy();
        Assert.AreEqual("table", pages.Events[0]);
        Assert.AreEqual(0, pages.Frames.Count);
        Assert.AreEqual(0, process.FrameCount);
    }

    [TestMethod]
    public void SyscallsReachTerminalExitAndFsBase()
    {
        var (process, _, _, output) = Start();
        int? exited = null;
        ulong fs = 0;
        process.OnExit = code => exited = code;
        process.OnFsBase = value => fs = value;
        Assert.IsTrue(process.TryWrite(Data + 0x200, Encoding.UTF8.GetBytes("Hallo\nWelt")));

        var write = new SyscallArgs { Number = LinuxSyscalls.Write, A0 = 1, A1 = Data + 0x200, A2 = 10 };
        Assert.AreEqual(10L, LinuxSyscalls.Dispatch(ref write, process));
        Assert.AreEqual("Hallo", string.Join("|", output.Text));
        var bad = new SyscallArgs { Number = LinuxSyscalls.Write, A0 = 3, A1 = Data, A2 = 1 };
        Assert.AreEqual(-LinuxSyscalls.EBADF, LinuxSyscalls.Dispatch(ref bad, process));
        var fault = new SyscallArgs { Number = LinuxSyscalls.Write, A0 = 2, A1 = 0x1000, A2 = 1 };
        Assert.AreEqual(-LinuxSyscalls.EFAULT, LinuxSyscalls.Dispatch(ref fault, process));

        var setFs = new SyscallArgs { Number = LinuxSyscalls.ArchPrctl, A0 = 0x1002, A1 = Data + 0x300 };
        Assert.AreEqual(0L, LinuxSyscalls.Dispatch(ref setFs, process));
        Assert.AreEqual(Data + 0x300, fs);

        var pid = new SyscallArgs { Number = LinuxSyscalls.Getpid };
        Assert.AreEqual(7L, LinuxSyscalls.Dispatch(ref pid, process));

        var exit = new SyscallArgs { Number = LinuxSyscalls.ExitGroup, A0 = 0x1_03 };
        LinuxSyscalls.Dispatch(ref exit, process);
        Assert.AreEqual(3, exited);
        Assert.AreEqual(3, process.ExitCode);
    }

    private static long Call(LinuxUserProcess process, long number, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0)
    {
        var args = new SyscallArgs { Number = number, A0 = a0, A1 = a1, A2 = a2, A3 = a3 };
        return LinuxSyscalls.Dispatch(ref args, process);
    }

    private static ulong PutString(LinuxUserProcess process, ulong at, string text)
    {
        Assert.IsTrue(process.TryWrite(at, Encoding.UTF8.GetBytes(text + "\0")));
        return at;
    }

    [TestMethod]
    public void FilesThroughDispatcherUseOwnTableAndFileSystem()
    {
        // V8: der echte Prozess als ILinuxFiles mit dem V7-Dateisystem (Arbeitsspeicher): mkdir, chdir, open relativ, write, dup,
        // close, erneut lesen. Ohne die Verdrahtung wäre jeder dieser Aufrufe -ENOSYS.
        var (process, _, _, _) = Start();
        Assert.IsInstanceOfType<ILinuxFiles>(process);
        Assert.AreEqual("/", process.Cwd);
        const long Read = 0, Write = 1, Open = 2, Close = 3, Dup = 32, Getcwd = 79, Chdir = 80, Mkdir = 83;
        const ulong Buf = Data + 0x800;
        Assert.AreEqual(0L, Call(process, Mkdir, PutString(process, Data + 0x100, "d"), 0x1ED));
        Assert.AreEqual(0L, Call(process, Chdir, PutString(process, Data + 0x100, "d")));
        Assert.AreEqual("/d", process.Cwd);

        long fd = Call(process, Open, PutString(process, Data + 0x100, "f.txt"), (ulong)(LinuxFileSystem.OWrOnly | LinuxFileSystem.OCreat), 0x1A4);
        Assert.AreEqual(3L, fd, "kleinste freie Nummer nach dem Terminal");
        Assert.IsTrue(process.TryWrite(Buf, "abc"u8));
        Assert.AreEqual(3L, Call(process, Write, (ulong)fd, Buf, 3));
        long copy = Call(process, Dup, (ulong)fd);
        Assert.AreEqual(4L, copy);
        Assert.AreSame(process.GetDescriptor(3), process.GetDescriptor(4));
        Assert.AreEqual(0L, Call(process, Close, (ulong)fd));
        Assert.AreEqual(0L, Call(process, Close, (ulong)copy));
        Assert.AreEqual(-LinuxSyscalls.EBADF, Call(process, Close, (ulong)copy));

        long again = Call(process, Open, PutString(process, Data + 0x100, "/d/f.txt"), (ulong)LinuxFileSystem.ORdOnly);
        Assert.AreEqual(3L, again, "geschlossene Nummer wird wieder vergeben");
        Assert.AreEqual(3L, Call(process, Read, (ulong)again, Buf + 0x100, 16));
        byte[] got = new byte[3];
        Assert.IsTrue(process.TryRead(Buf + 0x100, got));
        Assert.AreEqual("abc", Encoding.UTF8.GetString(got));
        Assert.AreEqual(3L, Call(process, Getcwd, Buf + 0x200, 64), "Länge mit NUL");
    }

    [TestMethod]
    public void DescriptorTableBoundsAndTerminalStay()
    {
        var (process, _, _, output) = Start();
        Assert.IsNull(process.GetDescriptor(-1));
        Assert.IsNull(process.GetDescriptor(LinuxUserProcess.MaxDescriptors));
        Assert.IsNull(process.GetDescriptor(3));
        ILinuxDescriptor terminal = process.GetDescriptor(1)!;
        Assert.AreSame(terminal, process.GetDescriptor(0));
        Assert.AreSame(terminal, process.GetDescriptor(2));

        Assert.AreEqual(10, process.AddDescriptor(terminal, 10));
        for (int fd = 3; fd < LinuxUserProcess.MaxDescriptors; fd++)
        {
            if (fd != 10)
            {
                Assert.AreEqual(fd, process.AddDescriptor(terminal, 0));
            }
        }

        Assert.AreEqual((int)-LinuxErrno.EMFILE, process.AddDescriptor(terminal, 0));
        Assert.AreEqual((int)-LinuxErrno.EMFILE, process.AddDescriptor(terminal, LinuxUserProcess.MaxDescriptors));
        Assert.IsFalse(process.RemoveDescriptor(-1));
        Assert.IsFalse(process.RemoveDescriptor(LinuxUserProcess.MaxDescriptors));
        Assert.IsTrue(process.RemoveDescriptor(1));
        Assert.IsFalse(process.RemoveDescriptor(1));
        Assert.AreEqual(1, process.AddDescriptor(terminal, 0));

        // close(1) schließt das Terminal nicht: Es gehört dem Prozess, fd 2 schreibt weiter.
        Assert.AreEqual(0L, Call(process, 3, 1));
        Assert.IsTrue(process.TryWrite(Data + 0x200, "x\n"u8));
        Assert.AreEqual(2L, Call(process, 1, 2, Data + 0x200, 2));
        Assert.AreEqual("x", output.Text.Last());
        process.Destroy();
    }

    [TestMethod]
    public void DestroyClosesEachOpenFileOnce()
    {
        var store = new MemoryFileStore();
        var output = new Lines();
        var terminal = new TerminalDescriptor(output);
        var fileSystem = new LinuxFileSystem(store, terminal);
        byte[] elf = Elf();
        UserImage image = ElfLoader.Load(elf, ["prog"], [], new ElfLoadOptions { Random = new byte[16], StackSize = 64 * 1024 });
        var pages = new FakePages();
        var process = new LinuxUserProcess(pages, terminal, 7, 64 << 20, fileSystem: fileSystem);
        Assert.IsTrue(process.Load(image, elf).IsOK());
        Assert.AreSame(fileSystem, process.FileSystem);

        long a = Call(process, 2, PutString(process, Data + 0x100, "/a"), (ulong)(LinuxFileSystem.ORdWr | LinuxFileSystem.OCreat), 0x1A4);
        long b = Call(process, 2, PutString(process, Data + 0x100, "/b"), (ulong)(LinuxFileSystem.ORdWr | LinuxFileSystem.OCreat), 0x1A4);
        Assert.AreEqual(7L, Call(process, 33, (ulong)a, 7), "dup2 auf eine hohe Nummer");
        Assert.AreEqual(2, fileSystem.OpenFiles);
        var file = (ILinuxFile)process.GetDescriptor((int)a)!;

        process.Destroy();
        Assert.AreEqual(0, fileSystem.OpenFiles, "jede Datei einmal geschlossen, auch die geteilte");
        Assert.IsNull(process.GetDescriptor((int)a));
        Assert.IsNull(process.GetDescriptor((int)b));
        Assert.IsNull(process.GetDescriptor(7));
        Assert.IsNull(process.GetDescriptor(1), "Tabelle ganz leer");
        Assert.AreEqual(-LinuxErrno.EBADF, file.Write("x"u8), "geschlossen");
        Assert.AreEqual(0, pages.Frames.Count, "Speicher trotzdem frei");
    }

    private static long Sys(LinuxUserProcess process, long number, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
    {
        var a = new SyscallArgs { Number = number, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
        return LinuxSyscalls.Dispatch(ref a, process);
    }

    private static bool Write64(LinuxUserProcess process, ulong addr, ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        return process.TryWrite(addr, b);
    }

    private static bool Read64(LinuxUserProcess process, ulong addr, out ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        bool ok = process.TryRead(addr, b);
        value = BinaryPrimitives.ReadUInt64LittleEndian(b);
        return ok;
    }

    [TestMethod]
    public void MuslStackmessungFindetDenGanzenStack()
    {
        // Nachbau von musl pthread_getattr_np für den Hauptthread (src/thread/pthread_getattr_np.c): ab der Seite über auxv
        // Seite um Seite nach unten mremap(p-l-PAGE, PAGE, 2*PAGE, 0), solange -ENOMEM kommt.
        var (process, _, image, _) = Start();
        ulong p = (image.StackPointer + Page - 1) & ~(Page - 1);
        ulong l = Page;
        while (Sys(process, LinuxSyscalls.Mremap, p - l - Page, Page, 2 * Page) == -LinuxSyscalls.ENOMEM)
        {
            l += Page;
        }

        // Die Schutzseite unter dem Stack ist eine Abbildung (PROT_NONE) und zählt mit: eine Seite mehr als bei Linux.
        Assert.AreEqual(p - image.StackBottom + Page, l);
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Sys(process, LinuxSyscalls.Mremap, image.StackBottom - 2 * Page, Page, 2 * Page));
    }

    [TestMethod]
    public void RlimitStackIstDerHauptstack()
    {
        var (process, _, _, _) = Start();
        ulong buf = (ulong)Sys(process, LinuxSyscalls.Mmap, 0, Page, 3, 0x22, ulong.MaxValue);
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Prlimit64, 0, 3, 0, buf));
        Assert.IsTrue(Read64(process, buf, out ulong cur));
        Assert.IsTrue(Read64(process, buf + 8, out ulong max));
        Assert.AreEqual(64UL * 1024, cur);
        Assert.AreEqual(64UL * 1024, max);
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Getrlimit, 7, buf));
        Assert.IsTrue(Read64(process, buf, out cur));
        Assert.AreEqual(1024UL, cur);
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Getrlimit, 9, buf)); // RLIMIT_AS
        Assert.IsTrue(Read64(process, buf, out cur));
        Assert.AreEqual(ulong.MaxValue, cur);
        Assert.AreEqual(-LinuxSyscalls.EPERM, Sys(process, LinuxSyscalls.Setrlimit, 3, buf));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.Getrlimit, 16, buf));
        Assert.AreEqual(-LinuxSyscalls.ESRCH, Sys(process, LinuxSyscalls.Prlimit64, 99, 3, 0, buf));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Sys(process, LinuxSyscalls.Getrlimit, 3, 0x10));
    }

    [TestMethod]
    public void RlimitGleicherWertGelingtUndNullzeigerIstEfault()
    {
        // Review V9 P3 a/b: setrlimit auf den geltenden Wert ist 0; prlimit64 schreibt old nur bei Erfolg (kernel/sys.c).
        var (process, _, _, _) = Start();
        ulong buf = (ulong)Sys(process, LinuxSyscalls.Mmap, 0, Page, 3, 0x22, ulong.MaxValue);
        ulong old = buf + 64;
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Getrlimit, 7, buf));
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Setrlimit, 7, buf)); // NOFILE Soft = Hard = 1024
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Prlimit64, 0, 7, buf, old));
        Assert.IsTrue(Read64(process, old + 8, out ulong max));
        Assert.AreEqual(1024UL, max);

        Assert.IsTrue(Write64(process, buf, 512));
        Assert.IsTrue(Write64(process, old, 7));
        Assert.AreEqual(-LinuxSyscalls.EPERM, Sys(process, LinuxSyscalls.Prlimit64, 0, 7, buf, old)); // Senken: Abweichung
        Assert.IsTrue(Read64(process, old, out ulong untouched));
        Assert.AreEqual(7UL, untouched);

        Assert.AreEqual(-LinuxSyscalls.EFAULT, Sys(process, LinuxSyscalls.Getrlimit, 7, 0));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Sys(process, LinuxSyscalls.Setrlimit, 7, 0));
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Prlimit64, 0, 7, 0, 0)); // prlimit64: beide Zeiger optional
    }

    [TestMethod]
    public void MremapVerkleinertNurAnOrtUndStelle()
    {
        var (process, _, _, _) = Start();
        ulong m = (ulong)Sys(process, LinuxSyscalls.Mmap, 0, 3 * Page, 3, 0x22, ulong.MaxValue);
        Assert.AreEqual((long)m, Sys(process, LinuxSyscalls.Mremap, m, 3 * Page, 3 * Page));
        Assert.AreEqual(-LinuxSyscalls.ENOMEM, Sys(process, LinuxSyscalls.Mremap, m, 3 * Page, 4 * Page, 1)); // MAYMOVE: Abweichung
        Assert.AreEqual((long)m, Sys(process, LinuxSyscalls.Mremap, m, 3 * Page, Page + 1));
        Assert.IsTrue(process.IsMapped(m, 2 * Page));
        Assert.IsFalse(process.IsMapped(m + 2 * Page, Page));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Sys(process, LinuxSyscalls.Mremap, m, 3 * Page, Page));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.Mremap, m + 1, Page, Page));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.Mremap, m, Page, Page, 2)); // FIXED ohne MAYMOVE
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.Mremap, m, Page, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.Mremap, m, 0, Page, 1)); // Review V9 P3 c: oldSize 0 privat
    }

    [TestMethod]
    public void AffinitaetUndThreadname()
    {
        var (process, _, _, _) = Start();
        ulong buf = (ulong)Sys(process, LinuxSyscalls.Mmap, 0, Page, 3, 0x22, ulong.MaxValue);
        Assert.AreEqual(8L, Sys(process, LinuxSyscalls.SchedGetaffinity, 0, 128, buf));
        Assert.IsTrue(Read64(process, buf, out ulong mask));
        Assert.AreEqual(1UL, mask);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.SchedGetaffinity, 0, 4, buf));
        Assert.AreEqual(-LinuxSyscalls.ESRCH, Sys(process, LinuxSyscalls.SchedGetaffinity, 99, 8, buf));
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Prctl, 15, buf)); // PR_SET_NAME
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Sys(process, LinuxSyscalls.Prctl, 15, 0x10));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.Prctl, 0x53564d41, 0)); // PR_SET_VMA (mimalloc)
    }

    [TestMethod]
    public void FileCallsWaitForFileGateTerminalWriteDoesNot()
    {
        // V11: Dateiaufrufe laufen unter FileGate; hält ein anderer Thread sie, wartet open. Das Terminal schreibt ohne Sperre,
        // sonst hielte eine volle Ausgabe alle Dateiaufrufe auf.
        var (process, _, _, output) = Start();
        const long Write = 1, Open = 2;
        ulong path = PutString(process, Data + 0x100, "/f");
        Assert.IsTrue(process.TryWrite(Data + 0x200, "hi\n"u8));
        Task<long> open;
        lock (process.FileGate)
        {
            open = Task.Run(() => Call(process, Open, path, (ulong)(LinuxFileSystem.OWrOnly | LinuxFileSystem.OCreat), 0x1A4));
            Assert.IsFalse(open.Wait(200), "open darf nicht an der gehaltenen Dateisperre vorbei");
            Task<long> tty = Task.Run(() => Call(process, Write, 1, Data + 0x200, 3));
            Assert.IsTrue(tty.Wait(5000), "Terminal braucht die Dateisperre nicht");
            Assert.AreEqual(3L, tty.Result);
        }

        Assert.IsTrue(open.Wait(5000));
        Assert.AreEqual(3L, open.Result);
        Assert.AreEqual(0L, Call(process, 3, 3), "close");
        CollectionAssert.Contains(output.Text, "hi");
    }

    [TestMethod]
    public void ConcurrentFileCallsKeepTableAndFileSystemConsistent()
    {
        // V11: vier Threads öffnen, beschreiben, dup-en und schließen gleichzeitig. Ohne Sperre teilen sie die Arbeitslisten der
        // Pfadauflösung, vergeben dieselbe Nummer doppelt oder schließen eine Datei zweimal; danach müssen Tabelle und
        // Dateibudget leer sein.
        var (process, _, _, _) = Start();
        const long Write = 1, Open = 2, Close = 3, Dup = 32, Mkdir = 83;
        Assert.AreEqual(0L, Call(process, Mkdir, PutString(process, Data + 0x100, "/d"), 0x1ED));
        const int Threads = 4, Rounds = 300;
        var errors = new List<string>();
        var workers = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int k = t;
            ulong path = PutString(process, Data + 0x100 + (ulong)k * 0x100, "/d/f" + k);
            ulong buf = Data + 0x1000 + (ulong)k * 0x100;
            Assert.IsTrue(process.TryWrite(buf, "x"u8));
            workers[t] = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < Rounds; i++)
                    {
                        long fd = Call(process, Open, path, (ulong)(LinuxFileSystem.OWrOnly | LinuxFileSystem.OCreat | LinuxFileSystem.OAppend), 0x1A4);
                        long copy = Call(process, Dup, (ulong)fd);
                        long w = Call(process, Write, (ulong)copy, buf, 1);
                        long c1 = Call(process, Close, (ulong)fd);
                        long c2 = Call(process, Close, (ulong)copy);
                        if (fd < 3 || copy < 3 || w != 1 || c1 != 0 || c2 != 0)
                        {
                            lock (errors)
                            {
                                errors.Add($"t{k} i{i}: fd {fd} dup {copy} write {w} close {c1}/{c2}");
                            }

                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    lock (errors)
                    {
                        errors.Add($"t{k}: {e.GetType().Name} {e.Message}");
                    }
                }
            });
        }

        foreach (Thread w in workers)
        {
            w.Start();
        }

        foreach (Thread w in workers)
        {
            Assert.IsTrue(w.Join(60000));
        }

        Assert.AreEqual(0, errors.Count, string.Join("; ", errors.Take(5)));
        for (int fd = 3; fd < LinuxUserProcess.MaxDescriptors; fd++)
        {
            Assert.IsNull(process.GetDescriptor(fd), "fd " + fd);
        }

        Assert.AreEqual(0, ((LinuxFileSystem)process.FileSystem).OpenFiles, "jede Datei genau einmal geschlossen");
        long stat = Call(process, 4, PutString(process, Data + 0x100, "/d/f0"), Data + 0x1800); // stat
        Assert.AreEqual(0L, stat);
        byte[] size = new byte[8];
        Assert.IsTrue(process.TryRead(Data + 0x1800 + 48, size));
        Assert.AreEqual((long)Rounds, BitConverter.ToInt64(size), "jedes Anhängen kam an");
    }

    // V12: Dateiabbildung als Kopie und /proc/self/exe.
    private const ulong MapAnon = 0x22, MapSharedFlag = 0x01, MapPrivateFlag = 0x02, ProtR = 1, ProtRW = 3;

    private static (LinuxUserProcess Process, byte[] Content) StartWithFile(int size) => StartWithFile(size, new FakePages());

    private static (LinuxUserProcess Process, byte[] Content) StartWithFile(int size, FakePages pages)
    {
        var store = new MemoryFileStore();
        var terminal = new TerminalDescriptor(new Lines());
        byte[] elf = Elf();
        UserImage image = ElfLoader.Load(elf, ["prog"], [], new ElfLoadOptions { Random = new byte[16], StackSize = 64 * 1024 });
        var process = new LinuxUserProcess(pages, terminal, 7, 64 << 20, fileSystem: new LinuxFileSystem(store, terminal));
        Assert.IsTrue(process.Load(image, elf).IsOK());
        byte[] content = new byte[size];
        for (int i = 0; i < size; i++)
        {
            content[i] = (byte)((i * 7) + (i >> 8));
        }

        ulong buf = (ulong)Sys(process, LinuxSyscalls.Mmap, 0, (ulong)size, ProtRW, MapAnon, ulong.MaxValue, 0);
        Assert.IsTrue(process.TryWrite(buf, content));
        long fd = Sys(process, 2, PutString(process, Data + 0x100, "/icu.dat"), (ulong)(LinuxFileSystem.OWrOnly | LinuxFileSystem.OCreat), 0x1A4);
        Assert.AreEqual((long)size, Sys(process, 1, (ulong)fd, buf, (ulong)size));
        Assert.AreEqual(0L, Sys(process, 3, (ulong)fd));
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Munmap, buf, (ulong)size));
        return (process, content);
    }

    private static long OpenIcu(LinuxUserProcess process, int flags) =>
        Sys(process, 2, PutString(process, Data + 0x100, "/icu.dat"), (ulong)flags, 0);

    [TestMethod]
    public void MmapDateiNurLesenGeteiltKopiertInhalt()
    {
        // Wie Ladybird MappedFile (ICU): open O_RDONLY, mmap PROT_READ MAP_SHARED, close; die Abbildung bleibt.
        var (process, content) = StartWithFile(5000);
        long fd = OpenIcu(process, LinuxFileSystem.ORdOnly);
        Assert.AreEqual(10L, Sys(process, 8, (ulong)fd, 10, 0), "Position vorher");
        long at = Sys(process, LinuxSyscalls.Mmap, 0, 5000, ProtR, MapSharedFlag, (ulong)fd, 0);
        Assert.IsTrue(at > 0, "mmap " + at);
        Assert.AreEqual(10L, Sys(process, 8, (ulong)fd, 0, 1), "Position unverändert wie bei Linux");
        Assert.AreEqual(0L, Sys(process, 3, (ulong)fd));

        byte[] got = new byte[2 * Page];
        Assert.IsTrue(process.TryRead((ulong)at, got));
        CollectionAssert.AreEqual(content, got.AsSpan(0, 5000).ToArray());
        Assert.IsTrue(got.AsSpan(5000).ToArray().All(b => b == 0), "hinter dem Dateiende Nullen");
        Assert.IsFalse(process.TryWrite((ulong)at, "x"u8), "PROT_READ bleibt nur lesbar");
        Assert.AreEqual(0L, Sys(process, LinuxSyscalls.Munmap, (ulong)at, 5000));
        process.Destroy();
    }

    [TestMethod]
    public void MmapDateiMitVersatzUndPrivatBeschreibbar()
    {
        var (process, content) = StartWithFile(3 * (int)Page + 100);
        long fd = OpenIcu(process, LinuxFileSystem.ORdWr);
        long at = Sys(process, LinuxSyscalls.Mmap, 0, Page + 100, ProtRW, MapPrivateFlag, (ulong)fd, 2 * Page);
        Assert.IsTrue(at > 0, "mmap " + at);
        byte[] got = new byte[Page + 100];
        Assert.IsTrue(process.TryRead((ulong)at, got));
        CollectionAssert.AreEqual(content.AsSpan(2 * (int)Page, (int)Page + 100).ToArray(), got);

        // Privat: Schreiben ändert nur die Kopie, nicht die Datei.
        Assert.IsTrue(process.TryWrite((ulong)at, "zz"u8));
        byte[] one = new byte[2];
        ulong buf = (ulong)Sys(process, LinuxSyscalls.Mmap, 0, Page, ProtRW, MapAnon, ulong.MaxValue, 0);
        Assert.AreEqual(2L, Sys(process, 17, (ulong)fd, buf, 2, 2 * Page));
        Assert.IsTrue(process.TryRead(buf, one));
        CollectionAssert.AreEqual(content.AsSpan(2 * (int)Page, 2).ToArray(), one);
        process.Destroy();
    }

    [TestMethod]
    public void MmapDateiFehler()
    {
        var (process, _) = StartWithFile(100);
        long ro = OpenIcu(process, LinuxFileSystem.ORdOnly);
        long wo = OpenIcu(process, LinuxFileSystem.OWrOnly);
        Assert.AreEqual(-LinuxSyscalls.ENODEV, Sys(process, LinuxSyscalls.Mmap, 0, Page, ProtRW, MapSharedFlag, (ulong)ro, 0), "geteilt beschreibbar");
        Assert.AreEqual(-LinuxSyscalls.EACCES, Sys(process, LinuxSyscalls.Mmap, 0, Page, ProtR, MapPrivateFlag, (ulong)wo, 0), "nur zum Schreiben offen");
        Assert.AreEqual(-LinuxSyscalls.ENODEV, Sys(process, LinuxSyscalls.Mmap, 0, Page, ProtR, MapPrivateFlag, 1, 0), "Terminal");
        Assert.AreEqual(-LinuxSyscalls.EBADF, Sys(process, LinuxSyscalls.Mmap, 0, Page, ProtR, MapPrivateFlag, 40, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.Mmap, 0, Page, ProtR, MapPrivateFlag, (ulong)ro, 100), "Versatz nicht seitenausgerichtet");
        process.Destroy();
    }

    [TestMethod]
    public void MmapDateiFrameMangelMittenInDerKopieIstFehler()
    {
        // Review Stufe c: Fehlt mitten in der Kopie ein Frame, darf mmap keinen Erfolg mit teilgenullter Abbildung melden.
        var pages = new FakePages();
        const int Pages = 20; // mehr als ein Kopierstück (64 KiB = 16 Seiten): das erste Stück gelingt, das zweite nicht
        var (process, _) = StartWithFile(Pages * (int)Page, pages);
        long fd = OpenIcu(process, LinuxFileSystem.ORdOnly);
        int before = pages.Frames.Count;
        pages.Limit = before + 17;
        Assert.AreEqual(-LinuxSyscalls.ENOMEM, Sys(process, LinuxSyscalls.Mmap, 0, Pages * Page, ProtR, MapPrivateFlag, (ulong)fd, 0));
        Assert.AreEqual(before, pages.Frames.Count, "teilgefüllte Abbildung freigegeben");
        pages.Limit = int.MaxValue;
        Assert.AreEqual(0L, Sys(process, 8, (ulong)fd, 0, 1), "Dateiposition unverändert");
        Assert.IsTrue(Sys(process, LinuxSyscalls.Mmap, 0, Pages * Page, ProtR, MapPrivateFlag, (ulong)fd, 0) > 0, "danach wieder möglich");
        process.Destroy();
    }

    [TestMethod]
    public void MmapDateiFixedAbgelehntAltbestandBleibt()
    {
        // Review Stufe c: Datei-MAP_FIXED darf den alten Bereich nicht entfernen, auch nicht bei einem Fehler wie O_WRONLY.
        const ulong MapFixedFlag = 0x10;
        var (process, _) = StartWithFile(100);
        long ro = OpenIcu(process, LinuxFileSystem.ORdOnly);
        long wo = OpenIcu(process, LinuxFileSystem.OWrOnly);
        ulong old = (ulong)Sys(process, LinuxSyscalls.Mmap, 0, 2 * Page, ProtRW, MapAnon, ulong.MaxValue, 0);
        Assert.IsTrue(process.TryWrite(old, "alt"u8));
        Assert.IsTrue(process.TryWrite(old + Page, "zwei"u8));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.Mmap, old, 2 * Page, ProtR, MapPrivateFlag | MapFixedFlag, (ulong)wo, 0), "nur zum Schreiben offen");
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Sys(process, LinuxSyscalls.Mmap, old, 2 * Page, ProtR, MapPrivateFlag | MapFixedFlag, (ulong)ro, 0), "lesbar");
        byte[] a = new byte[3], b = new byte[4];
        Assert.IsTrue(process.TryRead(old, a));
        Assert.IsTrue(process.TryRead(old + Page, b));
        Assert.AreEqual("alt", System.Text.Encoding.ASCII.GetString(a));
        Assert.AreEqual("zwei", System.Text.Encoding.ASCII.GetString(b));
        Assert.IsTrue(process.TryWrite(old, "neu"u8), "weiter beschreibbar");
        process.Destroy();
    }

    [TestMethod]
    public void ReadlinkProcSelfExe()
    {
        var (process, _) = StartWithFile(1);
        ulong buf = Data + 0x400;
        Assert.AreEqual(-LinuxErrno.ENOENT, Sys(process, 89, PutString(process, Data + 0x100, "/proc/self/exe"), buf, 64), "ohne Programmpfad");
        process.ExePath = "/bin/js";
        foreach (string link in new[] { "/proc/self/exe", "/proc/7/exe", "/proc/self/../self/exe" })
        {
            Assert.AreEqual(7L, Sys(process, 89, PutString(process, Data + 0x100, link), buf, 64), link);
            byte[] got = new byte[7];
            Assert.IsTrue(process.TryRead(buf, got));
            Assert.AreEqual("/bin/js", System.Text.Encoding.ASCII.GetString(got));
        }

        Assert.AreEqual(-LinuxErrno.ENOENT, Sys(process, 89, PutString(process, Data + 0x100, "/proc/8/exe"), buf, 64), "fremde PID");
        process.Destroy();
    }
}

[TestClass]
public sealed class TerminalDescriptorTests
{
    private sealed class Lines : ITextOutput
    {
        public readonly List<string> Text = new();
        public void WriteLine(string text) => Text.Add(text);
        public void Clear() => Text.Clear();
    }

    [TestMethod]
    public void WritesWholeLinesAndKeepsSplitCharacters()
    {
        var lines = new Lines();
        var terminal = new TerminalDescriptor(lines);
        byte[] text = Encoding.UTF8.GetBytes("Grüße\r\nzwei");
        Assert.AreEqual(3L, terminal.Write(text.AsSpan(0, 3)));      // "Gr" und das erste Byte von ü
        Assert.AreEqual(0, lines.Text.Count);
        Assert.AreEqual((long)text.Length - 3, terminal.Write(text.AsSpan(3)));
        Assert.AreEqual("Grüße", string.Join("|", lines.Text));
        terminal.Flush();
        Assert.AreEqual("Grüße|zwei", string.Join("|", lines.Text));
        terminal.Flush();
        Assert.AreEqual(2, lines.Text.Count, "nichts doppelt");
        terminal.Write(Encoding.UTF8.GetBytes("\n"));
        Assert.AreEqual("", lines.Text[2], "leere Zeile bleibt");
    }

    [TestMethod]
    public void ControlSequencesDoNotReachTheTerminal()
    {
        Assert.AreEqual("rot normal", TerminalDescriptor.Printable("\x1b[1;31mrot\x1b[0m normal"));
        Assert.AreEqual("> ", TerminalDescriptor.Printable("\x1b[2K\x1b[G> "), "Zeile löschen, Spalte 1");
        Assert.AreEqual("Text", TerminalDescriptor.Printable("\x1b]0;Titel\aText"));
        Assert.AreEqual("Text", TerminalDescriptor.Printable("\x1b]0;Titel\x1b\\Text"));
        Assert.AreEqual("100%", TerminalDescriptor.Printable("10%\r50%\r100%"), "\\r beginnt die Zeile neu");
        Assert.AreEqual("ende", TerminalDescriptor.Printable("ende\r"));
        Assert.AreEqual("ab", TerminalDescriptor.Printable("abc\b"));
        Assert.AreEqual("a\tb", TerminalDescriptor.Printable("a\u0007\u0000\tb\u007f"));
        Assert.AreEqual("x", TerminalDescriptor.Printable("x\x1b["), "abgeschnittene Folge");
        Assert.AreEqual("äö", TerminalDescriptor.Printable("äö"));
        Assert.AreEqual("ab", TerminalDescriptor.Printable("ab\r\x1b[0m"), "\\r vor Steuerfolgen ist noch Zeilenende (Review P3 a)");
        Assert.AreEqual("neu", TerminalDescriptor.Printable("alt\r\x1b[Kneu"), "\\r ESC[K Text");
        Assert.AreEqual("x", TerminalDescriptor.Printable("abc\r\bx"));
    }

    [TestMethod]
    public void ClosedTerminalDropsOutput()
    {
        var lines = new Lines();
        var mirrored = 0;
        var terminal = new TerminalDescriptor(lines) { Mirror = d => mirrored += d.Length };
        terminal.Write("a\n"u8);
        terminal.Close();
        Assert.AreEqual(2L, terminal.Write("b\n"u8));
        terminal.Flush();
        Assert.AreEqual("a", string.Join("|", lines.Text));
        Assert.AreEqual(2, mirrored);
    }

    [TestMethod]
    public void MirrorGetsRawBytes()
    {
        var lines = new Lines();
        var seen = new List<byte>();
        var terminal = new TerminalDescriptor(lines) { Mirror = data => seen.AddRange(data.ToArray()) };
        terminal.Write("a\r\nb"u8);
        Assert.AreEqual("a\r\nb", Encoding.UTF8.GetString(seen.ToArray()));
        Assert.AreEqual("a", string.Join("|", lines.Text));
    }

    [TestMethod]
    public void LongLinesAreCut()
    {
        var lines = new Lines();
        var terminal = new TerminalDescriptor(lines);
        terminal.Write(new byte[TerminalDescriptor.MaxLine + 1].Select(_ => (byte)'x').ToArray());
        Assert.AreEqual(1, lines.Text.Count);
        Assert.AreEqual(TerminalDescriptor.MaxLine, lines.Text[0].Length);
        terminal.Flush();
        Assert.AreEqual("x", lines.Text[1]);
    }

    [TestMethod]
    public void CutNeverSplitsAnEscapeOrACharacter()
    {
        // Review P3 b: Die Folge am Ende der vollen Zeile wandert ganz in die nächste.
        var lines = new Lines();
        var terminal = new TerminalDescriptor(lines);
        byte[] fill = Enumerable.Repeat((byte)'x', TerminalDescriptor.MaxLine - 3).ToArray();
        terminal.Write(fill);
        terminal.Write("\x1b[31mrot"u8);
        terminal.Flush();
        Assert.AreEqual(2, lines.Text.Count);
        Assert.AreEqual(TerminalDescriptor.MaxLine - 3, lines.Text[0].Length);
        Assert.AreEqual("rot", lines.Text[1], "kein \"[31m\" sichtbar");

        lines.Text.Clear();
        terminal.Write(Enumerable.Repeat((byte)'x', TerminalDescriptor.MaxLine - 1).ToArray());
        terminal.Write("ä!"u8);
        terminal.Flush();
        Assert.AreEqual(TerminalDescriptor.MaxLine - 1, lines.Text[0].Length);
        Assert.AreEqual("ä!", lines.Text[1], "UTF-8-Zeichen bleibt ganz");

        Assert.AreEqual(-1, TerminalDescriptor.IncompleteTail("ab\x1b[0m"u8.ToArray(), 6));
        Assert.AreEqual(2, TerminalDescriptor.IncompleteTail("ab\x1b[1;3"u8.ToArray(), 7));
        Assert.AreEqual(2, TerminalDescriptor.IncompleteTail("ab\x1b]0;Tit"u8.ToArray(), 9));
        Assert.AreEqual(-1, TerminalDescriptor.IncompleteTail("ab\x1b]0;T\a"u8.ToArray(), 8));
        Assert.AreEqual(2, TerminalDescriptor.IncompleteTail("ab\x1b"u8.ToArray(), 3));
        Assert.AreEqual(-1, TerminalDescriptor.IncompleteTail("ab\x1bM"u8.ToArray(), 4));
        Assert.AreEqual(1, TerminalDescriptor.IncompleteTail(new byte[] { (byte)'a', 0xe2, 0x82 }, 3));
        Assert.AreEqual(-1, TerminalDescriptor.IncompleteTail(new byte[] { (byte)'a', 0xe2, 0x82, 0xac }, 4));

        // Eine einzige Folge über die ganze Zeile geht so, statt endlos zu warten.
        lines.Text.Clear();
        terminal.Write("\x1b]"u8);
        terminal.Write(Enumerable.Repeat((byte)'t', TerminalDescriptor.MaxLine).ToArray());
        Assert.AreEqual(1, lines.Text.Count);
    }
}
