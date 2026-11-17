using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

/// <summary>Referenzbilder: Vergleich ueber die Pruefsumme der Pixel, das PNG liegt nur zum Ansehen daneben.</summary>
internal static class Golden
{
    public const string UpdateVariable = "ZEHOS_UPDATE_GOLDEN";

    // Ohne echte Schrift waeren alle Referenzbilder textlos und wertlos; in der CI ist das deshalb ein Fehler.
    public static UiPainter Painter()
    {
        var (data, width, height) = Font();
        return new UiPainter(data, width, height);
    }

    public static (byte[] Data, int Width, int Height) Font()
    {
        if (ScreenshotSupport.LoadCosmosFontData() is { } font) return font;
        string message = "Cosmos-Schrift nicht im NuGet-Cache (Cosmos.Kernel.System 3.0.97); dotnet restore ausfuehren.";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) Assert.Fail(message);
        Assert.Inconclusive(message);
        return default;
    }

    public static void Check(string name, int width, int height, int[] pixels) =>
        Check(name, width, height, pixels, Path.Combine(RepositoryRoot(), "ProjektZeh.Tests", "Golden"));

    internal static void Check(string name, int width, int height, int[] pixels, string directory)
    {
        string actual = Hash(width, height, pixels);
        string reference = Path.Combine(directory, name + ".sha256");
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(reference, actual + "\n");
            ScreenshotSupport.WritePng(Path.Combine(directory, name + ".png"), width, height, pixels);
            return;
        }
        if (!File.Exists(reference))
            Assert.Fail($"Referenzbild {reference} fehlt; mit {UpdateVariable}=1 erzeugen und das PNG ansehen.");
        string expected = File.ReadAllText(reference).Trim();
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
        string output = Path.Combine(RepositoryRoot(), "TestResults", "golden");
        Directory.CreateDirectory(output);
        string actualPng = Path.Combine(output, name + ".actual.png");
        ScreenshotSupport.WritePng(actualPng, width, height, pixels);
        Assert.Fail($"Bild {name} weicht ab: erwartet {Path.ChangeExtension(reference, ".png")}, erhalten {actualPng}.");
    }

    // Groesse gehoert zur Pruefsumme, damit gleiche Pixelfolgen in anderer Aufteilung nicht gleich gelten.
    internal static string Hash(int width, int height, int[] pixels)
    {
        var bytes = new byte[8 + pixels.Length * 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), height);
        for (int index = 0; index < pixels.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8 + index * 4), pixels[index]);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProjektZeh", "ProjektZeh.csproj"))) return directory.FullName;
        }
        throw new InvalidOperationException("Repository-Stamm mit ProjektZeh/ProjektZeh.csproj nicht gefunden.");
    }
}
