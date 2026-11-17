using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

/// <summary>Etappe 13.4 (Spec 2026-11-17-app-storage-13-4.md): App-Speicher /save einer Paket-App.</summary>
[TestClass]
public sealed class WasmSaveStoreTests
{
    private string root = "";
    private bool blocked;

    [TestInitialize]
    public void Create() => root = Directory.CreateTempSubdirectory("zehos-save-").FullName;

    [TestCleanup]
    public void Delete()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private WasmSaveStore Store(WasmSaveLimits? limits = null, long available = long.MaxValue) =>
        new(root, () => blocked, diskSpace: _ => ResultObjectFactory.CreateSuccess((long.MaxValue, available)), limits: limits);

    private static int Create(WasmSaveStore store, string path, out int handle) =>
        store.Open(path, create: true, exclusive: false, truncate: true, directory: false, write: true, out handle);

    private static int Write(WasmSaveStore store, int handle, string text, long offset = 0) =>
        store.Write(handle, offset, Encoding.UTF8.GetBytes(text), out _);

    private string Disk(string relative) => File.ReadAllText(Path.Combine(root, relative));

    [TestMethod]
    public void WrittenFileAppearsOnlyAfterSyncOrCloseAndSurvivesANewStore()
    {
        var store = Store();
        Assert.AreEqual(WasiErrno.Success, Create(store, "welt.cw", out int handle));
        Assert.AreEqual("", Disk("welt.cw"), "O_CREAT legt die Datei sofort an");
        Assert.AreEqual(WasiErrno.Success, Write(store, handle, "Hallo Welt"));
        Assert.AreEqual("", Disk("welt.cw"), "vor Sync bleibt die alte Fassung");
        Assert.AreEqual(WasiErrno.Success, store.Sync(handle));
        Assert.AreEqual("Hallo Welt", Disk("welt.cw"));
        Assert.AreEqual(WasiErrno.Success, Write(store, handle, "!", 10));
        Assert.AreEqual(WasiErrno.Success, store.Close(handle));
        Assert.AreEqual(0, store.BufferedBytes);

        // Neuer Lauf (Neustart der App): die Datei ist da und lesbar.
        var again = Store();
        Assert.AreEqual(WasiErrno.Success, again.Open("welt.cw", false, false, false, false, false, out int reading));
        var buffer = new byte[64];
        Assert.AreEqual(WasiErrno.Success, again.Read(reading, 0, buffer, out int read));
        Assert.AreEqual("Hallo Welt!", Encoding.UTF8.GetString(buffer, 0, read));
        Assert.AreEqual(11, again.Size(reading));
        Assert.AreEqual(WasiErrno.BadFile, again.Write(reading, 0, [1], out _), "nur lesend geoeffnet");
    }

    [TestMethod]
    public void TemporaryFileAndRenameReplaceAtomically()
    {
        var store = Store();
        Create(store, "welt.cw", out int first);
        Write(store, first, "alt");
        store.Close(first);
        Create(store, "welt.cw.tmp", out int temporary);
        Write(store, temporary, "neu");
        store.Close(temporary);
        Assert.AreEqual(WasiErrno.Success, store.Rename("welt.cw.tmp", "welt.cw"));
        Assert.AreEqual("neu", Disk("welt.cw"));
        Assert.IsFalse(File.Exists(Path.Combine(root, "welt.cw.tmp")));
        Assert.IsFalse(Directory.EnumerateFiles(root).Any(path => Path.GetFileName(path).StartsWith(".zehos-", StringComparison.Ordinal)), "keine Reste");
    }

    [TestMethod]
    public void PathsStayInsideSaveAndSystemNamesAreHidden()
    {
        var store = Store();
        foreach (string path in new[] { "../x", "a/../b", "/abs", "", ".zehos-x", "a//b", "a:b", "a\\b", "x." })
            Assert.AreEqual(WasiErrno.Access, store.Open(path, true, false, false, false, true, out _), path);
        Assert.AreEqual(WasiErrno.Access, store.Rename("../x", "y"));
        Assert.AreEqual(WasiErrno.Access, store.CreateDirectory("a/.."));

        File.WriteAllText(Path.Combine(root, ".zehos-intern.tmp"), "x");
        Assert.AreEqual(WasiErrno.Success, store.CreateDirectory("maps"));
        Create(store, "maps/a.cw", out int handle);
        store.Close(handle);
        Assert.AreEqual(WasiErrno.Success, store.Open("maps", false, false, false, true, false, out int directory));
        Assert.AreEqual(WasiErrno.Success, store.List(directory, out var entries));
        CollectionAssert.AreEqual(new[] { "a.cw" }, entries.Select(entry => entry.Name).ToArray());

        // opendir("/save") der WASI-libc oeffnet "."; die Wurzel ist lesbar, aber nicht veraenderbar.
        Assert.AreEqual(WasiErrno.Success, store.Open(".", false, false, false, true, false, out int rootDirectory));
        Assert.AreEqual(WasiErrno.Success, store.List(rootDirectory, out entries));
        CollectionAssert.AreEqual(new[] { "maps" }, entries.Select(entry => entry.Name).ToArray(), ".zehos- bleibt verborgen");
        Assert.AreEqual(WasiErrno.IsDirectory, store.Open(".", true, false, true, false, true, out _));
        Assert.AreEqual(WasiErrno.Access, store.RemoveDirectory("."));
        Assert.AreEqual(WasiErrno.Access, store.Rename(".", "x"));
        Assert.AreEqual(WasiErrno.IsDirectory, store.Unlink("."));
        Assert.AreEqual(WasiErrno.Success, store.Stat(".", out _, out bool isDirectory));
        Assert.IsTrue(isDirectory);
    }

