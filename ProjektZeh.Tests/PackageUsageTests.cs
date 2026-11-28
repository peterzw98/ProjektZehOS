using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Packages;

namespace ProjektZeh.Tests;

/// <summary>
/// N3-B3: Nutzungszaehlung je Paketversion. Ein Upgrade entfernt alte Versionen sofort, wenn niemand sie gebunden hat; ein laufendes
/// Programm behaelt seine Dateien, bis es freigibt, und erst die Bereinigung danach entfernt sie. Reconcile beim Einbinden laesst
/// gebundene Versionen liegen.
/// </summary>
public sealed partial class PackageIndex2Tests
{
    private string[] MuslVersions() =>
        Directory.Exists(Path.Combine(Root, "v2", "musl"))
            ? Directory.GetDirectories(Path.Combine(Root, "v2", "musl")).Select(path => Path.GetFileName(path)!).Order().ToArray()
            : [];

    private static readonly PackageVersion2 MuslR1Version = Version("1.2.5-r1");
    private static readonly PackageVersion2 MuslR2Version = Version("1.2.5-r2");

    private static PackageVersion2 Version(string text)
    {
        Assert.IsTrue(PackageVersion2.TryParse(text, out var version));
        return version;
    }

    [TestMethod]
    public void UpgradeWithoutRunningProgramRemovesOldVersionAtOnce()
    {
        var (store, service) = Installed();
        Assert.AreEqual(1, store.Users("musl", MuslR1Version), "Der veroeffentlichte Stand haelt seine Versionen.");

        UpgradeMusl(service);
        CollectionAssert.AreEqual(new[] { "1.2.5-r2" }, MuslVersions());
        Assert.AreEqual(0, store.Users("musl", MuslR1Version));
        Assert.AreEqual(1, store.Users("musl", MuslR2Version));
        Assert.IsFalse(service.CollectPending);
    }

    [TestMethod]
    public void RunningProgramKeepsOldFilesUntilLastReleaseThenCollectRemovesThem()
    {
        var (store, service) = Installed();
        var first = service.Bind().Value!;
        var second = service.Bind().Value!;

        UpgradeMusl(service);
        // Der Auftrag hat nach dem Wechsel aufgeraeumt, r1 aber liegen lassen: Zwei Laeufe lesen noch daraus.
        CollectionAssert.AreEqual(new[] { "1.2.5-r1", "1.2.5-r2" }, MuslVersions());
        Assert.AreEqual(1, store.Users("musl", MuslR1Version), "Ein Nutzer je lebendem Stand, nicht je Lauf.");
        Assert.AreEqual("musl r1", ReadAll(RootOf(first.Index, out _), "/usr/lib64/libc.so"));

        first.Dispose();
        Assert.IsFalse(service.CollectPending, "Der zweite Lauf haelt r1 noch.");
        Assert.IsTrue(service.CollectUnused().IsOK());
        CollectionAssert.AreEqual(new[] { "1.2.5-r1", "1.2.5-r2" }, MuslVersions());
        Assert.AreEqual("musl r1", ReadAll(RootOf(second.Index, out _), "/usr/lib64/libc.so"));

        second.Dispose();
        second.Dispose(); // doppelte Freigabe zaehlt nicht doppelt
        Assert.IsTrue(service.CollectPending);
        Assert.AreEqual(0, store.Users("musl", MuslR1Version));
        Assert.IsTrue(service.CollectUnused().IsOK());
        Assert.IsFalse(service.CollectPending);
        CollectionAssert.AreEqual(new[] { "1.2.5-r2" }, MuslVersions());
        // libfoo und app sind in beiden Staenden gleich und bleiben; der neue Stand startet weiter.
        Assert.AreEqual(1, store.Users("libfoo", Version("1.0.0-r1")));
        using var next = service.Bind().Value!;
        Assert.AreEqual("musl r2 neu", ReadAll(RootOf(next.Index, out _), "/usr/lib64/libc.so"));
    }

    [TestMethod]
    public void ReconcileAtMountKeepsVersionsOfRunningPrograms()
    {
        var (store, service) = Installed();
        var lease = service.Bind().Value!;
        UpgradeMusl(service);

        // Einbinden (disk mount) waehrend des Laufs: volles Reconcile, aber r1 bleibt, weil der Lauf es gebunden hat.
        Assert.IsTrue(service.Reconcile().IsOK());
        CollectionAssert.AreEqual(new[] { "1.2.5-r1", "1.2.5-r2" }, MuslVersions());
        Assert.IsTrue(store.Reconcile().IsOK(), "Auch der Store allein entfernt nichts Gebundenes.");
        CollectionAssert.AreEqual(new[] { "1.2.5-r1", "1.2.5-r2" }, MuslVersions());
        Assert.AreEqual("musl r1", ReadAll(RootOf(lease.Index, out _), "/usr/lib64/libc.so"));

        lease.Dispose();
        Assert.IsTrue(service.Reconcile().IsOK());
        CollectionAssert.AreEqual(new[] { "1.2.5-r2" }, MuslVersions());
        Assert.IsFalse(service.CollectPending, "Reconcile beim Einbinden erledigt auch die Vormerkung.");
    }

    [TestMethod]
    public void ReleaseDuringJobIsCleanedByTheJobAndCollectWaitsForIt()
    {
        PackageService2? service = null;
        PackageIndexLease? lease = null;
        string? collectDuringJob = null;
        var (store, created) = Installed(step =>
        {
            if (lease == null) return;
            // Mitten im Auftrag endet der Lauf (vor dem neuen Stand); vorgemerkt wird erst mit dessen Veroeffentlichung, und eine
            // Bereinigung von aussen kommt dann nicht durch, weil der Auftrag laeuft.
            if (step == "index") lease.Dispose();
            if (step == "aufraeumen")
            {
                Assert.IsTrue(service!.CollectPending);
                collectDuringJob = service.CollectUnused().Message;
            }
        });
        service = created;
        lease = service.Bind().Value!;
        UpgradeMusl(service);

        StringAssert.Contains(collectDuringJob, "bearbeitet gerade");
        // Der Auftrag hat nach dem Veroeffentlichen des neuen Stands selbst aufgeraeumt.
        Assert.IsFalse(service.CollectPending);
        Assert.AreEqual(0, store.Users("musl", MuslR1Version));
        CollectionAssert.AreEqual(new[] { "1.2.5-r2" }, MuslVersions());
        Assert.AreEqual(0, service.BoundUsers().Length);
    }
}
