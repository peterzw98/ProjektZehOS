using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ProjektZeh.Tests;

// Cosmos 3.0.97 formatiert ext2 inkonsistent. Storage/CosmosExt2FormatterPlug.cs leitet den Paketformatter auf
// FormatForKernel um (object-Signatur, sonst ILC-Initialisierer-Zyklus). Kernelcode ruft trotzdem bevorzugt
// CosmosExt2Formatter.FormatForKernel direkt auf und nie CosmosExt2Formatter.Format (erlaubt 1K/2K).
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
        string kernel = Path.Combine(FindRepositoryRoot(), "ProjektZeh");
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

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProjektZeh", "ProjektZeh.csproj"))) return directory.FullName;
        }
        throw new InvalidOperationException("Repository-Stamm mit ProjektZeh/ProjektZeh.csproj nicht gefunden.");
    }
}
