#pragma warning disable CA1822 // Stubs der Schnittstelle
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Tests.Processes;

/// <summary>V13: pipe/pipe2 im Prozess, poll auf Pipe-Enden, sysinfo.</summary>
[TestClass]
public class LinuxPipeTests
{
    private const ulong Base = 0x10000;
    private const int Size = 0x40000;
    private const long Read = 0, Write = 1, Close = 3, Fstat = 5, Poll = 7, Lseek = 8, Pipe = 22, Dup = 32, Fcntl = 72, Sysinfo = 99, Pipe2 = 293;
    private const int ONonblock = 0x800, OCloexec = 0x80000;
    private const short PollIn = 1, PollOut = 4, PollErr = 8, PollHup = 0x10;

    // Bereiche im Benutzerspeicher: Paar, poll-Einträge, Lesepuffer, Schreibpuffer.
    private const ulong Fds = Base, PollFds = Base + 0x100, RBuf = Base + 0x1000, WBuf = Base + 0x20000;

    private sealed class Mem : IUserMemory
    {
        public readonly byte[] Data = new byte[Size];

        public bool TryRead(ulong addr, Span<byte> dst)
        {
            if (!UserRange.Contains(Base, Base + (ulong)Data.Length, addr, (ulong)dst.Length)) return false;
            lock (Data) Data.AsSpan((int)(addr - Base), dst.Length).CopyTo(dst);
            return true;
        }

        public bool TryWrite(ulong addr, ReadOnlySpan<byte> src)
        {
            if (!UserRange.Contains(Base, Base + (ulong)Data.Length, addr, (ulong)src.Length)) return false;
            lock (Data) src.CopyTo(Data.AsSpan((int)(addr - Base)));
            return true;
        }

        public bool CanWrite(ulong addr, ulong length) => UserRange.Contains(Base, Base + (ulong)Data.Length, addr, length);

        public bool TryReadCString(ulong addr, int max, out string s)
        {
            s = "";
            return false;
        }
    }

    private sealed class Term : ILinuxDescriptor
    {
        public long Write(ReadOnlySpan<byte> data) => data.Length;
    }

    private sealed class NoFs : ILinuxFileSystem
    {
        public long Open(string path, int flags, uint mode, out ILinuxFile? file) { file = null; return -2; }
        public long Stat(string path, bool follow, out LinuxStat st) { st = default; return -2; }
        public long Access(string path, int mode) => -2;
        public long ReadLink(string path, out string target) { target = ""; return -2; }
        public long Mkdir(string path, uint mode) => -2;
        public long Unlink(string path) => -2;
        public long Rmdir(string path) => -2;
        public long Rename(string a, string b) => -2;
        public long Symlink(string target, string link) => -2;
    }

    private sealed class Proc : ILinuxProcess, ILinuxFiles, ILinuxAbort, ILinuxHost, ILinuxSystemInfo
    {
        public readonly Mem Mem = new();
        public readonly Dictionary<int, ILinuxDescriptor> Table = new() { [0] = new Term(), [1] = new Term(), [2] = new Term() };
        public int Max = 1024;
        public volatile bool Abort;
        public IUserMemory Memory => Mem;
        public ILinuxDescriptor? GetDescriptor(int fd)
        {
            lock (Table) return Table.GetValueOrDefault(fd);
        }
        public ulong Brk => 0;
        public bool TrySetBrk(ulong n) => false;
        public ulong MapAnonymous(ulong len, int prot) => 0;
        public bool Unmap(ulong a, ulong l) => false;
        public bool Protect(ulong a, ulong l, int p) => false;
        public bool MapFixed(ulong a, ulong l, int p) => false;
        public bool Discard(ulong a, ulong l) => false;
        public ulong Uid => 1000;
        public ulong Gid => 1000;
        public ulong FsBase { get; set; }
        public ulong ClearChildTid { get; set; }
        public int Pid => 1;
        public LinuxSignalState Signals { get; } = new();
        public void Exit(int code) { }
        public ILinuxFileSystem FileSystem { get; } = new NoFs();
        public object FileGate { get; } = new();
        public string Cwd { get; set; } = "/";
        public int AddDescriptor(ILinuxDescriptor d, int min)
        {
            lock (Table)
            {
                for (int i = min; i < Max; i++)
                {
                    if (!Table.ContainsKey(i)) { Table[i] = d; return i; }
                }
                return -24;
            }
        }
        public bool RemoveDescriptor(int fd)
        {
            lock (Table) return Table.Remove(fd);
        }
        public bool IsAborted => Abort;
        public long RealtimeNanos => 0;
        public long MonotonicNanos => 3_725_000_000_000L; // 3725 s
        public bool TryFillRandom(Span<byte> dst) => false;
        public void SleepNanos(long ns) { }
        public bool TryGetMemory(out ulong totalBytes, out ulong freeBytes)
        {
            totalBytes = 512UL << 20;
            freeBytes = 300UL << 20;
            return true;
        }
    }

