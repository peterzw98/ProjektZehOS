using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

[TestClass]
public sealed class LinuxUserProcessTests
{
    private static readonly byte[] One = [1];
    private static readonly string[] Hal = ["hal"];

    private sealed class FakePages : IUserPages
    {
        public readonly Dictionary<ulong, byte[]> Frames = new();
        public readonly Dictionary<ulong, MapAccess> Access = new();
        public bool Full;

        public bool Map(ulong page, MapAccess access)
        {
            if (Full) return false;
            if (!Frames.ContainsKey(page)) Frames[page] = new byte[4096];
            Access[page] = access;
            return true;
        }

        public bool IsMapped(ulong page) => Frames.ContainsKey(page);
        public void Protect(ulong page, MapAccess access) => Access[page] = access;
        public void Unmap(ulong page) { Frames.Remove(page); Access.Remove(page); }
        public void Read(ulong page, int offset, Span<byte> dst) => Frames[page].AsSpan(offset, dst.Length).CopyTo(dst);
        public void Write(ulong page, int offset, ReadOnlySpan<byte> src) => src.CopyTo(Frames[page].AsSpan(offset));
    }

    private sealed class Lines : ITextOutput
    {
        public readonly List<string> Items = new();
        public void WriteLine(string text) => Items.Add(text);
        public void Clear() => Items.Clear();
    }

