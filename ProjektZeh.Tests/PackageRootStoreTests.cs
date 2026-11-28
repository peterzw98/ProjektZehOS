using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Packages;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

/// <summary>
/// N3-B2: Bindung eines Prozessstarts an einen Indexstand (bis zur Freigabe, auch ueber ein Upgrade hinweg) und die Prozesswurzel aus
/// Index und beschreibbarem Speicher, ueber das echte <see cref="LinuxFileSystem"/> geprueft.
/// </summary>
public sealed partial class PackageIndex2Tests
{
    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    private static LinuxFileSystem RootOf(PackageIndex2 index, out PackageRootStore store)
    {
        store = new PackageRootStore(index, new MemoryFileStore());
        return new LinuxFileSystem(store, new TerminalDescriptor(new Lines()));
    }

    private static string ReadAll(LinuxFileSystem fs, string path)
    {
        long opened = fs.Open(path, LinuxFileSystem.ORdOnly, 0, out ILinuxFile? file);
        if (opened < 0) return "errno " + -opened;
        var bytes = new List<byte>();
        var buffer = new byte[3]; // klein, damit mehrere ReadAt mit Versatz laufen
        for (long read; (read = file!.Read(buffer)) > 0;) bytes.AddRange(buffer.Take((int)read));
        file!.Close();
        return Encoding.ASCII.GetString([.. bytes]);
    }

    private void UpgradeMusl(PackageService2 service)
    {
        Publish(MuslR2);
        PublishCatalog(2, MuslR2, FooR1, AppR1);
        var source = new DirectoryPackageSource(Repo);
        Assert.IsTrue(service.Update(source, CancellationToken.None).IsOK());
        var upgraded = service.Upgrade(source, CancellationToken.None);
        Assert.IsTrue(upgraded.IsOK(), upgraded.Status.Message);
    }

    [TestMethod]
    public void BusyRunReleasesTheHandedOverBinding()
    {
        // Review B2, P2: Run kehrt bei belegtem Lauf vor seinem try zurueck; die abgegebene Bindung darf dort nicht liegen bleiben.
        var (_, service) = Installed();
        var lease = service.Bind().Value!;
        int busy = 1;
        Assert.IsFalse(LinuxRunGate.TryEnter(ref busy, lease));
        Assert.AreEqual(1, busy);
        Assert.IsTrue(lease.Released);
        Assert.AreEqual(0, service.BoundUsers().Length);

        // Freier Lauf: die Bindung gehoert danach dem Lauf und bleibt bestehen.
        var held = service.Bind().Value!;
        busy = 0;
        Assert.IsTrue(LinuxRunGate.TryEnter(ref busy, held));
        Assert.AreEqual(1, busy);
        Assert.IsFalse(held.Released);
        Assert.AreEqual(1, service.BoundUsers().Single().Users);
        held.Dispose();
    }

    [TestMethod]
    public void BoundStartKeepsItsIndexAcrossUpgradeUntilReleased()
    {
        var (_, service) = Installed();
        var first = service.Bind();
        Assert.IsTrue(first.IsOK(), first.Status.Message);
        var lease = first.Value!;
        long oldGeneration = lease.Index.Generation;
        Assert.AreEqual(1, service.BoundUsers().Single().Users);

        UpgradeMusl(service);
        var second = service.Bind().Value!;
        Assert.IsTrue(second.Index.Generation > oldGeneration);
        Assert.AreEqual("musl 1.2.5-r2 lib/ld-musl-x86_64.so.1 exec", Describe(second.Index.Resolve("/usr/lib/libc.so")));

        // Der alte Lauf liest ueber seine Wurzel weiter r1, der neue r2 (Inhalt aus der jeweiligen Ablage, gegen das Manifest geprueft).
        Assert.AreEqual("musl r1", ReadAll(RootOf(lease.Index, out _), "/usr/lib64/libc.so"));
        Assert.AreEqual("musl r2 neu", ReadAll(RootOf(second.Index, out _), "/usr/lib64/libc.so"));
        CollectionAssert.AreEqual(new[] { (oldGeneration, 1), (second.Index.Generation, 1) }, service.BoundUsers());

        lease.Dispose();
        lease.Dispose(); // Fehlerwege duerfen doppelt freigeben, gezaehlt wird einmal.
        Assert.IsTrue(lease.Released);
        CollectionAssert.AreEqual(new[] { (second.Index.Generation, 1) }, service.BoundUsers());
        second.Dispose();
        Assert.AreEqual(0, service.BoundUsers().Length);
    }

