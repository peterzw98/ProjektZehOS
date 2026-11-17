using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Migration;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Tests;

/// <summary>Etappe 4.3: Profilwahl beim Boot (Spec storage-etappe4-design, Abschnitt 2, Tabelle); jede Zeile ein Test.</summary>
[TestClass]
public sealed class LayoutProfileTests
{
    private static readonly Guid Disk = new("11111111-2222-4333-8444-555555555555");
    private static readonly IdentityResult Healthy = new(InstalledState.Healthy, "cause='Healthy' message='ok'");
    private static readonly IdentityResult Unclean = new(InstalledState.RootUnclean, "cause='RootUnclean' message='offline e2fsck'");

    private static ResultObject<LayoutMarker> Marker(LayoutState state, Guid? disk = null) =>
        ResultObjectFactory.CreateSuccess(new LayoutMarker(state, disk ?? Disk));

    private static LayoutDecision Decide(IdentityResult? identity, ResultObject<LayoutMarker>? marker, bool accounts) =>
        LayoutProfile.Decide(identity, marker, accounts, Disk);

    [TestMethod]
    public void NoLayout_Legacy()
    {
        var decision = Decide(null, null, false);
        Assert.AreEqual(LayoutOutcome.Legacy, decision.Outcome);
        Assert.AreEqual(PathProfile.Legacy, decision.Profile);
        Assert.IsNull(decision.BlockedReason);
    }

    [TestMethod]
    public void LegacyIdentity_Legacy() =>
        Assert.AreEqual(LayoutOutcome.Legacy, Decide(new(InstalledState.Legacy, "cause='NoInstallMarker' message='x'"), null, true).Outcome);

    [TestMethod]
    public void HealthyReadyWithAccounts_RootReadWrite()
    {
        var decision = Decide(Healthy, Marker(LayoutState.Ready), true);
        Assert.AreEqual(LayoutOutcome.Root, decision.Outcome);
        Assert.AreEqual(PathProfile.Root, decision.Profile);
        Assert.IsFalse(decision.SetupAllowed);
        Assert.IsFalse(decision.MarkReadyOnSave);
        Assert.IsNull(decision.BlockedReason);
    }

    [TestMethod]
    public void HealthyFreshWithoutAccounts_RootWithSetup()
    {
        var decision = Decide(Healthy, Marker(LayoutState.Fresh), false);
        Assert.AreEqual(LayoutOutcome.Root, decision.Outcome);
        Assert.IsTrue(decision.SetupAllowed);
        Assert.IsTrue(decision.MarkReadyOnSave);
    }

    [TestMethod]
    public void HealthyFreshWithAccounts_RootNoSetupReadyOnNextSave()
    {
        // Einrichtung nach dem Speichern der Konten abgebrochen: Konten existieren, nie ein zweiter Admin.
        var decision = Decide(Healthy, Marker(LayoutState.Fresh), true);
        Assert.AreEqual(LayoutOutcome.Root, decision.Outcome);
        Assert.IsFalse(decision.SetupAllowed);
        Assert.IsTrue(decision.MarkReadyOnSave);
    }

    [TestMethod]
    public void UncleanReady_RootReadOnly()
    {
        var decision = Decide(Unclean, Marker(LayoutState.Ready), true);
        Assert.AreEqual(LayoutOutcome.RootReadOnly, decision.Outcome);
        Assert.AreEqual(PathProfile.Root, decision.Profile);
        Assert.IsFalse(decision.SetupAllowed);
        StringAssert.Contains(decision.BlockedReason, "RootUnclean");
    }

    [TestMethod]
    [DataRow((int)LayoutState.Fresh)]
    [DataRow((int)LayoutState.Migrating)]
    public void UncleanNotReady_Unclear(int state) =>
        AssertUnclear(Decide(Unclean, Marker((LayoutState)state), true));

    [TestMethod]
    public void HealthyReadyWithoutAccounts_Unclear()
    {
        // Kontendatei verloren: nie ein neuer Admin (7a Entscheidung 3), SetupWork nur bei fresh.
        var decision = Decide(Healthy, Marker(LayoutState.Ready), false);
        AssertUnclear(decision);
        StringAssert.Contains(decision.BlockedReason, "AccountsMissing");
    }

    [TestMethod]
    public void HealthyMigrating_Unclear() => AssertUnclear(Decide(Healthy, Marker(LayoutState.Migrating), true));

    [TestMethod]
    public void HealthyMarkerMissing_Unclear() => AssertUnclear(Decide(Healthy, null, true));

    [TestMethod]
    public void HealthyMarkerInvalid_Unclear()
    {
        var invalid = ResultObjectFactory.CreateFailure<LayoutMarker>(ResultCode.Failure, "cause='MarkerChecksum' message='x'");
        var decision = Decide(Healthy, invalid, true);
        AssertUnclear(decision);
        StringAssert.Contains(decision.BlockedReason, "MarkerChecksum");
    }

    [TestMethod]
    public void HealthyMarkerOtherDisk_Unclear()
    {
        // Ein auf eine andere Platte kopiertes Root gehoert nicht zu diesem ESP-Marker.
        var decision = Decide(Healthy, Marker(LayoutState.Ready, new Guid("aaaaaaaa-2222-4333-8444-555555555555")), true);
        AssertUnclear(decision);
        StringAssert.Contains(decision.BlockedReason, "LayoutDiskMismatch");
    }

    [TestMethod]
    [DataRow((int)InstalledState.RootMissing)]
    [DataRow((int)InstalledState.RootDamaged)]
    [DataRow((int)InstalledState.Unclear)]
    public void IdentityBlocked_LegacyEspAsDataBlocked(int state)
    {
        // Wie 7a: ESP als /data, Profil Legacy, Einrichtung und Recovery gesperrt; der Root-Marker zaehlt dann nicht.
        var identity = new IdentityResult((InstalledState)state, "cause='X' message='gesperrt'");
        var decision = Decide(identity, Marker(LayoutState.Ready), true);
        Assert.AreEqual(LayoutOutcome.Blocked, decision.Outcome);
        Assert.AreEqual(PathProfile.Legacy, decision.Profile);
        Assert.IsFalse(decision.SetupAllowed);
        Assert.AreEqual(identity.Reason, decision.BlockedReason);
    }

    private static void AssertUnclear(LayoutDecision decision)
    {
        Assert.AreEqual(LayoutOutcome.Blocked, decision.Outcome);
        Assert.AreEqual(PathProfile.Legacy, decision.Profile);
        Assert.IsFalse(decision.SetupAllowed);
        Assert.IsFalse(decision.MarkReadyOnSave);
        StringAssert.Contains(decision.BlockedReason, "Keine Ersteinrichtung");
    }
}
