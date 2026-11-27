using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using E = ProjektZeh.Processes.Vfs.LinuxErrno;
using Fs = ProjektZeh.Processes.Vfs.LinuxFileSystem;

namespace ProjektZeh.Tests.Processes;

[TestClass]
public sealed class LinuxFileSystemTests
{
    private const int Create = Fs.OCreat | Fs.ORdWr;
    private static readonly string[] ShortNameDisk = [".zehos-security.db", "langdir", "langername", "langlink", "neu"];
    private static readonly string[] DevOnlyListing = [".", "..", "dev"];
    private static readonly string[] DotsOnly = [".", ".."];
    private static readonly string[] DevListing = [".", "..", "null", "tty", "zero"];
    private static readonly string[] ShadowListing = [".", "..", "dev", "z"];
    private static readonly string[] DiskListing = [".", "..", "dev", "abs", "e", "lf", "rel", "up"];
    private static readonly string[] DiskOutside = ["geheim", "root"];
    private static readonly string[] AmbiguousNames = ["/a:b", "/x.", "/x ", "/a\\b", "/.zehos-neu", "/zeile\nx", "/sub/..."];
    private static readonly string[] AmbiguousListing = [".", "..", "dev", "sub"];
    private static readonly string[] AmbiguousOnDisk = [".zehos-system", "sub"];
    private static readonly string[] BadRoots = ["", "relativ", "/a/../b", "/a/", "/a/.."];
    private static readonly string[] FuzzOutside = ["a", "root"];

    private sealed class Sink : ILinuxDescriptor
    {
        public readonly List<byte> Bytes = new();

        public long Write(ReadOnlySpan<byte> data)
        {
            Bytes.AddRange(data.ToArray());
            return data.Length;
        }
    }

    private static (Fs Fs, MemoryFileStore Store, Sink Tty) New(int maxOpen = Fs.DefaultMaxOpenFiles, MemoryFileStore? store = null)
    {
        store ??= new MemoryFileStore();
        var tty = new Sink();
        return (new Fs(store, tty, maxOpen), store, tty);
    }

    private static ILinuxFile OpenOk(Fs fs, string path, int flags, uint mode = 0x1A4)
    {
        long r = fs.Open(path, flags, mode, out ILinuxFile? file);
        Assert.AreEqual(0L, r, path);
        return file!;
    }

    private static void WriteFile(Fs fs, string path, string text)
    {
        ILinuxFile f = OpenOk(fs, path, Fs.OCreat | Fs.OWrOnly | Fs.OTrunc);
        byte[] data = Encoding.UTF8.GetBytes(text);
        Assert.AreEqual((long)data.Length, f.Write(data));
        f.Close();
    }

    private static string ReadFile(Fs fs, string path)
    {
        ILinuxFile f = OpenOk(fs, path, Fs.ORdOnly);
        var all = new List<byte>();
        var buf = new byte[7];
        long n;
        while ((n = f.Read(buf)) > 0)
        {
            all.AddRange(buf.Take((int)n));
        }

        Assert.AreEqual(0L, n);
        f.Close();
        return Encoding.UTF8.GetString(all.ToArray());
    }

    private static List<string> ListDir(ILinuxFile dir)
    {
        var names = new List<string>();
        for (long i = 0; ; i++)
        {
            long r = dir.ReadDir(i, out LinuxDirEntry e);
            if (r == 1)
            {
                return names;
            }

            Assert.AreEqual(0L, r);
            names.Add(e.Name);
        }
    }

    // ---- Verhalten ----

    [TestMethod]
    public void CreateWriteReadSeekAppend()
    {
        var (fs, _, _) = New();
        ILinuxFile f = OpenOk(fs, "/a.txt", Create);
        Assert.AreEqual(5L, f.Write("hallo"u8));
        Assert.AreEqual(1L, f.Seek(1, 0));
        var buf = new byte[10];
        Assert.AreEqual(4L, f.Read(buf));
        Assert.AreEqual("allo", Encoding.ASCII.GetString(buf, 0, 4));
        Assert.AreEqual(0L, f.Read(buf));
        Assert.AreEqual(8L, f.Seek(3, 2));
        Assert.AreEqual(1L, f.Write("!"u8)); // Lücke 5..7 wird genullt
        Assert.AreEqual(0L, f.Stat(out LinuxStat st));
        Assert.AreEqual(9L, st.Size);
        Assert.AreEqual(LinuxMode.Regular, st.Mode & LinuxMode.TypeMask);
        Assert.AreEqual("/a.txt", f.Path);
        f.Close();
        Assert.AreEqual("hallo\0\0\0!", ReadFile(fs, "/a.txt"));

        ILinuxFile app = OpenOk(fs, "/a.txt", Fs.OWrOnly | Fs.OAppend);
        Assert.AreEqual(0L, app.Seek(0, 0));
        Assert.AreEqual(1L, app.Write("x"u8));
        app.Close();
        Assert.AreEqual("hallo\0\0\0!x", ReadFile(fs, "/a.txt"));

        WriteFile(fs, "/a.txt", "neu"); // O_TRUNC
        Assert.AreEqual("neu", ReadFile(fs, "/a.txt"));
        Assert.AreEqual(0, fs.OpenFiles);
    }

    [TestMethod]
    public void AccessModesAreEnforcedOnDescriptor()
    {
        var (fs, _, _) = New();
        WriteFile(fs, "/f", "abc");
        ILinuxFile ro = OpenOk(fs, "/f", Fs.ORdOnly);
        Assert.AreEqual(-E.EBADF, ro.Write("x"u8));
        ILinuxFile wo = OpenOk(fs, "/f", Fs.OWrOnly);
        Assert.AreEqual(-E.EBADF, wo.Read(new byte[1]));
        ro.Close();
        ro.Close(); // zweites Close ist folgenlos
        Assert.AreEqual(-E.EBADF, ro.Read(new byte[1]));
        wo.Close();
        Assert.AreEqual(0, fs.OpenFiles);
    }