    [TestMethod]
    public void BindWithoutValidIndexFailsInsteadOfEmptyRoot()
    {
        var store = new PackageStore2(Root, Trust, Kernel);
        var service = new PackageService2(Root, store, Trust);
        // Leerer Bestand ist ein gueltiger, leerer Index; nur ein beschaedigtes active2 verhindert den Start.
        var empty = service.Bind();
        Assert.IsTrue(empty.IsOK(), empty.Status.Message);
        empty.Value!.Dispose();
        var untrusted = new PackageService2(Root, store, null).Bind();
        Assert.IsFalse(untrusted.IsOK());
    }

    [TestMethod]
    public void ConcurrentStartsDuringUpgradeEachBindOneWholeState()
    {
        var (_, service) = Installed();
        Publish(MuslR2);
        PublishCatalog(2, MuslR2, FooR1, AppR1);
        var source = new DirectoryPackageSource(Repo);
        Assert.IsTrue(service.Update(source, CancellationToken.None).IsOK());

        var seen = new List<string>();
        var failures = new List<string>();
        using var stop = new CancellationTokenSource();
        var starters = Enumerable.Range(0, 4).Select(number => new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var bound = service.Bind();
                if (!bound.IsOK())
                {
                    lock (failures) failures.Add(bound.Status.Message);
                    continue;
                }

                using var lease = bound.Value!;
                // Ein ganzer Stand: musl und der Link darauf kommen aus derselben Menge, die Datei passt zur Version.
                string version = lease.Index.Resolve("/usr/lib/libc.so").Value!.Version.ToString();
                string content = ReadAll(RootOf(lease.Index, out _), "/lib/ld-musl-x86_64.so.1");
                lock (seen) seen.Add(version + "=" + content);
            }
        })).ToArray();
        foreach (var thread in starters) thread.Start();
        var upgraded = service.Upgrade(source, CancellationToken.None);
        Thread.Sleep(50);
        stop.Cancel();
        foreach (var thread in starters) thread.Join();

        Assert.IsTrue(upgraded.IsOK(), upgraded.Status.Message);
        Assert.AreEqual(0, failures.Count, string.Join("; ", failures));
        CollectionAssert.IsSubsetOf(seen.Distinct().ToArray(), new[] { "1.2.5-r1=musl r1", "1.2.5-r2=musl r2 neu" });
        Assert.IsTrue(seen.Contains("1.2.5-r2=musl r2 neu"));
        Assert.AreEqual(0, service.BoundUsers().Length);
    }

    [TestMethod]
    public void ProcessRootServesPackagesReadOnlyAndKeepsScratchWritable()
    {
        var (_, service) = Installed();
        using var lease = service.Bind().Value!;
        var fs = RootOf(lease.Index, out _);
        Assert.AreEqual("app r1", ReadAll(fs, "/usr/bin/app-link"));
        Assert.AreEqual("foo r1", ReadAll(fs, "/usr/lib64/libfoo.so.1"));
        Assert.AreEqual(0, fs.ReadLink("/usr/lib/libc.so", out string target));
        Assert.AreEqual("../../lib/ld-musl-x86_64.so.1", target);
        Assert.AreEqual(0, fs.Stat("/usr/bin/app", true, out LinuxStat st));
        Assert.AreEqual(LinuxMode.Regular | 0x1EDu, st.Mode);
        Assert.AreEqual(6, st.Size);
        Assert.AreEqual(0, fs.Stat("/usr/share/app/readme", true, out st));
        Assert.AreEqual(LinuxMode.Regular | 0x1A4u, st.Mode);

        // Paketbaum: keine Aenderung, auch nicht in einem neuen Unterordner oder ueber eine andere Schreibweise.
        Assert.AreEqual(-LinuxErrno.EROFS, fs.Mkdir("/usr/bin/neu", 0x1ED));
        Assert.AreEqual(-LinuxErrno.EROFS, fs.Mkdir("/USR/neu", 0x1ED));
        Assert.AreEqual(-LinuxErrno.EROFS, fs.Unlink("/usr/bin/app"));
        Assert.AreEqual(-LinuxErrno.EROFS, fs.Symlink("/x", "/lib/neu"));
        long created = fs.Open("/usr/bin/neu", LinuxFileSystem.OWrOnly | LinuxFileSystem.OCreat, 0x1A4, out _);
        Assert.AreEqual(-LinuxErrno.EROFS, created);
        long written = fs.Open("/usr/bin/app", LinuxFileSystem.ORdWr, 0, out _);
        Assert.AreEqual(-LinuxErrno.EROFS, written);

        // Ausserhalb: beschreibbar, und die Wurzel zeigt beides.
        Assert.AreEqual(0, fs.Mkdir("/tmp", 0x1FF));
        Assert.AreEqual(0, fs.Open("/tmp/a", LinuxFileSystem.OWrOnly | LinuxFileSystem.OCreat, 0x1A4, out ILinuxFile? scratch));
        Assert.AreEqual(1, scratch!.Write("x"u8));
        scratch.Close();
        Assert.AreEqual(-LinuxErrno.EROFS, fs.Rename("/tmp/a", "/usr/a"));
        Assert.AreEqual(-LinuxErrno.EROFS, fs.Rename("/usr/bin/app", "/tmp/b"));
        Assert.AreEqual(0, fs.Open("/", LinuxFileSystem.ORdOnly | LinuxFileSystem.ODirectory, 0, out ILinuxFile? dir));
        var names = new List<string>();
        for (long i = 0; dir!.ReadDir(i, out LinuxDirEntry entry) == 0; i++) names.Add(entry.Name);
        dir.Close();
        CollectionAssert.IsSubsetOf(new[] { "lib", "tmp", "usr" }, names);
    }

    [TestMethod]
    public void ProcessRootRejectsTamperedPackageFileAndVerifiesOnce()
    {
        var (_, service) = Installed();
        using var lease = service.Bind().Value!;
        var checks = new List<string>();
        var store = new PackageRootStore(lease.Index, new MemoryFileStore()) { OnVerify = checks.Add };
        var fs = new LinuxFileSystem(store, new TerminalDescriptor(new Lines()));
        Assert.AreEqual("app r1", ReadAll(fs, "/usr/bin/app"));
        Assert.AreEqual("app r1", ReadAll(fs, "/usr/bin/app-link"));
        CollectionAssert.AreEqual(new[] { "usr/bin/app" }, checks);

        // Gleiche Laenge, anderer Inhalt: eine neue Wurzel (neuer Start) prueft wieder und verweigert die Datei.
        string location = lease.Index.Resolve("/usr/lib/libfoo.so.1").Value!.Location!;
        File.WriteAllText(location, "foo XX", Encoding.ASCII);
        Assert.AreEqual("errno " + LinuxErrno.EIO, ReadAll(fs, "/usr/lib/libfoo.so.1"));
        File.Delete(location);
        Assert.AreEqual("errno " + LinuxErrno.EIO, ReadAll(RootOf(lease.Index, out _), "/usr/lib/libfoo.so.1"));
    }
}
