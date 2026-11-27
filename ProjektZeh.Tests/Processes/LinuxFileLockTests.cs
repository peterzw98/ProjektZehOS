using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>V28b: fcntl-Bereichssperren (POSIX und OFD) über Prozesse eines Laufs, Freigabe bei close und Prozessende; chmod und link auf FAT.</summary>
[TestClass]
public sealed class LinuxFileLockTests
{
    private const ulong Page = 4096;
    private const long Open = 2, Close = 3, Lseek = 8, Dup = 32, Dup2 = 33, Fcntl = 72, Flock = 73, CloseRange = 436, Link = 86, Chmod = 90, Fchmod = 91, Linkat = 265, Fchmodat2 = 452;
    private const int GetLk = 5, SetLk = 6, SetLkW = 7, OfdGetLk = 36, OfdSetLk = 37;
    private const short Rd = 0, Wr = 1, Un = 2;
    private const long ENOENT = 2, EINTR = 4, EBADF = 9, EAGAIN = 11, EEXIST = 17, EINVAL = 22, EPERM = 1, EOVERFLOW = 75;
    private const ulong ORdwrCreat = 0x42;

    private sealed class Frames
    {
        public readonly Dictionary<ulong, byte[]> Live = new();
        public ulong Next = 0x100000;
    }

    private sealed class Pages(Frames store) : IUserPages
    {
        public ulong AllocateFrame() { store.Live[store.Next] = new byte[Page]; store.Next += Page; return store.Next - Page; }
        public void FreeFrame(ulong frame) => store.Live.Remove(frame);
        public Span<byte> Frame(ulong frame) => store.Live[frame];
        public bool Map(ulong page, ulong frame, MapAccess access) => true;
        public bool Unmap(ulong page) => true;
        public void DestroyTable() { }
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    private readonly Frames _frames = new();
    private readonly LinuxOpenFiles _open = new();
    private readonly LinuxIds _ids = new(1);
    private readonly MemoryFileStore _disk = new();

    private (LinuxUserProcess P, ulong Mem) NewProcess(int pid)
    {
        var term = new TerminalDescriptor(new Lines());
        var p = new LinuxUserProcess(new Pages(_frames), term, pid, 64 << 20, null, new LinuxFileSystem(_disk, term), _open, _ids);
        ulong mem = p.MapAnonymous(4 * Page, 3);
        Assert.AreNotEqual(0UL, mem);
        return (p, mem);
    }

    private static long Call(LinuxUserProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    private static ulong Str(LinuxUserProcess p, ulong at, string s)
    {
        Assert.IsTrue(p.TryWrite(at, Encoding.ASCII.GetBytes(s + "\0")));
        return at;
    }

    private static int OpenFile(LinuxUserProcess p, ulong mem, string path = "/db")
    {
        long fd = Call(p, Open, Str(p, mem + 2048, path), ORdwrCreat, 0x1A4);
        Assert.IsTrue(fd >= 3, $"open {fd}");
        return (int)fd;
    }

    /// <summary>Schreibt struct flock bei <paramref name="mem"/> und ruft fcntl.</summary>
    private static long Lock(LinuxUserProcess p, ulong mem, int fd, int cmd, short type, long start, long len, short whence = 0, int pid = 0)
    {
        var b = new byte[32];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), type);
        BitConverter.TryWriteBytes(b.AsSpan(2, 2), whence);
        BitConverter.TryWriteBytes(b.AsSpan(8, 8), start);
        BitConverter.TryWriteBytes(b.AsSpan(16, 8), len);
        BitConverter.TryWriteBytes(b.AsSpan(24, 4), pid);
        Assert.IsTrue(p.TryWrite(mem, b));
        return Call(p, Fcntl, (ulong)fd, (ulong)cmd, mem);
    }

