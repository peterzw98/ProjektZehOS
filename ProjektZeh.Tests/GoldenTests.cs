using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class GoldenTests
{
    private string directory = "";
    private string? update;

    [TestInitialize]
    public void Create()
    {
        directory = Path.Combine(Path.GetTempPath(), $"zehos-golden-{Guid.NewGuid():N}");
        update = Environment.GetEnvironmentVariable(Golden.UpdateVariable);
    }

    [TestCleanup]
    public void Remove()
    {
        Environment.SetEnvironmentVariable(Golden.UpdateVariable, update);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        if (File.Exists(ActualPng)) File.Delete(ActualPng);
    }

    private static string ActualPng => Path.Combine(Golden.RepositoryRoot(), "TestResults", "golden", "selftest.actual.png");

    private void WriteReference(int[] pixels)
    {
        Environment.SetEnvironmentVariable(Golden.UpdateVariable, "1");
        Golden.Check("selftest", 2, 2, pixels, directory);
        Environment.SetEnvironmentVariable(Golden.UpdateVariable, null);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "selftest.png")));
    }

    [TestMethod]
    public void Check_SamePixels_Passes()
    {
        WriteReference([1, 2, 3, 4]);
        Golden.Check("selftest", 2, 2, [1, 2, 3, 4], directory);
    }

    [TestMethod]
    public void Check_OnePixelChanged_FailsAndWritesActualPng()
    {
        WriteReference([1, 2, 3, 4]);
        var error = Assert.ThrowsException<AssertFailedException>(() => Golden.Check("selftest", 2, 2, [1, 2, 3, 5], directory));
        StringAssert.Contains(error.Message, ActualPng);
        StringAssert.Contains(error.Message, Path.Combine(directory, "selftest.png"));
        Assert.IsTrue(File.Exists(ActualPng));
    }

    [TestMethod]
    public void Check_SamePixelsOtherSize_Fails()
    {
        WriteReference([1, 2, 3, 4]);
        Assert.ThrowsException<AssertFailedException>(() => Golden.Check("selftest", 4, 1, [1, 2, 3, 4], directory));
    }

    [TestMethod]
    public void Check_MissingReference_FailsWithHintToUpdateVariable()
    {
        Environment.SetEnvironmentVariable(Golden.UpdateVariable, null);
        var error = Assert.ThrowsException<AssertFailedException>(() => Golden.Check("selftest", 2, 2, [1, 2, 3, 4], directory));
        StringAssert.Contains(error.Message, Golden.UpdateVariable);
    }

    [TestMethod]
    public void Font_MissingPackage_FailsInCiAndIsInconclusiveLocally()
    {
        string? packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        string? ci = Environment.GetEnvironmentVariable("CI");
        Directory.CreateDirectory(directory);
        try
        {
            // Ohne Schrift waeren alle Referenzbilder textlos; in der CI darf das nicht als uebersprungen durchgehen.
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", directory);
            Environment.SetEnvironmentVariable("CI", "true");
            Assert.ThrowsException<AssertFailedException>(() => Golden.Font());
            Environment.SetEnvironmentVariable("CI", null);
            Assert.ThrowsException<AssertInconclusiveException>(() => Golden.Font());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);
            Environment.SetEnvironmentVariable("CI", ci);
        }
    }

    [TestMethod]
    public void DesktopClock_UsesInjectedTime()
    {
        var first = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string Render(DateTime utc, long milliseconds)
        {
            var (font, glyphWidth, glyphHeight) = Golden.Font();
            var desktop = new Desktop(1024, 768, font, glyphWidth, glyphHeight, () => new ShellService(), () => new FileService(directory),
                getUtcNow: () => utc);
            var pixels = new int[1024 * 768];
            desktop.Update(milliseconds);
            desktop.Render(new PixelSurface(1024, 768, pixels), milliseconds, 10, 10, PointerButtons.None);
            return Golden.Hash(1024, 768, pixels);
        }
        Directory.CreateDirectory(directory);
        // Der zweite Lauf liegt 61 s spaeter im Takt; die Uhr darf trotzdem nur der eingespeisten Zeit folgen.
        string reference = Render(first, 0);
        Assert.AreEqual(reference, Render(first, 61_000));
        Assert.AreNotEqual(reference, Render(first.AddMinutes(1), 0));
    }
}
