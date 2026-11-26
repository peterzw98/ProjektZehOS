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

    private sealed class Proc : ILinuxProcess, ILinuxHost
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
        public LinuxSignalState Signals { get; } = new();
        public long RealtimeNanos => 1_700_000_000_123_456_789L;
        public long MonotonicNanos => 5_000_000_007L;
        public int RandomCalls;
        public bool RandomFails;
        public long SleptNanos;
        public void SleepNanos(long ns) { SleptNanos += ns; }
        public bool TryFillRandom(Span<byte> d) { RandomCalls++; if (RandomFails) return false; for (int i = 0; i < d.Length; i++) d[i] = (byte)(i * 31 + 7); return true; }
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

    private const ulong Buf = Base + 0x100;

    [TestMethod]
    public void ClockGettimeLiefertSekundenUndNanosekunden()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.ClockGettime, 0, Buf));
        Assert.AreEqual(1_700_000_000L, BitConverter.ToInt64(p.Mem.Data, 0x100));
        Assert.AreEqual(123_456_789L, BitConverter.ToInt64(p.Mem.Data, 0x108));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.ClockGettime, 1, Buf));
        Assert.AreEqual(5L, BitConverter.ToInt64(p.Mem.Data, 0x100));
        Assert.AreEqual(7L, BitConverter.ToInt64(p.Mem.Data, 0x108));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.ClockGettime, 8, Buf));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.ClockGettime, 0, 8));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.ClockGettime, 0, Base + 0x4000 - 8));
    }

    [TestMethod]
    public void GetrandomFuelltInStuecken()
    {
        var p = new Proc();
        Assert.AreEqual(10000L, Call(p, LinuxSyscalls.Getrandom, Buf, 10000, 0));
        Assert.IsTrue(p.RandomCalls >= 3);
        Assert.AreEqual(7, p.Mem.Data[0x100]);
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Getrandom, Buf, 0, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Getrandom, Buf, 8, 8));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Getrandom, Buf, 8, 6));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Getrandom, 8, 8, 0));
        p.RandomFails = true;
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, Call(p, LinuxSyscalls.Getrandom, Buf, 8, 1));
    }

    [TestMethod]
    public void UnameMeldetLinuxX8664()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Uname, Buf));
        Assert.AreEqual("Linux", Encoding.ASCII.GetString(p.Mem.Data, 0x100, 5));
        Assert.AreEqual(0, p.Mem.Data[0x100 + 5]);
        Assert.AreEqual("x86_64", Encoding.ASCII.GetString(p.Mem.Data, 0x100 + 4 * 65, 6));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Uname, Base + 0x4000 - 100));
    }

    [TestMethod]
    public void SigactionMerktSichHandlerUndLehntKillAb()
    {
        var p = new Proc();
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x100, 8), 0xdead0000UL);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x108, 8), 4UL);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x118, 8), ulong.MaxValue);
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.RtSigaction, 11, Buf, 0, 8));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.RtSigaction, 11, 0, Buf + 0x40, 8));
        Assert.AreEqual(0xdead0000UL, BitConverter.ToUInt64(p.Mem.Data, 0x140));
        Assert.AreEqual(4UL, BitConverter.ToUInt64(p.Mem.Data, 0x148));
        Assert.AreEqual(ulong.MaxValue & ~((1UL << 8) | (1UL << 18)), BitConverter.ToUInt64(p.Mem.Data, 0x158));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.RtSigaction, 9, Buf, 0, 8));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.RtSigaction, 9, 0, Buf + 0x40, 8)); // Abfragen ist erlaubt
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.RtSigaction, 0, Buf, 0, 8));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.RtSigaction, 65, Buf, 0, 8));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.RtSigaction, 11, Buf, 0, 16));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.RtSigaction, 11, 8, 0, 8));
    }

    [TestMethod]
    public void SigprocmaskBlockUnblockSetmask()
    {
        var p = new Proc();
        void SetIn(ulong v) => BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x100, 8), v);
        ulong Old() => BitConverter.ToUInt64(p.Mem.Data, 0x140);
        SetIn(0b1100);
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.RtSigprocmask, 0, Buf, Buf + 0x40, 8));
        Assert.AreEqual(0UL, Old());
        SetIn(0b0100);
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.RtSigprocmask, 1, Buf, Buf + 0x40, 8));
        Assert.AreEqual(0b1100UL, Old());
        SetIn(ulong.MaxValue);
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.RtSigprocmask, 2, Buf, Buf + 0x40, 8));
        Assert.AreEqual(0b1000UL, Old());
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.RtSigprocmask, 0, 0, Buf + 0x40, 8));
        Assert.AreEqual(ulong.MaxValue & ~((1UL << 8) | (1UL << 18)), Old()); // 9 und 19 bleiben frei
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.RtSigprocmask, 3, Buf, 0, 8));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.RtSigprocmask, 0, Buf, 0, 4));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.RtSigprocmask, 0, 8, 0, 8));
        Assert.AreEqual(new Proc().Pid, 7); // anderer Prozess hat eigenen Zustand
        var q = new Proc();
        Assert.AreEqual(0L, Call(q, LinuxSyscalls.RtSigprocmask, 0, 0, Buf + 0x40, 8));
        Assert.AreEqual(0UL, BitConverter.ToUInt64(q.Mem.Data, 0x140));
    }

    [TestMethod]
    public void SigaltstackSetzenAbfragenAbschalten()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Sigaltstack, 0, Buf + 0x40));
        Assert.AreEqual(2, BitConverter.ToInt32(p.Mem.Data, 0x148)); // SS_DISABLE
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x100, 8), 0x7000_0000UL);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x108, 4), 0);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x110, 8), 8192UL);
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Sigaltstack, Buf, 0));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Sigaltstack, 0, Buf + 0x40));
        Assert.AreEqual(0x7000_0000UL, BitConverter.ToUInt64(p.Mem.Data, 0x140));
        Assert.AreEqual(8192UL, BitConverter.ToUInt64(p.Mem.Data, 0x150));
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x110, 8), 100UL);
        Assert.AreEqual(-LinuxSyscalls.ENOMEM, Call(p, LinuxSyscalls.Sigaltstack, Buf, 0));
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x108, 4), 5);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Sigaltstack, Buf, 0));
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x108, 4), 2);
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Sigaltstack, Buf, 0));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Sigaltstack, 0, Buf + 0x40));
        Assert.AreEqual(2, BitConverter.ToInt32(p.Mem.Data, 0x148));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Sigaltstack, 8, 0));
    }

    [TestMethod]
    public void SchedYieldIstNull()
    {
        Assert.AreEqual(0L, Call(new Proc(), LinuxSyscalls.SchedYield));
    }

    [TestMethod]
    public void PollMeldetSchreibbereitschaftUndUngueltigeFds()
    {
        var p = new Proc(); // nur fd 1 ist offen
        void Entry(int i, int fd, short events) { BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x100 + i * 8, 4), fd); BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x104 + i * 8, 2), events); BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x106 + i * 8, 2), (short)99); }
        Entry(0, 1, 4 | 1);
        Entry(1, 5, 1);
        Entry(2, -1, 1);
        Assert.AreEqual(2L, Call(p, LinuxSyscalls.Poll, Buf, 3, ulong.MaxValue));
        Assert.AreEqual((short)4, BitConverter.ToInt16(p.Mem.Data, 0x106));
        Assert.AreEqual((short)0x20, BitConverter.ToInt16(p.Mem.Data, 0x10e));
        Assert.AreEqual((short)0, BitConverter.ToInt16(p.Mem.Data, 0x116));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Poll, 0, 0, 0));
        Assert.AreEqual(0L, p.SleptNanos); // Frist 0: kein Schlafen
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Poll, 0, 0, 25));
        Assert.AreEqual(25_000_000L, p.SleptNanos); // keine bereiten Eintraege: Frist abschlafen
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Poll, 0, 0, ulong.MaxValue));
        Assert.AreEqual(25_000_000L, p.SleptNanos); // -1 schlaeft nie
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Poll, Buf, 1025, 0));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Poll, Base + 0x4000 - 4, 1, 0));
    }

    [TestMethod]
    public void MadviseHinweiseOkDontneedAbgelehnt()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Madvise, 0x7000_0000, 8192, 0));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Madvise, 0x7000_0000, 8192, 3));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Madvise, 0x7000_0000, 0, 14));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Madvise, 0x7000_0001, 4096, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Madvise, 0x7000_0000, 4096, 4));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Madvise, 0x7000_0000, 4096, 99));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Madvise, 0x7000_0000, ulong.MaxValue, 0));
    }

    [TestMethod]
    public void FutexEinzelthread()
    {
        var p = new Proc();
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x100, 4), 5);
        const long F = LinuxSyscalls.Futex;
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, Call(p, F, Buf, 0 | 128, 4));       // Wert passt nicht
        Assert.AreEqual(-LinuxSyscalls.EDEADLK, Call(p, F, Buf, 0 | 128, 5));      // würde ewig warten
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x120, 8), 0L);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x128, 8), 1000L);
        Assert.AreEqual(-LinuxSyscalls.ETIMEDOUT, Call(p, F, Buf, 0, 5, Buf + 0x20));
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x128, 8), 2_000_000_000L);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, F, Buf, 0, 5, Buf + 0x20));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, F, Buf, 0, 5, 8));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, F, Buf + 2, 0, 5));          // nicht ausgerichtet
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, F, 8, 0, 5));
        Assert.AreEqual(0L, Call(p, F, Buf, 1 | 128, 1));                           // WAKE: niemand wartet
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, F, Buf, 10, 1, 0, 0, 0));    // WAKE_BITSET mit Maske 0
        Assert.AreEqual(0L, Call(p, F, Buf, 10, 1, 0, 0, 1));
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, Call(p, F, Buf, 4, 1, 1, Buf, 9));   // CMP_REQUEUE, Wert verschieden
        Assert.AreEqual(0L, Call(p, F, Buf, 4, 1, 1, Buf, 5));
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, Call(p, F, Buf, 5, 1));              // WAKE_OP nicht umgesetzt
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, F, 8, 1, 1));                // WAKE mit ungueltigem Zeiger
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, F, 8, 10, 1, 0, 0, 1));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, F, Buf, 3, 1, 1, 8, 0));     // REQUEUE: uaddr2 ungueltig
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, F, Buf, 3, 1, 1, Buf + 2, 0));
    }

    [TestMethod]
    public void FehlerpfadeUngueltigerZeiger()
    {
        var p = new Proc();
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Uname, 8));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Poll, 8, 1, 0));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.RtSigaction, 2, 8, 0, 8));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.RtSigaction, 2, 0, 8, 8));
    }

    [TestMethod]
    public void GetrandomOhneHostIstEnosys()
    {
        var p = new NoHostProc();
        var a = new SyscallArgs { Number = LinuxSyscalls.Getrandom, A0 = Buf, A1 = 8 };
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, LinuxSyscalls.Dispatch(ref a, p));
        a = new SyscallArgs { Number = LinuxSyscalls.ClockGettime, A0 = 0, A1 = Buf };
        Assert.AreEqual(0L, LinuxSyscalls.Dispatch(ref a, p)); // Zeit hat einen Ersatz
        Assert.IsTrue(BitConverter.ToInt64(p.Mem.Data, 0x100) > 1_700_000_000L);
    }

    private sealed class NoHostProc : ILinuxProcess
    {
        public readonly FakeMemory Mem = new();
        public IUserMemory Memory => Mem;
        public ILinuxDescriptor? GetDescriptor(int fd) => null;
        public ulong Brk => 0;
        public bool TrySetBrk(ulong n) => false;
        public ulong MapAnonymous(ulong len, int prot) => 0;
        public bool Unmap(ulong a, ulong l) => false;
        public bool Protect(ulong a, ulong l, int p) => false;
        public ulong FsBase { get; set; }
        public ulong ClearChildTid { get; set; }
        public int Pid => 1;
        public LinuxSignalState Signals { get; } = new();
        public void Exit(int code) { }
    }
}
