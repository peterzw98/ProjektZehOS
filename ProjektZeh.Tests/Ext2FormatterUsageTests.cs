using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ProjektZeh.Tests;

// Das Layout schreibt der Formatter aus Cosmos (Patch ext2-formatter); sein Standard ist 1024 Byte, und der Treiber
// adressiert 1-KiB-Volumes falsch. Kernelcode formatiert ext2 deshalb nur ueber CosmosExt2Formatter.FormatForKernel
// (nur 4096 Byte, hoechstens 128 GiB) und nie ueber Ext2Formatter, TryFormat mit Ext2FormatOptions oder "ext2".
[TestClass]
public sealed class Ext2FormatterUsageTests
{
    // shortcut: erkennt nur direkte Aufrufe und "ext2"/Ext2FormatOptions im selben TryFormat-Ausdruck; ueber Variablen
    // weitergereichte Dateisystemnamen bleiben unerkannt. Bei einer ext2-Formatierung im Kernel den Aufrufer zusaetzlich reviewen.
    private static readonly Regex Forbidden = new(
        @"\b(Cosmos)?Ext2Formatter\.Format\s*\(|TryFormat\s*\([^;]*(""ext2""|Ext2FormatOptions)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [TestMethod]
    public void KernelSources_FormatExt2_OnlyThroughFormatForKernel()
    {
        string kernel = Path.Combine(Golden.RepositoryRoot(), "ProjektZeh");
        string allowed = Path.Combine(kernel, "Storage", "Ext2") + Path.DirectorySeparatorChar;
        string[] offenders = Directory.EnumerateFiles(kernel, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(allowed, StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => Forbidden.IsMatch(File.ReadAllText(path)))
            .ToArray();
        Assert.AreEqual(0, offenders.Length, "ext2 nur ueber CosmosExt2Formatter.FormatForKernel formatieren: " + string.Join(", ", offenders));
    }

    [TestMethod]
    public void Forbidden_DetectsOriginalAndVfsCalls()
    {
        Assert.IsTrue(Forbidden.IsMatch("Ext2Formatter.Format(device, options);"));
        Assert.IsTrue(Forbidden.IsMatch("CosmosExt2Formatter.Format(device, null);"));
        Assert.IsTrue(Forbidden.IsMatch("VfsManager.TryFormat(\"ext2\", source, null);"));
        Assert.IsTrue(Forbidden.IsMatch("type.TryFormat(source, new Ext2FormatOptions { BlockSize = 4096 });"));
        Assert.IsFalse(Forbidden.IsMatch("CosmosExt2Formatter.FormatForKernel(device, null);"));
        Assert.IsFalse(Forbidden.IsMatch("VfsManager.TryFormat(fileSystemName, partition, new FatFormatOptions());"));
    }
}
