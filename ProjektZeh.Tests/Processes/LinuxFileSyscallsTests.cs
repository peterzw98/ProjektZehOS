#pragma warning disable CA1822 // Stubs der Schnittstelle
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Tests.Processes;

[TestClass]
public class LinuxFileSyscallsTests
{
    private const ulong Base = 0x10000;
    private const long Read = 0, Open = 2, Close = 3, Lseek = 8, Pread64 = 17, Readv = 19, Dup = 32, Dup2 = 33, Fcntl = 72,
        Getcwd = 79, Chdir = 80, Fchdir = 81, Openat = 257, Dup3 = 292, Write = 1;
    private static readonly ulong AtFdcwd = unchecked((ulong)-100L);

    private sealed class Mem : IUserMemory
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
            if (addr < Base || addr >= Base + (ulong)Data.Length) return false;
            int at = (int)(addr - Base), len = 0;
            while (at + len < Data.Length && Data[at + len] != 0) len++;
            if (at + len >= Data.Length || len > max) return false;
            s = Encoding.UTF8.GetString(Data, at, len);
            return true;
        }

        public ulong Put(int offset, string text)
        {
            Encoding.UTF8.GetBytes(text).CopyTo(Data, offset);
            Data[offset + Encoding.UTF8.GetByteCount(text)] = 0;
            return Base + (ulong)offset;
        }
    }

    private sealed class Term : ILinuxDescriptor
    {
        public long Write(ReadOnlySpan<byte> data) => data.Length;
    }

    private sealed class MemFile(string path, bool dir, byte[] content) : ILinuxFile
    {
        public string Path { get; } = path;
        public bool IsDirectory { get; } = dir;
        public long DirIndex { get; set; }
        public long Pos;
        public int Closes;
        public long Write(ReadOnlySpan<byte> data) => -9;
        public long Read(Span<byte> dst)
        {
            if (IsDirectory) return -21;
            int n = (int)Math.Max(0, Math.Min(dst.Length, content.Length - Pos));
            content.AsSpan((int)Pos, n).CopyTo(dst);
            Pos += n;
            return n;
        }
        public long Seek(long offset, int whence)
        {
            long t = whence switch { 0 => offset, 1 => Pos + offset, _ => content.Length + offset };
            if (t < 0) return -22;
            Pos = t;
            return t;
        }
        public long Stat(out LinuxStat st) { st = default; return 0; }
        public long Truncate(long length) => -22;
        public long Sync() => 0;
        public long ReadDir(long index, out LinuxDirEntry entry) { entry = default; return 1; }
        public long Close()
        {
            Closes++;
            return CloseResult;
        }

        public long CloseResult { get; set; }
    }

    private sealed class Fs : ILinuxFileSystem
    {
        public readonly List<string> Calls = new();
        public readonly Dictionary<string, MemFile> Files = new();
        public long Open(string path, int flags, uint mode, out ILinuxFile? file)
        {
            Calls.Add($"open {path} {flags:x} {Convert.ToString(mode, 8)}");
            file = Files.GetValueOrDefault(path);
            return file is null ? -2 : 0;
        }
        public long Stat(string path, bool follow, out LinuxStat st)
        {
            st = new LinuxStat { Mode = path == "/" || path.StartsWith("/dir", StringComparison.Ordinal) ? 0x41edu : 0x81a4u };
            return path.Contains("nix", StringComparison.Ordinal) ? -2 : 0;
        }
        public long Access(string path, int mode) => path.Contains("geheim", StringComparison.Ordinal) ? -13 : 0;
        public long ReadLink(string path, out string target) { target = ""; return -22; }
        public long Mkdir(string path, uint mode) => 0;
        public long Unlink(string path) => 0;
        public long Rmdir(string path) => 0;
        public long Rename(string a, string b) => 0;
        public long Symlink(string target, string link) => 0;
    }

    private sealed class Proc : ILinuxProcess, ILinuxFiles
    {
        public readonly Mem Mem = new();
        public readonly Fs Fs = new();
        public readonly Dictionary<int, ILinuxDescriptor> Table = new() { [0] = new Term(), [1] = new Term(), [2] = new Term() };
        public int Max = 1024;
        public IUserMemory Memory => Mem;
        public ILinuxDescriptor? GetDescriptor(int fd) => Table.GetValueOrDefault(fd);
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
        public ILinuxFileSystem FileSystem => Fs;
        public object FileGate { get; } = new();
        public string Cwd { get; set; } = "/home/u";
        public int AddDescriptor(ILinuxDescriptor d, int min)
        {
            for (int i = min; i < Max; i++)
            {
                if (!Table.ContainsKey(i)) { Table[i] = d; return i; }
            }
            return -24;
        }
        public bool RemoveDescriptor(int fd) => Table.Remove(fd);
    }

    private static long Call(ILinuxProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    private static Proc WithFile(string path, string content)
    {
        var p = new Proc();
        p.Fs.Files[path] = new MemFile(path, false, Encoding.UTF8.GetBytes(content));
        return p;
    }

    [TestMethod]
    [DataRow("/", "/")]
    [DataRow("/a//b/./c", "/a/b/c")]
    [DataRow("/a/b/../../../c", "/c")]
    [DataRow("/a/", "/a/")]
    [DataRow("/a/.", "/a/")]
    [DataRow("/a/b/..", "/a/")]
    [DataRow("/..", "/")]
    [DataRow("/a/..", "/")]
    public void NormalizeKeepsTrailingDirectoryMarker(string input, string expected)
    {
        Assert.AreEqual(expected, LinuxPath.Normalize(input));
    }

    [TestMethod]
    public void ResolveUsesCwdOrDirfd()
    {
        var p = new Proc();
        p.Table[7] = new MemFile("/etc", true, []);
        p.Table[8] = new MemFile("/etc/x", false, []);
        Assert.AreEqual(0L, LinuxPath.Resolve(p, p, LinuxPath.AtFdcwd, "a/../b", out string path));
        Assert.AreEqual("/home/u/b", path);
        Assert.AreEqual(0L, LinuxPath.Resolve(p, p, 7, "k", out path));
        Assert.AreEqual("/etc/k", path);
        Assert.AreEqual(0L, LinuxPath.Resolve(p, p, 99, "/abs", out path), "absoluter Pfad ignoriert dirfd");
        Assert.AreEqual(-9L, LinuxPath.Resolve(p, p, 99, "k", out _));
        Assert.AreEqual(-20L, LinuxPath.Resolve(p, p, 8, "k", out _), "Datei als Verzeichnis-fd");
        Assert.AreEqual(-20L, LinuxPath.Resolve(p, p, 1, "k", out _), "Terminal als Verzeichnis-fd");
        Assert.AreEqual(-2L, LinuxPath.Resolve(p, p, LinuxPath.AtFdcwd, "", out _));
    }

    [TestMethod]
    public void ReadPathDistinguishesTooLongFromFault()
    {
        var p = new Proc();
        Assert.AreEqual(-14L, LinuxPath.ReadPath(p, 0x10, out _));
        p.Mem.Data.AsSpan(0x100, 5000).Fill((byte)'a');
        Assert.AreEqual(-36L, LinuxPath.ReadPath(p, Base + 0x100, out _));
    }

    // V14: pwrite64, preadv/pwritev, ftruncate, fsync/fdatasync auf echten Dateien (LinuxFileSystem über MemoryFileStore).
    private const long Pwrite64 = 18, Fsync = 74, Fdatasync = 75, Ftruncate = 77, Preadv = 295, Pwritev = 296;

    private static (Proc P, LinuxFileSystem Fs) WithRealFs()
    {
        var p = new Proc();
        var fs = new LinuxFileSystem(new MemoryFileStore(), new Term());
        return (p, fs);
    }

    private static int OpenReal(Proc p, LinuxFileSystem fs, string path, int flags)
    {
        Assert.AreEqual(0L, fs.Open(path, flags, 0x1A4, out ILinuxFile? file), path);
        return p.AddDescriptor(file!, 3);
    }

    private static string Content(LinuxFileSystem fs, string path)
    {
        Assert.AreEqual(0L, fs.Open(path, LinuxFileSystem.ORdOnly, 0, out ILinuxFile? file));
        var buf = new byte[256];
        long n = file!.Read(buf);
        file.Close();
        return Encoding.Latin1.GetString(buf, 0, (int)n);
    }

    private static void PutIov(Proc p, int at, params (ulong Base, ulong Len)[] entries)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(at + i * 16, 8), entries[i].Base);
            BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(at + i * 16 + 8, 8), entries[i].Len);
        }
    }

    [TestMethod]
    public void PwriteWritesAtOffsetAndKeepsPosition()
    {
        var (p, fs) = WithRealFs();
        int fd = OpenReal(p, fs, "/a", LinuxFileSystem.OCreat | LinuxFileSystem.ORdWr);
        p.Mem.Put(0x100, "abcdef");
        Assert.AreEqual(6L, Call(p, Write, (ulong)fd, Base + 0x100, 6));
        p.Mem.Put(0x100, "XY");
        Assert.AreEqual(2L, Call(p, Pwrite64, (ulong)fd, Base + 0x100, 2, 1));
        Assert.AreEqual(6L, Call(p, Lseek, (ulong)fd, 0, 1), "Position bleibt");
        Assert.AreEqual(2L, Call(p, Pwrite64, (ulong)fd, Base + 0x100, 2, 10), "hinter dem Ende: Lücke");
        Assert.AreEqual(6L, Call(p, Lseek, (ulong)fd, 0, 1));
        Assert.AreEqual("aXYdef\0\0\0\0XY", Content(fs, "/a"));
        Assert.AreEqual(-22L, Call(p, Pwrite64, 99, Base + 0x100, 2, unchecked((ulong)-1L)), "negative Position vor dem Deskriptor");
        Assert.AreEqual(-9L, Call(p, Pwrite64, 99, Base + 0x100, 2, 0));
        Assert.AreEqual(-29L, Call(p, Pwrite64, 1, Base + 0x100, 2, 0), "Terminal");
        Assert.AreEqual(-14L, Call(p, Pwrite64, (ulong)fd, 0x10, 2, 0));
        Assert.AreEqual(6L, Call(p, Lseek, (ulong)fd, 0, 1), "Position auch nach Fehler");
        Assert.AreEqual(-22L, Call(p, Pwrite64, (ulong)fd, Base + 0x100, 2, (ulong)long.MaxValue), "Überlauf");
    }

    [TestMethod]
    public void PwriteRespectsAccessModeAndAppend()
    {
        var (p, fs) = WithRealFs();
        int w = OpenReal(p, fs, "/a", LinuxFileSystem.OCreat | LinuxFileSystem.OWrOnly);
        p.Mem.Put(0x100, "1234");
        Assert.AreEqual(4L, Call(p, Write, (ulong)w, Base + 0x100, 4));
        int r = OpenReal(p, fs, "/a", LinuxFileSystem.ORdOnly);
        Assert.AreEqual(-9L, Call(p, Pwrite64, (ulong)r, Base + 0x100, 1, 0), "nur lesend offen");
        int ap = OpenReal(p, fs, "/a", LinuxFileSystem.OWrOnly | LinuxFileSystem.OAppend);
        p.Mem.Put(0x100, "Z");
        Assert.AreEqual(1L, Call(p, Pwrite64, (ulong)ap, Base + 0x100, 1, 0), "O_APPEND hängt wie Linux an");
        Assert.AreEqual(0L, Call(p, Lseek, (ulong)ap, 0, 1));
        Assert.AreEqual("1234Z", Content(fs, "/a"));
        Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ed));
        int d = OpenReal(p, fs, "/d", LinuxFileSystem.ORdOnly);
        Assert.AreEqual(-9L, Call(p, Pwrite64, (ulong)d, Base + 0x100, 1, 0), "Verzeichnis");
        Assert.AreEqual(-21L, Call(p, Preadv, (ulong)d, Base + 0x300, 1, 0), "Verzeichnis lesen");
    }

    [TestMethod]
    public void PreadvPwritevUseOffset()
    {
        var (p, fs) = WithRealFs();
        int fd = OpenReal(p, fs, "/v", LinuxFileSystem.OCreat | LinuxFileSystem.ORdWr);
        p.Mem.Put(0x100, "hallo");
        p.Mem.Put(0x110, "welt");
        PutIov(p, 0x300, (Base + 0x100, 5), (Base + 0x110, 4));
        Assert.AreEqual(9L, Call(p, Pwritev, (ulong)fd, Base + 0x300, 2, 3));
        Assert.AreEqual(0L, Call(p, Lseek, (ulong)fd, 0, 1));
        Assert.AreEqual(12L, Call(p, Lseek, (ulong)fd, 0, 2));
        PutIov(p, 0x300, (Base + 0x200, 2), (Base + 0x210, 3));
        Assert.AreEqual(5L, Call(p, Preadv, (ulong)fd, Base + 0x300, 2, 4));
        Assert.AreEqual("al", Encoding.UTF8.GetString(p.Mem.Data, 0x200, 2));
        Assert.AreEqual("low", Encoding.UTF8.GetString(p.Mem.Data, 0x210, 3));
        Assert.AreEqual(12L, Call(p, Lseek, (ulong)fd, 0, 1), "Position bleibt");
        Assert.AreEqual(0L, Call(p, Preadv, (ulong)fd, Base + 0x300, 2, 100), "hinter dem Ende");
        Assert.AreEqual(-22L, Call(p, Pwritev, (ulong)fd, Base + 0x300, 1025, 0));
        Assert.AreEqual(-22L, Call(p, Preadv, 99, Base + 0x300, 1, unchecked((ulong)-5L)));
        Assert.AreEqual(-14L, Call(p, Pwritev, (ulong)fd, 0x10, 1, 0));
        Assert.AreEqual(-29L, Call(p, Preadv, 0, Base + 0x300, 1, 0), "Terminal");
    }

    [TestMethod]
    public void FtruncateShrinksGrowsAndChecksMode()
    {
        var (p, fs) = WithRealFs();
        int fd = OpenReal(p, fs, "/t", LinuxFileSystem.OCreat | LinuxFileSystem.ORdWr);
        p.Mem.Put(0x100, "abcdef");
        Assert.AreEqual(6L, Call(p, Write, (ulong)fd, Base + 0x100, 6));
        Assert.AreEqual(0L, Call(p, Ftruncate, (ulong)fd, 3));
        Assert.AreEqual(6L, Call(p, Lseek, (ulong)fd, 0, 1), "Position bleibt");
        Assert.AreEqual("abc", Content(fs, "/t"));
        Assert.AreEqual(0L, Call(p, Ftruncate, (ulong)fd, 5));
        Assert.AreEqual("abc\0\0", Content(fs, "/t"));
        Assert.AreEqual(-22L, Call(p, Ftruncate, 99, unchecked((ulong)-1L)), "Länge vor dem Deskriptor");
        Assert.AreEqual(-9L, Call(p, Ftruncate, 99, 0));
        Assert.AreEqual(-22L, Call(p, Ftruncate, 1, 0), "Terminal");
        int r = OpenReal(p, fs, "/t", LinuxFileSystem.ORdOnly);
        Assert.AreEqual(-22L, Call(p, Ftruncate, (ulong)r, 0), "nur lesend offen");
        Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ed));
        int d = OpenReal(p, fs, "/d", LinuxFileSystem.ORdOnly);
        Assert.AreEqual(-22L, Call(p, Ftruncate, (ulong)d, 0), "Verzeichnis");
        int n = OpenReal(p, fs, "/dev/null", LinuxFileSystem.OWrOnly);
        Assert.AreEqual(-22L, Call(p, Ftruncate, (ulong)n, 0), "Gerät");
        Assert.AreEqual(-27L, Call(p, Ftruncate, (ulong)fd, 1L << 40), "über der Dateigrenze des Speichers");
        Assert.AreEqual(5L, Call(p, Lseek, (ulong)fd, 0, 2));
    }

    [TestMethod]
    public void FsyncAcceptsFilesAndDirectoriesOnly()
    {
        var (p, fs) = WithRealFs();
        int fd = OpenReal(p, fs, "/s", LinuxFileSystem.OCreat | LinuxFileSystem.ORdWr);
        Assert.AreEqual(0L, Call(p, Fsync, (ulong)fd));
        Assert.AreEqual(0L, Call(p, Fdatasync, (ulong)fd));
        int r = OpenReal(p, fs, "/s", LinuxFileSystem.ORdOnly);
        Assert.AreEqual(0L, Call(p, Fsync, (ulong)r), "auch nur lesend offen");
        int d = OpenReal(p, fs, "/", LinuxFileSystem.ORdOnly);
        Assert.AreEqual(0L, Call(p, Fsync, (ulong)d), "Verzeichnis");
        int n = OpenReal(p, fs, "/dev/null", LinuxFileSystem.ORdWr);
        Assert.AreEqual(-22L, Call(p, Fsync, (ulong)n), "Gerät");
        Assert.AreEqual(-22L, Call(p, Fdatasync, 1), "Terminal");
        Assert.AreEqual(-9L, Call(p, Fsync, 99));
    }

    [TestMethod]
    public void OpenReadSeekClose()
    {
        var p = WithFile("/home/u/d.txt", "Hallo Welt");
        long fd = Call(p, Open, p.Mem.Put(0x100, "./d.txt"), 0x80000, 0x1ff);
        Assert.AreEqual(3L, fd, "kleinste freie Nummer");
        Assert.AreEqual("open /home/u/d.txt 80000 777", p.Fs.Calls[^1]);
        Assert.AreEqual(5L, Call(p, Read, (ulong)fd, Base + 0x200, 5));
        Assert.AreEqual("Hallo", Encoding.UTF8.GetString(p.Mem.Data, 0x200, 5));
        Assert.AreEqual(6L, Call(p, Lseek, (ulong)fd, unchecked((ulong)-4L), 2));
        Assert.AreEqual(4L, Call(p, Read, (ulong)fd, Base + 0x200, 100), "kurzes Lesen am Ende");
        Assert.AreEqual(0L, Call(p, Read, (ulong)fd, Base + 0x200, 100), "Ende");
        Assert.AreEqual(-22L, Call(p, Lseek, (ulong)fd, 0, 3));
        var f = p.Fs.Files["/home/u/d.txt"];
        Assert.AreEqual(0L, Call(p, Close, (ulong)fd));
        Assert.AreEqual(1, f.Closes);
        Assert.AreEqual(-9L, Call(p, Close, (ulong)fd));
        Assert.AreEqual(1, f.Closes, "kein zweites Close");
    }

    [TestMethod]
    public void OpenErrors()
    {
        var p = WithFile("/x", "1");
        Assert.AreEqual(-2L, Call(p, Open, p.Mem.Put(0x100, "fehlt"), 0, 0));
        Assert.AreEqual(-14L, Call(p, Open, 0x10, 0, 0));
        p.Max = 3;
        Assert.AreEqual(-24L, Call(p, Open, p.Mem.Put(0x100, "/x"), 0, 0));
        Assert.AreEqual(1, p.Fs.Files["/x"].Closes, "ohne Deskriptor gleich wieder geschlossen");
    }

    [TestMethod]
    public void OpenatRelativeToDirectory()
    {
        var p = WithFile("/etc/passwd", "root");
        p.Table[9] = new MemFile("/etc", true, []);
        Assert.AreEqual(3L, Call(p, Openat, 9, p.Mem.Put(0x100, "passwd"), 0, 0));
        Assert.AreEqual(4L, Call(p, Openat, AtFdcwd, p.Mem.Put(0x100, "/etc/passwd"), 0, 0));
        Assert.AreEqual(-20L, Call(p, Openat, 3, p.Mem.Put(0x100, "x"), 0, 0));
        Assert.AreEqual(-9L, Call(p, Openat, 50, p.Mem.Put(0x100, "x"), 0, 0));
    }

    [TestMethod]
    public void ReadFaultRewindsPosition()
    {
        var p = WithFile("/f", "abcdef");
        Call(p, Open, p.Mem.Put(0x100, "/f"), 0, 0);
        Assert.AreEqual(-14L, Call(p, Read, 3, 0x10, 4));
        Assert.AreEqual(0L, p.Fs.Files["/f"].Pos, "nichts verbraucht");
        Assert.AreEqual(-9L, Call(p, Read, 1, Base, 4), "Terminal nicht lesbar");
        Assert.AreEqual(-9L, Call(p, Read, 40, Base, 4));
    }

    [TestMethod]
    public void ReadOnDirectoryIsEisdirAndLseekMovesDirIndex()
    {
        var p = new Proc();
        var dir = new MemFile("/dir", true, []);
        p.Table[3] = dir;
        Assert.AreEqual(-21L, Call(p, Read, 3, Base, 4));
        dir.DirIndex = 5;
        Assert.AreEqual(5L, Call(p, Lseek, 3, 0, 1), "telldir");
        Assert.AreEqual(0L, Call(p, Lseek, 3, 0, 0), "rewinddir");
        Assert.AreEqual(0L, dir.DirIndex);
        Assert.AreEqual(-22L, Call(p, Lseek, 3, 0, 2));
        Assert.AreEqual(-29L, Call(p, Lseek, 1, 0, 0), "Terminal");
    }

    [TestMethod]
    public void ReadvFillsSeveralBuffers()
    {
        var p = WithFile("/f", "abcdefgh");
        Call(p, Open, p.Mem.Put(0x100, "/f"), 0, 0);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x300), Base + 0x400);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x308), 3UL);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x310), Base + 0x500);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0x318), 10UL);
        Assert.AreEqual(8L, Call(p, Readv, 3, Base + 0x300, 2));
        Assert.AreEqual("abc", Encoding.UTF8.GetString(p.Mem.Data, 0x400, 3));
        Assert.AreEqual("defgh", Encoding.UTF8.GetString(p.Mem.Data, 0x500, 5));
        Assert.AreEqual(-22L, Call(p, Readv, 3, Base + 0x300, 2000));
    }

    [TestMethod]
    public void PreadKeepsPosition()
    {
        var p = WithFile("/f", "0123456789");
        Call(p, Open, p.Mem.Put(0x100, "/f"), 0, 0);
        Call(p, Read, 3, Base + 0x200, 2);
        Assert.AreEqual(3L, Call(p, Pread64, 3, Base + 0x200, 3, 6));
        Assert.AreEqual("678", Encoding.UTF8.GetString(p.Mem.Data, 0x200, 3));
        Assert.AreEqual(2L, p.Fs.Files["/f"].Pos);
        Assert.AreEqual(-22L, Call(p, Pread64, 3, Base, 1, unchecked((ulong)-1L)));
        Assert.AreEqual(-29L, Call(p, Pread64, 1, Base, 1, 0));
    }

    [TestMethod]
    public void DupSharesFileAndClosesOnLastReference()
    {
        var p = WithFile("/f", "x");
        Call(p, Open, p.Mem.Put(0x100, "/f"), 0, 0);
        var f = p.Fs.Files["/f"];
        Assert.AreEqual(4L, Call(p, Dup, 3));
        Assert.AreEqual(10L, Call(p, Fcntl, 3, 0, 10), "F_DUPFD ab 10");
        Assert.AreEqual(0L, Call(p, Close, 3));
        Assert.AreEqual(0L, Call(p, Close, 10));
        Assert.AreEqual(0, f.Closes, "fd 4 zeigt noch darauf");
        Assert.AreEqual(0L, Call(p, Close, 4));
        Assert.AreEqual(1, f.Closes);
    }

    [TestMethod]
    public void CloseReportsWritebackErrorOnLastReference()
    {
        var p = WithFile("/f", "x");
        Call(p, Open, p.Mem.Put(0x100, "/f"), 0, 0);
        var f = p.Fs.Files["/f"];
        f.CloseResult = -5;
        Assert.AreEqual(4L, Call(p, Dup, 3));
        Assert.AreEqual(0L, Call(p, Close, 3), "nicht der letzte Verweis: kein Schließen, kein Fehler");
        Assert.AreEqual(-5L, Call(p, Close, 4), "-EIO beim Zurückschreiben erreicht das Programm");
        Assert.AreEqual(-9L, Call(p, Close, 4), "die Nummer ist trotz Fehler frei");
        Assert.AreEqual(1, f.Closes);
    }

    [TestMethod]
    public void Dup2ReplacesTarget()
    {
        var p = WithFile("/f", "x");
        p.Fs.Files["/g"] = new MemFile("/g", false, []);
        Call(p, Open, p.Mem.Put(0x100, "/f"), 0, 0);
        Call(p, Open, p.Mem.Put(0x100, "/g"), 0, 0);
        Assert.AreEqual(4L, Call(p, Dup2, 3, 4));
        Assert.AreEqual(1, p.Fs.Files["/g"].Closes, "altes Ziel still geschlossen");
        Assert.AreSame(p.Table[3], p.Table[4]);
        Assert.AreEqual(1L, Call(p, Dup2, 3, 1), "Terminal ersetzt, z. B. Umleitung von stdout");
        Assert.AreEqual(3L, Call(p, Dup2, 3, 3));
        Assert.AreEqual(-22L, Call(p, Dup3, 3, 3, 0));
        Assert.AreEqual(-22L, Call(p, Dup3, 3, 5, 1));
        Assert.AreEqual(5L, Call(p, Dup3, 3, 5, 0x80000));
        Assert.AreEqual(-9L, Call(p, Dup2, 30, 5));
        Assert.AreEqual(-9L, Call(p, Dup2, 3, 5000));
    }

    [TestMethod]
    public void FcntlFlags()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, Fcntl, 1, 1));
        Assert.AreEqual(0L, Call(p, Fcntl, 1, 2, 1));
        Assert.AreEqual(2L, Call(p, Fcntl, 1, 3));
        Assert.AreEqual(0L, Call(p, Fcntl, 1, 4, 0x800));
        Assert.AreEqual(-22L, Call(p, Fcntl, 1, 999));
        Assert.AreEqual(-9L, Call(p, Fcntl, 40, 1));
    }

    [TestMethod]
    public void GetcwdAndChdir()
    {
        var p = new Proc();
        Assert.AreEqual(8L, Call(p, Getcwd, Base + 0x200, 100), "Länge mit NUL");
        Assert.AreEqual("/home/u\0", Encoding.UTF8.GetString(p.Mem.Data, 0x200, 8));
        Assert.AreEqual(-34L, Call(p, Getcwd, Base + 0x200, 7));
        Assert.AreEqual(-14L, Call(p, Getcwd, 0x10, 100));

        Assert.AreEqual(0L, Call(p, Chdir, p.Mem.Put(0x100, "/dir/a/")));
        Assert.AreEqual("/dir/a", p.Cwd, "ohne abschließendes /");
        Assert.AreEqual(0L, Call(p, Chdir, p.Mem.Put(0x100, "..")));
        Assert.AreEqual("/dir", p.Cwd);
        Assert.AreEqual(-20L, Call(p, Chdir, p.Mem.Put(0x100, "/datei")));
        Assert.AreEqual(-2L, Call(p, Chdir, p.Mem.Put(0x100, "/dir/nix")));
        Assert.AreEqual(-13L, Call(p, Chdir, p.Mem.Put(0x100, "/dir/geheim")));
        Assert.AreEqual("/dir", p.Cwd, "Fehler ändern cwd nicht");
        Assert.AreEqual(0L, Call(p, Chdir, p.Mem.Put(0x100, "/..")));
        Assert.AreEqual("/", p.Cwd);
    }

    [TestMethod]
    public void FchdirUsesDirectoryPath()
    {
        var p = new Proc();
        p.Table[3] = new MemFile("/dir/x", true, []);
        p.Table[4] = new MemFile("/datei", false, []);
        Assert.AreEqual(0L, Call(p, Fchdir, 3));
        Assert.AreEqual("/dir/x", p.Cwd);
        Assert.AreEqual(-20L, Call(p, Fchdir, 4));
        Assert.AreEqual(-20L, Call(p, Fchdir, 1));
        Assert.AreEqual(-9L, Call(p, Fchdir, 40));
        p.Table[5] = new MemFile("/dir/y/", true, []);
        Assert.AreEqual(0L, Call(p, Fchdir, 5));
        Assert.AreEqual("/dir/y", p.Cwd, "abschließendes / aus open(\"dir/\") fällt weg");
        p.Table[6] = new MemFile("/", true, []);
        Assert.AreEqual(0L, Call(p, Fchdir, 6));
        Assert.AreEqual("/", p.Cwd);
    }

    [TestMethod]
    public void WithoutFileCapabilityEverythingIsEnosys()
    {
        var p = new NoFiles();
        Assert.AreEqual(-38L, Call(p, Open, Base, 0, 0));
        Assert.AreEqual(-38L, Call(p, Close, 1));
        Assert.AreEqual(-38L, Call(p, 4 /* stat */, Base, Base));
        Assert.AreEqual(5L, Call(p, Write, 1, Base, 5), "Schreiben bleibt wie S1");
    }

    private sealed class NoFiles : ILinuxProcess
    {
        private readonly Mem _mem = new();
        public IUserMemory Memory => _mem;
        public ILinuxDescriptor? GetDescriptor(int fd) => fd == 1 ? new Term() : null;
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
    }
}
