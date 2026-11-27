using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V16: AF_UNIX socketpair (Stream, Seqpacket), sendmsg/recvmsg mit SCM_RIGHTS über Prozesse, shutdown, EOF/EPIPE.</summary>
[TestClass]
public sealed class LinuxUnixSocketTests
{
    private const ulong Page = 4096;
    private const ulong Spawn = LinuxUserProcess.CloneVm | LinuxUserProcess.CloneVfork | 17;
    private const long Read = 0, Write = 1, Close = 3, Fstat = 5, Poll = 7, Pipe = 22;
    private const int Stream = 1, Seqpacket = 5, Dgram = 2, SockNonblock = 0x800, SockCloexec = 0x80000;
    private const int MsgPeek = 2, MsgCtrunc = 8, MsgTrunc = 0x20, MsgDontwait = 0x40, MsgWaitall = 0x100;
    private const long EBADF = 9, EAGAIN = 11, EFAULT = 14, EINVAL = 22, EPIPE = 32, ENOTSOCK = 88, EMSGSIZE = 90, ESOCKTNOSUPPORT = 94,
        EAFNOSUPPORT = 97, EISCONN = 106, ECONNREFUSED = 111;

    private sealed class Pages : IUserPages
    {
        private readonly Dictionary<ulong, byte[]> _frames = new();
        private ulong _next = 0x100000;
        public ulong AllocateFrame() { _frames[_next] = new byte[Page]; _next += Page; return _next - Page; }
        public void FreeFrame(ulong frame) => _frames.Remove(frame);
        public Span<byte> Frame(ulong frame) => _frames[frame];
        public bool Map(ulong page, ulong frame, MapAccess access) => true;
        public bool Unmap(ulong page) => true;
        public void DestroyTable() { }
    }

    private sealed class Host : ILinuxHost
    {
        public long RealtimeNanos => 0;
        public long MonotonicNanos => 0;
        public bool TryFillRandom(Span<byte> dst) { dst.Fill(7); return true; }
        public void SleepNanos(long ns) { }
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    /// <summary>Elternprozess mit Speicher; vfork-Kind wie in LinuxSpawnTests (ein Thread, kein Ring 3).</summary>
    private sealed class Fixture
    {
        public readonly LinuxUserProcess Parent;
        public readonly ulong Mem;
        public LinuxUserProcess? Child;

        // Bereiche: Paare, msghdr, iovec, Hilfsdaten, Sende- und Empfangspuffer.
        public ulong Pair => Mem;
        public ulong Msg => Mem + 0x100;
        public ulong Iov => Mem + 0x200;
        public ulong Ctrl => Mem + 0x300;
        public ulong Data => Mem + 0x1000;
        public ulong RBuf => Mem + 0x2000;

        public Fixture()
        {
            var term = new TerminalDescriptor(new Lines());
            Parent = new LinuxUserProcess(new Pages(), term, 1, 64 << 20, new Host(), new LinuxFileSystem(new MemoryFileStore(), term));
            Parent.StartThread = _ => true;
            Parent.StartVfork = (c, stack) =>
            {
                Child = c;
                c.OnExit = _ => c.ReportEnd(c.WaitStatusOf(9));
                return true;
            };
            Mem = Parent.MapAnonymous(16 * Page, 3);
            Assert.AreNotEqual(0UL, Mem);
        }