    private static (short Type, long Start, long Len, int Pid) ReadFlock(LinuxUserProcess p, ulong mem)
    {
        var b = new byte[32];
        Assert.IsTrue(p.TryRead(mem, b));
        return (BitConverter.ToInt16(b, 0), BitConverter.ToInt64(b, 8), BitConverter.ToInt64(b, 16), BitConverter.ToInt32(b, 24));
    }

    [TestMethod]
    public void WriteLockBlocksOtherProcessUntilClose()
    {
        var (a, ma) = NewProcess(1);
        var (b, mb) = NewProcess(2);
        int fa = OpenFile(a, ma), fb = OpenFile(b, mb);

        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 0x40000000, 1));
        Assert.AreEqual(-EAGAIN, Lock(b, mb, fb, SetLk, Rd, 0x40000000, 1));
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Wr, 0x40000001, 10), "angrenzender Bereich ist frei");

        Assert.AreEqual(0, Lock(b, mb, fb, GetLk, Rd, 0, 0));
        Assert.AreEqual(((short)Wr, 0x40000000L, 1L, 1), ReadFlock(b, mb));

        Assert.AreEqual(0, Call(a, Close, (ulong)fa));
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Wr, 0x40000000, 1), "close gibt die POSIX-Sperre frei");
    }

    [TestMethod]
    public void ReadLocksShareAndBlockWriters()
    {
        var (a, ma) = NewProcess(1);
        var (b, mb) = NewProcess(2);
        int fa = OpenFile(a, ma), fb = OpenFile(b, mb);

        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Rd, 0, 100));
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Rd, 50, 100));
        Assert.AreEqual(-EAGAIN, Lock(b, mb, fb, SetLk, Wr, 0, 1));

        // Eigene Lesesperre zur Schreibsperre hochstufen scheitert am anderen Leser, gelingt außerhalb seines Bereichs.
        Assert.AreEqual(-EAGAIN, Lock(a, ma, fa, SetLk, Wr, 0, 100));
        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 0, 50));

        Assert.AreEqual(0, Lock(b, mb, fb, GetLk, Rd, 0, 0));
        Assert.AreEqual(((short)Wr, 0L, 50L, 1), ReadFlock(b, mb));
    }

    [TestMethod]
    public void UnlockSplitsAndMergesOwnRanges()
    {
        var (a, ma) = NewProcess(1);
        var (b, mb) = NewProcess(2);
        int fa = OpenFile(a, ma), fb = OpenFile(b, mb);

        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 0, 100));
        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Un, 10, 10));
        Assert.AreEqual(2, _open.Locks.Count, "Loch teilt den Bereich");
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Wr, 10, 10));
        Assert.AreEqual(-EAGAIN, Lock(b, mb, fb, SetLk, Wr, 9, 1));
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Un, 0, 0));

        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 10, 10));
        Assert.AreEqual(1, _open.Locks.Count, "angrenzende Bereiche gleichen Typs verschmelzen");
        Assert.AreEqual(0, Lock(b, mb, fb, GetLk, Wr, 0, 0));
        Assert.AreEqual(((short)Wr, 0L, 100L, 1), ReadFlock(b, mb));

        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Un, 0, 0));
        Assert.AreEqual(0, _open.Locks.Count);
        Assert.AreEqual(0, Lock(b, mb, fb, GetLk, Wr, 0, 0));
        Assert.AreEqual(Un, ReadFlock(b, mb).Type);
    }

    [TestMethod]
    public void CloseOfAnyDescriptorDropsPosixLocksOfTheFile()
    {
        var (a, ma) = NewProcess(1);
        var (b, mb) = NewProcess(2);
        int f1 = OpenFile(a, ma), f2 = OpenFile(a, ma), fb = OpenFile(b, mb);
        int other = OpenFile(a, ma, "/other");

        Assert.AreEqual(0, Lock(a, ma, f1, SetLk, Wr, 0, 0));
        Assert.AreEqual(0, Lock(a, ma, other, SetLk, Wr, 0, 0));
        Assert.AreEqual(0, Lock(a, ma, f2, SetLk, Wr, 0, 0), "eigene Sperre über eine zweite Nummer");
        Assert.AreEqual(0, Call(a, Close, (ulong)f2));
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Wr, 0, 0), "close der zweiten Nummer löst auch die Sperre über die erste (POSIX)");
        Assert.AreEqual(2, _open.Locks.Count, "Sperre auf /other bleibt (dazu die neue von b)");
    }

    [TestMethod]
    public void OfdLocksBelongToTheOpenFile()
    {
        var (a, ma) = NewProcess(1);
        int f1 = OpenFile(a, ma), f2 = OpenFile(a, ma);
        int dup = (int)Call(a, Dup, (ulong)f1);

        Assert.AreEqual(0, Lock(a, ma, f1, OfdSetLk, Wr, 0, 10));
        Assert.AreEqual(-EAGAIN, Lock(a, ma, f2, OfdSetLk, Wr, 5, 1), "andere offene Datei im selben Prozess");
        Assert.AreEqual(-EAGAIN, Lock(a, ma, f2, SetLk, Rd, 5, 1), "POSIX und OFD sind verschiedene Besitzer");
        Assert.AreEqual(0, Lock(a, ma, dup, OfdSetLk, Wr, 5, 1), "dup teilt die offene Datei");

        Assert.AreEqual(0, Lock(a, ma, f2, OfdGetLk, Rd, 0, 0));
        Assert.AreEqual(((short)Wr, 0L, 10L, -1), ReadFlock(a, ma));

        Assert.AreEqual(0, Call(a, Close, (ulong)f1));
        Assert.AreEqual(-EAGAIN, Lock(a, ma, f2, OfdSetLk, Wr, 5, 1), "dup hält die offene Datei noch");
        Assert.AreEqual(0, Call(a, Close, (ulong)dup));
        Assert.AreEqual(0, Lock(a, ma, f2, OfdSetLk, Wr, 5, 1), "letztes close löst die OFD-Sperre");
    }

    [TestMethod]
    public void ProcessEndReleasesLocks()
    {
        var (a, ma) = NewProcess(1);
        var (b, mb) = NewProcess(2);
        int fa = OpenFile(a, ma), fb = OpenFile(b, mb);
        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 0, 0));
        Assert.AreEqual(-EAGAIN, Lock(a, ma, fa, OfdSetLk, Rd, 0, 0), "OFD gegen eigene POSIX-Sperre scheitert");

        a.Destroy();
        Assert.AreEqual(0, _open.Locks.Count);
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Wr, 0, 0));
    }

    [TestMethod]
    public void RangeRulesLikeLinux()
    {
        var (a, ma) = NewProcess(1);
        var (b, mb) = NewProcess(2);
        int fa = OpenFile(a, ma), fb = OpenFile(b, mb);

        Assert.AreEqual(-EINVAL, Lock(a, ma, fa, SetLk, 3, 0, 0), "unbekannter Typ");
        Assert.AreEqual(-EINVAL, Lock(a, ma, fa, GetLk, Un, 0, 0), "F_GETLK mit F_UNLCK");
        Assert.AreEqual(-EINVAL, Lock(a, ma, fa, OfdSetLk, Wr, 0, 0, pid: 5), "OFD mit l_pid");
        Assert.AreEqual(-EINVAL, Lock(a, ma, fa, SetLk, Wr, -1, 1), "Anfang unter 0");
        Assert.AreEqual(-EINVAL, Lock(a, ma, fa, SetLk, Wr, 0, 1, whence: 3));
        Assert.AreEqual(-EINVAL, Lock(a, ma, fa, SetLk, Wr, 2, -3), "negative Länge vor 0");
        Assert.AreEqual(-EOVERFLOW, Lock(a, ma, fa, SetLk, Wr, long.MaxValue, 2));
        Assert.AreEqual(-EBADF, Lock(a, ma, 99, SetLk, Wr, 0, 0));
        Assert.AreEqual(-EINVAL, Lock(a, ma, 1, SetLk, Wr, 0, 0), "Terminal");

        // SEEK_CUR mit negativer Länge: [pos-4, pos-1].
        Assert.AreEqual(20L, Call(a, Lseek, (ulong)fa, 20, 0));
        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 0, -4, whence: 1));
        Assert.AreEqual(0, Lock(b, mb, fb, GetLk, Wr, 0, 0));
        Assert.AreEqual(((short)Wr, 16L, 4L, 1), ReadFlock(b, mb));
    }

    /// <summary>Schlafen ruft einen Rückruf; so gibt der Test die Sperre während des Wartens frei.</summary>
    private sealed class Host(Action onSleep) : ILinuxHost
    {
        public int Sleeps;
        public long RealtimeNanos => 0;
        public long MonotonicNanos => 0;
        public bool TryFillRandom(Span<byte> dst) => false;
        public void SleepNanos(long ns) { Sleeps++; onSleep(); }
    }

    [TestMethod]
    public void SetLkWaitRetriesUntilReleased()
    {
        var (a, ma) = NewProcess(1);
        int fa = OpenFile(a, ma);
        Assert.AreEqual(0, Lock(a, ma, fa, SetLkW, Wr, 0, 0), "ohne Gegenspieler sofort");

        Host? host = null;
        host = new Host(() =>
        {
            if (host!.Sleeps == 3)
            {
                Assert.AreEqual(0, Call(a, Close, (ulong)fa));
            }
        });
        var term = new TerminalDescriptor(new Lines());
        var b = new LinuxUserProcess(new Pages(_frames), term, 2, 64 << 20, host, new LinuxFileSystem(_disk, term), _open, _ids);
        ulong mb = b.MapAnonymous(4 * Page, 3);
        int fb = OpenFile(b, mb);

        Assert.AreEqual(0, Lock(b, mb, fb, SetLkW, Wr, 0, 0));
        Assert.AreEqual(3, host.Sleeps, "wartet, bis a die Sperre mit close freigibt");
        Assert.AreEqual(-EAGAIN, Lock(a, ma, OpenFile(a, ma), SetLk, Rd, 0, 1));
    }

    [TestMethod]
    public void FlockSharesExcludesAndIgnoresFcntlLocks()
    {
        const ulong Sh = 1, Ex = 2, Nb = 4, Unl = 8;
        var (a, ma) = NewProcess(1);
        var (b, mb) = NewProcess(2);
        int fa = OpenFile(a, ma), fb = OpenFile(b, mb);

        Assert.AreEqual(0, Call(a, Flock, (ulong)fa, Sh));
        Assert.AreEqual(0, Call(b, Flock, (ulong)fb, Sh | Nb), "Leser teilen");
        Assert.AreEqual(-EAGAIN, Call(b, Flock, (ulong)fb, Ex | Nb), "Umstufen scheitert am anderen Leser");
        Assert.AreEqual(0, Call(a, Flock, (ulong)fa, Ex | Nb), "b hat seine Lesesperre beim Umstufen verloren (wie Linux)");
        Assert.AreEqual(-EAGAIN, Call(b, Flock, (ulong)fb, Sh | Nb));

        // flock und fcntl sehen einander nicht; ein zweites open desselben Prozesses ist ein anderer Besitzer.
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Wr, 0, 0));
        int fa2 = OpenFile(a, ma);
        Assert.AreEqual(-EAGAIN, Call(a, Flock, (ulong)fa2, Sh | Nb));

        Assert.AreEqual(-EINVAL, Call(a, Flock, (ulong)fa, 3));
        Assert.AreEqual(-EBADF, Call(a, Flock, 99, Sh));

        // dup teilt die offene Datei: close der ersten Nummer gibt nichts frei, erst das letzte close.
        long dupped = Call(a, Dup, (ulong)fa);
        Assert.IsTrue(dupped >= 3);
        Assert.AreEqual(0, Call(a, Close, (ulong)fa));
        Assert.AreEqual(-EAGAIN, Call(b, Flock, (ulong)fb, Sh | Nb));
        Assert.AreEqual(0, Call(a, Close, (ulong)dupped));
        Assert.AreEqual(0, Call(b, Flock, (ulong)fb, Ex | Nb), "letztes close gibt die flock-Sperre frei");
        Assert.AreEqual(0, Call(b, Flock, (ulong)fb, Unl));
        Assert.AreEqual(0, Call(a, Flock, (ulong)fa2, Ex | Nb), "LOCK_UN gibt frei");
    }

    [TestMethod]
    public void CloseRangeReleasesPosixLocks()
    {
        var (a, ma) = NewProcess(1);
        var (b, mb) = NewProcess(2);
        int fa = OpenFile(a, ma), fb = OpenFile(b, mb);

        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 0, 0));
        Assert.AreEqual(-EAGAIN, Lock(b, mb, fb, SetLk, Wr, 0, 1));
        Assert.AreEqual(0, Call(a, CloseRange, (ulong)fa, (ulong)fa, 0));
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Wr, 0, 1), "close_range gibt die POSIX-Sperre frei wie close");
    }

    [TestMethod]
    public void SetLkWaitEndsWithEintrOnSignalWithHandler()
    {
        const int SigUsr1 = 10;
        var (a, ma) = NewProcess(1);
        int fa = OpenFile(a, ma);
        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 0, 0));

        Host? host = null;
        LinuxUserProcess? b = null;
        host = new Host(() =>
        {
            if (host!.Sleeps == 2)
            {
                b!.RaiseSignal(SigUsr1); // wie kill von außen während des Wartens
            }
        });
        var term = new TerminalDescriptor(new Lines());
        b = new LinuxUserProcess(new Pages(_frames), term, 2, 64 << 20, host, new LinuxFileSystem(_disk, term), _open, _ids);
        ulong mb = b.MapAnonymous(4 * Page, 3);
        int fb = OpenFile(b, mb);

        // Handler eintragen (struct sigaction: Handler, Flags, Restorer, Maske); ohne Handler beendete das Signal den Prozess.
        Assert.IsTrue(b.TryWrite(mb + 0x400, new byte[32]));
        Assert.IsTrue(b.TryWrite(mb + 0x400, BitConverter.GetBytes(0x401000UL)));
        Assert.AreEqual(0, Call(b, LinuxSyscalls.RtSigaction, SigUsr1, mb + 0x400, 0, 8));

        Assert.AreEqual(-EINTR, Lock(b, mb, fb, SetLkW, Wr, 0, 1));
        Assert.AreEqual(2, host.Sleeps);
        Assert.AreEqual(-EAGAIN, Lock(b, mb, fb, SetLk, Wr, 0, 1), "nichts gesetzt, a hält weiter");
    }

    [TestMethod]
    public void ForkChildNeitherInheritsNorReleasesPosixLocksButSharesFlock()
    {
        var (a, ma) = NewProcess(1);
        var (other, mo) = NewProcess(3);
        LinuxUserProcess? child = null;
        a.CreatePages = () => new Pages(_frames);
        a.StartFork = (c, stack) =>
        {
            child = c;
            return true;
        };
        int fa = OpenFile(a, ma), fo = OpenFile(other, mo);
        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 0, 10));
        Assert.AreEqual(0, Call(a, Flock, (ulong)fa, 2 | 4));

        Assert.IsTrue(Call(a, LinuxSyscalls.Fork) > 0);
        Assert.IsNotNull(child);

        // Das Kind erbt die POSIX-Sperre nicht (anderer Besitzer): es stößt an der des Elternteils an.
        Assert.AreEqual(-EAGAIN, Lock(child, ma, fa, SetLk, Rd, 0, 1));
        Assert.AreEqual(0, Lock(child, ma, fa, SetLk, Wr, 20, 1), "freier Bereich gehört dann dem Kind");
        Assert.AreEqual(0, Lock(other, mo, fo, GetLk, Wr, 20, 1));
        Assert.AreEqual(((short)Wr, 20L, 1L, child.Pid), ReadFlock(other, mo));

        // close im Kind gibt nur dessen POSIX-Sperren frei; die flock-Sperre teilt es mit dem Elternteil (gleiche offene Datei).
        Assert.AreEqual(0, Call(child, Close, (ulong)fa));
        Assert.AreEqual(-EAGAIN, Lock(other, mo, fo, SetLk, Rd, 0, 1), "Sperre des Elternteils bleibt");
        Assert.AreEqual(0, Lock(other, mo, fo, SetLk, Rd, 20, 1), "Sperre des Kindes ist frei");
        Assert.AreEqual(-EAGAIN, Call(other, Flock, (ulong)fo, 1 | 4), "flock hält, solange der Elternteil die Datei offen hat");
        Assert.AreEqual(0, Call(a, Close, (ulong)fa));
        Assert.AreEqual(0, Call(other, Flock, (ulong)fo, 1 | 4), "letztes close gibt flock frei");
    }

    [TestMethod]
    public void Dup2OntoLockedNumberReleasesPosixLocks()
    {
        var (a, ma) = NewProcess(1);
        var (b, mb) = NewProcess(2);
        int fa = OpenFile(a, ma), fb = OpenFile(b, mb);
        int other = OpenFile(a, ma, "/anders");

        Assert.AreEqual(0, Lock(a, ma, fa, SetLk, Wr, 0, 100));
        Assert.AreEqual(0, Lock(a, ma, fa, OfdSetLk, Rd, 100, 1), "OFD ist ein anderer Besitzer: nicht überlappend");
        Assert.AreEqual(-EAGAIN, Lock(b, mb, fb, SetLk, Rd, 0, 1));

        Assert.AreEqual((long)fa, Call(a, Dup2, (ulong)other, (ulong)fa));
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Rd, 0, 1), "dup2 schließt die alte Nummer und gibt die POSIX-Sperre frei");
        Assert.AreEqual(0, Lock(b, mb, fb, SetLk, Wr, 100, 1), "letzte Nummer der offenen Datei weg: OFD-Sperre frei");
    }

    [TestMethod]
    public void ChmodChecksTargetAndLinkFailsLikeVfat()
    {
        var (a, ma) = NewProcess(1);
        int fd = OpenFile(a, ma, "/f");
        Assert.AreEqual(0, Call(a, Chmod, Str(a, ma, "/f"), 0x180));
        Assert.AreEqual(-ENOENT, Call(a, Chmod, Str(a, ma, "/nix"), 0x180));
        Assert.AreEqual(0, Call(a, Fchmod, (ulong)fd, 0x180));
        Assert.AreEqual(-EBADF, Call(a, Fchmod, 99, 0x180));
        Assert.AreEqual(-EINVAL, Call(a, Fchmodat2, unchecked((ulong)-100), Str(a, ma, "/f"), 0x180, 0x1));

        Assert.AreEqual(-EPERM, Call(a, Link, Str(a, ma, "/f"), Str(a, ma + 256, "/g")));
        Assert.AreEqual(-EEXIST, Call(a, Link, Str(a, ma, "/f"), Str(a, ma + 256, "/f")));
        Assert.AreEqual(-ENOENT, Call(a, Link, Str(a, ma, "/nix"), Str(a, ma + 256, "/g")));
        Assert.AreEqual(-ENOENT, Call(a, Link, Str(a, ma, "/f"), Str(a, ma + 256, "/kein/g")));
        Assert.AreEqual(-EINVAL, Call(a, Linkat, unchecked((ulong)-100), Str(a, ma, "/f"), unchecked((ulong)-100), Str(a, ma + 256, "/g"), 0x1));
    }
}
