using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Tests;

/// <summary>s_state-Baustein des Root (Spec 7a-S4 §6.4, Entscheidung lead2 17.11. 15:16); 4.3 und 7b uebernehmen ihn.</summary>
[TestClass]
public sealed class RootVolumeStateTests
{
    [TestMethod]
    public void Open_SetsStateZeroAndFlushes()
    {
        var volume = new Volume(state: 1);

        Assert.IsTrue(RootVolumeState.Open(volume).IsOK());

        Assert.AreEqual(0, volume.State);
        CollectionAssert.AreEqual(new[] { "read", "write", "flush" }, volume.Calls);
        Assert.IsTrue(volume.OnlyStateChanged(), "Lesen-aendern-schreiben: nur s_state aendert sich.");
    }

    [TestMethod]
    public void Close_SyncsTwiceSetsStateOneFlushesAndRereads()
    {
        var volume = new Volume(state: 0);

        Assert.IsTrue(RootVolumeState.Close(volume).IsOK());

        Assert.AreEqual(1, volume.State);
        CollectionAssert.AreEqual(new[] { "sync", "sync", "read", "write", "flush", "read" }, volume.Calls);
        Assert.IsTrue(volume.OnlyStateChanged());
    }

    // Tests je Fehlerstelle: jeder Schritt scheitert einmal, der Fehler nennt ihn und es folgt kein weiterer Schritt.
    [DataTestMethod]
    [DataRow("read", 1)]
    [DataRow("write", 1)]
    [DataRow("flush", 1)]
    public void Open_StepFails_StopsAndNamesStep(string step, int occurrence)
    {
        var volume = new Volume(state: 1) { FailOn = (step, occurrence) };

        var result = RootVolumeState.Open(volume);

        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, step);
        Assert.AreEqual(step, volume.Calls[^1]);
    }

    [DataTestMethod]
    [DataRow("sync", 1)]
    [DataRow("sync", 2)]
    [DataRow("read", 1)]
    [DataRow("write", 1)]
    [DataRow("flush", 1)]
    [DataRow("read", 2)]
    public void Close_StepFails_StopsAndNamesStep(string step, int occurrence)
    {
        var volume = new Volume(state: 0) { FailOn = (step, occurrence) };

        var result = RootVolumeState.Close(volume);

        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, step);
        Assert.AreEqual(step, volume.Calls[^1]);
    }

    [TestMethod]
    public void Close_SyncReportsFalse_FailsWithoutWriting()
    {
        var volume = new Volume(state: 0) { SyncResult = false };

        var result = RootVolumeState.Close(volume);

        Assert.IsFalse(result.IsOK());
        CollectionAssert.DoesNotContain(volume.Calls, "write");
    }

    [TestMethod]
    public void Close_RereadNotClean_Fails()
    {
        // Ein Treiber koennte den gepufferten Superblock nach unserem Schreiben zurueckschreiben.
        var volume = new Volume(state: 0) { StateAfterFlush = 0 };

        var result = RootVolumeState.Close(volume);

        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "s_state");
    }

    [TestMethod]
    public void Close_RereadNotSupportedProfile_Fails()
    {
        var volume = new Volume(state: 0) { CorruptAfterFlush = true };

        var result = RootVolumeState.Close(volume);

        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "CheckRoot");
    }

    [DataTestMethod]
    [DataRow((ushort)2)]
    [DataRow((ushort)3)]
    public void Open_ErrorBitSet_RefusesToTouchState(ushort state)
    {
        // Fehlerbit (s_state 2/3) bleibt stehen; der Baustein ueberschreibt es nie mit 0 oder 1.
        var volume = new Volume(state);

        Assert.IsFalse(RootVolumeState.Open(volume).IsOK());
        Assert.IsFalse(RootVolumeState.Close(volume).IsOK());
        CollectionAssert.DoesNotContain(volume.Calls, "write");
        Assert.AreEqual(state, volume.State);
    }

    private sealed class Volume : IRootVolume
    {
        private readonly byte[] original;
        private byte[] superblock;
        private readonly Dictionary<string, int> counts = [];

        public Volume(ushort state)
        {
            superblock = GptInstallTests.Superblock(state);
            original = (byte[])superblock.Clone();
        }

        public List<string> Calls { get; } = [];
        public (string Step, int Occurrence)? FailOn { get; init; }
        public bool SyncResult { get; init; } = true;
        public ushort? StateAfterFlush { get; init; }
        public bool CorruptAfterFlush { get; init; }
        public ushort State => BitConverter.ToUInt16(superblock, Ext2FeatureGate.StateOffset);

        public bool OnlyStateChanged()
        {
            for (int index = 0; index < superblock.Length; index++)
                if (index is not (Ext2FeatureGate.StateOffset or Ext2FeatureGate.StateOffset + 1) && superblock[index] != original[index]) return false;
            return true;
        }

        private void Step(string name)
        {
            Calls.Add(name);
            counts[name] = counts.GetValueOrDefault(name) + 1;
            if (FailOn is { } fail && fail.Step == name && fail.Occurrence == counts[name])
                throw new System.IO.IOException(name + " Geraetefehler");
        }

        public bool Sync()
        {
            Step("sync");
            return SyncResult;
        }

        public void ReadSuperblock(Span<byte> target)
        {
            Step("read");
            superblock.CopyTo(target);
        }

        public void WriteSuperblock(ReadOnlySpan<byte> source)
        {
            Step("write");
            superblock = source.ToArray();
        }

        public void Flush()
        {
            Step("flush");
            if (StateAfterFlush is { } state) BitConverter.TryWriteBytes(superblock.AsSpan(Ext2FeatureGate.StateOffset), state);
            if (CorruptAfterFlush) BitConverter.TryWriteBytes(superblock.AsSpan(24), 0u);
        }
    }
}