        public static long Call(LinuxUserProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
        {
            var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
            return LinuxSyscalls.Dispatch(ref a, p);
        }

        public (int A, int B) SocketPair(int type, LinuxUserProcess? p = null)
        {
            Assert.AreEqual(0, Call(p ?? Parent, LinuxSyscalls.Socketpair, 1, (ulong)type, 0, Pair));
            return (Int(Pair), Int(Pair + 4));
        }

        public (int R, int W) MakePipe(LinuxUserProcess? p = null)
        {
            Assert.AreEqual(0, Call(p ?? Parent, Pipe, Pair + 8));
            return (Int(Pair + 8), Int(Pair + 12));
        }

        public int Int(ulong at)
        {
            var b = new byte[4];
            Assert.IsTrue(Parent.TryRead(at, b));
            return BitConverter.ToInt32(b);
        }

        public void Put(ulong at, byte[] b) => Assert.IsTrue(Parent.TryWrite(at, b));

        public byte[] Get(ulong at, int n)
        {
            var b = new byte[n];
            Assert.IsTrue(Parent.TryRead(at, b));
            return b;
        }

        private void Header(ulong iovLen, ulong ctrl, ulong ctrlLen)
        {
            var h = new byte[56];
            BitConverter.TryWriteBytes(h.AsSpan(16, 8), Iov);
            BitConverter.TryWriteBytes(h.AsSpan(24, 8), iovLen);
            BitConverter.TryWriteBytes(h.AsSpan(32, 8), ctrl);
            BitConverter.TryWriteBytes(h.AsSpan(40, 8), ctrlLen);
            Put(Msg, h);
        }

        private void IovEntry(int i, ulong addr, ulong len)
        {
            var e = new byte[16];
            BitConverter.TryWriteBytes(e.AsSpan(0, 8), addr);
            BitConverter.TryWriteBytes(e.AsSpan(8, 8), len);
            Put(Iov + (ulong)(i * 16), e);
        }

        public long SendMsg(int fd, byte[] data, int[]? fds = null, int flags = 0, LinuxUserProcess? p = null)
        {
            Put(Data, data);
            IovEntry(0, Data, (ulong)data.Length);
            ulong ctrlLen = 0;
            if (fds is { Length: > 0 })
            {
                var c = new byte[16 + (fds.Length * 4)];
                BitConverter.TryWriteBytes(c.AsSpan(0, 8), (ulong)c.Length);
                BitConverter.TryWriteBytes(c.AsSpan(8, 4), 1);
                BitConverter.TryWriteBytes(c.AsSpan(12, 4), 1);
                for (int i = 0; i < fds.Length; i++)
                {
                    BitConverter.TryWriteBytes(c.AsSpan(16 + (i * 4), 4), fds[i]);
                }

                Put(Ctrl, c);
                ctrlLen = (ulong)c.Length;
            }

            Header(1, ctrlLen == 0 ? 0 : Ctrl, ctrlLen);
            return Call(p ?? Parent, LinuxSyscalls.Sendmsg, (ulong)fd, Msg, (ulong)flags);
        }

        /// <summary>recvmsg in einen Puffer von <paramref name="len"/> Bytes, Hilfsdaten bis <paramref name="ctrlLen"/>.</summary>
        public long RecvMsg(int fd, int len, int ctrlLen, out byte[] data, out int[] fds, out int msgFlags, int flags = 0, LinuxUserProcess? p = null)
        {
            Put(Ctrl, new byte[64]);
            IovEntry(0, RBuf, (ulong)len);
            Header(1, ctrlLen == 0 ? 0 : Ctrl, (ulong)ctrlLen);
            long r = Call(p ?? Parent, LinuxSyscalls.Recvmsg, (ulong)fd, Msg, (ulong)flags);
            data = r > 0 ? Get(RBuf, (int)Math.Min(r, len)) : Array.Empty<byte>();
            byte[] h = Get(Msg, 56);
            msgFlags = BitConverter.ToInt32(h, 48);
            ulong outLen = r >= 0 ? BitConverter.ToUInt64(h, 40) : 0;
            var list = new List<int>();
            if (outLen >= 16)
            {
                byte[] c = Get(Ctrl, (int)outLen);
                Assert.AreEqual(1, BitConverter.ToInt32(c, 8), "SOL_SOCKET");
                Assert.AreEqual(1, BitConverter.ToInt32(c, 12), "SCM_RIGHTS");
                ulong cl = BitConverter.ToUInt64(c, 0);
                for (int o = 16; o + 4 <= (int)cl; o += 4)
                {
                    list.Add(BitConverter.ToInt32(c, o));
                }
            }

            fds = list.ToArray();
            return r;
        }

        public long WriteBytes(int fd, byte[] b, LinuxUserProcess? p = null)
        {
            Put(Data, b);
            return Call(p ?? Parent, Write, (ulong)fd, Data, (ulong)b.Length);
        }

        public long ReadBytes(int fd, int n, out byte[] got, LinuxUserProcess? p = null)
        {
            long r = Call(p ?? Parent, Read, (ulong)fd, RBuf, (ulong)n);
            got = r > 0 ? Get(RBuf, (int)r) : Array.Empty<byte>();
            return r;
        }
    }

