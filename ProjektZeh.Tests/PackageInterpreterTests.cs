using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Tests;

/// <summary>
/// N3-E: Der Interpreter (PT_INTERP) des ersten Programms von <c>run --pkg</c> kommt aus der gebundenen Paketwurzel, mit denselben
/// Regeln wie ein spaeteres execve (Links, Ausfuehrrecht, Inhaltspruefung) und aus dem gebundenen Indexstand.
/// </summary>
public sealed partial class PackageIndex2Tests
{
    private static string Interpreter(PackageRootStore store, string path)
    {
        long r = LinuxInterpreterFile.Read(store, path, 1000, 1000, out byte[] content);
        return r < 0 ? "errno " + -r : Encoding.ASCII.GetString(content);
    }

    [TestMethod]
    public void InterpreterComesFromTheBoundPackageRootThroughLinks()
    {
        var (_, service) = Installed();
        using var lease = service.Bind().Value!;
        RootOf(lease.Index, out PackageRootStore store);
        Assert.AreEqual("musl r1", Interpreter(store, "/lib/ld-musl-x86_64.so.1"));
        // Datei- und Ordnerlink ueber Paketgrenzen (usr/lib64 -> lib, libc.so -> ../../lib/ld-musl…) wie beim execve.
        Assert.AreEqual("musl r1", Interpreter(store, "/usr/lib64/libc.so"));
    }

    [TestMethod]
    public void InterpreterOfARunningStartStaysAcrossUpgradeAndARestartTakesTheNewOne()
    {
        var (_, service) = Installed();
        var lease = service.Bind().Value!;
        RootOf(lease.Index, out PackageRootStore running);
        UpgradeMusl(service);

        // Laufender Start liest weiter seinen Stand (etwa ein spaeteres execve), ein Neustart bindet den neuen.
        Assert.AreEqual("musl r1", Interpreter(running, "/lib/ld-musl-x86_64.so.1"));
        using var restart = service.Bind().Value!;
        RootOf(restart.Index, out PackageRootStore next);
        Assert.AreEqual("musl r2 neu", Interpreter(next, "/lib/ld-musl-x86_64.so.1"));
        lease.Dispose();
    }

    [TestMethod]
    public void MissingOrUnusableInterpreterIsAnErrnoNotAnImage()
    {
        var (_, service) = Installed();
        using var lease = service.Bind().Value!;
        RootOf(lease.Index, out PackageRootStore store);
        Assert.AreEqual("errno " + LinuxErrno.ENOENT, Interpreter(store, "/lib/ld-linux-x86-64.so.2"));
        Assert.AreEqual("errno " + LinuxErrno.EACCES, Interpreter(store, "/usr/share/app/readme")); // kein Ausfuehrrecht
        Assert.AreEqual("errno " + LinuxErrno.EACCES, Interpreter(store, "/usr/lib")); // Verzeichnis

        // Veraenderter Inhalt bei gleicher Laenge: die Inhaltspruefung der Wurzel verweigert den Interpreter.
        string location = lease.Index.Resolve("/lib/ld-musl-x86_64.so.1").Value!.Location!;
        File.WriteAllText(location, "musl XX", Encoding.ASCII);
        RootOf(lease.Index, out PackageRootStore fresh);
        Assert.AreEqual("errno " + LinuxErrno.EIO, Interpreter(fresh, "/lib/ld-musl-x86_64.so.1"));
        Assert.IsTrue(service.BoundUsers().Single().Users == 1);
    }

    // Kleines ET_DYN: ein PT_LOAD R|X ab 0 ueber loadSize Bytes, optional PT_INTERP bei 0x180, danach bis size Bytes "Debugabschnitte".
    private static byte[] DynElf(int size, ulong loadSize, string? interp = null)
    {
        byte[] b = new byte[size];
        for (int i = 0x200; i < size; i++) b[i] = (byte)(i * 7 + 1);
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F'; b[4] = 2; b[5] = 1; b[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), ElfFile.TypeShared);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), ElfFile.MachineX86_64);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(54), ElfFile.ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), (ushort)(interp == null ? 1 : 2));
        void Header(int i, uint type, uint flags, ulong offset, ulong fileSize)
        {
            Span<byte> p = b.AsSpan(64 + (i * 56), 56);
            BinaryPrimitives.WriteUInt32LittleEndian(p, type);
            BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(4), flags);
            BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(8), offset);
            BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(16), offset);
            BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(24), offset);
            BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(32), fileSize);
            BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(40), fileSize);
            BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(48), 4096);
        }

        Header(0, ElfProgramHeader.Load, 5, 0, loadSize);
        if (interp != null)
        {
            Encoding.ASCII.GetBytes(interp).CopyTo(b, 0x180);
            Header(1, ElfProgramHeader.Interpreter, 4, 0x180, (ulong)interp.Length + 1);
        }

        return b;
    }

    private static MemoryFileStore StoreWith(string path, byte[] content)
    {
        var store = new MemoryFileStore();
        Assert.AreEqual(0L, store.CreateDirectory("lib", 0x1ED));
        Assert.AreEqual(0L, store.CreateFile(path.TrimStart('/'), 0x1ED, out ILinuxStoreFile? file));
        Assert.AreEqual(content.Length, file!.WriteAt(0, content));
        return store;
    }

    [TestMethod]
    public void InterpreterLimitCountsOnlyTheLoadablePartLikeExecve()
    {
        // Review N3-E P3-1: 0x1100 ladbar, dahinter 8 KiB Debugabschnitte. Vorher zaehlte die ganze Datei (0x3100) gegen die Grenze.
        MemoryFileStore store = StoreWith("/lib/ld.so", DynElf(0x3100, 0x1100));
        Assert.AreEqual(0L, LinuxInterpreterFile.Read(store, "/lib/ld.so", 1000, 1000, 0x2000, out byte[] content));
        Assert.AreEqual(0x1100, content.Length, "nur der ladbare Teil");
        Assert.AreEqual(-ProjektZeh.Processes.Syscalls.LinuxSyscalls.ENOMEM, LinuxInterpreterFile.Read(store, "/lib/ld.so", 1000, 1000, 0x1000, out _));
    }

    [TestMethod]
    public void LoadOptionsOfARunReadTheInterpreterFromTheStore()
    {
        // Review N3-E P3-2: die Verdrahtung von Prepare (CosmosLinuxRun) ohne Kernel: Lader -> Leser -> Speicher.
        byte[] interp = DynElf(0x1100, 0x1100);
        MemoryFileStore store = StoreWith("/lib/ld.so", interp);
        byte[] random = new byte[16];
        ElfLoadOptions options = LinuxInterpreterFile.LoadOptions(store, random, 1);
        Assert.AreEqual(1UL, options.HwCap);
        UserImage image = ElfLoader.Load(DynElf(0x1100, 0x1100, "/lib/ld.so"), ["/bin/prog"], [], options);
        CollectionAssert.AreEqual(interp, image.Interpreter);
        Assert.AreEqual(ElfLoader.InterpreterBase + 0x1000, image.Entry);

        var missing = Assert.ThrowsException<ElfLoadException>(() =>
            ElfLoader.Load(DynElf(0x1100, 0x1100, "/lib/fehlt.so"), ["/bin/prog"], [], LinuxInterpreterFile.LoadOptions(store, random, 1)));
        Assert.AreEqual(ElfLoadError.InterpreterUnavailable, missing.Kind);
        Assert.AreEqual(LinuxErrno.ENOENT, missing.Errno);
    }
}