    private static long Call(ILinuxProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    private static (int R, int W) MakePipe(Proc p, int flags = 0)
    {
        Assert.AreEqual(0L, Call(p, Pipe2, Fds, (ulong)flags));
        return (BitConverter.ToInt32(p.Mem.Data, 0), BitConverter.ToInt32(p.Mem.Data, 4));
    }

    private static long WriteText(Proc p, int fd, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        p.Mem.TryWrite(WBuf, bytes);
        return Call(p, Write, (ulong)fd, WBuf, (ulong)bytes.Length);
    }

    private static string ReadText(Proc p, int fd, int max, out long result)
    {
        result = Call(p, Read, (ulong)fd, RBuf, (ulong)max);
        return result > 0 ? Encoding.ASCII.GetString(p.Mem.Data, (int)(RBuf - Base), (int)result) : "";
    }

    /// <summary>Schreibt poll-Einträge, ruft poll und liefert die revents.</summary>
    private static short[] DoPoll(Proc p, long timeoutMs, out long result, params (int Fd, short Events)[] entries)
    {
        byte[] b = new byte[entries.Length * 8];
        for (int i = 0; i < entries.Length; i++)
        {
            BitConverter.TryWriteBytes(b.AsSpan(i * 8, 4), entries[i].Fd);
            BitConverter.TryWriteBytes(b.AsSpan(i * 8 + 4, 2), entries[i].Events);
        }

        p.Mem.TryWrite(PollFds, b);
        result = Call(p, Poll, PollFds, (ulong)entries.Length, unchecked((ulong)timeoutMs));
        p.Mem.TryRead(PollFds, b);
        var revents = new short[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            revents[i] = BitConverter.ToInt16(b, i * 8 + 6);
        }

        return revents;
    }

    [TestMethod]
    public void PipeTransportsBytesInOrder()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p);
        Assert.AreEqual(3, r);
        Assert.AreEqual(4, w);
        Assert.AreEqual(3L, WriteText(p, w, "abc"));
        Assert.AreEqual(4L, WriteText(p, w, "defg"));
        Assert.AreEqual("ab", ReadText(p, r, 2, out _));
        Assert.AreEqual("cdefg", ReadText(p, r, 100, out _), "Lesen liefert alles Verfügbare, nicht mehr");
    }

    [TestMethod]
    public void PipeWithoutFlagsIsSameAsPipe2Zero()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, Pipe, Fds));
        Assert.AreEqual(3, BitConverter.ToInt32(p.Mem.Data, 0));
        Assert.AreEqual(4, BitConverter.ToInt32(p.Mem.Data, 4));
        Assert.AreEqual(0L, Call(p, Fcntl, 3, 3), "F_GETFL Leseende: O_RDONLY");
        Assert.AreEqual(1L, Call(p, Fcntl, 4, 3), "F_GETFL Schreibende: O_WRONLY");
    }

    [TestMethod]
    public void Pipe2RejectsUnknownFlagsAndFreesNumbersOnFault()
    {
        var p = new Proc();
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, Pipe2, Fds, 0x4000), "O_DIRECT (Paketmodus) nicht umgesetzt");
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, Pipe2, 8, 0));
        Assert.IsNull(p.GetDescriptor(3), "Nach -EFAULT sind beide Nummern frei");
        Assert.IsNull(p.GetDescriptor(4));
        p.Max = 4;
        Assert.AreEqual(-24L, Call(p, Pipe2, Fds, 0), "nur eine Nummer frei: -EMFILE");
        Assert.IsNull(p.GetDescriptor(3), "das schon eingetragene Leseende ist wieder frei");
    }

    [TestMethod]
    public void CloexecIsAcceptedAndNonblockApplies()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p, OCloexec | ONonblock);
        Assert.AreEqual((long)ONonblock, Call(p, Fcntl, (ulong)r, 3));
        Assert.AreEqual((long)(ONonblock | 1), Call(p, Fcntl, (ulong)w, 3));
        ReadText(p, r, 10, out long res);
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, res, "leer und O_NONBLOCK");

        // F_SETFL schaltet O_NONBLOCK ab; dup teilt die offene Datei und damit das Flag.
        int r2 = (int)Call(p, Dup, (ulong)r);
        Assert.AreEqual(0L, Call(p, Fcntl, (ulong)r, 4, 0));
        Assert.AreEqual(0L, Call(p, Fcntl, (ulong)r2, 3));
    }

    [TestMethod]
    public void WrongDirectionAndSeekAreRejected()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p);
        Assert.AreEqual(-LinuxSyscalls.EBADF, WriteText(p, r, "x"), "Leseende nicht zum Schreiben offen");
        ReadText(p, w, 1, out long res);
        Assert.AreEqual(-LinuxSyscalls.EBADF, res, "Schreibende nicht zum Lesen offen");
        Assert.AreEqual(-29L, Call(p, Lseek, (ulong)r, 0, 0), "ESPIPE");
    }

    [TestMethod]
    public void FstatReportsFifo()
    {
        var p = new Proc();
        var (r, _) = MakePipe(p);
        Assert.AreEqual(0L, Call(p, Fstat, (ulong)r, RBuf));
        uint mode = BitConverter.ToUInt32(p.Mem.Data, (int)(RBuf - Base) + 24);
        Assert.AreEqual(0x1000u, mode & 0xF000u, "S_IFIFO");
    }

    [TestMethod]
    public void LastWriterCloseGivesEofAndHangup()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p);
        int w2 = (int)Call(p, Dup, (ulong)w);
        Assert.AreEqual(2L, WriteText(p, w, "zz"));
        Assert.AreEqual(0L, Call(p, Close, (ulong)w));
        var rev = DoPoll(p, 0, out long n, (r, PollIn));
        Assert.AreEqual(1L, n);
        Assert.AreEqual(PollIn, rev[0], "eine Nummer des Schreibendes ist noch offen: kein POLLHUP");

        Assert.AreEqual(0L, Call(p, Close, (ulong)w2));
        rev = DoPoll(p, 0, out _, (r, PollIn));
        Assert.AreEqual((short)(PollIn | PollHup), rev[0]);
        Assert.AreEqual("zz", ReadText(p, r, 10, out _), "Gepuffertes bleibt lesbar");
        ReadText(p, r, 10, out long eof);
        Assert.AreEqual(0L, eof, "danach EOF statt Blockieren");
        rev = DoPoll(p, -1, out n, (r, PollIn));
        Assert.AreEqual(1L, n, "poll ohne Frist kehrt bei POLLHUP sofort zurück");
        Assert.AreEqual(PollHup, rev[0]);
    }

    [TestMethod]
    public void ReaderCloseGivesEpipeAndPollErr()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p);
        var rev = DoPoll(p, 0, out long n, (w, PollOut));
        Assert.AreEqual(1L, n);
        Assert.AreEqual(PollOut, rev[0]);
        Assert.AreEqual(0L, Call(p, Close, (ulong)r));
        Assert.AreEqual(-32L, WriteText(p, w, "x"), "EPIPE");
        rev = DoPoll(p, 0, out _, (w, PollOut));
        Assert.AreEqual(PollErr, rev[0]);
    }

    [TestMethod]
    public void NonblockingWriteFillsCapacityThenEagain()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p, ONonblock);
        long total = 0;
        while (true)
        {
            long n = Call(p, Write, (ulong)w, WBuf, 0x10000);
            if (n < 0)
            {
                Assert.AreEqual(-LinuxSyscalls.EAGAIN, n);
                break;
            }

            total += n;
            Assert.IsTrue(total <= LinuxPipe.Capacity);
        }

        Assert.AreEqual((long)LinuxPipe.Capacity, total);
        var rev = DoPoll(p, 0, out long ready, (w, PollOut));
        Assert.AreEqual(0L, ready, "voll: kein POLLOUT");
        Assert.AreEqual((short)0, rev[0]);

        // 100 Bytes frei reichen nicht für ein atomares Schreiben von 200 Bytes.
        ReadText(p, r, 100, out _);
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, Call(p, Write, (ulong)w, WBuf, 200), "PIPE_BUF: ganz oder gar nicht");
        Assert.AreEqual(100L, Call(p, Write, (ulong)w, WBuf, 100));
    }

    [TestMethod]
    public void RingKeepsOrderAcrossWrapAndGrowth()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p);
        var expected = new StringBuilder();
        var got = new StringBuilder();
        for (int round = 0; round < 300; round++)
        {
            string chunk = new string((char)('a' + (round % 26)), 1 + (round * 37 % 700));
            Assert.AreEqual((long)chunk.Length, WriteText(p, w, chunk));
            expected.Append(chunk);
            if (round % 3 == 2)
            {
                got.Append(ReadText(p, r, 900, out _));
            }
        }

        while (got.Length < expected.Length)
        {
            got.Append(ReadText(p, r, 4096, out _));
        }

        Assert.AreEqual(expected.ToString(), got.ToString());
    }

    [TestMethod]
    [Timeout(10000)]
    public void BlockingReadWaitsForWriterThread()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p);
        var reader = Task.Run(() => ReadText(p, r, 16, out _));
        Thread.Sleep(100);
        Assert.IsFalse(reader.IsCompleted, "Lesen blockiert ohne Daten");
        // Der zweite Thread schreibt direkt ins Ende: der Ersatzspeicher hat nur einen Schreibpuffer.
        Assert.AreEqual(4L, ((ILinuxDescriptor)p.GetDescriptor(w)!).Write("wake"u8));
        Assert.AreEqual("wake", reader.Result);
    }

    [TestMethod]
    [Timeout(10000)]
    public void BlockingWriteWaitsForSpace()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p);
        var end = (ILinuxDescriptor)p.GetDescriptor(w)!;
        Assert.AreEqual((long)LinuxPipe.Capacity, end.Write(new byte[LinuxPipe.Capacity]));
        var writer = Task.Run(() => end.Write(new byte[10]));
        Thread.Sleep(100);
        Assert.IsFalse(writer.IsCompleted, "voll: Schreiben blockiert");
        var reader = (ILinuxInput)p.GetDescriptor(r)!;
        Assert.AreEqual(4096L, reader.ReadInput(new byte[4096]));
        Assert.AreEqual(10L, writer.Result);
    }

    [TestMethod]
    [Timeout(10000)]
    public void BlockedWriterGetsEpipeWhenReaderCloses()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p);
        var end = (ILinuxDescriptor)p.GetDescriptor(w)!;
        end.Write(new byte[LinuxPipe.Capacity]);
        var writer = Task.Run(() => end.Write(new byte[10]));
        Thread.Sleep(50);
        Assert.AreEqual(0L, Call(p, Close, (ulong)r));
        Assert.AreEqual(-32L, writer.Result);
    }

    [TestMethod]
    [Timeout(10000)]
    public void AbortEndsBlockedRead()
    {
        var p = new Proc();
        var (r, _) = MakePipe(p);
        var reader = Task.Run(() =>
        {
            ReadText(p, r, 16, out long res);
            return res;
        });
        Thread.Sleep(80);
        p.Abort = true;
        Assert.AreEqual(-LinuxSyscalls.EINTR, reader.Result);
    }

    [TestMethod]
    [Timeout(10000)]
    public void PollWithoutDeadlineWakesOnWrite()
    {
        var p = new Proc();
        var (r, w) = MakePipe(p);
        var end = (ILinuxDescriptor)p.GetDescriptor(w)!;
        var timer = Task.Run(() =>
        {
            Thread.Sleep(120);
            end.Write("x"u8);
        });
        var rev = DoPoll(p, -1, out long n, (r, PollIn));
        Assert.AreEqual(1L, n);
        Assert.AreEqual(PollIn, rev[0]);
        timer.Wait();
    }

    [TestMethod]
    [Timeout(10000)]
    public void PollOnSeveralPipesReportsTheSecond()
    {
        // LibCore wartet auf die Weckpipe neben anderen Deskriptoren: ein Schreiben in den zweiten muss poll beenden.
        var p = new Proc();
        var (r1, _) = MakePipe(p);
        var (r2, w2) = MakePipe(p);
        var end = (ILinuxDescriptor)p.GetDescriptor(w2)!;
        var timer = Task.Run(() =>
        {
            Thread.Sleep(120);
            end.Write("y"u8);
        });
        var rev = DoPoll(p, 5000, out long n, (r1, PollIn), (r2, PollIn), (1, PollOut));
        Assert.AreEqual(1L, n, "fd 1 meldet POLLOUT sofort");
        Assert.AreEqual(PollOut, rev[2]);

        rev = DoPoll(p, 5000, out n, (r1, PollIn), (r2, PollIn));
        Assert.AreEqual(1L, n);
        Assert.AreEqual((short)0, rev[0]);
        Assert.AreEqual(PollIn, rev[1]);
        timer.Wait();
    }

    [TestMethod]
    [Timeout(10000)]
    public void PollDeadlineExpiresOnSeveralPipes()
    {
        var p = new Proc();
        var (r1, _) = MakePipe(p);
        var (r2, _) = MakePipe(p);
        var started = DateTime.UtcNow;
        DoPoll(p, 60, out long n, (r1, PollIn), (r2, PollIn));
        Assert.AreEqual(0L, n);
        Assert.IsTrue(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(50), "Frist abgewartet");
    }

    [TestMethod]
    [Timeout(10000)]
    public void AbortEndsPollWithoutDeadline()
    {
        var p = new Proc();
        var (r1, _) = MakePipe(p);
        var (r2, _) = MakePipe(p);
        var poller = Task.Run(() =>
        {
            DoPoll(p, -1, out long n, (r1, PollIn), (r2, PollIn));
            return n;
        });
        Thread.Sleep(80);
        p.Abort = true;
        Assert.AreEqual(0L, poller.Result);
    }

    [TestMethod]
    [Timeout(10000)]
    public void PollDeadlineHoldsDespiteForeignPulses()
    {
        // V13-Review P2-1: Jedes Lesen eines anderen Threads weckt den Wartenden (PulseAll). Die Frist darf dadurch nicht schrumpfen,
        // für ein Ende allein und für mehrere Einträge (Scheibenweg in DoPoll).
        var p = new Proc();
        var (r, w) = MakePipe(p, ONonblock);
        var (r2, _) = MakePipe(p);
        while (Call(p, Write, (ulong)w, WBuf, 0x10000) > 0)
        {
        }

        using var stop = new CancellationTokenSource();
        var reader = (ILinuxInput)p.GetDescriptor(r)!;
        var drip = Task.Run(() =>
        {
            // 1 Byte je 2 ms: Platz entsteht, aber nie PIPE_BUF, also nie POLLOUT.
            byte[] one = new byte[1];
            while (!stop.IsCancellationRequested)
            {
                reader.ReadInput(one);
                Thread.Sleep(2);
            }
        });

        foreach (var entries in new[] { new[] { (w, PollOut) }, new[] { (w, PollOut), (r2, PollIn) } })
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            DoPoll(p, 400, out long n, entries);
            Assert.AreEqual(0L, n, "kein Eintrag bereit");
            Assert.IsTrue(started.ElapsedMilliseconds >= 380, $"Frist 400 ms, gewartet {started.ElapsedMilliseconds} ms bei {entries.Length} Einträgen");
        }

        stop.Cancel();
        drip.Wait();
    }

    [TestMethod]
    [Timeout(10000)]
    public void ReadLeavesBufferAloneWithoutData()
    {
        // V13-Review P2-2: Linux schreibt den Puffer nur mit gelesenen Bytes; -EAGAIN, -EINTR und EOF lassen ihn unverändert.
        var p = new Proc();
        byte[] marker = [9, 9, 9, 9];
        var (r, w) = MakePipe(p, ONonblock);
        p.Mem.TryWrite(RBuf, marker);
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, Call(p, Read, (ulong)r, RBuf, 4));
        CollectionAssert.AreEqual(marker, p.Mem.Data[(int)(RBuf - Base)..(int)(RBuf - Base + 4)], "EAGAIN");

        Assert.AreEqual(0L, Call(p, Close, (ulong)w));
        Assert.AreEqual(0L, Call(p, Read, (ulong)r, RBuf, 4));
        CollectionAssert.AreEqual(marker, p.Mem.Data[(int)(RBuf - Base)..(int)(RBuf - Base + 4)], "EOF");

        var (rb, _) = MakePipe(p);
        p.Abort = true;
        Assert.AreEqual(-LinuxSyscalls.EINTR, Call(p, Read, (ulong)rb, RBuf, 4));
        CollectionAssert.AreEqual(marker, p.Mem.Data[(int)(RBuf - Base)..(int)(RBuf - Base + 4)], "EINTR");

        // Die Prüfung vor dem Warten bleibt: ein ungültiger Puffer ist weiter -EFAULT.
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, Read, (ulong)rb, Base + Size - 2, 4));
    }

    [TestMethod]
    public void SysinfoFillsUptimeAndMemory()
    {
        var p = new Proc();
        Array.Fill(p.Mem.Data, (byte)0xEE, (int)(RBuf - Base), 112);
        Assert.AreEqual(0L, Call(p, Sysinfo, RBuf));
        int at = (int)(RBuf - Base);
        Assert.AreEqual(3725L, BitConverter.ToInt64(p.Mem.Data, at), "uptime in s");
        Assert.AreEqual(0UL, BitConverter.ToUInt64(p.Mem.Data, at + 8), "loads[0]");
        Assert.AreEqual(512UL << 20, BitConverter.ToUInt64(p.Mem.Data, at + 32), "totalram");
        Assert.AreEqual(300UL << 20, BitConverter.ToUInt64(p.Mem.Data, at + 40), "freeram");
        Assert.AreEqual(0UL, BitConverter.ToUInt64(p.Mem.Data, at + 64), "totalswap");
        Assert.AreEqual((ushort)1, BitConverter.ToUInt16(p.Mem.Data, at + 80), "procs");
        Assert.AreEqual(1u, BitConverter.ToUInt32(p.Mem.Data, at + 104), "mem_unit");
        Assert.AreEqual(0u, BitConverter.ToUInt32(p.Mem.Data, at + 108), "Rest genullt");
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, Sysinfo, 8));
    }
}
