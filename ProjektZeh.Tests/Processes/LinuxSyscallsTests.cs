using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Tests.Processes;

[TestClass]
public sealed class LinuxSyscallsTests
{
    private const ulong Base = 0x10000;

    private sealed class FakeMemory : IUserMemory
    {
        public readonly byte[] Data = new byte[0x4000];

        public bool TryRead(ulong addr, Span<byte> dst)
        {
            if (!UserRange.Contains(Base, Base + (ulong)Data.Length, addr, (ulong)dst.Length)) return false;
            Data.AsSpan((int)(addr - Base), dst.Length).CopyTo(dst);
            return true;
        }

        public bool TryWrite(ulong addr, ReadOnlySpan<byte> src)
        {
            if (!UserRange.Contains(Base, Base + (ulong)Data.Length, addr, (ulong)src.Length)) return false;
            src.CopyTo(Data.AsSpan((int)(addr - Base)));
            return true;
        }

        public bool TryReadCString(ulong addr, int max, out string s)
        {
            s = "";
            return false;
        }
    }

    private sealed class Sink : ILinuxDescriptor
    {
        public readonly List<byte> Bytes = new();
        public long Write(ReadOnlySpan<byte> data) { Bytes.AddRange(data.ToArray()); return data.Length; }
    }

    private sealed class Proc : ILinuxProcess
    {
        public readonly FakeMemory Mem = new();
        public readonly Sink Out = new();
        public IUserMemory Memory => Mem;
        public ILinuxDescriptor? GetDescriptor(int fd) => fd == 1 ? Out : null;
        public ulong Brk { get; set; } = 0x400000UL;
        public bool TrySetBrk(ulong n) { if (n < 0x400000UL || n > 0x500000UL) return false; Brk = n; return true; }
        public ulong MapAnonymous(ulong len, int prot) { LastLen = len; return len > 0x1000000 ? 0UL : 0x7000_0000UL; }
        public ulong LastLen;
        public ulong UnmapLen, ProtectLen;
        public bool Unmap(ulong a, ulong l) { UnmapLen = l; return true; }
        public bool Protect(ulong a, ulong l, int p) { ProtectLen = l; return true; }
        public ulong FsBase { get; set; }
        public ulong ClearChildTid { get; set; }
        public int Pid => 7;
        public int? Exited;
        public void Exit(int code) => Exited = code;
    }

