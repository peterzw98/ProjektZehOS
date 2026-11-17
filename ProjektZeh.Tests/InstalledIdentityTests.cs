using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Tests;

/// <summary>Etappe 7a Schritt 3a: ESP-Marker und Identitaet des installierten Systems (Spec 7a, Abschnitt 5).</summary>
[TestClass]
public sealed class InstalledIdentityTests
{
    private const ulong Blocks = 64UL * 1024 * 1024 * 1024 / 512;
    private static readonly Guid Disk = new("11111111-2222-4333-8444-555555555555");
    private static readonly Guid EspGuid = new("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly Guid RootGuid = new("99999999-8888-4777-8666-555555555555");
    private static readonly GptEntry Esp = new(GptLayout.EspType, EspGuid, 2048, 2048 + 1048576 - 1, GptLayoutWriter.EspName);
    private static readonly GptEntry Root = new(GptLayout.LinuxFileSystemType, RootGuid, 1050624, Blocks - 40, GptLayoutWriter.RootName);
    private static readonly InstallMarker Marker = new(Disk, RootGuid, Root.FirstLba, Root.LastLba - Root.FirstLba + 1);

    private static ResultObject<GptTable> Table(params GptEntry[] entries) => ResultObjectFactory.CreateSuccess(new GptTable(Disk, entries));
    private static readonly ResultObject<GptTable> InvalidTable = GptLayout.Failure("PrimaryHeaderCrc", "kaputt");

    private static InstalledState Evaluate(string? marker, ResultObject<GptTable>? table, RootCheck gate = RootCheck.Ok) =>
        InstalledIdentity.Evaluate(marker, table, _ => gate).State;

    // --- Marker-Format ---

    [TestMethod]
    public void Marker_RoundTrip()
    {
        string text = Marker.Serialize();
        Assert.IsTrue(Encoding.UTF8.GetByteCount(text) <= InstallMarker.MaximumBytes);
        StringAssert.StartsWith(text, "zehos-install 1\n");
        var parsed = InstallMarker.Parse(text);
        Assert.IsTrue(parsed.IsOK(), parsed.Status.Message);
        Assert.AreEqual(Marker, parsed.Value);
    }

    [TestMethod]
    public void Marker_ChangedLine_FailsChecksum()
    {
        string text = Marker.Serialize().Replace("root-first=1050624", "root-first=1050632", StringComparison.Ordinal);
        StringAssert.Contains(InstallMarker.Parse(text).Status.Message, "cause='MarkerChecksum'");
    }

    [TestMethod]
    [DataRow("", DisplayName = "leer")]
    [DataRow("zehos-install 1\n", DisplayName = "nur Kopfzeile")]
    [DataRow("zehos-install 2\ndisk=x\ncrc32=00000000\n", DisplayName = "unbekannte Version")]
    public void Marker_Malformed_Invalid(string text) => Assert.IsFalse(InstallMarker.Parse(text).IsOK());

    [TestMethod]
    public void Marker_TooLongOrTooManyLines_Invalid()
    {
        StringAssert.Contains(InstallMarker.Parse(new string('x', 513)).Status.Message, "cause='MarkerTooLarge'");
        string lines = string.Concat(System.Linq.Enumerable.Repeat("a\n", 13));
        StringAssert.Contains(InstallMarker.Parse(lines).Status.Message, "cause='MarkerTooLarge'");
    }

    [TestMethod]
    public void Marker_ResealedButWrongFields_Invalid()
    {
        // Gueltige Pruefsumme, aber unbekannter Schluessel bzw. falsche Zahl: die Feldpruefung muss abweisen.
        StringAssert.Contains(InstallMarker.Parse(Reseal("zehos-install 1\ndisk=" + Disk + "\nroot=" + RootGuid + "\nroot-first=1\nroot-size=2\n")).Status.Message, "cause='MarkerField'");
        StringAssert.Contains(InstallMarker.Parse(Reseal("zehos-install 1\ndisk=" + Disk + "\nroot=" + RootGuid + "\nroot-first=-1\nroot-sectors=2\n")).Status.Message, "cause='MarkerField'");
        StringAssert.Contains(InstallMarker.Parse(Reseal("zehos-install 1\ndisk=kein-guid\nroot=" + RootGuid + "\nroot-first=1\nroot-sectors=2\n")).Status.Message, "cause='MarkerField'");
    }

    private static string Reseal(string body) =>
        body + "crc32=" + Crc32.Compute(Encoding.UTF8.GetBytes(body)).ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + "\n";

    // --- Tabelle Abschnitt 5 ---

    [TestMethod]
    public void NoMarker_NoGpt_Legacy() => Assert.AreEqual(InstalledState.Legacy, Evaluate(null, null));

    [TestMethod]
    public void NoMarker_GptWithoutRoot_Legacy() => Assert.AreEqual(InstalledState.Legacy, Evaluate(null, Table(Esp)));

    [TestMethod]
    public void NoMarker_GptWithRootPartition_Unclear() => Assert.AreEqual(InstalledState.Unclear, Evaluate(null, Table(Esp, Root)));

    [TestMethod]
    public void NoMarker_InvalidGpt_Unclear() => Assert.AreEqual(InstalledState.Unclear, Evaluate(null, InvalidTable));

    [TestMethod]
    public void Marker_Healthy() => Assert.AreEqual(InstalledState.Healthy, Evaluate(Marker.Serialize(), Table(Esp, Root)));

    [TestMethod]
    public void Marker_RootEntryDeleted_RootMissing() => Assert.AreEqual(InstalledState.RootMissing, Evaluate(Marker.Serialize(), Table(Esp)));

    [TestMethod]
    public void Marker_Unreadable_Unclear()
    {
        Assert.AreEqual(InstalledState.Unclear, Evaluate("zehos-install 1\nkaputt\n", Table(Esp, Root)));
    }

    [TestMethod]
    public void Marker_WithoutGptOrInvalidGpt_Unclear()
    {
        Assert.AreEqual(InstalledState.Unclear, Evaluate(Marker.Serialize(), null));
        Assert.AreEqual(InstalledState.Unclear, Evaluate(Marker.Serialize(), InvalidTable));
    }

    [TestMethod]
    public void Marker_OtherDiskGuid_Unclear()
    {
        string other = (Marker with { DiskGuid = Guid.NewGuid() }).Serialize();
        Assert.AreEqual(InstalledState.Unclear, Evaluate(other, Table(Esp, Root)));
    }

    [TestMethod]
    [DataRow(8L, 0L, DisplayName = "Start verschoben")]
    [DataRow(0L, 8L, DisplayName = "vergroessert")]
    [DataRow(0L, -8L, DisplayName = "verkleinert")]
    public void Marker_RootPositionOrSizeDiffers_RootDamaged(long startShift, long endShift)
    {
        var moved = Root with { FirstLba = (ulong)((long)Root.FirstLba + startShift), LastLba = (ulong)((long)Root.LastLba + endShift) };
        Assert.AreEqual(InstalledState.RootDamaged, Evaluate(Marker.Serialize(), Table(Esp, moved)));
    }

    [TestMethod]
    public void Marker_RootGuidWithWrongType_RootDamaged()
    {
        var wrongType = Root with { Type = GptLayout.EspType };
        Assert.AreEqual(InstalledState.RootDamaged, Evaluate(Marker.Serialize(), Table(Esp, wrongType)));
    }

    [TestMethod]
    [DataRow((int)RootCheck.Reject)]
    [DataRow((int)RootCheck.ReadOnly)]
    // RootCheck ist internal; oeffentliche Testmethoden nehmen den Wert deshalb als int (CS0051).
    public void Marker_GateNotOk_RootDamaged(int gate) =>
        Assert.AreEqual(InstalledState.RootDamaged, Evaluate(Marker.Serialize(), Table(Esp, Root), (RootCheck)gate));

    [TestMethod]
    public void Marker_RootUnclean_OwnStateAndBlocked()
    {
        var result = InstalledIdentity.Evaluate(Marker.Serialize(), Table(Esp, Root), _ => RootCheck.Unclean);
        Assert.AreEqual(InstalledState.RootUnclean, result.State);
        StringAssert.Contains(result.BlockedReason, "e2fsck");
    }

    // --- CheckRoot: s_state getrennt von unbekanntem RoCompat ---

    [TestMethod]
    [DataRow((ushort)1, 0x1u, (int)RootCheck.Ok, DisplayName = "sauber")]
    [DataRow((ushort)0, 0x1u, (int)RootCheck.Unclean, DisplayName = "s_state 0")]
    [DataRow((ushort)2, 0x1u, (int)RootCheck.ReadOnly, DisplayName = "s_state Fehlerbit: beschaedigt, nicht nur unsauber")]
    [DataRow((ushort)3, 0x1u, (int)RootCheck.ReadOnly, DisplayName = "s_state sauber mit Fehlerbit: beschaedigt")]
    [DataRow((ushort)1, 0x9u, (int)RootCheck.ReadOnly, DisplayName = "unbekanntes RoCompat")]
    [DataRow((ushort)0, 0x9u, (int)RootCheck.ReadOnly, DisplayName = "RoCompat und s_state: bleibt ReadOnly")]
    public void CheckRoot_SeparatesStateFromRoCompat(ushort state, uint roCompat, int expected) =>
        Assert.AreEqual((RootCheck)expected, InstalledIdentity.CheckRoot(Superblock(state, roCompat)));

    [TestMethod]
    [DataRow((ushort)2)]
    [DataRow((ushort)3)]
    public void Marker_RootErrorState_RootDamaged(ushort state)
    {
        // Spec 7a Abschnitt 5: s_state mit Fehlerbit (2/3) ist RootDamaged, nur s_state 0 ist RootUnclean.
        var result = InstalledIdentity.Evaluate(Marker.Serialize(), Table(Esp, Root), _ => InstalledIdentity.CheckRoot(Superblock(state, 0x1)));
        Assert.AreEqual(InstalledState.RootDamaged, result.State);
    }

    [TestMethod]
    public void CheckRoot_WrongMagic_Reject()
    {
        byte[] superblock = Superblock(1, 0x1);
        superblock[56] = 0;
        Assert.AreEqual(RootCheck.Reject, InstalledIdentity.CheckRoot(superblock));
    }

    /// <summary>Minimaler Superblock im geprueften Profil (4 KiB, eine Gruppe, FILETYPE, SPARSE_SUPER).</summary>
    private static byte[] Superblock(ushort state, uint roCompat)
    {
        var block = new byte[1024];
        void U32(int offset, uint value) => BitConverter.TryWriteBytes(block.AsSpan(offset, 4), value);
        U32(0, 8192);
        U32(4, 32768);
        U32(24, 2);
        U32(32, 32768);
        U32(40, 8192);
        BitConverter.TryWriteBytes(block.AsSpan(56, 2), (ushort)0xEF53);
        BitConverter.TryWriteBytes(block.AsSpan(58, 2), state);
        U32(76, 1);
        BitConverter.TryWriteBytes(block.AsSpan(88, 2), (ushort)256);
        U32(96, 0x2);
        U32(100, roCompat);
        return block;
    }

    [TestMethod]
    public void Marker_SuperblockIoError_RootDamagedWithRestartHint()
    {
        var result = InstalledIdentity.Evaluate(Marker.Serialize(), Table(Esp, Root), RootCheck (_) => throw new IOException("AHCI-Fehler"));
        Assert.AreEqual(InstalledState.RootDamaged, result.State);
        StringAssert.Contains(result.Reason, "cause='RootIoError'");
        StringAssert.Contains(result.Reason, "Neustart");
    }

    [TestMethod]
    public void Gate_OnlyAskedForTheMarkedRootEntry()
    {
        GptEntry? asked = null;
        InstalledIdentity.Evaluate(Marker.Serialize(), Table(Esp, Root), entry => { asked = entry; return RootCheck.Ok; });
        Assert.AreEqual(Root, asked);
        bool called = false;
        InstalledIdentity.Evaluate(Marker.Serialize(), Table(Esp), _ => { called = true; return RootCheck.Ok; });
        Assert.IsFalse(called, "ohne Root-Eintrag kein Plattenzugriff");
    }

    [TestMethod]
    public void BlockedReason_OnlyForRootFailures()
    {
        Assert.IsNull(InstalledIdentity.Evaluate(null, null, _ => RootCheck.Ok).BlockedReason);
        Assert.IsNull(InstalledIdentity.Evaluate(Marker.Serialize(), Table(Esp, Root), _ => RootCheck.Ok).BlockedReason);
        foreach (var result in new[]
        {
            InstalledIdentity.Evaluate(Marker.Serialize(), Table(Esp), _ => RootCheck.Ok),
            InstalledIdentity.Evaluate(Marker.Serialize(), Table(Esp, Root), _ => RootCheck.Reject),
            InstalledIdentity.Evaluate(null, Table(Esp, Root), _ => RootCheck.Ok),
        })
        {
            Assert.IsNotNull(result.BlockedReason, result.State.ToString());
            StringAssert.Contains(result.BlockedReason, "Keine Ersteinrichtung");
        }
    }
}
