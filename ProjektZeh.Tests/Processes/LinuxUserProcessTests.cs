using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
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
}
