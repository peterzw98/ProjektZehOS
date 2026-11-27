using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Tests.Processes;

/// <summary>
/// Ladybird V41: Fontconfig nimmt eine Cache-Datei nur, wenn ihre Änderungszeit strikt über 0 s liegt. Der Zähler des Speichers
/// ergab zuvor st_mtime 0 (Nanosekunden 1, 2, ...), und der Cache unter /tmp wurde in jedem Prozess verworfen.
/// </summary>
[TestClass]
public sealed class MemoryFileStoreTimeTests
{
    [TestMethod]
    public void NewFileHasMtimeOverOneSecondAndRenameAdvancesIt()
    {
        var store = new MemoryFileStore();
        Assert.AreEqual(0, store.CreateFile("/a", 0x1A4, out ILinuxStoreFile? _));
        Assert.AreEqual(0, store.Lookup("/a", out LinuxStoreNode first));
        Assert.IsTrue(first.MtimeNanos >= 1_000_000_000L, "mtime " + first.MtimeNanos);
        Assert.AreEqual(0, store.Rename("/a", "/b"));
        Assert.AreEqual(0, store.Lookup("/b", out LinuxStoreNode second));
        Assert.IsTrue(second.MtimeNanos > first.MtimeNanos);
    }

    [TestMethod]
    public void FileTimeNanosReplacesZeroByFixedValue()
    {
        Assert.AreEqual(DirectoryFileStore.UnknownFileTimeNanos, DirectoryFileStore.FileTimeNanos(System.DateTime.UnixEpoch));
        Assert.AreEqual(DirectoryFileStore.UnknownFileTimeNanos, DirectoryFileStore.FileTimeNanos(System.DateTime.UnixEpoch));
    }
}