    [TestMethod]
    public void OpenFlagsBehaveLikeWasi()
    {
        var store = Store();
        Assert.AreEqual(WasiErrno.NotFound, store.Open("fehlt", false, false, false, false, false, out _));
        Create(store, "da", out int handle);
        store.Close(handle);
        Assert.AreEqual(WasiErrno.Exists, store.Open("da", true, true, false, false, true, out _), "O_EXCL");
        Assert.AreEqual(WasiErrno.NotDirectory, store.Open("da", false, false, false, true, false, out _));
        store.CreateDirectory("ordner");
        Assert.AreEqual(WasiErrno.IsDirectory, store.Open("ordner", true, false, false, false, true, out _));
        Assert.AreEqual(WasiErrno.Exists, store.CreateDirectory("ordner"));
        Assert.AreEqual(WasiErrno.NotFound, store.CreateDirectory("nicht/da"));
        Assert.AreEqual(WasiErrno.Invalid, store.Open("ordner", true, false, false, true, false, out _));
    }

    [TestMethod]
    public void RemoveAndUnlinkCheckTheEntryType()
    {
        var store = Store();
        store.CreateDirectory("ordner");
        Create(store, "ordner/datei", out int handle);
        store.Close(handle);
        Assert.AreEqual(WasiErrno.NotEmpty, store.RemoveDirectory("ordner"));
        Assert.AreEqual(WasiErrno.IsDirectory, store.Unlink("ordner"));
        Assert.AreEqual(WasiErrno.NotDirectory, store.RemoveDirectory("ordner/datei"));
        Assert.AreEqual(WasiErrno.Success, store.Unlink("ordner/datei"));
        Assert.AreEqual(WasiErrno.Success, store.RemoveDirectory("ordner"));
        Assert.AreEqual(WasiErrno.NotFound, store.Unlink("ordner/datei"));
        Assert.AreEqual(WasiErrno.NotFound, store.Stat("x", out _, out _));
    }

    [TestMethod]
    public void LimitsAreCheckedBeforeAllocationAndWriting()
    {
        var limits = new WasmSaveLimits(TotalBytes: 100, Entries: 3, FileBytes: 64, BufferBytes: 96, WritableFiles: 2);
        var store = Store(limits);
        Create(store, "a", out int a);
        Assert.AreEqual(WasiErrno.FileTooLarge, store.Write(a, 0, new byte[65], out _), "Datei ueber 64 Byte");
        Assert.AreEqual(WasiErrno.FileTooLarge, store.SetSize(a, 65));
        Assert.AreEqual(WasiErrno.Success, store.Write(a, 0, new byte[64], out _));
        Create(store, "b", out int b);
        Assert.AreEqual(WasiErrno.NoSpace, store.Write(b, 0, new byte[64], out _), "Pufferbudget 96 Byte");
        Assert.IsTrue(store.BufferedBytes <= 96);
        Assert.AreEqual(WasiErrno.TooManyFiles, Create(store, "c", out _), "hoechstens zwei beschreibbare");
        Assert.AreEqual(WasiErrno.Success, store.Close(a));
        Assert.AreEqual(WasiErrno.Success, store.Write(b, 0, new byte[40], out _));
        Assert.AreEqual(WasiErrno.NoSpace, store.Close(b), "Quote 100 Byte: 64 + 40");
        Assert.AreEqual(0, store.BufferedBytes, "Close gibt den Puffer auch bei Fehler frei");
        Assert.AreEqual(0, new FileInfo(Path.Combine(root, "b")).Length, "alte Fassung bleibt");
        Create(store, "c", out int c);
        store.Close(c);
        Assert.AreEqual(WasiErrno.NoSpace, Create(store, "d", out _), "hoechstens drei Eintraege");
        Assert.AreEqual(WasiErrno.NoSpace, store.CreateDirectory("e"));
    }

