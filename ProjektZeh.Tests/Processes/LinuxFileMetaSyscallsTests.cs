#pragma warning disable CA1822 // Stubs der Schnittstelle
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Tests.Processes;

[TestClass]
public class LinuxFileMetaSyscallsTests
{
    private const ulong Base = 0x10000;

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

    private sealed class FakeFile(string path, bool dir) : ILinuxFile
    {
        public string Path { get; } = path;
        public bool IsDirectory { get; } = dir;
        public long DirIndex { get; set; }
        public List<LinuxDirEntry> Entries = new() { new(1, 4, "."), new(1, 4, ".."), new(5, 8, "datei.txt"), new(6, 4, "ä") };
        public long Write(ReadOnlySpan<byte> data) => -9;
        public long Read(Span<byte> dst) => 0;
        public long Seek(long offset, int whence) => 0;
        public long Stat(out LinuxStat st)
        {
            st = new LinuxStat { Dev = 3, Ino = 77, Mode = IsDirectory ? 0x41ed : 0x81a4u, Nlink = 2, Uid = 1000, Gid = 100, Size = 1234,
                BlockSize = 4096, Blocks = 8, AtimeNanos = 1_700_000_000_000_000_001L, MtimeNanos = 2_000_000_002L, CtimeNanos = 3_000_000_003L };
            return 0;
        }
        public long ReadDir(long index, out LinuxDirEntry entry)
        {
            if (index >= Entries.Count) { entry = default; return 1; }
            entry = Entries[(int)index];
            return 0;
        }
        public long Close() => 0;
    }

    private sealed class Fs : ILinuxFileSystem
    {
        public readonly List<string> Calls = new();
        public bool Follow;
        public long Open(string path, int flags, uint mode, out ILinuxFile? file) { file = null; return -38; }
        public long Stat(string path, bool follow, out LinuxStat st) { Calls.Add("stat " + path); Follow = follow; st = new LinuxStat { Mode = 0x81a4, Size = path.Length }; return path == "/nix" ? -2 : 0; }
        public long Access(string path, int mode) { Calls.Add($"access {path} {mode}"); return 0; }
        public long ReadLink(string path, out string target) { Calls.Add("readlink " + path); target = "ziel/länger"; return 0; }
        public long Mkdir(string path, uint mode) { Calls.Add($"mkdir {path} {Convert.ToString(mode, 8)}"); return 0; }
        public long Unlink(string path) { Calls.Add("unlink " + path); return 0; }
        public long Rmdir(string path) { Calls.Add("rmdir " + path); return 0; }
        public long Rename(string a, string b) { Calls.Add($"rename {a} {b}"); return 0; }
        public long Symlink(string target, string link) { Calls.Add($"symlink {target} {link}"); return 0; }
    }

    private sealed class Proc : ILinuxProcess, ILinuxFiles
    {
        public readonly Mem Mem = new();
        public readonly Fs Fs = new();
        public readonly Dictionary<int, ILinuxDescriptor> Table = new() { [1] = new Term(), [5] = new FakeFile("/etc", true), [6] = new FakeFile("/etc/x", false) };
        public IUserMemory Memory => Mem;
        public ILinuxDescriptor? GetDescriptor(int fd) => Table.GetValueOrDefault(fd);
        public ulong Brk => 0;
        public bool TrySetBrk(ulong n) => false;
        public ulong MapAnonymous(ulong len, int prot) => 0;
        public bool Unmap(ulong a, ulong l) => false;
        public bool Protect(ulong a, ulong l, int p) => false;
        public bool MapFixed(ulong a, ulong l, int p) => false;
        public bool Discard(ulong a, ulong l) => false;
        public ulong FsBase { get; set; }
        public ulong ClearChildTid { get; set; }
        public int Pid => 1;
        public ulong Uid => 1000;
        public ulong Gid => 100;
        public LinuxSignalState Signals { get; } = new();
        public void Exit(int code) { }
        public ILinuxFileSystem FileSystem => Fs;
        public string Cwd { get; set; } = "/home/u";
        public int AddDescriptor(ILinuxDescriptor d, int min) => -24;
        public bool RemoveDescriptor(int fd) => false;
    }

