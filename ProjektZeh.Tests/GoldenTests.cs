using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

// Die Tests setzen prozessweite Umgebungsvariablen.
[TestClass]
[DoNotParallelize]
public sealed class GoldenTests
{
    private string directory = "";
    private string? update;
    private string? ci;

    [TestInitialize]
    public void Create()
    {
        directory = Path.Combine(Path.GetTempPath(), $"zehos-golden-{Guid.NewGuid():N}");
        update = Environment.GetEnvironmentVariable(Golden.UpdateVariable);
        ci = Environment.GetEnvironmentVariable("CI");
    }

    [TestCleanup]
    public void Remove()
    {
        Environment.SetEnvironmentVariable(Golden.UpdateVariable, update);
        Environment.SetEnvironmentVariable("CI", ci);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        if (File.Exists(ActualPng)) File.Delete(ActualPng);
    }

    private static string ActualPng => Path.Combine(Golden.RepositoryRoot(), "TestResults", "golden", "selftest.actual.png");

    private void WriteReference(int[] pixels)
    {
        Environment.SetEnvironmentVariable("CI", null);
        Environment.SetEnvironmentVariable(Golden.UpdateVariable, "1");
        Golden.Check("selftest", 2, 2, pixels, directory);
        Environment.SetEnvironmentVariable("CI", ci);
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
    public void Check_UpdateInCi_FailsWithoutWriting()
    {
        Environment.SetEnvironmentVariable("CI", "true");
        Environment.SetEnvironmentVariable(Golden.UpdateVariable, "1");
        Assert.ThrowsException<AssertFailedException>(() => Golden.Check("selftest", 2, 2, [1, 2, 3, 4], directory));
        Assert.IsFalse(Directory.Exists(directory));
    }

    // Ein Reviewer sieht nur das PNG; es muss genau die Pixel zeigen, deren Pruefsumme verglichen wird.
    [TestMethod]
    public void ReferencePngs_MatchTheirChecksums()
    {
        string folder = Path.Combine(Golden.RepositoryRoot(), "ProjektZeh.Tests", "Golden");
        string[] references = Directory.GetFiles(folder, "*.sha256");
        Assert.AreNotEqual(0, references.Length);
        foreach (string reference in references)
        {
            var (width, height, pixels) = ReadPng(Path.ChangeExtension(reference, ".png"));
            Assert.AreEqual(File.ReadAllText(reference).Trim(), Golden.Hash(width, height, pixels), reference);
        }
    }

    // Liest nur das Format von ScreenshotSupport.WritePng: RGB mit 8 Bit, ein IDAT, Filter 0 je Zeile, Alpha immer voll.
    private static (int Width, int Height, int[] Pixels) ReadPng(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int width = 0, height = 0;
        using var data = new MemoryStream();
        for (int offset = 8; offset < file.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(file, offset + 4, 4);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset + 8));
                height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset + 12));
            }
            if (type == "IDAT") data.Write(file, offset + 8, length);
            offset += 12 + length;
        }
        data.Position = 0;
        using var zlib = new ZLibStream(data, CompressionMode.Decompress);
        var row = new byte[1 + width * 3];
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            zlib.ReadExactly(row);
            Assert.AreEqual(0, row[0], "Nur Filter 0 erwartet.");
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = unchecked((int)0xff000000) | row[1 + x * 3] << 16 | row[2 + x * 3] << 8 | row[3 + x * 3];
        }
        return (width, height, pixels);
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
