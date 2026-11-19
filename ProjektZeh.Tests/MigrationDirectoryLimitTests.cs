using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MigrationDirectoryLimitTests
{
    [TestMethod]
    public void BoundaryCountsDirectChildrenRatherThanTheWholeInventory()
    {
        string source = Path.Combine(Path.GetTempPath(), "ZehOS-MigrationLimit-" + Guid.NewGuid().ToString("N"));
        string bulk = Path.Combine(source, "bulk");
        Directory.CreateDirectory(bulk);
        try
        {
            for (int i = 0; i < FileService.MaximumExt2DirectoryEntries; i++) File.Create(Path.Combine(bulk, $"f{i:D4}.txt")).Dispose();
            Assert.AreEqual(0, Plan(source).Conflicts.Count, "Genau die Grenze an direkten Kindern passt.");
            string extra = Path.Combine(bulk, "extra.txt");
            File.Create(extra).Dispose();
            var tooLarge = Plan(source);
            Assert.IsTrue(tooLarge.Conflicts.Any(conflict => conflict.Contains("DirectoryEntryLimit", StringComparison.Ordinal)
                && conflict.Contains("bulk", StringComparison.Ordinal)), "Ein Kind ueber der Grenze muss einen benannten Konflikt ausloesen.");
            string other = Path.Combine(source, "other");
            Directory.CreateDirectory(other);
            File.Move(extra, Path.Combine(other, "extra.txt"));
            Assert.AreEqual(0, Plan(source).Conflicts.Count, "Die Gesamtzahl darf ueber der Grenze liegen, solange jeder Ordner passt.");

            string home = Path.Combine(source, "home", "Alice");
            Directory.CreateDirectory(Path.Combine(home, ".config"));
            Directory.Move(bulk, Path.Combine(home, ".config", "zehos"));
            File.Create(Path.Combine(home, "zehos-desktop.conf")).Dispose();
            var mappedOverflow = Plan(source, ["Alice"]);
            Assert.IsTrue(mappedOverflow.Conflicts.Any(conflict => conflict.Contains("DirectoryEntryLimit", StringComparison.Ordinal)
                && conflict.Contains("/home/Alice/.config/zehos", StringComparison.Ordinal)), "Auch die Zielzuordnung darf keinen Ordner ueberfuellen.");
        }
        finally { Directory.Delete(source, true); }
    }

    private static MigrationPlan Plan(string source, string[]? accounts = null)
    {
        var inventory = MigrationInventory.Read(source);
        Assert.IsTrue(inventory.IsOK(), inventory.Status.Message);
        return MigrationPlan.Create(inventory.Value!, accounts ?? Array.Empty<string>(), SystemPaths.Legacy, SystemPaths.Root);
    }
}