    // Ein statisches ET_EXEC mit einer Lade-Seite (Text "hallo\n" bei 0x401000, lesbar/ausfuehrbar).
    private static byte[] Elf()
    {
        byte[] b = new byte[0x2000];
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F'; b[4] = 2; b[5] = 1; b[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), 62);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), 0x401000);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), 1);
        Span<byte> p = b.AsSpan(64, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(p, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(4), 5);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(16), 0x400000);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(24), 0x400000);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(32), 0x2000);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(40), 0x2000);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(48), 0x1000);
        Encoding.ASCII.GetBytes("hallo\nrest").CopyTo(b, 0x1000);
        return b;
    }

    private static (LinuxUserProcess P, FakePages Pages, Lines Out, int[] Exit) Start()
    {
        byte[] elf = Elf();
        UserImage image = ElfLoader.Load(elf, ["prog"], []);
        var pages = new FakePages();
        var output = new Lines();
        int[] exit = [-1];
        var p = new LinuxUserProcess(image, pages, output, 7, c => exit[0] = c);
        Assert.IsTrue(p.Load(elf).IsOK());
        return (p, pages, output, exit);
    }

    [TestMethod]
    public void LadenFuelltSeitenUndStack()
    {
        var (p, pages, _, _) = Start();
        Assert.AreEqual((byte)'h', pages.Frames[0x401000][0]);
        Assert.AreEqual(MapAccess.ReadExecute, pages.Access[0x401000]);
        Assert.IsTrue(pages.IsMapped(0x401000));
        Assert.IsTrue(p.Memory.TryReadCString(0x401000, 16, out string s) && s == "hallo\nrest");
    }

    [TestMethod]
    public void WriteGehtZeilenweiseAufsTerminal()
    {
        var (p, _, output, _) = Start();
        Assert.AreEqual(6, p.Syscall(LinuxSyscalls.Write, 1, 0x401000, 6, 0, 0, 0));
        Assert.AreEqual(1, output.Items.Count);
        Assert.AreEqual("hallo", output.Items[0]);
        Assert.AreEqual(-LinuxSyscalls.EBADF, p.Syscall(LinuxSyscalls.Write, 3, 0x401000, 6, 0, 0, 0));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, p.Syscall(LinuxSyscalls.Write, 2, 0x10, 6, 0, 0, 0));
    }

    [TestMethod]
    public void ExitSpuelRestzeileUndMeldetCode()
    {
        var (p, _, output, exit) = Start();
        p.Syscall(LinuxSyscalls.Write, 1, 0x401000, 3, 0, 0, 0); // "hal" ohne Zeilenende
        p.Syscall(LinuxSyscalls.ExitGroup, 3, 0, 0, 0, 0, 0);
        Assert.AreEqual(3, exit[0]);
        Assert.IsTrue(p.Exited);
        CollectionAssert.AreEqual(Hal, output.Items);
    }

    [TestMethod]
    public void BrkWaechstUndSchrumpft()
    {
        var (p, pages, _, _) = Start();
        ulong start = (ulong)p.Syscall(LinuxSyscalls.Brk, 0, 0, 0, 0, 0, 0);
        ulong grown = (ulong)p.Syscall(LinuxSyscalls.Brk, start + 0x3010, 0, 0, 0, 0, 0);
        Assert.AreEqual(start + 0x3010, grown);
        Assert.IsTrue(p.Memory.TryWrite(start + 0x3000, One));
        Assert.IsFalse(p.Memory.TryWrite(start + 0x4000, One));
        p.Syscall(LinuxSyscalls.Brk, start, 0, 0, 0, 0, 0);
        Assert.IsFalse(pages.IsMapped(start + 0x3000));
        Assert.AreEqual(start, (ulong)p.Syscall(LinuxSyscalls.Brk, start - 1, 0, 0, 0, 0, 0)); // unter dem Anfang: alter Wert
    }

    [TestMethod]
    public void MmapMunmapMprotect()
    {
        var (p, pages, _, _) = Start();
        long addr = p.Syscall(LinuxSyscalls.Mmap, 0, 8192, 3, 0x22, ulong.MaxValue, 0);
        Assert.IsTrue(addr > 0);
        Assert.IsFalse(pages.IsMapped((ulong)addr)); // erst beim Zugriff
        Assert.IsTrue(p.Memory.TryWrite((ulong)addr + 4096, One));
        Assert.AreEqual(0, p.Syscall(LinuxSyscalls.Mprotect, (ulong)addr, 8192, 1, 0, 0, 0));
        Assert.AreEqual(MapAccess.Read, pages.Access[(ulong)addr + 4096]);
        Assert.IsFalse(p.Memory.TryWrite((ulong)addr, One));
        Assert.AreEqual(0, p.Syscall(LinuxSyscalls.Munmap, (ulong)addr, 8192, 0, 0, 0, 0));
        Assert.IsFalse(pages.IsMapped((ulong)addr + 4096));
        Assert.IsFalse(p.Memory.TryRead((ulong)addr, new byte[1]));
        Assert.AreEqual(-LinuxSyscalls.ENOMEM, p.Syscall(LinuxSyscalls.Mmap, 0, 4096, 7, 0x22, ulong.MaxValue, 0)); // W+X
    }

    [TestMethod]
    public void SeitenfehlerLegtNurBelegteSeitenAn()
    {
        var (p, _, _, _) = Start();
        long addr = p.Syscall(LinuxSyscalls.Mmap, 0, 4096, 3, 0x22, ulong.MaxValue, 0);
        Assert.IsTrue(p.HandlePageFault((ulong)addr + 8, write: true, instructionFetch: false));
        Assert.IsFalse(p.HandlePageFault((ulong)addr + 8, write: true, instructionFetch: false)); // schon da
        Assert.IsFalse(p.HandlePageFault(0x10, write: false, instructionFetch: false));
        long ro = p.Syscall(LinuxSyscalls.Mmap, 0, 4096, 1, 0x22, ulong.MaxValue, 0);
        Assert.IsFalse(p.HandlePageFault((ulong)ro, write: true, instructionFetch: false));
    }

    [TestMethod]
    public void OhneSpeicherScheitertDerZugriff()
    {
        var (p, pages, _, _) = Start();
        long addr = p.Syscall(LinuxSyscalls.Mmap, 0, 4096, 3, 0x22, ulong.MaxValue, 0);
        pages.Full = true;
        Assert.IsFalse(p.Memory.TryWrite((ulong)addr, One));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, p.Syscall(LinuxSyscalls.Write, 1, (ulong)addr, 1, 0, 0, 0));
    }
}