    private static long Call(Proc p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4 };
        return LinuxSyscalls.Dispatch(ref a, p); // über den ganzen Verteiler: belegt auch den Haken in DispatchMore
    }

    private const long Stat = 4, Fstat = 5, Lstat = 6, Access = 21, Rename = 82, Mkdir = 83, Rmdir = 84, Unlink = 87, Symlink = 88,
        Readlink = 89, Getdents64 = 217, Mkdirat = 258, Newfstatat = 262, Unlinkat = 263, Faccessat = 269;
    private static readonly ulong Cwd = unchecked((ulong)-100L);

    [TestMethod]
    public void FstatSchreibtStruct144()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, Fstat, 6, Base));
        var d = p.Mem.Data;
        Assert.AreEqual(3UL, BitConverter.ToUInt64(d, 0));
        Assert.AreEqual(77UL, BitConverter.ToUInt64(d, 8));
        Assert.AreEqual(2UL, BitConverter.ToUInt64(d, 16));
        Assert.AreEqual(0x81a4u, BitConverter.ToUInt32(d, 24));
        Assert.AreEqual(1000u, BitConverter.ToUInt32(d, 28));
        Assert.AreEqual(100u, BitConverter.ToUInt32(d, 32));
        Assert.AreEqual(1234L, BitConverter.ToInt64(d, 48));
        Assert.AreEqual(4096L, BitConverter.ToInt64(d, 56));
        Assert.AreEqual(8L, BitConverter.ToInt64(d, 64));
        Assert.AreEqual(1_700_000_000L, BitConverter.ToInt64(d, 72));
        Assert.AreEqual(1L, BitConverter.ToInt64(d, 80));
        Assert.AreEqual(2L, BitConverter.ToInt64(d, 88));
        Assert.AreEqual(2L, BitConverter.ToInt64(d, 96));
        Assert.AreEqual(3L, BitConverter.ToInt64(d, 104));
        Assert.AreEqual(0L, BitConverter.ToInt64(d, 144), "hinter 144 Bytes bleibt unberührt");
    }

    [TestMethod]
    public void FstatTerminalUndFehler()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, Fstat, 1, Base));
        Assert.AreEqual(0x2000u, BitConverter.ToUInt32(p.Mem.Data, 24) & 0xf000, "Zeichengerät");
        Assert.AreEqual(-9L, Call(p, Fstat, 9, Base));
        Assert.AreEqual(-14L, Call(p, Fstat, 6, 0x10));
    }

    [TestMethod]
    public void StatLoestPfadeRelativZumArbeitsverzeichnis()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, Stat, p.Mem.Put(0x100, "a/./b/../c"), Base));
        Assert.AreEqual("stat /home/u/a/c", p.Fs.Calls[^1]);
        Assert.IsTrue(p.Fs.Follow);
        Assert.AreEqual(0L, Call(p, Lstat, p.Mem.Put(0x100, "/../x//y/"), Base));
        Assert.AreEqual("stat /x/y/", p.Fs.Calls[^1], "abschließendes / bleibt: Verzeichnis verlangt");
        Assert.IsFalse(p.Fs.Follow);
        Assert.AreEqual(-2L, Call(p, Stat, p.Mem.Put(0x100, "/nix"), Base));
        Assert.AreEqual(-2L, Call(p, Stat, p.Mem.Put(0x100, ""), Base), "leerer Pfad");
        Assert.AreEqual(-14L, Call(p, Stat, 0x10, Base));
    }

    [TestMethod]
    public void NewfstatatMitDirfdUndLeerPfad()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, Newfstatat, 5, p.Mem.Put(0x100, "k"), Base, 0x100));
        Assert.AreEqual("stat /etc/k", p.Fs.Calls[^1]);
        Assert.IsFalse(p.Fs.Follow);
        Assert.AreEqual(0L, Call(p, Newfstatat, 6, p.Mem.Put(0x100, ""), Base, 0x1000), "AT_EMPTY_PATH = fstat");
        Assert.AreEqual(77UL, BitConverter.ToUInt64(p.Mem.Data, 8));
        Assert.AreEqual(-9L, Call(p, Newfstatat, 9, p.Mem.Put(0x100, "k"), Base, 0));
        Assert.AreEqual(-20L, Call(p, Newfstatat, 6, p.Mem.Put(0x100, "k"), Base, 0), "Datei als Verzeichnis-fd");
        Assert.AreEqual(-22L, Call(p, Newfstatat, Cwd, p.Mem.Put(0x100, "k"), Base, 0x4));
        Assert.AreEqual(0L, Call(p, Newfstatat, 9, p.Mem.Put(0x100, "/abs"), Base, 0), "absoluter Pfad ignoriert dirfd");
        Assert.AreEqual(0L, Call(p, Newfstatat, Cwd, p.Mem.Put(0x100, ""), Base, 0x1000), "AT_EMPTY_PATH mit AT_FDCWD = cwd");
        Assert.AreEqual("stat /home/u", p.Fs.Calls[^1]);
        Assert.IsTrue(p.Fs.Follow);
    }

    [TestMethod]
    public void AccessPrueftModus()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, Access, p.Mem.Put(0x100, "f"), 6));
        Assert.AreEqual("access /home/u/f 6", p.Fs.Calls[^1]);
        Assert.AreEqual(-22L, Call(p, Access, p.Mem.Put(0x100, "f"), 8));
        Assert.AreEqual(0L, Call(p, Faccessat, 5, p.Mem.Put(0x100, "f"), 0));
        Assert.AreEqual("access /etc/f 0", p.Fs.Calls[^1]);
    }

    [TestMethod]
    public void Getdents64PackenUndSetztFort()
    {
        var p = new Proc();
        Assert.AreEqual(-9L, Call(p, Getdents64, 9, Base, 512));
        Assert.AreEqual(-20L, Call(p, Getdents64, 6, Base, 512));
        Assert.AreEqual(-22L, Call(p, Getdents64, 5, Base, 16), "kein Eintrag passt");

        // Platz für genau zwei Einträge ("." und ".." je 24 Bytes): danach geht es beim dritten weiter.
        Assert.AreEqual(48L, Call(p, Getdents64, 5, Base, 50));
        var d = p.Mem.Data;
        Assert.AreEqual(1UL, BitConverter.ToUInt64(d, 0));
        Assert.AreEqual(1L, BitConverter.ToInt64(d, 8), "d_off = nächster Index");
        Assert.AreEqual((ushort)24, BitConverter.ToUInt16(d, 16));
        Assert.AreEqual((byte)4, d[18]);
        Assert.AreEqual((byte)'.', d[19]);
        Assert.AreEqual(2L, ((FakeFile)p.Table[5]).DirIndex);

        long n = Call(p, Getdents64, 5, Base, 512);
        Assert.AreEqual(32L + 24, n); // "datei.txt" (9+19+1 = 29 -> 32) und "ä" (2+19+1 = 22 -> 24)
        Assert.AreEqual("datei.txt", Encoding.UTF8.GetString(d, 19, 9));
        Assert.AreEqual((ushort)32, BitConverter.ToUInt16(d, 16));
        Assert.AreEqual(0L, Call(p, Getdents64, 5, Base, 512), "Ende");
    }

    [TestMethod]
    public void ReadlinkSchneidetAb()
    {
        var p = new Proc();
        ulong path = p.Mem.Put(0x100, "l");
        int len = Encoding.UTF8.GetByteCount("ziel/länger");
        Assert.AreEqual((long)len, Call(p, Readlink, path, Base + 0x200, 100));
        Assert.AreEqual(0, p.Mem.Data[0x200 + len], "keine NUL angehängt");
        Assert.AreEqual(4L, Call(p, Readlink, path, Base + 0x200, 4));
        Assert.AreEqual(-22L, Call(p, Readlink, path, Base + 0x200, 0));
        Assert.AreEqual(-14L, Call(p, Readlink, path, 0x10, 10));
    }

    [TestMethod]
    public void VerzeichnisbaumAenderungen()
    {
        var p = new Proc();
        Assert.AreEqual(0L, Call(p, Unlink, p.Mem.Put(0x100, "x")));
        Assert.AreEqual("unlink /home/u/x", p.Fs.Calls[^1]);
        Assert.AreEqual(0L, Call(p, Rmdir, p.Mem.Put(0x100, "d")));
        Assert.AreEqual("rmdir /home/u/d", p.Fs.Calls[^1]);
        Assert.AreEqual(0L, Call(p, Unlinkat, 5, p.Mem.Put(0x100, "d"), 0x200));
        Assert.AreEqual("rmdir /etc/d", p.Fs.Calls[^1]);
        Assert.AreEqual(-22L, Call(p, Unlinkat, 5, p.Mem.Put(0x100, "d"), 1));
        Assert.AreEqual(0L, Call(p, Mkdir, p.Mem.Put(0x100, "/m"), 0x41ff));
        Assert.AreEqual("mkdir /m 777", p.Fs.Calls[^1]);
        Assert.AreEqual(0L, Call(p, Mkdirat, 5, p.Mem.Put(0x100, "n"), 0x1ed));
        Assert.AreEqual("mkdir /etc/n 755", p.Fs.Calls[^1]);
        Assert.AreEqual(0L, Call(p, Rename, p.Mem.Put(0x100, "a"), p.Mem.Put(0x180, "/b")));
        Assert.AreEqual("rename /home/u/a /b", p.Fs.Calls[^1]);
        Assert.AreEqual(0L, Call(p, Symlink, p.Mem.Put(0x100, "../relativ"), p.Mem.Put(0x180, "ln")));
        Assert.AreEqual("symlink ../relativ /home/u/ln", p.Fs.Calls[^1], "Ziel bleibt unverändert");
        Assert.AreEqual(-2L, Call(p, Symlink, p.Mem.Put(0x100, ""), p.Mem.Put(0x180, "ln")));
        Assert.AreEqual(-14L, Call(p, Rename, p.Mem.Put(0x100, "a"), 0x10));
    }

    [TestMethod]
    public void UnbekanntesIstEnosys()
    {
        Assert.AreEqual(-38L, Call(new Proc(), 9999));
    }
}