    private static long Call(Proc p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    [TestMethod]
    public void WriteSchreibtAufTerminal()
    {
        var p = new Proc();
        Encoding.ASCII.GetBytes("hallo\n").CopyTo(p.Mem.Data, 0x10);
        Assert.AreEqual(6, Call(p, LinuxSyscalls.Write, 1, Base + 0x10, 6));
        Assert.AreEqual("hallo\n", Encoding.ASCII.GetString(p.Out.Bytes.ToArray()));
    }

    [TestMethod]
    public void WriteFehler()
    {
        var p = new Proc();
        Assert.AreEqual(-LinuxSyscalls.EBADF, Call(p, LinuxSyscalls.Write, 5, Base, 1));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Write, 1, 0x10, 4));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Write, 1, ulong.MaxValue - 1, 8)); // Überlauf
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Write, 1, 0x10, 0));
    }

    [TestMethod]
    public void WriteTeilweiseUeberDasEndeLiefertGeschriebenes()
    {
        var p = new Proc();
        ulong end = Base + (ulong)p.Mem.Data.Length;
        // 8192 ab 4096 vor dem Ende: erstes Stück (4096) gelingt, zweites nicht.
        Assert.AreEqual(4096, Call(p, LinuxSyscalls.Write, 1, end - 4096, 8192));
    }

    [TestMethod]
    public void WritevFasstZusammen()
    {
        var p = new Proc();
        Encoding.ASCII.GetBytes("ab").CopyTo(p.Mem.Data, 0x100);
        Encoding.ASCII.GetBytes("cde").CopyTo(p.Mem.Data, 0x110);
        BitConverter.GetBytes(Base + 0x100).CopyTo(p.Mem.Data, 0);
        BitConverter.GetBytes(2UL).CopyTo(p.Mem.Data, 8);
        BitConverter.GetBytes(Base + 0x110).CopyTo(p.Mem.Data, 16);
        BitConverter.GetBytes(3UL).CopyTo(p.Mem.Data, 24);
        Assert.AreEqual(5, Call(p, LinuxSyscalls.Writev, 1, Base, 2));
        Assert.AreEqual("abcde", Encoding.ASCII.GetString(p.Out.Bytes.ToArray()));
    }

    [TestMethod]
    public void WritevGrenzen()
    {
        var p = new Proc();
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Writev, 1, Base, 1025));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Writev, 1, 0x10, 1));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Writev, 1, ulong.MaxValue - 7, 1));
        BitConverter.GetBytes(Base).CopyTo(p.Mem.Data, 0);
        BitConverter.GetBytes(ulong.MaxValue).CopyTo(p.Mem.Data, 8);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Writev, 1, Base, 1));
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Writev, 1, Base, 0));
    }

    [TestMethod]
    public void ExitGroupMeldetCode()
    {
        var p = new Proc();
        Call(p, LinuxSyscalls.ExitGroup, 3);
        Assert.AreEqual(3, p.Exited);
    }

    [TestMethod]
    public void BrkLiefertAltenWertBeiFehler()
    {
        var p = new Proc();
        Assert.AreEqual(0x400000, Call(p, LinuxSyscalls.Brk, 0));
        Assert.AreEqual(0x410000, Call(p, LinuxSyscalls.Brk, 0x410000));
        Assert.AreEqual(0x410000, Call(p, LinuxSyscalls.Brk, 0x900000));
        Assert.AreEqual(0x410000, Call(p, LinuxSyscalls.Brk, 0x1000));
    }

    [TestMethod]
    public void MmapAnonymUndFehler()
    {
        var p = new Proc();
        Assert.AreEqual(0x7000_0000, Call(p, LinuxSyscalls.Mmap, 0, 5000, 3, 0x22, ulong.MaxValue, 0));
        Assert.AreEqual(8192UL, p.LastLen);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Mmap, 0, 0, 3, 0x22, ulong.MaxValue, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Mmap, 0, 4096, 3, 0x20, ulong.MaxValue, 0)); // weder PRIVATE noch SHARED
        Assert.AreEqual(-LinuxSyscalls.EBADF, Call(p, LinuxSyscalls.Mmap, 0, 4096, 3, 0x02, ulong.MaxValue, 0));
        Assert.AreEqual(-LinuxSyscalls.ENODEV, Call(p, LinuxSyscalls.Mmap, 0, 4096, 3, 0x02, 3, 0));
        Assert.AreEqual(-LinuxSyscalls.ENOMEM, Call(p, LinuxSyscalls.Mmap, 0, 1UL << 40, 3, 0x22, ulong.MaxValue, 0));
        Assert.AreEqual(-LinuxSyscalls.ENOMEM, Call(p, LinuxSyscalls.Mmap, 0, ulong.MaxValue - 5, 3, 0x22, ulong.MaxValue, 0));
    }

    [TestMethod]
    public void MunmapMprotectPruefenLaenge()
    {
        var p = new Proc();
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Munmap, 0x7000_0000, 5000));
        Assert.AreEqual(8192UL, p.UnmapLen);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Munmap, 0x7000_0000, ulong.MaxValue));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Munmap, 0x7000_0000, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Munmap, 0x7000_0001, 4096));
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Mprotect, 0x7000_0000, 5000, 3));
        Assert.AreEqual(8192UL, p.ProtectLen);
        p.ProtectLen = 0;
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Mprotect, 0x7000_0000, 0, 3));
        Assert.AreEqual(0UL, p.ProtectLen);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Mprotect, 0x7000_0000, 1UL << 48, 3));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Mprotect, 0x7000_0000, 4096, 0x100));
    }

    [TestMethod]
    public void MmapFdIstInt()
    {
        var p = new Proc();
        Assert.AreEqual(-LinuxSyscalls.EBADF, Call(p, LinuxSyscalls.Mmap, 0, 4096, 3, 0x02, 0xffff_ffffUL, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Mmap, 0, 4096, 0x100, 0x22, ulong.MaxValue, 0));
    }

    [TestMethod]
    public void ArchPrctlSetztUndLiestFs()
    {
        var p = new Proc();
        Assert.AreEqual(0, Call(p, LinuxSyscalls.ArchPrctl, 0x1002, 0x7000_1000));
        Assert.AreEqual(0x7000_1000UL, p.FsBase);
        Assert.AreEqual(0, Call(p, LinuxSyscalls.ArchPrctl, 0x1003, Base));
        Assert.AreEqual(0x7000_1000UL, BitConverter.ToUInt64(p.Mem.Data, 0));
        Assert.AreEqual(-LinuxSyscalls.EPERM, Call(p, LinuxSyscalls.ArchPrctl, 0x1002, 0x0000_8000_0000_0000UL));
        Assert.AreEqual(-LinuxSyscalls.EPERM, Call(p, LinuxSyscalls.ArchPrctl, 0x1002, 0xffff_8000_0000_0000UL)); // Kernelhälfte
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.ArchPrctl, 0x1003, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.ArchPrctl, 0x9999, 0));
    }

    [TestMethod]
    public void SetTidAddressUndUnbekannt()
    {
        var p = new Proc();
        Assert.AreEqual(7, Call(p, LinuxSyscalls.SetTidAddress, 0x1234));
        Assert.AreEqual(0x1234UL, p.ClearChildTid);
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, Call(p, 9999));
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, Call(p, LinuxSyscalls.Rseq));
    }

    [TestMethod]
    public void UserRangeUeberlauf()
    {
        Assert.IsFalse(UserRange.Contains(0x1000, 0x9000, 0x8000, ulong.MaxValue));
        Assert.IsFalse(UserRange.Contains(0x1000, 0x9000, ulong.MaxValue, 2));
        Assert.IsTrue(UserRange.Contains(0x1000, 0x9000, 0x8000, 0x1000));
        Assert.IsFalse(UserRange.Contains(0x1000, 0x9000, 0x8000, 0x1001));
    }
}
