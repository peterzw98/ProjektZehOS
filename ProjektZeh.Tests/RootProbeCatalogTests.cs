using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Tests;

/// <summary>E4.0 (Spec storage-e4-0-rw-qualification): Sollzustand und Inhalt der Integritaetsdateien der Gastprobe.</summary>
[TestClass]
public sealed class RootProbeCatalogTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void Expected_PathsUnique(int stage)
    {
        var paths = RootProbeCatalog.Expected(stage).Select(entry => entry.Path).ToList();
        Assert.AreEqual(paths.Count, paths.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void Expected_StepD_FourThousandFilesInTenDirectories()
    {
        var d = RootProbeCatalog.Expected(1).Where(entry => entry.Path.StartsWith("/d/", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(4000, d.Count);
        Assert.AreEqual(10, d.Select(entry => entry.Path[..6]).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(d.Sum(entry => entry.Length) < 256L * 1024 * 1024, "d passt auf den 2-GiB-Root");
        foreach (long size in RootProbeCatalog.DSizes) Assert.IsTrue(d.Any(entry => entry.Length == size), $"Groesse {size} fehlt");
    }

    [TestMethod]
    public void Expected_StepE_RenamedDeletedAndNew()
    {
        var e = RootProbeCatalog.Expected(1).Where(entry => entry.Path.StartsWith("/e/", StringComparison.Ordinal)).ToList();
        // 3 000 - 300 geloescht + 300 neu; 300 tragen den Inhalt ihres alten Namens.
        Assert.AreEqual(3000, e.Count);
        Assert.AreEqual(300, e.Count(entry => entry.Path != entry.Source));
        Assert.IsFalse(e.Any(entry => entry.Path == RootProbeCatalog.EName(2)));
    }

    [TestMethod]
    public void Expected_Stage2_ChangesOnlyGAndH()
    {
        var first = RootProbeCatalog.Expected(1).ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var second = RootProbeCatalog.Expected(2).ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (string path in first.Keys.Union(second.Keys))
            if (!first.TryGetValue(path, out var a) || !second.TryGetValue(path, out var b) || a != b) changed.Add(path);
        Assert.IsTrue(changed.All(path => path.StartsWith("/g/", StringComparison.Ordinal) || path.StartsWith("/h/", StringComparison.Ordinal)));
        // g: 500 Dateien neu, davon 250 mit .bak; h: 500 geloescht, 500 neu.
        Assert.AreEqual(500 + 250 + 500 + 500, changed.Count);
    }

    [TestMethod]
    public void Expected_StepG_BackupOnlyWithKeepBackup()
    {
        var g = RootProbeCatalog.Expected(1).Where(entry => entry.Path.StartsWith("/g/", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1000 + 500, g.Count);
        var backup = g.Single(entry => entry.Path == "/g/r0000.bak");
        Assert.AreEqual("g0", backup.Seed);
        Assert.AreEqual(RootProbeCatalog.GInitialLength(0), backup.Length);
        Assert.IsFalse(g.Any(entry => entry.Path == "/g/r0001.bak"));
        Assert.AreEqual(0, g.Single(entry => entry.Path == "/g/r0002").Length);
    }

    [TestMethod]
    public void Expected_NamesAndBigFile()
    {
        var entries = RootProbeCatalog.Expected(1).ToList();
        Assert.IsTrue(entries.Any(entry => entry.Path == "/i/" + new string('n', 255)));
        Assert.IsTrue(entries.Any(entry => entry.Path == "/i/A") && entries.Any(entry => entry.Path == "/i/a"));
        Assert.IsFalse(entries.Any(entry => entry.Path == RootProbeCatalog.BigPath), "k gehoert in den Hochgruppenlauf");
        Assert.AreEqual(65536, entries.Single(entry => entry.Path == RootProbeCatalog.FullReplaceTarget).Length);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void Expected_StaysBelowCosmosInodeCacheLimit(int stage)
    {
        // Lauf 2 liest jede Integritaetsdatei in einem Mount; bei 17 519 Inodes scheitert der Cosmos-Cache (Diagnoselauf 12).
        Assert.IsTrue(RootProbeCatalog.Expected(stage).Count() < 15000);
    }

    [TestMethod]
    public void Expected_InvalidStage_Throws() =>
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => RootProbeCatalog.Expected(3).ToList());

    [TestMethod]
    public void Content_Deterministic()
    {
        CollectionAssert.AreEqual(RootProbeContent.Create("d", "/d/x", 10000), RootProbeContent.Create("d", "/d/x", 10000));
    }

    [TestMethod]
    public void Content_DependsOnSeedAndPath()
    {
        byte[] baseline = RootProbeContent.Create("d", "/d/x", 4096);
        CollectionAssert.AreNotEqual(baseline, RootProbeContent.Create("e", "/d/x", 4096));
        CollectionAssert.AreNotEqual(baseline, RootProbeContent.Create("d", "/d/y", 4096));
    }

    [TestMethod]
    public void Content_StreamingEqualsWhole()
    {
        // Der Gast prueft abschnittsweise (1 MiB); Teile ab einem Offset muessen dem Gesamtinhalt entsprechen.
        byte[] whole = RootProbeContent.Create("k", "/k/big", 3 * 4096 + 100);
        byte[] part = new byte[4096 + 100];
        RootProbeContent.Fill("k", "/k/big", 2 * 4096, part);
        CollectionAssert.AreEqual(whole[(2 * 4096)..], part);
    }

    [TestMethod]
    public void Content_ChunksDiffer()
    {
        byte[] content = RootProbeContent.Create("d", "/d/x", 2 * 4096);
        CollectionAssert.AreNotEqual(content[..4096], content[4096..]);
    }

    [TestMethod]
    public void Content_UnalignedOffset_Throws() =>
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => RootProbeContent.Fill("d", "/d/x", 100, new byte[10]));
}