    [TestMethod]
    public void OpenErrors()
    {
        var (fs, _, _) = New();
        WriteFile(fs, "/f", "x");
        Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ED));
        Assert.AreEqual(-E.ENOENT, fs.Open("/fehlt", Fs.ORdOnly, 0, out _));
        Assert.AreEqual(-E.ENOENT, fs.Open("/fehlt/x", Create, 0, out _));
        Assert.AreEqual(-E.EEXIST, fs.Open("/f", Create | Fs.OExcl, 0, out _));
        Assert.AreEqual(-E.EISDIR, fs.Open("/d", Fs.OWrOnly, 0, out _));
        Assert.AreEqual(-E.EISDIR, fs.Open("/d", Fs.OCreat, 0, out _));
        Assert.AreEqual(-E.ENOTDIR, fs.Open("/f", Fs.ODirectory, 0, out _));
        Assert.AreEqual(-E.ENOTDIR, fs.Open("/f/x", Fs.ORdOnly, 0, out _));
        Assert.AreEqual(-E.EINVAL, fs.Open("/f", 3, 0, out _));
        Assert.AreEqual(-E.EINVAL, fs.Open("/f", 0x4000, 0, out _)); // O_DIRECT
        Assert.AreEqual(-E.EINVAL, fs.Open("/f", 0x200000, 0, out _)); // O_PATH
        Assert.AreEqual(-E.EINVAL, fs.Open("/n", Fs.OCreat | Fs.ODirectory, 0, out _));
        Assert.AreEqual(-E.EOPNOTSUPP, fs.Open("/d", 0x410000 | Fs.ORdWr, 0, out _)); // O_TMPFILE
        Assert.AreEqual(0, fs.OpenFiles);
        ILinuxFile d = OpenOk(fs, "/d", Fs.ORdOnly | Fs.ODirectory);
        Assert.IsTrue(d.IsDirectory);
        Assert.AreEqual(-E.EISDIR, d.Read(new byte[1]));
        d.Close();
    }

    [TestMethod]
    public void DevNullAndTty()
    {
        var (fs, _, tty) = New();
        ILinuxFile n = OpenOk(fs, "/dev/null", Fs.ORdWr);
        Assert.AreEqual(0L, n.Read(new byte[16]));
        Assert.AreEqual(3L, n.Write("abc"u8));
        Assert.AreEqual(0L, n.Seek(100, 0));
        Assert.AreEqual(0L, n.Stat(out LinuxStat st));
        Assert.AreEqual(LinuxMode.CharDevice, st.Mode & LinuxMode.TypeMask);
        Assert.AreEqual(0x103UL, st.Rdev);
        n.Close();

        ILinuxFile t = OpenOk(fs, "/dev/tty", Fs.OWrOnly | Fs.OCreat);
        Assert.AreEqual(3L, t.Write("hi\n"u8));
        Assert.AreEqual("hi\n", Encoding.ASCII.GetString(tty.Bytes.ToArray()));
        Assert.AreEqual(-E.ESPIPE, t.Seek(0, 0));
        t.Close();
        Assert.AreEqual(-E.EEXIST, fs.Open("/dev/null", Fs.OCreat | Fs.OExcl | Fs.OWrOnly, 0, out _));
        Assert.AreEqual(-E.ENOTDIR, fs.Open("/dev/null", Fs.ODirectory, 0, out _));
        Assert.AreEqual(-E.ENOENT, fs.Open("/dev/sda", Fs.ORdOnly, 0, out _));
        Assert.AreEqual(-E.ENOTDIR, fs.Open("/dev/null/x", Fs.ORdOnly, 0, out _));

        ILinuxFile dev = OpenOk(fs, "/dev", Fs.ORdOnly);
        CollectionAssert.AreEqual(DevListing, ListDir(dev));
        dev.Close();

        Assert.AreEqual(-E.EACCES, fs.Open("/dev/neu", Create, 0, out _));
        Assert.AreEqual(-E.EACCES, fs.Mkdir("/dev/x", 0x1ED));
        Assert.AreEqual(-E.EACCES, fs.Unlink("/dev/null"));
        Assert.AreEqual(-E.EISDIR, fs.Unlink("/dev"));
        Assert.AreEqual(-E.EBUSY, fs.Rmdir("/dev"));
        Assert.AreEqual(-E.EEXIST, fs.Mkdir("/dev", 0x1ED));
        WriteFile(fs, "/f", "x");
        Assert.AreEqual(-E.EXDEV, fs.Rename("/f", "/dev/f"));
        Assert.AreEqual(-E.EXDEV, fs.Rename("/dev/null", "/n"));
        Assert.AreEqual(-E.EBUSY, fs.Rename("/dev", "/d2"));
        Assert.AreEqual(0L, fs.Access("/dev/null", 6));
        Assert.AreEqual(-E.EACCES, fs.Access("/dev", 2));
        Assert.AreEqual(0L, fs.Access("/dev", 5));
        Assert.AreEqual(0L, fs.Stat("/dev", true, out LinuxStat ds));
        Assert.AreEqual(LinuxMode.Directory, ds.Mode & LinuxMode.TypeMask);
        Assert.AreEqual(0, fs.OpenFiles);
    }

    [TestMethod]
    public void DevZeroReadsZerosAndDiscardsWrites()
    {
        var (fs, _, _) = New();
        ILinuxFile z = OpenOk(fs, "/dev/zero", Fs.ORdWr);
        byte[] buf = Enumerable.Repeat((byte)0xAA, 70_000).ToArray();
        Assert.AreEqual(70_000L, z.Read(buf));
        Assert.IsTrue(buf.All(b => b == 0));
        Assert.AreEqual(0L, z.Read(Span<byte>.Empty));
        Assert.AreEqual(5L, z.Write("abcde"u8));
        Assert.AreEqual(0L, z.Seek(123, 0));
        Assert.AreEqual(0L, z.Stat(out LinuxStat st));
        Assert.AreEqual(LinuxMode.CharDevice, st.Mode & LinuxMode.TypeMask);
        Assert.AreEqual(0x105UL, st.Rdev);
        Assert.AreEqual("/dev/zero", z.Path);
        z.Close();
        Assert.AreEqual(0L, fs.Stat("/dev/zero", true, out st));
        Assert.AreEqual(0x105UL, st.Rdev);
        Assert.AreEqual(-E.EACCES, fs.Unlink("/dev/zero"));
        Assert.AreEqual(0, fs.OpenFiles);
    }

    /// <summary>Speicher, dessen Dateien beim Schließen scheitern: einmal mit -EIO, einmal mit einer IOException.</summary>
    private sealed class FailingCloseStore(MemoryFileStore inner) : ILinuxFileStore
    {
        private int _opened;

        public StringComparison NameComparison => inner.NameComparison;

        public long Lookup(string path, out LinuxStoreNode node) => inner.Lookup(path, out node);

        public long ReadLink(string path, out string target) => inner.ReadLink(path, out target);

        public long List(string path, out string[] names) => inner.List(path, out names);

        public long OpenFile(string path, bool write, out ILinuxStoreFile? file)
        {
            long r = inner.OpenFile(path, write, out ILinuxStoreFile? f);
            file = f is null ? null : new Failing(f, _opened++ % 2 == 0);
            return r;
        }

        public long CreateFile(string path, uint mode, out ILinuxStoreFile? file) => inner.CreateFile(path, mode, out file);

        public long CreateDirectory(string path, uint mode) => inner.CreateDirectory(path, mode);

        public long CreateSymlink(string path, string target) => inner.CreateSymlink(path, target);

        public long DeleteFile(string path) => inner.DeleteFile(path);

        public long DeleteDirectory(string path) => inner.DeleteDirectory(path);

        public long Rename(string from, string to) => inner.Rename(from, to);

        private sealed class Failing(ILinuxStoreFile f, bool throws) : ILinuxStoreFile
        {
            public long Length() => f.Length();

            public long ReadAt(long offset, Span<byte> dst) => f.ReadAt(offset, dst);

            public long WriteAt(long offset, ReadOnlySpan<byte> src) => f.WriteAt(offset, src);

            public long SetLength(long length) => f.SetLength(length);

            public long Close()
            {
                f.Close();
                return throws ? throw new IOException("Zurückschreiben gescheitert") : -E.EIO;
            }
        }
    }

    [TestMethod]
    public void FailingCloseStillClosesEveryFile()
    {
        var memory = new MemoryFileStore();
        var (fs, _, _) = New(maxOpen: 4, store: memory);
        WriteFile(fs, "/f", "x");
        var failing = new Fs(new FailingCloseStore(memory), new Sink(), maxOpenFiles: 4);
        var files = Enumerable.Range(0, 4).Select(_ => OpenOk(failing, "/f", Fs.ORdOnly)).ToList();
        Assert.AreEqual(-E.EMFILE, failing.Open("/f", Fs.ORdOnly, 0, out _));

        // Wie CloseAllFiles eines Prozesses: jede Datei einmal schließen, ohne dass eine Ausnahme die Schleife abbricht.
        var errors = files.Select(f => ((LinuxOpenFile)f).CloseChecked()).ToList();
        CollectionAssert.AreEqual(new[] { -E.EIO, -E.EIO, -E.EIO, -E.EIO }, errors);
        Assert.AreEqual(0, failing.OpenFiles);
        Assert.AreEqual(0L, ((LinuxOpenFile)files[0]).CloseChecked()); // zweites Schließen: nichts mehr zu tun
        files[1].Close();
        Assert.AreEqual(0, failing.OpenFiles);
        Assert.AreEqual(-E.EBADF, files[2].Read(new byte[1]));
        OpenOk(failing, "/f", Fs.ORdOnly).Close(); // Budget wieder frei
        Assert.AreEqual(0, failing.OpenFiles);
    }

    [TestMethod]
    public void SymlinkOnDriveWithoutLinksIsEperm()
    {
        string tmp = TempDir();
        try
        {
            string root = Path.Combine(tmp, "root");
            if (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
            }

            var fs = new Fs(DirectoryFileStore.Open(root, new NoLinks(), StringComparison.Ordinal).Value!, new Sink());
            Assert.AreEqual(-E.EPERM, fs.Symlink("ziel", "/l"));
            Assert.AreEqual(-E.ENOENT, fs.Stat("/l", false, out _));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    /// <summary>Wie FAT unter Cosmos: Symlink anlegen meldet false.</summary>
    private sealed class NoLinks : ILinuxDiskLinks
    {
        private readonly HostLinuxDiskLinks _host = new();

        public LinuxNodeKind KindStorageLocked(string parent, string name, out string found) => _host.KindStorageLocked(parent, name, out found);

        public string ReadLinkStorageLocked(string full) => _host.ReadLinkStorageLocked(full);

        public bool CreateSymlinkStorageLocked(string full, string target) => false;
    }

    /// <summary>Wie Cosmos-FAT: findet Einträge auch über ihren 8.3-Kurznamen und meldet dann den Langnamen als geführten Namen.</summary>
    private sealed class ShortNameLinks(Dictionary<string, string> aliases) : ILinuxDiskLinks
    {
        private readonly HostLinuxDiskLinks _host = new();

        public LinuxNodeKind KindStorageLocked(string parent, string name, out string found)
        {
            if (aliases.TryGetValue(name, out string? longName))
            {
                LinuxNodeKind kind = _host.KindStorageLocked(parent, longName, out _);
                found = longName;
                return kind;
            }

            return _host.KindStorageLocked(parent, name, out found);
        }

        public string ReadLinkStorageLocked(string full) => _host.ReadLinkStorageLocked(full);

        public bool CreateSymlinkStorageLocked(string full, string target) => _host.CreateSymlinkStorageLocked(full, target);
    }

    [TestMethod]
    public void ShortNamesDoNotReachLongNameEntries()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel; Symlinks auf dem Host nur unter Unix.");
        }

        string tmp = TempDir();
        try
        {
            string root = Path.Combine(tmp, "root");
            Directory.CreateDirectory(Path.Combine(root, "langdir"));
            File.WriteAllText(Path.Combine(root, ".zehos-security.db"), "GEHEIM");
            File.WriteAllText(Path.Combine(root, "langername"), "L");
            File.WriteAllText(Path.Combine(root, "langdir", "x"), "X");
            File.CreateSymbolicLink(Path.Combine(root, "langlink"), "langername");
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["_ZEHOS~1.DB"] = ".zehos-security.db",
                ["LANGER~1"] = "langername",
                ["LANGDI~1"] = "langdir",
                ["LANGLI~1"] = "langlink",
            };
            var store = DirectoryFileStore.Open(root, new ShortNameLinks(aliases), StringComparison.OrdinalIgnoreCase).Value!;
            var fs = new Fs(store, new Sink());

            // Lesen und Abfragen: der Zweitname gilt als fehlend.
            Assert.AreEqual(-E.ENOENT, fs.Open("/_ZEHOS~1.DB", Fs.ORdOnly, 0, out _));
            Assert.AreEqual(-E.ENOENT, fs.Open("/_zehos~1.db", Fs.ORdWr, 0, out _));
            Assert.AreEqual(-E.ENOENT, fs.Stat("/LANGER~1", true, out _));
            Assert.AreEqual(-E.ENOENT, fs.Open("/LANGDI~1/x", Fs.ORdOnly, 0, out _));
            Assert.AreEqual(-E.ENOENT, fs.ReadLink("/LANGLI~1", out _));
            Assert.AreEqual(-E.ENOENT, fs.Open("/LANGLI~1", Fs.ORdOnly, 0, out _));
            Assert.AreEqual(-E.ENOENT, store.ReadLink("LANGLI~1", out _));

            // Entfernen über den Zweitnamen erreicht nichts.
            Assert.AreEqual(-E.ENOENT, fs.Unlink("/_ZEHOS~1.DB"));
            Assert.AreEqual(-E.ENOENT, fs.Unlink("/LANGLI~1"));
            Assert.AreEqual(-E.ENOENT, fs.Rmdir("/LANGDI~1"));
            Assert.AreEqual(-E.ENOENT, store.DeleteFile("LANGER~1"));
            Assert.AreEqual(-E.ENOENT, store.DeleteDirectory("LANGDI~1"));
            Assert.AreEqual(-E.ENOENT, fs.Rename("/LANGER~1", "/neu"));

            // Anlegen und Umbenennen auf einen Zweitnamen: belegt, nichts überschrieben.
            Assert.AreEqual(-E.EEXIST, fs.Open("/_ZEHOS~1.DB", Fs.OCreat | Fs.OWrOnly | Fs.OTrunc, 0x1A4, out _));
            Assert.AreEqual(-E.EEXIST, fs.Mkdir("/LANGER~1", 0x1ED));
            Assert.AreEqual(-E.EEXIST, fs.Symlink("ziel", "/LANGDI~1"));
            Assert.AreEqual(-E.EEXIST, fs.Rename("/langername", "/LANGER~1")); // derselbe Eintrag über seinen Kurznamen
            WriteFile(fs, "/neu", "N");
            Assert.AreEqual(-E.EEXIST, fs.Rename("/neu", "/_ZEHOS~1.DB"));
            Assert.AreEqual(-E.EEXIST, store.CreateDirectory("LANGLI~1", 0x1ED));

            Assert.AreEqual("GEHEIM", File.ReadAllText(Path.Combine(root, ".zehos-security.db")));
            Assert.AreEqual("L", File.ReadAllText(Path.Combine(root, "langername")));
            Assert.AreEqual("L", ReadFile(fs, "/langername"));
            Assert.AreEqual("X", ReadFile(fs, "/langdir/x"));
            Assert.AreEqual("L", ReadFile(fs, "/langlink"));
            CollectionAssert.AreEquivalent(ShortNameDisk,
                Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToArray());
            Assert.AreEqual(0, fs.OpenFiles);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void SameNameFollowsTheDriveRule()
    {
        Assert.IsTrue(DirectoryFileStore.SameName("Datei", "DATEI", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(DirectoryFileStore.SameName("Datei", "DATEI", StringComparison.Ordinal));
        Assert.IsTrue(DirectoryFileStore.SameName("a", "a", StringComparison.Ordinal));
        Assert.IsFalse(DirectoryFileStore.SameName(".zehos-security.db", "_ZEHOS~1.DB", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(DirectoryFileStore.SameName("langername", "LANGER~1", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(DirectoryFileStore.SameName(null, "a", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DiskGrowthRunsInStepsAndCountsAgainstQuota()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        const int Mib = 1 << 20;
        string tmp = TempDir();
        try
        {
            string root = Path.Combine(tmp, "root");
            var store = DirectoryFileStore.Open(root, new HostLinuxDiskLinks(), StringComparison.Ordinal, quotaBytes: 8L * Mib).Value!;
            var fs = new Fs(store, new Sink());
            Assert.AreEqual(Mib, ProjektZeh.Storage.StorageLock.ChunkBytes);

            // ftruncate nach oben: in Schritten zu je höchstens ChunkBytes, jeder unter eigener Sperre.
            Assert.AreEqual(0L, store.CreateFile("g", 0x1A4, out ILinuxStoreFile? g));
            Assert.AreEqual(0L, g!.SetLength(3 * Mib + Mib / 2));
            Assert.AreEqual(4, store.GrowSteps);
            Assert.AreEqual(3L * Mib + Mib / 2, g.Length());
            Assert.AreEqual(3L * Mib + Mib / 2, store.GrownBytes);

            // 1 GiB auf einmal: abgelehnt, bevor irgendetwas wächst.
            Assert.AreEqual(-E.ENOSPC, g.SetLength(1L << 30));
            Assert.AreEqual(4, store.GrowSteps);
            Assert.AreEqual(3L * Mib + Mib / 2, g.Length());

            // lseek weit nach vorn und 1 Byte: die Lücke zählt mit.
            ILinuxFile f = OpenOk(fs, "/f", Create);
            Assert.AreEqual((1L << 30) - 1, f.Seek((1L << 30) - 1, 0));
            Assert.AreEqual(-E.ENOSPC, f.Write("x"u8));
            Assert.AreEqual(0L, new FileInfo(Path.Combine(root, "f")).Length);
            Assert.AreEqual(4L * Mib, f.Seek(4L * Mib, 0));
            Assert.AreEqual(3L, f.Write("abc"u8)); // Lücke 4 MiB in vier Schritten, dann die Daten
            Assert.AreEqual(8, store.GrowSteps);
            Assert.AreEqual(4L * Mib + 3, new FileInfo(Path.Combine(root, "f")).Length);
            Assert.AreEqual(7L * Mib + Mib / 2 + 3, store.GrownBytes);

            // Rest des Kontingents: gekürzt, danach -ENOSPC.
            long room = 8L * Mib - store.GrownBytes;
            Assert.AreEqual(room, f.Write(new byte[Mib]));
            Assert.AreEqual(-E.ENOSPC, f.Write("y"u8));
            f.Close();

            // Löschen und Kürzen geben frei.
            Assert.AreEqual(0L, fs.Unlink("/f"));
            Assert.AreEqual(3L * Mib + Mib / 2, store.GrownBytes);
            Assert.AreEqual(0L, g.SetLength(0));
            Assert.AreEqual(0L, store.GrownBytes);
            Assert.AreEqual(0L, g.Close());
            WriteFile(fs, "/wieder", "ok");
            Assert.AreEqual(0, fs.OpenFiles);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    /// <summary>Plattenwurzel mit kleinem Kontingent für die Tests der aufgeschobenen Gutschrift.</summary>
    private static (Fs Fs, DirectoryFileStore Store) QuotaDisk(string tmp, long quota)
    {
        var store = DirectoryFileStore.Open(Path.Combine(tmp, "root"), new HostLinuxDiskLinks(), StringComparison.Ordinal, quotaBytes: quota).Value!;
        return (new Fs(store, new Sink()), store);
    }

    [TestMethod]
    public void UnlinkOfOpenFilesKeepsQuotaUntilLastClose()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        const int Mib = 1 << 20;
        string tmp = TempDir();
        try
        {
            var (fs, store) = QuotaDisk(tmp, Mib);
            byte[] full = new byte[Mib];

            // Gegenfall aus dem Review: voll schreiben, entfernen, Deskriptor behalten, nächste Datei.
            var kept = new List<ILinuxFile>();
            for (int i = 0; i < 8; i++)
            {
                ILinuxFile f = OpenOk(fs, "/f" + i, Create);
                long written = f.Write(full);
                Assert.AreEqual(i == 0 ? Mib : -E.ENOSPC, written, "Datei " + i);
                Assert.AreEqual(0L, fs.Unlink("/f" + i));
                kept.Add(f);
            }

            Assert.AreEqual((long)Mib, store.GrownBytes, "die entfernte, offene Datei belegt weiter");
            foreach (ILinuxFile f in kept)
            {
                Assert.AreEqual(0L, f.Close());
            }

            Assert.AreEqual(0L, store.GrownBytes, "frei erst nach dem letzten Schließen");
            Assert.AreEqual(0, store.OpenEntryCount);
            ILinuxFile again = OpenOk(fs, "/g", Create);
            Assert.AreEqual((long)Mib, again.Write(full));
            again.Close();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void CreatedModeFollowsDirectoryRenameAndIsDroppedOnUnlink()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        string tmp = TempDir();
        try
        {
            var (fs, _) = QuotaDisk(tmp, 4L << 20);
            Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ED));
            Assert.AreEqual(0L, OpenOk(fs, "/d/f", Create, 0x180).Close());
            Assert.AreEqual(0L, fs.Stat("/d/f", true, out LinuxStat st));
            Assert.AreEqual(0x180u, st.Mode & 0xFFFu);

            Assert.AreEqual(0L, fs.Rename("/d", "/e"));
            Assert.AreEqual(0L, fs.Stat("/e/f", true, out st));
            Assert.AreEqual(0x180u, st.Mode & 0xFFFu, "Modus wandert mit dem Verzeichnis");

            // Unlink vergisst den Modus: eine neue Datei gleichen Namens bekommt ihren eigenen.
            Assert.AreEqual(0L, fs.Unlink("/e/f"));
            Assert.AreEqual(0L, OpenOk(fs, "/e/f", Create, 0x1A4).Close());
            Assert.AreEqual(0L, fs.Stat("/e/f", true, out st));
            Assert.AreEqual(0x1A4u, st.Mode & 0xFFFu);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void DeferredCreditHandlesSharedDescriptorsRenameAndReuse()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        const int Kib = 1 << 10;
        string tmp = TempDir();
        try
        {
            var (fs, store) = QuotaDisk(tmp, 4L << 20);

            // Zwei Deskriptoren auf dieselbe Datei: erst das zweite Schließen gibt frei.
            ILinuxFile a1 = OpenOk(fs, "/a", Create);
            Assert.AreEqual(512L * Kib, a1.Write(new byte[512 * Kib]));
            ILinuxFile a2 = OpenOk(fs, "/a", Fs.ORdOnly);
            Assert.AreEqual(1, store.OpenEntryCount);
            Assert.AreEqual(0L, fs.Unlink("/a"));
            Assert.AreEqual(0L, a1.Close());
            Assert.AreEqual(512L * Kib, store.GrownBytes);
            Assert.AreEqual(0L, a2.Close());
            Assert.AreEqual(0L, store.GrownBytes);

            // rename über eine noch offene Zieldatei: deren Platz bleibt belegt, bis sie geschlossen ist.
            ILinuxFile t = OpenOk(fs, "/t", Create);
            Assert.AreEqual(256L * Kib, t.Write(new byte[256 * Kib]));
            WriteFile(fs, "/s", new string('s', 100));
            Assert.AreEqual(0L, fs.Rename("/s", "/t"));
            Assert.AreEqual(256L * Kib + 100, store.GrownBytes);
            Assert.AreEqual(0L, t.Close());
            Assert.AreEqual(100L, store.GrownBytes);

            // Neu anlegen unter demselben Namen während des Aufschubs: keine doppelte Gutschrift.
            ILinuxFile old = OpenOk(fs, "/x", Create);
            Assert.AreEqual(512L * Kib, old.Write(new byte[512 * Kib]));
            Assert.AreEqual(0L, fs.Unlink("/x"));
            ILinuxFile fresh = OpenOk(fs, "/x", Create);
            Assert.AreEqual(256L * Kib, fresh.Write(new byte[256 * Kib]));
            Assert.AreEqual(0L, fresh.Close());
            Assert.AreEqual(100L + 768 * Kib, store.GrownBytes);
            Assert.AreEqual(0L, old.Close());
            Assert.AreEqual(100L + 256 * Kib, store.GrownBytes);
            Assert.AreEqual(0L, fs.Unlink("/x"));
            Assert.AreEqual(100L, store.GrownBytes);

            // Umbenannte offene Datei, dann unter dem neuen Namen entfernt: Aufschub bleibt am Eintrag.
            ILinuxFile r = OpenOk(fs, "/r", Create);
            Assert.AreEqual(128L * Kib, r.Write(new byte[128 * Kib]));
            Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ED));
            Assert.AreEqual(0L, fs.Rename("/r", "/d/r"));
            Assert.AreEqual(-E.EBUSY, fs.Rename("/d", "/e")); // Ordner mit offener Datei darunter: Cosmos zöge deren Pfad nicht mit
            Assert.AreEqual(0L, fs.Unlink("/d/r"));
            Assert.AreEqual(100L + 128 * Kib, store.GrownBytes);
            Assert.AreEqual(0L, r.Close());
            Assert.AreEqual(100L, store.GrownBytes);
            Assert.AreEqual(0, store.OpenEntryCount);
            Assert.AreEqual(0, fs.OpenFiles);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void TruncateOfRemovedOpenFileCannotDriveQuotaNegative()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        // Gegenfall aus dem V14-Review: ftruncate verkleinert eine entfernte, offene Datei. Würde unlink die Länge sofort gutschreiben,
        // zählte das Verkleinern ein zweites Mal und das Kontingent liefe ins Minus (vorher -2 MiB, Kontingent umgehbar).
        const int Mib = 1 << 20;
        string tmp = TempDir();
        try
        {
            var (fs, store) = QuotaDisk(tmp, 2L * Mib);
            ILinuxFile f = OpenOk(fs, "/big", Create);
            Assert.AreEqual(0L, f.Truncate(2L * Mib));
            Assert.AreEqual(2L * Mib, store.GrownBytes);
            Assert.AreEqual(0L, fs.Unlink("/big"));
            Assert.AreEqual(2L * Mib, store.GrownBytes, "entfernt, aber offen: weiter belegt");
            Assert.AreEqual(0L, f.Truncate(Mib));
            Assert.AreEqual((long)Mib, store.GrownBytes);
            Assert.AreEqual(0L, f.Truncate(0));
            Assert.AreEqual(0L, store.GrownBytes, "Verkleinern zählt genau einmal");
            Assert.AreEqual(0L, f.Close());
            Assert.AreEqual(0L, store.GrownBytes, "Schließen schreibt nur die Restlänge 0 gut");

            // Wieder wachsen auf dem entfernten fd ist Wachstum wie jedes andere: über das Kontingent -ENOSPC.
            ILinuxFile g = OpenOk(fs, "/g", Create);
            Assert.AreEqual(0L, fs.Unlink("/g"));
            Assert.AreEqual(0L, g.Truncate(2L * Mib));
            Assert.AreEqual(-E.ENOSPC, g.Truncate(2L * Mib + 1));
            Assert.AreEqual(0L, g.Truncate(Mib));
            Assert.AreEqual(0L, g.Close());
            Assert.AreEqual(0L, store.GrownBytes);
            ILinuxFile h = OpenOk(fs, "/h", Create);
            Assert.AreEqual(0L, h.Truncate(2L * Mib), "volles Kontingent wieder verfügbar");
            Assert.AreEqual(-E.ENOSPC, h.Truncate(2L * Mib + 1));
            Assert.AreEqual(0L, h.Close());
            Assert.AreEqual(0, store.OpenEntryCount);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void PendingNameOfRemovedOpenFileIsGoneAndCreditedOnce()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        const int Mib = 1 << 20;
        string tmp = TempDir();
        try
        {
            var (fs, store) = QuotaDisk(tmp, 4L * Mib);
            string root = Path.Combine(tmp, "root");
            Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ED));
            ILinuxFile a = OpenOk(fs, "/d/a", Create);
            Assert.AreEqual((long)Mib, a.Write(new byte[Mib]));
            Assert.AreEqual(0L, fs.Unlink("/d/a"));

            // Wie Cosmos (MarkPendingIfOpen): der Eintrag steht bis zum letzten Schließen weiter auf dem Laufwerk.
            string zombie = Path.Combine(root, "d", "a");
            File.WriteAllBytes(zombie, new byte[16]);
            Assert.AreEqual(1, store.PendingPathCount);
            Assert.AreEqual((long)Mib, store.GrownBytes);

            // Wiederholtes Entfernen, erneutes Öffnen und Entfernen: der Name ist weg, nichts wird erneut gutgeschrieben.
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(-E.ENOENT, fs.Unlink("/d/a"));
                Assert.AreEqual(-E.ENOENT, fs.Open("/d/a", Fs.ORdOnly, 0, out _));
                Assert.AreEqual(-E.ENOENT, fs.Stat("/d/a", false, out _));
                Assert.AreEqual(-E.ENOENT, fs.Rename("/d/a", "/d/b"));
                Assert.AreEqual((long)Mib, store.GrownBytes);
            }

            // Anlegen und rename auf den stehenden Namen: belegt, solange das Laufwerk ihn führt (kein ".~replace", keine Gutschrift).
            Assert.AreEqual(-E.EEXIST, fs.Open("/d/a", Fs.OCreat | Fs.OExcl | Fs.OWrOnly, 0x1A4, out _));
            Assert.AreEqual(-E.EEXIST, fs.Open("/d/a", Fs.OCreat | Fs.OWrOnly, 0x1A4, out _));
            Assert.AreEqual(-E.EEXIST, fs.Mkdir("/d/a", 0x1ED));
            Assert.AreEqual(-E.EEXIST, fs.Symlink("x", "/d/a"));
            WriteFile(fs, "/c", "C");
            Assert.AreEqual(-E.EEXIST, fs.Rename("/c", "/d/a"));
            Assert.AreEqual("C", ReadFile(fs, "/c"));
            Assert.AreEqual(1L + Mib, store.GrownBytes);

            // Nicht sichtbar, und der Ordner ist bis zum Schließen nicht leer.
            ILinuxFile dir = OpenOk(fs, "/d", Fs.ORdOnly);
            CollectionAssert.AreEqual(DotsOnly, ListDir(dir));
            dir.Close();
            Assert.AreEqual(-E.ENOTEMPTY, fs.Rmdir("/d"));

            // Letztes Schließen (Cosmos entfernt dann den Eintrag): genau eine Gutschrift.
            File.Delete(zombie);
            Assert.AreEqual(0L, a.Close());
            Assert.AreEqual(1L, store.GrownBytes);
            Assert.AreEqual(0, store.PendingPathCount);
            Assert.AreEqual(0L, fs.Rmdir("/d"));
            WriteFile(fs, "/d2", "frei");
            Assert.AreEqual(0, fs.OpenFiles);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void DirectoryWithOpenOrPendingChildIsNotRenamed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        const int Kib = 1 << 10;
        string tmp = TempDir();
        try
        {
            var (fs, store) = QuotaDisk(tmp, 4L << 20);
            string root = Path.Combine(tmp, "root");

            // Ablauf aus dem Review: /d/k offen, entfernt, rename /d -> /e, neues /d/k, alten Deskriptor schließen.
            Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ED));
            ILinuxFile k = OpenOk(fs, "/d/k", Create);
            Assert.AreEqual(256L * Kib, k.Write(new byte[256 * Kib]));
            Assert.AreEqual(0L, fs.Unlink("/d/k"));
            string zombie = Path.Combine(root, "d", "k");
            File.WriteAllBytes(zombie, new byte[16]); // Cosmos führt den Eintrag bis zum letzten Schließen weiter
            Assert.AreEqual(-E.EBUSY, fs.Rename("/d", "/e"));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "d")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "e")));
            Assert.AreEqual(256L * Kib, store.GrownBytes);

            // Ziel, das leer wirkt, in dem aber ein entfernter, offener Eintrag steht: -ENOTEMPTY statt -EIO.
            Assert.AreEqual(0L, fs.Mkdir("/s", 0x1ED));
            Assert.AreEqual(-E.ENOTEMPTY, fs.Rename("/s", "/d"));

            File.Delete(zombie);
            Assert.AreEqual(0L, k.Close());
            Assert.AreEqual(0L, store.GrownBytes);
            WriteFile(fs, "/d/k", "neu");
            Assert.AreEqual(0L, fs.Rename("/d", "/e")); // nichts mehr offen: geht
            Assert.AreEqual("neu", ReadFile(fs, "/e/k"));

            // Auch eine nur offene (nicht entfernte) Datei darunter sperrt das Umbenennen des Ordners.
            ILinuxFile open = OpenOk(fs, "/e/k", Fs.ORdOnly);
            Assert.AreEqual(-E.EBUSY, fs.Rename("/e", "/f"));
            Assert.AreEqual(0L, open.Close());
            Assert.AreEqual(0L, fs.Rename("/e", "/f"));
            Assert.AreEqual("neu", ReadFile(fs, "/f/k"));
            Assert.AreEqual(0, fs.OpenFiles);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void ReplaceNamesOfCosmosAreHidden()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        string tmp = TempDir();
        try
        {
            var (fs, _) = QuotaDisk(tmp, 1 << 20);
            File.WriteAllText(Path.Combine(tmp, "root", "a.~replace"), "alt");
            Assert.AreEqual(-E.ENOENT, fs.Stat("/a.~replace", false, out _));
            Assert.AreEqual(-E.EINVAL, fs.Open("/b.~REPLACE", Create, 0x1A4, out _));
            ILinuxFile root = OpenOk(fs, "/", Fs.ORdOnly);
            CollectionAssert.AreEqual(DevOnlyListing, ListDir(root));
            root.Close();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void GapThatExactlyFillsQuotaIsNotCreated()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        const int Mib = 1 << 20;
        string tmp = TempDir();
        try
        {
            var (fs, store) = QuotaDisk(tmp, Mib);
            ILinuxFile f = OpenOk(fs, "/f", Create);
            Assert.AreEqual((long)Mib, f.Seek(Mib, 0));
            Assert.AreEqual(-E.ENOSPC, f.Write("x"u8));
            Assert.AreEqual(0L, new FileInfo(Path.Combine(tmp, "root", "f")).Length, "nichts angelegt");
            Assert.AreEqual(0, store.GrowSteps);
            Assert.AreEqual(Mib - 1L, f.Seek(Mib - 1, 0));
            Assert.AreEqual(1L, f.Write("x"u8)); // Lücke + 1 Byte passt genau
            Assert.AreEqual((long)Mib, store.GrownBytes);
            f.Close();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void FailedGrowthRollsBackLengthAndQuota()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        const long Mib = 1 << 20;
        string tmp = TempDir();
        try
        {
            var store = DirectoryFileStore.Open(Path.Combine(tmp, "root"), new HostLinuxDiskLinks(), StringComparison.Ordinal).Value!;
            Assert.AreEqual(0L, store.CreateFile("f", 0x1A4, out ILinuxStoreFile? f));
            Assert.AreEqual(0L, f!.SetLength(Mib / 2));
            long quotaBefore = store.GrownBytes;

            // Wie volle Platte: der dritte Schritt scheitert, der Rückbau gelingt.
            var lengths = new List<long>();
            long length = Mib / 2;
            void FullAtThird(long next)
            {
                lengths.Add(next);
                if (lengths.Count == 3)
                {
                    throw new IOException("Platte voll", 0x10034);
                }

                length = next;
            }

            Assert.AreEqual(-E.ENOSPC, store.GrowInSteps(FullAtThird, Mib / 2, 5 * Mib));
            CollectionAssert.AreEqual(new[] { Mib + Mib / 2, 2 * Mib + Mib / 2, 3 * Mib + Mib / 2, Mib / 2 }, lengths);
            Assert.AreEqual(Mib / 2, length, "zurück auf die alte Länge");
            Assert.AreEqual(quotaBefore, store.GrownBytes, "Kontingent zurückgebucht");

            // Scheitert auch der Rückbau, gilt der erste Fehler, und das Kontingent bleibt für die erreichte Länge gebucht.
            lengths.Clear();
            void FullAndStuck(long next)
            {
                lengths.Add(next);
                if (lengths.Count >= 3)
                {
                    throw new IOException(lengths.Count == 3 ? "Platte voll" : "Rückbau gescheitert", lengths.Count == 3 ? 0x10034 : 5);
                }
            }

            Assert.AreEqual(-E.ENOSPC, store.GrowInSteps(FullAndStuck, Mib / 2, 5 * Mib));
            Assert.AreEqual(quotaBefore + 2 * Mib, store.GrownBytes);

            // Echte Datei: Wachstum ohne Fehler bleibt stehen.
            Assert.AreEqual(Mib / 2, f.Length());
            Assert.AreEqual(0L, f.Close());
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void DiskFullMapsToEnospc()
    {
        bool windows = OperatingSystem.IsWindows();
        Assert.AreEqual(windows ? -E.EIO : -E.ENOSPC, DirectoryFileStore.Errno(new IOException("voll", 28)));
        Assert.AreEqual(-E.ENOSPC, DirectoryFileStore.Errno(new IOException("voll", unchecked((int)0x80070070))));
        Assert.AreEqual(-E.ENOSPC, DirectoryFileStore.Errno(new IOException("voll", 0x10034))); // Cosmos: PalError.ENOSPC
        Assert.AreEqual(-E.ENOSPC, DirectoryFileStore.Errno(new IOException("voll", unchecked((int)0x80070027))));
        // Nackte Windows-Codes nur unter Windows; unter Linux ist 39 ENOTEMPTY und kein volles Laufwerk.
        Assert.AreEqual(windows ? -E.ENOSPC : -E.EIO, DirectoryFileStore.Errno(new IOException("voll", 0x70)));
        Assert.AreEqual(windows ? -E.ENOSPC : -E.EIO, DirectoryFileStore.Errno(new IOException("nicht leer", 39)));
        Assert.AreEqual(-E.EFBIG, DirectoryFileStore.Errno(new IOException("gross", 0x10016))); // Cosmos: PalError.EFBIG
        Assert.AreEqual(windows ? -E.EIO : -E.EFBIG, DirectoryFileStore.Errno(new IOException("gross", 27)));
        Assert.AreEqual(-E.EIO, DirectoryFileStore.Errno(new IOException("unbekannt", 0x10099)));
        Assert.AreEqual(-E.EIO, DirectoryFileStore.Errno(new IOException("anders")));
        Assert.AreEqual(-E.ENOENT, DirectoryFileStore.Errno(new FileNotFoundException()));
    }

    /// <summary>Speicher mit der Namensregel von FAT (ohne Groß-/Kleinschreibung) für die /dev-Prüfung.</summary>
    private sealed class FoldingStore(MemoryFileStore inner) : ILinuxFileStore
    {
        public StringComparison NameComparison => StringComparison.OrdinalIgnoreCase;

        public long Lookup(string path, out LinuxStoreNode node) => inner.Lookup(path, out node);

        public long ReadLink(string path, out string target) => inner.ReadLink(path, out target);

        public long List(string path, out string[] names) => inner.List(path, out names);

        public long OpenFile(string path, bool write, out ILinuxStoreFile? file) => inner.OpenFile(path, write, out file);

        public long CreateFile(string path, uint mode, out ILinuxStoreFile? file) => inner.CreateFile(path, mode, out file);

        public long CreateDirectory(string path, uint mode) => inner.CreateDirectory(path, mode);

        public long CreateSymlink(string path, string target) => inner.CreateSymlink(path, target);

        public long DeleteFile(string path) => inner.DeleteFile(path);

        public long DeleteDirectory(string path) => inner.DeleteDirectory(path);

        public long Rename(string from, string to) => inner.Rename(from, to);
    }

    [TestMethod]
    public void DevCoversEverySpellingOnCaseInsensitiveStore()
    {
        var memory = new MemoryFileStore();
        Assert.AreEqual(0L, memory.CreateDirectory("DEV", 0x1ED));
        Assert.AreEqual(0L, memory.CreateDirectory("DEV/sda", 0x1ED));
        var fs = new Fs(new FoldingStore(memory), new Sink());
        Assert.AreEqual(0L, fs.Stat("/DEV", true, out LinuxStat st));
        Assert.AreEqual(Fs.DevDevice, st.Dev);
        Assert.AreEqual(0L, fs.Stat("/Dev/null", true, out st));
        Assert.AreEqual(0x103UL, st.Rdev);
        Assert.AreEqual(-E.ENOENT, fs.Stat("/DEV/sda", true, out _));
        ILinuxFile root = OpenOk(fs, "/", Fs.ORdOnly);
        CollectionAssert.AreEqual(DevOnlyListing, ListDir(root));
        root.Close();

        // Ordinaler Speicher: dort ist "DEV" ein eigener Ordner.
        var (plain, store, _) = New();
        Assert.AreEqual(0L, store.CreateDirectory("DEV", 0x1ED));
        Assert.AreEqual(0L, plain.Stat("/DEV", true, out st));
        Assert.AreEqual(Fs.StoreDevice, st.Dev);
    }

    [TestMethod]
    public void ResolvedPathsCountUtf8Bytes()
    {
        var (fs, _, _) = New();
        string name = new('ä', 120); // 240 Bytes, 120 Zeichen
        string real = string.Empty;
        for (int depth = 1; ; depth++)
        {
            // Kurzer Eingabepfad über einen Link auf das bisher tiefste Verzeichnis; der aufgelöste Pfad wächst um 241 Bytes je Ebene.
            fs.Unlink("/s");
            Assert.AreEqual(0L, fs.Symlink(real.Length == 0 ? "/" : real, "/s"));
            long r = fs.Mkdir("/s/" + name, 0x1ED);
            int bytes = Encoding.UTF8.GetByteCount(real + "/" + name) - 1; // Speicherpfad ohne führendes '/'
            if (bytes >= Fs.PathMax)
            {
                Assert.AreEqual(-E.ENAMETOOLONG, r, "Tiefe " + depth);
                Assert.IsTrue((real + "/" + name).Length < Fs.PathMax, "in Zeichen wäre es noch kürzer als PATH_MAX");
                break;
            }

            Assert.AreEqual(0L, r, "Tiefe " + depth);
            real += "/" + name;
        }
    }

    [TestMethod]
    public void VirtualDevShadowsStoreFolderAndIsListedOnce()
    {
        var (fs, store, _) = New();
        Assert.AreEqual(0L, store.CreateDirectory("dev", 0x1ED));
        Assert.AreEqual(0L, store.CreateDirectory("dev/sda", 0x1ED));
        Assert.AreEqual(-E.ENOENT, fs.Stat("/dev/sda", true, out _));
        WriteFile(fs, "/z", "1");
        ILinuxFile root = OpenOk(fs, "/", Fs.ORdOnly);
        CollectionAssert.AreEqual(ShadowListing, ListDir(root));
        Assert.AreEqual(0L, root.ReadDir(2, out LinuxDirEntry e));
        Assert.AreEqual(LinuxMode.DtDirectory, e.Type);
        Assert.AreEqual(0L, root.ReadDir(3, out e));
        Assert.AreEqual(LinuxMode.DtRegular, e.Type);
        Assert.AreEqual(1L, root.ReadDir(4, out _));
        Assert.AreEqual(-E.EINVAL, root.ReadDir(-1, out _));
        root.Close();
    }

    [TestMethod]
    public void DirectoryOperations()
    {
        var (fs, _, _) = New();
        Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ED));
        Assert.AreEqual(-E.EEXIST, fs.Mkdir("/d", 0x1ED));
        Assert.AreEqual(-E.EEXIST, fs.Mkdir("/", 0x1ED));
        Assert.AreEqual(-E.ENOENT, fs.Mkdir("/x/y", 0x1ED));
        WriteFile(fs, "/d/f", "1");
        Assert.AreEqual(-E.ENOTEMPTY, fs.Rmdir("/d"));
        Assert.AreEqual(-E.EISDIR, fs.Unlink("/d"));
        Assert.AreEqual(-E.ENOTDIR, fs.Rmdir("/d/f"));
        Assert.AreEqual(-E.EBUSY, fs.Rmdir("/"));
        Assert.AreEqual(-E.ENOENT, fs.Rmdir("/fehlt"));
        Assert.AreEqual(-E.ENOENT, fs.Unlink("/fehlt"));

        Assert.AreEqual(0L, fs.Mkdir("/d/sub", 0x1ED));
        Assert.AreEqual(-E.EINVAL, fs.Rename("/d", "/d/sub/d"));
        Assert.AreEqual(0L, fs.Mkdir("/e", 0x1ED));
        Assert.AreEqual(-E.EISDIR, fs.Rename("/d/f", "/e"));
        Assert.AreEqual(-E.ENOTDIR, fs.Rename("/e", "/d/f"));
        Assert.AreEqual(-E.ENOTEMPTY, fs.Rename("/e", "/d"));
        Assert.AreEqual(-E.EBUSY, fs.Rename("/", "/q"));
        Assert.AreEqual(-E.ENOENT, fs.Rename("/fehlt", "/q"));
        Assert.AreEqual(0L, fs.Rename("/d", "/d"));
        Assert.AreEqual(0L, fs.Rename("/d", "/e")); // ersetzt das leere /e mit dem ganzen Teilbaum
        Assert.AreEqual("1", ReadFile(fs, "/e/f"));
        Assert.AreEqual(0L, fs.Stat("/e/sub", true, out _));
        Assert.AreEqual(-E.ENOENT, fs.Stat("/d", true, out _));
        WriteFile(fs, "/g", "2");
        Assert.AreEqual(0L, fs.Rename("/g", "/e/f")); // ersetzt eine Datei
        Assert.AreEqual("2", ReadFile(fs, "/e/f"));
        Assert.AreEqual(0L, fs.Unlink("/e/f"));
        Assert.AreEqual(0L, fs.Rmdir("/e/sub"));
        Assert.AreEqual(0L, fs.Rmdir("/e"));
    }

    [TestMethod]
    public void UnlinkedOpenFileStaysReadableAndStatable()
    {
        var (fs, store, _) = New();
        WriteFile(fs, "/f", "inhalt");
        ILinuxFile f = OpenOk(fs, "/f", Fs.ORdOnly);
        Assert.AreEqual(0L, f.Stat(out LinuxStat before));
        Assert.AreEqual(0L, fs.Unlink("/f"));
        Assert.AreEqual(6L, store.TotalBytes);
        Assert.AreEqual(0L, f.Stat(out LinuxStat after));
        Assert.AreEqual(before.Ino, after.Ino);
        var buf = new byte[16];
        Assert.AreEqual(6L, f.Read(buf));
        f.Close();
        Assert.AreEqual(0L, store.TotalBytes);
    }

    // ---- Pfade und Ausbruch ----

    [TestMethod]
    public void RejectsNonNormalizedAndBadPaths()
    {
        var (fs, _, _) = New();
        WriteFile(fs, "/f", "x");
        Assert.AreEqual(-E.EINVAL, fs.Stat("/..", true, out _));
        Assert.AreEqual(-E.EINVAL, fs.Stat("/a/../f", true, out _));
        Assert.AreEqual(-E.EINVAL, fs.Stat("f", true, out _));
        Assert.AreEqual(-E.EINVAL, fs.Stat("../f", true, out _));
        Assert.AreEqual(-E.EINVAL, fs.Stat("/f\0/x", true, out _));
        Assert.AreEqual(-E.EINVAL, fs.Stat(null!, true, out _));
        Assert.AreEqual(-E.ENOENT, fs.Stat("", true, out _));
        Assert.AreEqual(0L, fs.Stat("//f", true, out _)); // doppelter Schrägstrich ist harmlos
        Assert.AreEqual(0L, fs.Stat("/./f", true, out _));
        Assert.AreEqual(-E.ENOTDIR, fs.Stat("/f/", true, out _));
        Assert.AreEqual(-E.EINVAL, fs.Symlink("a\0b", "/l"));
        Assert.AreEqual(-E.ENOENT, fs.Symlink("", "/l"));
    }

    [TestMethod]
    public void LengthLimits()
    {
        var (fs, _, _) = New();
        string name255 = new('n', 255);
        WriteFile(fs, "/" + name255, "x");
        Assert.AreEqual(-E.ENAMETOOLONG, fs.Stat("/" + new string('n', 256), true, out _));
        // UTF-8 zählt Bytes: 128 × 'ä' = 256 Bytes
        Assert.AreEqual(-E.ENAMETOOLONG, fs.Open("/" + new string('ä', 128), Create, 0, out _));
        string deep = "/" + string.Join("/", Enumerable.Repeat("a", 2047)); // 4094 Zeichen
        Assert.AreEqual(-E.ENOENT, fs.Stat(deep, true, out _));
        Assert.AreEqual(-E.ENAMETOOLONG, fs.Stat(deep + "/bb", true, out _));
        Assert.AreEqual(-E.ENAMETOOLONG, fs.Stat("/" + new string('x', 100_000), true, out _));
        Assert.AreEqual(-E.ENAMETOOLONG, fs.Symlink(new string('t', 4096), "/l"));
        Assert.AreEqual(0L, fs.Symlink(new string('t', 4095), "/l"));
    }

    [TestMethod]
    public void SymlinksStayInsideRoot()
    {
        var (fs, store, _) = New();
        Assert.AreEqual(0L, fs.Mkdir("/etc", 0x1ED));
        WriteFile(fs, "/etc/passwd", "innen");
        Assert.AreEqual(0L, fs.Mkdir("/a", 0x1ED));
        Assert.AreEqual(0L, fs.Mkdir("/a/b", 0x1ED));
        Assert.AreEqual(0L, fs.Symlink("/etc/passwd", "/a/abs"));
        Assert.AreEqual(0L, fs.Symlink("../../../../../../etc/passwd", "/a/b/up"));
        Assert.AreEqual(0L, fs.Symlink("/../../etc/./passwd", "/a/b/mixed"));
        Assert.AreEqual(0L, fs.Symlink("..", "/a/b/parent"));
        Assert.AreEqual(0L, fs.Symlink("/dev/null", "/nul"));
        Assert.AreEqual("innen", ReadFile(fs, "/a/abs"));
        Assert.AreEqual("innen", ReadFile(fs, "/a/b/up"));
        Assert.AreEqual("innen", ReadFile(fs, "/a/b/mixed"));
        Assert.AreEqual(0L, fs.Stat("/a/b/parent/b/parent/b", true, out LinuxStat st));
        Assert.AreEqual(LinuxMode.Directory, st.Mode & LinuxMode.TypeMask);
        ILinuxFile n = OpenOk(fs, "/nul", Fs.OWrOnly);
        Assert.AreEqual("/dev/null", n.Path);
        n.Close();

        // Physisches "..": über einen Link nach /a/b und von dort zwei Ebenen hoch ist die Wurzel.
        Assert.AreEqual(0L, fs.Symlink("/a/b", "/lb"));
        Assert.AreEqual(0L, fs.Symlink("/lb/../../etc/passwd", "/via"));
        Assert.AreEqual("innen", ReadFile(fs, "/via"));
        Assert.AreEqual(0L, fs.Symlink("/", "/a/r"));
        ILinuxFile r = OpenOk(fs, "/a/r", Fs.ORdOnly);
        Assert.AreEqual("/", r.Path);
        r.Close();
        Assert.AreEqual("innen", ReadFile(fs, "/a/r/a/r/etc/passwd"));
        ILinuxFile f = OpenOk(fs, "/a/b/up", Fs.ORdOnly);
        Assert.AreEqual("/etc/passwd", f.Path);
        f.Close();
        Assert.IsTrue(store.NodeCount < 20);
    }

    [TestMethod]
    public void SymlinkSemantics()
    {
        var (fs, _, _) = New();
        WriteFile(fs, "/ziel", "z");
        Assert.AreEqual(0L, fs.Symlink("ziel", "/l"));
        Assert.AreEqual(0L, fs.Stat("/l", false, out LinuxStat ls));
        Assert.AreEqual(LinuxMode.Symlink, ls.Mode & LinuxMode.TypeMask);
        Assert.AreEqual(4L, ls.Size);
        Assert.AreEqual(0L, fs.Stat("/l", true, out LinuxStat ts));
        Assert.AreEqual(LinuxMode.Regular, ts.Mode & LinuxMode.TypeMask);
        Assert.AreEqual(0L, fs.ReadLink("/l", out string target));
        Assert.AreEqual("ziel", target);
        Assert.AreEqual(-E.EINVAL, fs.ReadLink("/ziel", out _));
        Assert.AreEqual(-E.ENOENT, fs.ReadLink("/fehlt", out _));
        Assert.AreEqual(-E.ELOOP, fs.Open("/l", Fs.ORdOnly | Fs.ONoFollow, 0, out _));
        Assert.AreEqual(-E.EEXIST, fs.Symlink("x", "/l"));
        Assert.AreEqual(-E.EEXIST, fs.Open("/l", Create | Fs.OExcl, 0, out _));

        // Hängender Link: O_CREAT legt das Ziel an.
        Assert.AreEqual(0L, fs.Symlink("neu", "/haengt"));
        WriteFile(fs, "/haengt", "n");
        Assert.AreEqual("n", ReadFile(fs, "/neu"));
        Assert.AreEqual(0L, fs.Stat("/haengt", false, out ls));
        Assert.AreEqual(LinuxMode.Symlink, ls.Mode & LinuxMode.TypeMask);

        // Link auf Datei mit '/' am Ende verlangt ein Verzeichnis.
        Assert.AreEqual(0L, fs.Symlink("ziel/", "/slash"));
        Assert.AreEqual(-E.ENOTDIR, fs.Stat("/slash", true, out _));

        // Unlink und rename wirken auf den Link selbst.
        Assert.AreEqual(0L, fs.Rename("/l", "/l2"));
        Assert.AreEqual("z", ReadFile(fs, "/l2"));
        Assert.AreEqual(0L, fs.Unlink("/l2"));
        Assert.AreEqual("z", ReadFile(fs, "/ziel"));
        Assert.AreEqual(0L, fs.Mkdir("/dir", 0x1ED));
        Assert.AreEqual(0L, fs.Symlink("dir", "/ld"));
        Assert.AreEqual(-E.ENOTDIR, fs.Rmdir("/ld"));
        Assert.AreEqual(0L, fs.Unlink("/ld"));
        Assert.AreEqual(0L, fs.Stat("/dir", true, out _));
    }

    [TestMethod]
    public void SymlinkLoopsEndWithEloop()
    {
        var (fs, _, _) = New();
        Assert.AreEqual(0L, fs.Symlink("selbst", "/selbst"));
        Assert.AreEqual(-E.ELOOP, fs.Stat("/selbst", true, out _));
        Assert.AreEqual(0L, fs.Stat("/selbst", false, out _));
        Assert.AreEqual(0L, fs.Symlink("/p2", "/p1"));
        Assert.AreEqual(0L, fs.Symlink("p1", "/p2"));
        Assert.AreEqual(-E.ELOOP, fs.Open("/p1", Create, 0, out _));

        // Kette aus genau 40 Links geht, 41 nicht.
        WriteFile(fs, "/k0", "ende");
        for (int i = 1; i <= 41; i++)
        {
            Assert.AreEqual(0L, fs.Symlink("k" + (i - 1), "/k" + i));
        }

        Assert.AreEqual("ende", ReadFile(fs, "/k40"));
        Assert.AreEqual(-E.ELOOP, fs.Stat("/k41", true, out _));
    }

    [TestMethod]
    public void MaliciousLinkExpansionEndsQuickly()
    {
        var (fs, _, _) = New();
        // Exponentielle Kette: b{i} -> b{i-1}/b{i-1}/..., ohne Budget 2^30 Auflösungen.
        Assert.AreEqual(0L, fs.Symlink(".", "/b0"));
        for (int i = 1; i <= 30; i++)
        {
            Assert.AreEqual(0L, fs.Symlink(string.Join("/", Enumerable.Repeat("b" + (i - 1), 4)), "/b" + i));
        }

        // Lange Ziele aus "./": jedes Ziel 2047 Teile, das Schrittbudget greift vor dem Linkbudget.
        string dots = string.Join("/", Enumerable.Repeat(".", 2046)) + "/d";
        Assert.AreEqual(0L, fs.Symlink("/d", "/d0"));
        for (int i = 1; i <= 39; i++)
        {
            Assert.AreEqual(0L, fs.Symlink(dots + (i - 1), "/d" + i));
        }

        var sw = Stopwatch.StartNew();
        Assert.AreEqual(-E.ELOOP, fs.Stat("/b30", true, out _));
        Assert.AreEqual(-E.ELOOP, fs.Stat("/d39", true, out _));
        Assert.AreEqual(-E.ELOOP, fs.Open("/b30/x", Create, 0, out _));
        Assert.IsTrue(sw.ElapsedMilliseconds < 2000, sw.ElapsedMilliseconds + " ms");
    }

    [TestMethod]
    public void PermissionsGiveEacces()
    {
        var (fs, store, _) = New();
        Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ED));
        WriteFile(fs, "/d/f", "x");
        WriteFile(fs, "/ro", "x");
        Assert.IsTrue(store.SetMode("ro", 0x124));
        Assert.AreEqual(-E.EACCES, fs.Open("/ro", Fs.OWrOnly, 0, out _));
        Assert.AreEqual(-E.EACCES, fs.Access("/ro", 2));
        Assert.AreEqual(0L, fs.Access("/ro", 4));
        Assert.AreEqual(-E.EACCES, fs.Access("/ro", 1));
        Assert.AreEqual(-E.EINVAL, fs.Access("/ro", 8));
        Assert.AreEqual(-E.ENOENT, fs.Access("/fehlt", 0));

        Assert.IsTrue(store.SetMode("d", 0x16D)); // 0555: kein Schreiben
        Assert.AreEqual(-E.EACCES, fs.Open("/d/neu", Create, 0, out _));
        Assert.AreEqual(-E.EACCES, fs.Unlink("/d/f"));
        Assert.AreEqual(-E.EACCES, fs.Rename("/d/f", "/f2"));
        Assert.AreEqual(-E.EACCES, fs.Symlink("x", "/d/l"));
        Assert.AreEqual("x", ReadFile(fs, "/d/f"));

        Assert.IsTrue(store.SetMode("d", 0x180)); // 0600: kein Durchsuchen
        Assert.AreEqual(-E.EACCES, fs.Stat("/d/f", true, out _));
        Assert.AreEqual(0L, fs.Stat("/d", true, out _));
        Assert.IsTrue(store.SetMode("d", 0xC0)); // 0300 ohne r
        Assert.AreEqual(-E.EACCES, fs.Open("/d", Fs.ORdOnly, 0, out _));
    }

    // ---- Budgets ----

    [TestMethod]
    public void OpenFileBudgetGivesEmfile()
    {
        var (fs, _, _) = New(maxOpen: 3);
        WriteFile(fs, "/f", "x");
        var open = new List<ILinuxFile> { OpenOk(fs, "/f", 0), OpenOk(fs, "/dev/null", 0), OpenOk(fs, "/", 0) };
        Assert.AreEqual(-E.EMFILE, fs.Open("/f", 0, 0, out _));
        Assert.AreEqual(-E.EMFILE, fs.Open("/neu", Create, 0, out _));
        Assert.AreEqual(-E.ENOENT, fs.Stat("/neu", true, out _));
        open[0].Close();
        OpenOk(fs, "/f", 0).Close();
        open[1].Close();
        open[2].Close();
        Assert.AreEqual(0, fs.OpenFiles);
    }

    [TestMethod]
    public void SizeBudgetsAndSeekLimits()
    {
        var (fs, store, _) = New(store: new MemoryFileStore(maxFileBytes: 1000, maxTotalBytes: 1500, maxNodes: 8));
        ILinuxFile f = OpenOk(fs, "/f", Create);
        Assert.AreEqual(long.MaxValue - 1, f.Seek(long.MaxValue - 1, 0));
        Assert.AreEqual(-E.EFBIG, f.Write("x"u8));
        Assert.AreEqual(-E.EOVERFLOW, f.Seek(10, 1));
        Assert.AreEqual(-E.EINVAL, f.Seek(-1, 0));
        Assert.AreEqual(-E.EINVAL, f.Seek(0, 3));
        Assert.AreEqual(998L, f.Seek(998, 0));
        Assert.AreEqual(2L, f.Write("abcd"u8)); // gekürzt bis zur Grenze
        f.Close();
        ILinuxFile g = OpenOk(fs, "/g", Create);
        Assert.AreEqual(-E.ENOSPC, g.Write(new byte[600]));
        Assert.AreEqual(500L, g.Write(new byte[500]));
        g.Close();
        Assert.AreEqual(1500L, store.TotalBytes);
        for (int i = 0; store.NodeCount < 8; i++)
        {
            Assert.AreEqual(0L, fs.Mkdir("/m" + i, 0x1ED));
        }

        Assert.AreEqual(-E.ENOSPC, fs.Mkdir("/voll", 0x1ED));
    }

    // ---- Datenplatte (Ordner) mit echtem Ausbruchsversuch ----

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zehos-vfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void DirectoryStoreKeepsHostSymlinksInsideRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Symlinks auf dem Host nur unter Unix geprüft.");
        }

        string tmp = TempDir();
        try
        {
            string root = Path.Combine(tmp, "root");
            File.WriteAllText(Path.Combine(tmp, "geheim"), "GEHEIM");
            var opened = DirectoryFileStore.Open(root, new HostLinuxDiskLinks(), StringComparison.Ordinal);
            Assert.IsTrue(opened.IsOK());
            var tty = new Sink();
            var fs = new Fs(opened.Value!, tty);
            // Links, die ein anderer Weg (Paketinhalt, Kopie) auf die Platte gelegt hat, zeigen nach draußen.
            File.CreateSymbolicLink(Path.Combine(root, "rel"), "../geheim");
            File.CreateSymbolicLink(Path.Combine(root, "abs"), Path.Combine(tmp, "geheim"));
            Directory.CreateSymbolicLink(Path.Combine(root, "up"), "..");
            Assert.AreEqual(-E.ENOENT, fs.Open("/rel", 0, 0, out _));
            Assert.AreEqual(-E.ENOENT, fs.Open("/abs", 0, 0, out _));
            Assert.AreEqual(-E.ENOENT, fs.Open("/up/geheim", 0, 0, out _));
            Assert.AreEqual(-E.ENOENT, fs.Open("/up/root/up/geheim", 0, 0, out _));
            // Ein Anlegen über den Link landet in der Wurzel, nicht außerhalb.
            WriteFile(fs, "/rel", "neu");
            Assert.AreEqual("neu", File.ReadAllText(Path.Combine(root, "geheim")));
            Assert.AreEqual("GEHEIM", File.ReadAllText(Path.Combine(tmp, "geheim")));
            Assert.AreEqual(0L, fs.ReadLink("/rel", out string target));
            Assert.AreEqual("../geheim", target);

            // Hängender Link lässt sich umbenennen (Fuzz Saat 5: File.Move scheiterte am fehlenden Ziel).
            Assert.AreEqual(0L, fs.Symlink("nirgends", "/h"));
            Assert.AreEqual(0L, fs.Rename("/h", "/h2"));
            Assert.AreEqual(0L, fs.ReadLink("/h2", out target));
            Assert.AreEqual("nirgends", target);
            Assert.AreEqual(-E.ENOENT, fs.Stat("/h", false, out _));
            Assert.AreEqual(0L, fs.Unlink("/h2"));

            WriteFile(fs, "/geheim", "innen");
            Assert.AreEqual("innen", ReadFile(fs, "/rel"));
            Assert.AreEqual("GEHEIM", File.ReadAllText(Path.Combine(tmp, "geheim")));
            Assert.AreEqual(0L, fs.Unlink("/geheim"));

            // Rest des Ordners: Lesen, Schreiben, Verzeichnisse, eigene Links.
            Assert.AreEqual(0L, fs.Mkdir("/d", 0x1ED));
            WriteFile(fs, "/d/f", "hallo");
            Assert.AreEqual("hallo", File.ReadAllText(Path.Combine(root, "d", "f")));
            Assert.AreEqual(0L, fs.Symlink("/d/f", "/lf"));
            Assert.AreEqual("hallo", ReadFile(fs, "/lf"));
            Assert.AreEqual(0L, fs.Rename("/d", "/e"));
            Assert.AreEqual("hallo", ReadFile(fs, "/e/f"));
            Assert.AreEqual(-E.ENOTEMPTY, fs.Rmdir("/e"));
            Assert.AreEqual(-E.ENOENT, fs.Stat("/lf", true, out _));
            ILinuxFile dir = OpenOk(fs, "/", 0);
            CollectionAssert.AreEqual(DiskListing, ListDir(dir));
            dir.Close();
            Assert.AreEqual(0L, fs.Unlink("/e/f"));
            Assert.AreEqual(0L, fs.Rmdir("/e"));
            Assert.AreEqual(0, fs.OpenFiles);
            CollectionAssert.AreEquivalent(DiskOutside, Directory.GetFileSystemEntries(tmp).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void DirectoryStoreRejectsAmbiguousDiskNames()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel; Windows-Pfade nimmt DirectoryFileStore nicht an.");
        }

        string tmp = TempDir();
        try
        {
            string root = Path.Combine(tmp, "root");
            var fs = new Fs(DirectoryFileStore.Open(root, new HostLinuxDiskLinks(), StringComparison.Ordinal).Value!, new Sink());
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, ".zehos-system"), "s");
            foreach (string bad in AmbiguousNames)
            {
                Assert.AreEqual(-E.EINVAL, fs.Open(bad, Create, 0, out _), bad);
                Assert.AreEqual(-E.EINVAL, fs.Mkdir(bad, 0x1ED), bad);
            }

            Assert.AreEqual(-E.ENOENT, fs.Stat("/.zehos-system", true, out _));
            ILinuxFile dir = OpenOk(fs, "/", 0);
            CollectionAssert.AreEqual(AmbiguousListing, ListDir(dir));
            dir.Close();
            CollectionAssert.AreEquivalent(AmbiguousOnDisk, Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToArray());

            var bigFs = new Fs(DirectoryFileStore.Open(root, new HostLinuxDiskLinks(), StringComparison.Ordinal, maxFileBytes: 100).Value!, new Sink());
            ILinuxFile big = OpenOk(bigFs, "/big", Create);
            Assert.AreEqual(1L << 40, big.Seek(1L << 40, 0));
            Assert.AreEqual(-E.EFBIG, big.Write("x"u8));
            big.Close();
            Assert.AreEqual(0L, new FileInfo(Path.Combine(root, "big")).Length);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [TestMethod]
    public void DirectoryStoreOpenValidatesRootWithoutEchoingIt()
    {
        foreach (string bad in BadRoots)
        {
            var r = DirectoryFileStore.Open(bad, new HostLinuxDiskLinks(), StringComparison.Ordinal);
            Assert.IsFalse(r.IsOK(), bad);
            if (bad.Length > 1)
            {
                Assert.IsFalse(r.Status.Message!.Contains(bad, StringComparison.Ordinal), bad);
            }
        }
    }

    // ---- Fuzz gegen ein unabhängiges Modell ----

    /// <summary>
    /// Unabhängiges Modell: Wörterbuch absoluter Pfade, Auflösung rekursiv über ganze Linkziele (statt eines Teilestapels), ".."
    /// am Wurzelende abgeschnitten. Liefert nur ok/Fehler und Inhalte, keine Fehlernummern (deren Prüfreihenfolge ist nicht Teil
    /// des Modells).
    /// </summary>
    private sealed class Model
    {
        public sealed class Node
        {
            public char Kind; // 'd', 'f', 'l'
            public string Data = "";
            public string Target = "";
        }

        public readonly Dictionary<string, Node> Nodes = new() { ["/"] = new Node { Kind = 'd' } };

        private static string Join(List<string> parts) => "/" + string.Join("/", parts);

        /// <summary>(ok, realer Pfad, vorhanden).</summary>
        public (bool Ok, string Path, bool Exists) Resolve(string path, bool follow)
        {
            int links = 0;
            var r = Walk(new List<string>(), path.Split('/', StringSplitOptions.RemoveEmptyEntries), follow, ref links);
            return (r.Ok, r.Ok ? Join(r.Real) : "", r.Exists);
        }

        private (bool Ok, List<string> Real, bool Exists) Walk(List<string> start, string[] parts, bool followLast, ref int links)
        {
            var real = new List<string>(start);
            for (int i = 0; i < parts.Length; i++)
            {
                string c = parts[i];
                bool last = i == parts.Length - 1;
                if (c == ".")
                {
                    continue;
                }

                if (c == "..")
                {
                    if (real.Count > 0)
                    {
                        real.RemoveAt(real.Count - 1);
                    }

                    continue;
                }

                var candidate = new List<string>(real) { c };
                if (!Nodes.TryGetValue(Join(candidate), out Node? node))
                {
                    return last ? (true, candidate, false) : (false, real, false);
                }

                if (node.Kind == 'l' && (!last || followLast))
                {
                    if (++links > 40)
                    {
                        return (false, real, false);
                    }

                    var sub = Walk(node.Target.StartsWith('/') ? new List<string>() : real,
                        node.Target.Split('/', StringSplitOptions.RemoveEmptyEntries), true, ref links);
                    if (!sub.Ok)
                    {
                        return sub;
                    }

                    if (last)
                    {
                        return sub;
                    }

                    if (!sub.Exists || Nodes[Join(sub.Real)].Kind != 'd')
                    {
                        return (false, real, false);
                    }

                    real = sub.Real;
                    continue;
                }

                if (node.Kind != 'd')
                {
                    return last ? (true, candidate, true) : (false, real, false);
                }

                real = candidate;
            }

            return (true, real, true);
        }

        public static string Parent(string p) => p.LastIndexOf('/') == 0 ? "/" : p.Substring(0, p.LastIndexOf('/'));

        public bool IsEmptyDir(string p) => !Nodes.Keys.Any(k => k.StartsWith(p == "/" ? "/" : p + "/", StringComparison.Ordinal) && k != p);
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(7, false)]
    [DataRow(42, false)]
    [DataRow(2026, false)]
    [DataRow(5, true)]
    [DataRow(99, true)]
    public void FuzzAgainstModel(int seed, bool disk)
    {
        if (disk && OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Symlinks auf dem Host nur unter Unix geprüft.");
        }

        string? tmp = disk ? TempDir() : null;
        try
        {
            ILinuxFileStore store;
            if (tmp != null)
            {
                File.WriteAllText(Path.Combine(tmp, "a"), "DRAUSSEN");
                store = DirectoryFileStore.Open(Path.Combine(tmp, "root"), new HostLinuxDiskLinks(), StringComparison.Ordinal).Value!;
            }
            else
            {
                store = new MemoryFileStore();
            }

            RunFuzz(seed, new Fs(store, new Sink()), disk ? 400 : 1500);
            if (tmp != null)
            {
                Assert.AreEqual("DRAUSSEN", File.ReadAllText(Path.Combine(tmp, "a")));
                CollectionAssert.AreEquivalent(FuzzOutside, Directory.GetFileSystemEntries(tmp).Select(Path.GetFileName).ToArray());
            }
        }
        finally
        {
            if (tmp != null)
            {
                Directory.Delete(tmp, true);
            }
        }
    }

    [TestMethod]
    public void FuzzManySeedsInMemory()
    {
        for (int seed = 100; seed < 140; seed++)
        {
            RunFuzz(seed, new Fs(new MemoryFileStore(), new Sink()), 800);
        }
    }

    private static void RunFuzz(int seed, Fs fs, int steps)
    {
        var rnd = new Random(seed);
        var m = new Model();
        string[] names = ["a", "b", "c"];
        string[] targets = ["a", "b", "/a", "../a", "../../../b", "a/b", ".", "..", "/", "c/../a", "/a/b/c", "b/./c", "/c/a"];
        string RandomPath()
        {
            int depth = rnd.Next(1, 4);
            return "/" + string.Join("/", Enumerable.Range(0, depth).Select(_ => names[rnd.Next(names.Length)]));
        }

        int successes = 0;
        for (int step = 0; step < steps; step++)
        {
            string p = RandomPath();
            int op = rnd.Next(9);
            string ctx = $"Saat {seed}, Schritt {step}, Op {op}, {p}";
            switch (op)
            {
                case 0: // mkdir
                {
                    var r = m.Resolve(p, false);
                    bool ok = r.Ok && !r.Exists;
                    long got = fs.Mkdir(p, 0x1ED);
                    Assert.AreEqual(ok, got == 0, ctx + " " + got);
                    if (ok)
                    {
                        m.Nodes[r.Path] = new Model.Node { Kind = 'd' };
                    }

                    break;
                }

                case 1: // Datei schreiben
                {
                    string data = "d" + step;
                    var r = m.Resolve(p, true);
                    bool ok = r.Ok && (!r.Exists || m.Nodes[r.Path].Kind == 'f');
                    long got = fs.Open(p, Fs.OCreat | Fs.OWrOnly | Fs.OTrunc, 0x1A4, out ILinuxFile? f);
                    Assert.AreEqual(ok, got == 0, ctx + " " + got);
                    if (ok)
                    {
                        Assert.AreEqual((long)data.Length, f!.Write(Encoding.ASCII.GetBytes(data)));
                        f.Close();
                        m.Nodes[r.Path] = new Model.Node { Kind = 'f', Data = data };
                    }

                    break;
                }

                case 2: // lesen
                {
                    var r = m.Resolve(p, true);
                    long got = fs.Open(p, Fs.ORdOnly, 0, out ILinuxFile? f);
                    Assert.AreEqual(r.Ok && r.Exists, got == 0, ctx + " " + got);
                    if (got == 0)
                    {
                        Model.Node node = m.Nodes[r.Path];
                        Assert.AreEqual(node.Kind == 'd', f!.IsDirectory, ctx);
                        Assert.AreEqual(r.Path, f.Path, ctx);
                        if (node.Kind == 'f')
                        {
                            var buf = new byte[64];
                            long n = f.Read(buf);
                            Assert.AreEqual(node.Data, Encoding.ASCII.GetString(buf, 0, (int)n), ctx);
                        }
                        else
                        {
                            var expected = m.Nodes.Keys.Where(k => k != r.Path && Model.Parent(k) == r.Path).Select(k => k.Substring(k.LastIndexOf('/') + 1));
                            if (r.Path == "/")
                            {
                                expected = expected.Append("dev");
                            }

                            CollectionAssert.AreEquivalent(expected.Append(".").Append("..").ToList(), ListDir(f), ctx);
                        }

                        f.Close();
                    }

                    break;
                }

                case 3: // unlink
                {
                    var r = m.Resolve(p, false);
                    bool ok = r.Ok && r.Exists && m.Nodes[r.Path].Kind != 'd';
                    long got = fs.Unlink(p);
                    Assert.AreEqual(ok, got == 0, ctx + " " + got);
                    if (ok)
                    {
                        m.Nodes.Remove(r.Path);
                    }

                    break;
                }

                case 4: // rmdir
                {
                    var r = m.Resolve(p, false);
                    bool ok = r.Ok && r.Exists && r.Path != "/" && m.Nodes[r.Path].Kind == 'd' && m.IsEmptyDir(r.Path);
                    long got = fs.Rmdir(p);
                    Assert.AreEqual(ok, got == 0, ctx + " " + got);
                    if (ok)
                    {
                        m.Nodes.Remove(r.Path);
                    }

                    break;
                }

                case 5: // symlink
                {
                    string target = targets[rnd.Next(targets.Length)];
                    var r = m.Resolve(p, false);
                    bool ok = r.Ok && !r.Exists;
                    long got = fs.Symlink(target, p);
                    Assert.AreEqual(ok, got == 0, ctx + " " + got);
                    if (ok)
                    {
                        m.Nodes[r.Path] = new Model.Node { Kind = 'l', Target = target };
                    }

                    break;
                }

                case 6: // rename
                {
                    string q = RandomPath();
                    ctx += " -> " + q;
                    var a = m.Resolve(p, false);
                    var b = m.Resolve(q, false);
                    bool ok = a.Ok && b.Ok && a.Exists;
                    bool aDir = ok && m.Nodes[a.Path].Kind == 'd';
                    if (ok && a.Path == b.Path)
                    {
                        Assert.AreEqual(0L, fs.Rename(p, q), ctx);
                        break;
                    }

                    if (ok && aDir && b.Path.StartsWith(a.Path + "/", StringComparison.Ordinal))
                    {
                        ok = false;
                    }

                    if (ok && b.Exists)
                    {
                        bool bDir = m.Nodes[b.Path].Kind == 'd';
                        ok = aDir == bDir && (!bDir || m.IsEmptyDir(b.Path));
                    }

                    long got = fs.Rename(p, q);
                    Assert.AreEqual(ok, got == 0, ctx + " " + got);
                    if (ok)
                    {
                        m.Nodes.Remove(b.Path);
                        foreach (string key in m.Nodes.Keys.Where(k => k == a.Path || k.StartsWith(a.Path + "/", StringComparison.Ordinal)).ToList())
                        {
                            Model.Node node = m.Nodes[key];
                            m.Nodes.Remove(key);
                            m.Nodes[string.Concat(b.Path, key.AsSpan(a.Path.Length))] = node;
                        }
                    }

                    break;
                }

                default: // stat mit und ohne Folgen
                {
                    bool follow = op == 7;
                    var r = m.Resolve(p, follow);
                    long got = fs.Stat(p, follow, out LinuxStat st);
                    Assert.AreEqual(r.Ok && r.Exists, got == 0, ctx + " " + got);
                    if (got == 0)
                    {
                        Model.Node node = m.Nodes[r.Path];
                        uint type = node.Kind switch { 'd' => LinuxMode.Directory, 'f' => LinuxMode.Regular, _ => LinuxMode.Symlink };
                        Assert.AreEqual(type, st.Mode & LinuxMode.TypeMask, ctx);
                        if (node.Kind == 'f')
                        {
                            Assert.AreEqual((long)node.Data.Length, st.Size, ctx);
                        }
                    }

                    break;
                }
            }

            successes++;
        }

        Assert.AreEqual(steps, successes);
        Assert.AreEqual(0, fs.OpenFiles);
    }
}