    private static byte[] B(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    private static string S(byte[] b) => System.Text.Encoding.ASCII.GetString(b);

    [TestMethod]
    public void StreamPaarUebertraegtInBeideRichtungenUndMeldetEofUndEpipe()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Stream);
        Assert.AreEqual(3, f.WriteBytes(a, B("abc")));
        Assert.AreEqual(2, f.WriteBytes(a, B("de")));
        Assert.AreEqual(5, f.ReadBytes(b, 16, out byte[] got), "Stream fasst Nachrichten zusammen");
        Assert.AreEqual("abcde", S(got));
        Assert.AreEqual(2, f.WriteBytes(b, B("xy")));
        Assert.AreEqual(2, f.ReadBytes(a, 16, out got));
        Assert.AreEqual("xy", S(got));

        Assert.AreEqual(1, f.WriteBytes(a, B("z")));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)a));
        Assert.AreEqual(1, f.ReadBytes(b, 16, out got), "Gepuffertes bleibt nach dem Ende der Gegenseite lesbar");
        Assert.AreEqual(0, f.ReadBytes(b, 16, out _), "danach EOF");
        Assert.AreEqual(-EPIPE, f.WriteBytes(b, B("q")));
    }

    [TestMethod]
    public void SocketpairPrueftDomaeneTypUndFlags()
    {
        var f = new Fixture();
        Assert.AreEqual(-EAFNOSUPPORT, Fixture.Call(f.Parent, LinuxSyscalls.Socketpair, 2, Stream, 0, f.Pair));
        Assert.AreEqual(-ESOCKTNOSUPPORT, Fixture.Call(f.Parent, LinuxSyscalls.Socketpair, 1, 3 /* SOCK_RAW */, 0, f.Pair));
        Assert.AreEqual(-EFAULT, Fixture.Call(f.Parent, LinuxSyscalls.Socketpair, 1, Stream, 0, 8));
        var (a, b) = f.SocketPair(Stream | SockNonblock | SockCloexec);
        Assert.AreEqual(-EAGAIN, f.ReadBytes(a, 4, out _), "SOCK_NONBLOCK");
        Assert.AreEqual(1, Fixture.Call(f.Parent, 72 /* fcntl */, (ulong)b, 1 /* F_GETFD */), "SOCK_CLOEXEC");
        Assert.AreEqual(0, Fixture.Call(f.Parent, Fstat, (ulong)a, f.RBuf));
        Assert.AreEqual(0xC000u, BitConverter.ToUInt32(f.Get(f.RBuf + 24, 4)) & 0xF000u, "S_IFSOCK");
        Assert.AreEqual(-ENOTSOCK, Fixture.Call(f.Parent, LinuxSyscalls.Shutdown, 1, 1), "fd 1 ist das Terminal");
        Assert.AreEqual(-EBADF, Fixture.Call(f.Parent, LinuxSyscalls.Shutdown, 99, 1));
    }

    [TestMethod]
    public void SeqpacketHaeltNachrichtengrenzenUndMeldetTrunc()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Seqpacket);
        Assert.AreEqual(5, f.SendMsg(a, B("hallo")));
        Assert.AreEqual(3, f.SendMsg(a, B("du!")));
        Assert.AreEqual(4, f.RecvMsg(b, 4, 0, out byte[] got, out _, out int flags));
        Assert.AreEqual("hall", S(got));
        Assert.AreEqual(MsgTrunc, flags, "Rest der Nachricht verfällt");
        Assert.AreEqual(3, f.RecvMsg(b, 16, 0, out got, out _, out flags));
        Assert.AreEqual("du!", S(got));
        Assert.AreEqual(0, flags);

        Assert.AreEqual(2, f.SendMsg(a, B("ok")));
        Assert.AreEqual(2, f.RecvMsg(b, 1, 0, out _, out _, out _, MsgTrunc), "MSG_TRUNC liefert die ganze Länge");
        var s = (LinuxUnixSocket)f.Parent.GetDescriptor(a)!;
        Assert.AreEqual(-EMSGSIZE, s.Send(new byte[LinuxUnixSocket.Capacity + 1], null, false, null, out _));
    }

    [TestMethod]
    public void DgramPaarHaeltNachrichtengrenzenUndReichtDateienDurch()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Dgram);
        Assert.AreEqual(5, f.SendMsg(a, B("hallo")));
        Assert.AreEqual(0, f.SendMsg(a, []), "leeres Datagramm ist erlaubt");
        Assert.AreEqual(3, f.SendMsg(a, B("du!")));
        Assert.AreEqual(4, f.RecvMsg(b, 4, 0, out byte[] got, out _, out int flags));
        Assert.AreEqual("hall", S(got));
        Assert.AreEqual(MsgTrunc, flags);
        Assert.AreEqual(0, f.RecvMsg(b, 16, 0, out _, out _, out _), "das leere Datagramm kommt als eigene Nachricht");
        Assert.AreEqual(3, f.RecvMsg(b, 16, 0, out got, out _, out flags));
        Assert.AreEqual("du!", S(got));
        Assert.AreEqual(0, flags);
        Assert.AreEqual(-EAGAIN, f.RecvMsg(b, 16, 0, out _, out _, out _, MsgDontwait));

        var (r, w) = f.MakePipe();
        Assert.AreEqual(1, f.SendMsg(b, B("F"), [w]));
        Assert.AreEqual(1, f.RecvMsg(a, 16, 64, out got, out int[] fds, out _));
        Assert.AreEqual(1, fds.Length, "unix_fdpass: SCM_RIGHTS über SOCK_DGRAM");
        Assert.AreEqual(2, f.WriteBytes(fds[0], B("ok")));
        Assert.AreEqual(2, f.ReadBytes(r, 16, out got));
    }

    [TestMethod]
    public void DgramKenntKeinEofSondernEconnrefusedNachCloseDerGegenseite()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Dgram | SockNonblock);
        Assert.AreEqual(1, f.SendMsg(a, B("x")));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)a));
        Assert.AreEqual(1, f.RecvMsg(b, 16, 0, out _, out _, out _), "Gepuffertes bleibt lesbar");
        Assert.AreEqual(-EAGAIN, f.RecvMsg(b, 16, 0, out _, out _, out _), "kein EOF bei Dgram");
        Assert.AreEqual(-ECONNREFUSED, f.SendMsg(b, B("y")));
        Assert.AreEqual(0, ((LinuxUnixSocket)f.Parent.GetDescriptor(b)!).Shutdown(0));
        Assert.AreEqual(0, f.RecvMsg(b, 16, 0, out _, out _, out _), "eigenes SHUT_RD: 0");
    }

    [TestMethod]
    public void DgramWarteschlangeIstAufZehNachrichtenBegrenztAuchLeere()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Dgram | SockNonblock);
        for (int i = 0; i < LinuxUnixSocket.MaxDgramQueue; i++)
        {
            Assert.AreEqual(0, f.SendMsg(a, []), "leeres Datagramm " + i);
        }

        Assert.AreEqual(-EAGAIN, f.SendMsg(a, []), "elftes leeres Datagramm: voll");
        Assert.AreEqual(-EAGAIN, f.SendMsg(a, B("x")));
        Assert.AreEqual(0, f.RecvMsg(b, 16, 0, out _, out _, out _));
        Assert.AreEqual(1, f.SendMsg(a, B("x")), "nach dem Lesen wieder Platz");
    }

    [TestMethod]
    public void DgramPollMeldetPolloutNurSolangeDieWarteschlangeNichtVollIst()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Dgram | SockNonblock);
        void Entry(int fd, short events)
        {
            var e = new byte[8];
            BitConverter.TryWriteBytes(e.AsSpan(0, 4), fd);
            BitConverter.TryWriteBytes(e.AsSpan(4, 2), events);
            f.Put(f.Ctrl, e);
        }

        Entry(a, 4);
        Assert.AreEqual(1, Fixture.Call(f.Parent, Poll, f.Ctrl, 1, 0), "leer: schreibbereit");
        for (int i = 0; i < LinuxUnixSocket.MaxDgramQueue; i++)
        {
            Assert.AreEqual(1, f.SendMsg(a, B("x")));
        }

        Entry(a, 4);
        Assert.AreEqual(0, Fixture.Call(f.Parent, Poll, f.Ctrl, 1, 0), "voll: nicht schreibbereit");
        Entry(b, 1);
        Assert.AreEqual(1, Fixture.Call(f.Parent, Poll, f.Ctrl, 1, 0), "Empfänger lesbar");
        Assert.AreEqual(1, f.RecvMsg(b, 16, 0, out _, out _, out _));
        Entry(a, 4);
        Assert.AreEqual(1, Fixture.Call(f.Parent, Poll, f.Ctrl, 1, 0), "wieder schreibbereit");
    }

    [TestMethod]
    public void DgramEigenesShutWrGibtEpipeVorEconnrefused()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Dgram | SockNonblock);
        Assert.AreEqual(0, ((LinuxUnixSocket)f.Parent.GetDescriptor(a)!).Shutdown(1));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)b));
        Assert.AreEqual(-EPIPE, f.SendMsg(a, B("y")));
    }

    [TestMethod]
    public void SigpendingMeldetNurBlockierteAnhaengige()
    {
        var f = new Fixture();
        const long RtSigpending = 127, RtSigprocmask = 14;
        Assert.AreEqual(-EINVAL, Fixture.Call(f.Parent, RtSigpending, f.RBuf, 129));
        Assert.AreEqual(-EFAULT, Fixture.Call(f.Parent, RtSigpending, 8, 8));
        f.Put(f.Data, BitConverter.GetBytes(1UL << (10 - 1))); // SIGUSR1 blockieren
        Assert.AreEqual(0, Fixture.Call(f.Parent, RtSigprocmask, 0, f.Data, 0, 8));
        Assert.AreEqual(0, Fixture.Call(f.Parent, 200 /* tkill */, (ulong)f.Parent.Pid, 10));
        Assert.AreEqual(0, Fixture.Call(f.Parent, RtSigpending, f.RBuf, 8));
        Assert.AreEqual(1UL << 9, BitConverter.ToUInt64(f.Get(f.RBuf, 8)));
    }

    [TestMethod]
    public void ScmRightsUeberlebtCloseDesSendersUndZaehltMit()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Stream);
        var (r, w) = f.MakePipe();
        Assert.AreEqual(1, f.SendMsg(a, B("F"), [w]));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)w), "Sender schließt seine Nummer; die Nachricht hält die Pipe");
        Assert.AreEqual(1, f.RecvMsg(b, 16, 64, out byte[] got, out int[] fds, out int flags));
        Assert.AreEqual("F", S(got));
        Assert.AreEqual(0, flags);
        Assert.AreEqual(1, fds.Length);
        Assert.AreEqual(3, f.WriteBytes(fds[0], B("via")), "die empfangene Nummer schreibt in dieselbe Pipe");
        Assert.AreEqual(3, f.ReadBytes(r, 16, out got));
        Assert.AreEqual("via", S(got));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)fds[0]));
        Assert.AreEqual(0, f.ReadBytes(r, 16, out _), "letzter Schreiber zu: EOF");
    }

    [TestMethod]
    public void UngeleseneDateienWerdenBeimCloseDesSocketsFreigegeben()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Seqpacket);
        var (r, w) = f.MakePipe();
        Assert.AreEqual(1, f.SendMsg(a, B("x"), [w]));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)w));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)b), "Empfänger schließt ungelesen");
        Assert.AreEqual(0, f.ReadBytes(r, 16, out _), "die mitgeschickte Pipe ist zu: EOF");
        Assert.AreEqual(-104, f.SendMsg(a, B("y")), "Linux: ECONNRESET einmal, weil Ungelesenes verworfen wurde");
        Assert.AreEqual(-EPIPE, f.SendMsg(a, B("y")));
    }

    [TestMethod]
    public void ReadVerwirftMitgeschickteDateien()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Stream);
        var (r, w) = f.MakePipe();
        Assert.AreEqual(2, f.SendMsg(a, B("rw"), [w]));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)w));
        Assert.AreEqual(2, f.ReadBytes(b, 16, out _));
        Assert.AreEqual(0, f.ReadBytes(r, 16, out _), "read nimmt keine Dateien an, die Pipe schließt");
    }

    [TestMethod]
    public void ZuKleinerHilfsdatenpufferMeldetCtruncUndSchliesstDenRest()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Stream);
        var (r1, w1) = f.MakePipe();
        var (r2, w2) = f.MakePipe();
        Assert.AreEqual(1, f.SendMsg(a, B("2"), [w1, w2]));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)w1));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)w2));
        Assert.AreEqual(1, f.RecvMsg(b, 16, 16 + 4, out _, out int[] fds, out int flags));
        Assert.AreEqual(MsgCtrunc, flags);
        Assert.AreEqual(1, fds.Length);
        Assert.AreEqual(1, f.WriteBytes(fds[0], B("1")));
        Assert.AreEqual(1, f.ReadBytes(r1, 16, out _));
        Assert.AreEqual(0, f.ReadBytes(r2, 16, out _), "nicht zugestellte Datei ist geschlossen");
    }

    [TestMethod]
    public void StreamHaeltNachDerNachrichtMitDateienAn()
    {
        // Linux 6.12 (expected/v16/pair.txt): über eine Nachricht mit Dateien hinweg wird gelesen, danach endet der Aufruf.
        var f = new Fixture();
        var (a, b) = f.SocketPair(Stream);
        var (_, w) = f.MakePipe();
        Assert.AreEqual(2, f.SendMsg(a, B("ab")));
        Assert.AreEqual(2, f.SendMsg(a, B("cd"), [w]));
        Assert.AreEqual(2, f.SendMsg(a, B("ef"), [w]));
        Assert.AreEqual(2, f.SendMsg(a, B("gh")));
        Assert.AreEqual(4, f.RecvMsg(b, 16, 64, out byte[] got, out int[] fds, out _));
        Assert.AreEqual("abcd", S(got));
        Assert.AreEqual(1, fds.Length);
        Assert.AreEqual(2, f.RecvMsg(b, 16, 64, out got, out fds, out _));
        Assert.AreEqual("ef", S(got), "zweite Dateiliste: eigener Aufruf");
        Assert.AreEqual(1, fds.Length);
        Assert.AreEqual(2, f.RecvMsg(b, 16, 64, out got, out fds, out _));
        Assert.AreEqual("gh", S(got));
        Assert.AreEqual(0, fds.Length);
    }

    [TestMethod]
    public void PeekLaesstNachrichtUndDateienStehen()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Seqpacket);
        var (r, w) = f.MakePipe();
        Assert.AreEqual(1, f.SendMsg(a, B("p"), [w]));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)w));
        Assert.AreEqual(1, f.RecvMsg(b, 16, 64, out _, out int[] peeked, out _, MsgPeek));
        Assert.AreEqual(1, peeked.Length, "peek liefert eine zusätzliche Nummer");
        Assert.AreEqual(1, f.RecvMsg(b, 16, 64, out _, out int[] fds, out _));
        Assert.AreEqual(1, fds.Length);
        Assert.AreNotEqual(peeked[0], fds[0]);
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)peeked[0]));
        Assert.AreEqual(-EAGAIN, f.RecvMsg(b, 16, 64, out _, out _, out _, MsgDontwait));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)fds[0]));
        Assert.AreEqual(0, f.ReadBytes(r, 16, out _));
    }

    [TestMethod]
    public void ShutdownSchliesstNurDieGenannteRichtung()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Stream);
        Assert.AreEqual(1, f.WriteBytes(a, B("1")));
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Shutdown, (ulong)a, 1));
        Assert.AreEqual(-EPIPE, f.WriteBytes(a, B("2")), "SHUT_WR: eigenes Senden");
        Assert.AreEqual(1, f.ReadBytes(b, 16, out _));
        Assert.AreEqual(0, f.ReadBytes(b, 16, out _), "Gegenseite liest EOF");
        Assert.AreEqual(2, f.WriteBytes(b, B("ok")), "die andere Richtung bleibt offen");
        Assert.AreEqual(2, f.ReadBytes(a, 16, out _));
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Shutdown, (ulong)a, 0));
        Assert.AreEqual(-EPIPE, f.WriteBytes(b, B("x")), "SHUT_RD: Gegenseite bekommt EPIPE");
        Assert.AreEqual(-EINVAL, Fixture.Call(f.Parent, LinuxSyscalls.Shutdown, (ulong)a, 3));
    }

    [TestMethod]
    public void PollMeldetDatenUndEndeDerGegenseite()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Stream);
        void Entry(int fd, short events)
        {
            var e = new byte[8];
            BitConverter.TryWriteBytes(e.AsSpan(0, 4), fd);
            BitConverter.TryWriteBytes(e.AsSpan(4, 2), events);
            f.Put(f.Ctrl, e);
        }

        short Revents() => BitConverter.ToInt16(f.Get(f.Ctrl + 6, 2));

        Entry(b, 1 | 0x2000);
        Assert.AreEqual(0, Fixture.Call(f.Parent, Poll, f.Ctrl, 1, 0));
        Assert.AreEqual(1, f.WriteBytes(a, B("!")));
        Assert.AreEqual(1, Fixture.Call(f.Parent, Poll, f.Ctrl, 1, 0));
        Assert.AreEqual((short)1, Revents());
        Entry(a, 4);
        Assert.AreEqual(1, Fixture.Call(f.Parent, Poll, f.Ctrl, 1, 0));
        Assert.AreEqual((short)4, Revents());
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)a));
        Entry(b, 1 | 0x2000);
        Assert.AreEqual(1, Fixture.Call(f.Parent, Poll, f.Ctrl, 1, 0));
        Assert.AreEqual((short)(1 | 0x10 | 0x2000), Revents(), "POLLIN|POLLHUP|POLLRDHUP");
    }

    [TestMethod]
    public void BlockierendesLesenWachtBeimSchreibenDerGegenseiteAuf()
    {
        var f = new Fixture();
        var (a, b) = f.SocketPair(Stream);
        var reader = Task.Run(() => f.RecvMsg(b, 4, 0, out _, out _, out _, MsgWaitall));
        Thread.Sleep(80);
        Assert.IsFalse(reader.IsCompleted, "recvmsg wartet");
        var s = LinuxUserProcessSocket(f, a);
        Assert.AreEqual(2, s.Send(B("ab"), null, false, null, out _));
        Thread.Sleep(80);
        Assert.IsFalse(reader.IsCompleted, "MSG_WAITALL wartet auf alle vier Bytes");
        Assert.AreEqual(2, s.Send(B("cd"), null, false, null, out _));
        Assert.IsTrue(reader.Wait(5000));
        Assert.AreEqual(4, reader.Result);
    }

    private static LinuxUnixSocket LinuxUserProcessSocket(Fixture f, int fd) => (LinuxUnixSocket)f.Parent.GetDescriptor(fd)!;

    [TestMethod]
    public void StreamMitZieladresseIstEisconn()
    {
        var f = new Fixture();
        var (a, _) = f.SocketPair(Stream);
        Assert.AreEqual(-EISCONN, Fixture.Call(f.Parent, LinuxSyscalls.Sendto, (ulong)a, f.Data, 1, 0, f.Pair, 2));
        Assert.AreEqual(1, Fixture.Call(f.Parent, LinuxSyscalls.Sendto, (ulong)a, f.Data, 1, 0, 0, 0));
    }

    [TestMethod]
    public void GetsockoptKenntTypPuffergroesseUndPeercred()
    {
        var f = new Fixture();
        var (a, _) = f.SocketPair(Seqpacket);
        f.Put(f.Ctrl, BitConverter.GetBytes(4));
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Getsockopt, (ulong)a, 1, 3, f.RBuf, f.Ctrl));
        Assert.AreEqual(Seqpacket, f.Int(f.RBuf));
        f.Put(f.Ctrl, BitConverter.GetBytes(12));
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Getsockopt, (ulong)a, 1, 17, f.RBuf, f.Ctrl));
        Assert.AreEqual(1, f.Int(f.RBuf), "SO_PEERCRED: PID des Erzeugers");
        Assert.AreEqual(12, f.Int(f.Ctrl));
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Setsockopt, (ulong)a, 1, 7, f.RBuf, 4));
        Assert.AreEqual(-92, Fixture.Call(f.Parent, LinuxSyscalls.Setsockopt, (ulong)a, 1, 20, f.RBuf, 16), "SO_RCVTIMEO nicht still annehmen");
    }

    [TestMethod]
    public void KindReichtPipeUeberSocketpairWeiterUndEndet()
    {
        // Ladybird: WebContent (Kind) und Browser (Eltern) tauschen Dateien über das geerbte socketpair; das Kind darf vorher enden.
        var f = new Fixture();
        var (a, b) = f.SocketPair(Seqpacket);
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0));
        LinuxUserProcess child = f.Child!;
        var (r, w) = f.MakePipe(child);
        Assert.AreEqual(1, f.SendMsg(a, B("k"), [r, w], 0, child));
        Assert.AreEqual(0, Fixture.Call(child, LinuxSyscalls.ExitGroup, 0));
        Assert.IsNull(child.GetDescriptor(r), "Kind hat seine Dateien geschlossen");

        Assert.AreEqual(1, f.RecvMsg(b, 16, 64, out _, out int[] fds, out int flags));
        Assert.AreEqual(0, flags);
        Assert.AreEqual(2, fds.Length);
        Assert.AreEqual(4, f.WriteBytes(fds[1], B("echo")));
        Assert.AreEqual(4, f.ReadBytes(fds[0], 16, out byte[] got));
        Assert.AreEqual("echo", S(got));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Close, (ulong)fds[1]));
        Assert.AreEqual(0, f.ReadBytes(fds[0], 16, out _));
    }
}