    [TestMethod]
    public void FullDiskReportsNoSpaceAndKeepsTheOldFile()
    {
        var store = Store(available: 64 * 1024 + 10);
        Create(store, "welt", out int handle);
        Write(store, handle, "kurz");
        Assert.AreEqual(WasiErrno.Success, store.Sync(handle));
        Assert.AreEqual(WasiErrno.Success, store.Write(handle, 0, new byte[100], out _), "der Puffer im Kernel waechst noch");
        Assert.AreEqual(WasiErrno.NoSpace, store.Sync(handle), "erst das Schreiben scheitert am vollen Datentraeger");
        Assert.AreEqual("kurz", Disk("welt"));
    }

    [TestMethod]
    public void BlockedWritesReportBusyButReadsWork()
    {
        var store = Store();
        Create(store, "welt", out int handle);
        Write(store, handle, "x");
        blocked = true;
        Assert.AreEqual(WasiErrno.Busy, store.Sync(handle));
        Assert.AreEqual(WasiErrno.Busy, store.Write(handle, 0, [1], out _));
        Assert.AreEqual(WasiErrno.Busy, Create(store, "neu", out _));
        Assert.AreEqual(WasiErrno.Busy, store.Rename("welt", "x"));
        Assert.AreEqual(WasiErrno.Busy, store.Unlink("welt"));
        Assert.AreEqual(WasiErrno.Busy, store.CreateDirectory("d"));
        Assert.AreEqual(WasiErrno.Success, store.Open("welt", false, false, false, false, false, out _), "Lesen bleibt erlaubt");
        blocked = false;
        Assert.AreEqual(WasiErrno.Success, store.Close(handle));
        Assert.AreEqual("x", Disk("welt"));
    }

    [TestMethod]
    public void MissingDriveReportsNotFound()
    {
        var store = Store();
        Create(store, "welt", out int handle);
        Write(store, handle, "x");
        Directory.Delete(root, true);
        Assert.AreEqual(WasiErrno.NotFound, store.Close(handle));
        Assert.AreEqual(WasiErrno.NotFound, Create(store, "neu", out _));
        Assert.AreEqual(WasiErrno.NotFound, store.CreateDirectory("d"));
    }

    [TestMethod]
    public void DirectoryEntriesUseTheWasiLayoutAndResumeAtTheCookie()
    {
        (string, bool)[] entries = [("a.cw", false), ("maps", true)];
        var buffer = new byte[256];
        int used = WasiDirectoryEntries.Write(entries, 0, buffer);
        Assert.AreEqual(24 + 4 + 24 + 4, used);
        Assert.AreEqual(1UL, BitConverter.ToUInt64(buffer, 0), "d_next des ersten Eintrags");
        Assert.AreEqual(4U, BitConverter.ToUInt32(buffer, 16), "d_namlen");
        Assert.AreEqual(4, buffer[20], "regulaere Datei");
        Assert.AreEqual("a.cw", Encoding.UTF8.GetString(buffer, 24, 4));
        Assert.AreEqual(2UL, BitConverter.ToUInt64(buffer, 28));
        Assert.AreEqual(3, buffer[28 + 20], "Ordner");

        Assert.AreEqual(28, WasiDirectoryEntries.Write(entries, 1, buffer), "Fortsetzung ab Cookie 1");
        Assert.AreEqual("maps", Encoding.UTF8.GetString(buffer, 24, 4));
        Assert.AreEqual(0, WasiDirectoryEntries.Write(entries, 2, buffer), "Ende");
        Assert.AreEqual(30, WasiDirectoryEntries.Write(entries, 0, buffer.AsSpan(0, 30)), "abgeschnitten, Puffer voll");
    }

    [TestMethod]
    public void RenameStaysInsideAndRefusesDirectoryClashes()
    {
        var store = Store();
        store.CreateDirectory("a");
        store.CreateDirectory("b");
        Create(store, "f", out int handle);
        store.Close(handle);
        Assert.AreEqual(WasiErrno.IsDirectory, store.Rename("f", "a"));
        Assert.AreEqual(WasiErrno.Exists, store.Rename("a", "b"));
        Assert.AreEqual(WasiErrno.Success, store.Rename("a", "c"));
        Assert.AreEqual(WasiErrno.NotFound, store.Rename("fehlt", "x"));
        Assert.AreEqual(WasiErrno.NotFound, store.Rename("f", "nicht/da"));
    }
}
