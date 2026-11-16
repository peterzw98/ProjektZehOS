using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem.Ext2;

// format IMAGE --size BYTES --block-size N [--label L]: neues sparse Image anlegen und mit dem Cosmos-Ext2Formatter
// formatieren. Ergebnis als eine JSON-Zeile auf stdout. selftest: Grenz- und Argumentpruefungen ohne Cosmos-Aufruf.
if (args.Length == 1 && args[0] == "selftest") return SelfTest.Run();
if (args.Length < 2 || args[0] != "format")
{
    Console.Error.WriteLine("Aufruf: format IMAGE --size BYTES --block-size 1024|2048|4096 [--label L] | selftest");
    return 2;
}
string? problem = FormatRequest.TryParse(args, out var request);
if (problem != null) { Console.Error.WriteLine(problem); return 2; }

// Nie ein vorhandenes Image ueberschreiben (AGENTS.md: Testdaten schuetzen); alle Argumente sind vorher geprueft.
FileStream file;
try { file = new FileStream(request.Image, FileMode.CreateNew, FileAccess.ReadWrite); }
catch (IOException exception) { Console.Error.WriteLine("Image nicht angelegt: " + exception.Message); return 3; }
using (file)
{
    file.SetLength(request.Size);
    var device = new FileBlockDevice(file, Path.GetFileName(request.Image));
    var format = typeof(Ext2FormatOptions).Assembly.GetType("Cosmos.Kernel.System.FileSystem.Ext2.Ext2Formatter", true)!
        .GetMethod("Format", BindingFlags.Public | BindingFlags.Static)!;
    var clock = Stopwatch.StartNew();
    bool ok = false;
    string? error = null;
    try { ok = (bool)format.Invoke(null, [device, new Ext2FormatOptions { BlockSize = request.BlockSize, VolumeLabel = request.Label }])!; }
    catch (TargetInvocationException exception) { error = exception.InnerException?.ToString() ?? exception.ToString(); }
    file.Flush();
    Console.WriteLine(JsonSerializer.Serialize(new { ok, size = request.Size, blockSize = request.BlockSize, elapsedMs = clock.ElapsedMilliseconds, error }));
    return ok ? 0 : 1;
}

internal sealed record FormatRequest(string Image, long Size, uint BlockSize, string Label)
{
    // Ohne nachgewiesene sparse Anlage unter Windows wuerde SetLength die volle Groesse auf die Platte schreiben.
    internal const long WindowsMaximum = 64L * 1024 * 1024;

    internal static string? TryParse(string[] args, out FormatRequest request)
    {
        request = new FormatRequest(args.Length > 1 ? args[1] : "", 0, 0, "");
        if ((args.Length - 2) % 2 != 0) return "Optionen muessen paarweise angegeben werden.";
        long size = 0;
        uint blockSize = 0;
        string label = "";
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 2; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--size" when long.TryParse(args[i + 1], System.Globalization.NumberStyles.None, culture, out size): break;
                case "--block-size" when uint.TryParse(args[i + 1], System.Globalization.NumberStyles.None, culture, out blockSize): break;
                case "--label": label = args[i + 1]; break;
                case "--size" or "--block-size": return $"{args[i]}: keine gueltige Zahl.";
                default: return "Unbekannte Option " + args[i];
            }
        }
        if (size <= 0 || size % 512 != 0) return "--size muss ein positives Vielfaches von 512 sein.";
        if (blockSize is not (1024 or 2048 or 4096)) return "--block-size muss 1024, 2048 oder 4096 sein.";
        if (OperatingSystem.IsWindows() && size > WindowsMaximum) return "Unter Windows hoechstens 64 MiB, bis sparse Anlage dort geprueft ist.";
        request = new FormatRequest(args[1], size, blockSize, label);
        return null;
    }
}

/// <summary>Prueft Grenzen des Dateigeraets und die Argumentpruefung, ohne den Cosmos-Formatter aufzurufen.</summary>
internal static class SelfTest
{
    internal static int Run()
    {
        string directory = Directory.CreateTempSubdirectory("storageprobe-selftest-").FullName;
        int failures = 0;
        void Expect(bool condition, string name)
        {
            Console.WriteLine((condition ? "ok   " : "FAIL ") + name);
            if (!condition) failures++;
        }
        string path = Path.Combine(directory, "geraet.img");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            file.SetLength(4 * 512);
            var device = new FileBlockDevice(file, "geraet");
            byte[] buffer = new byte[2 * 512];
            Expect(Throws(() => device.ReadBlock(3, 2, buffer)), "Lesen ueber das Ende wird abgelehnt");
            Expect(Throws(() => device.WriteBlock(5, 0, buffer)), "Blocknummer hinter dem Ende wird abgelehnt");
            Expect(Throws(() => device.ReadBlock(0, 2, new byte[512])), "zu kleiner Puffer wird abgelehnt");
            Expect(Throws(() => device.ReadBlock(1, ulong.MaxValue, buffer)), "Ueberlauf der Blockzahl wird abgelehnt");
            device.WriteBlock(2, 2, buffer);
            Expect(file.Length == 4 * 512, "Schreiben bis zum Ende vergroessert die Datei nicht");
        }
        Expect(FormatRequest.TryParse(["format", path, "--size", "4194304", "--block-size", "4096"], out _) == null, "gueltige Argumente");
        Expect(FormatRequest.TryParse(["format", path, "--size", "4194304", "--block-size", "512"], out _) != null, "Blockgroesse 512 abgelehnt");
        Expect(FormatRequest.TryParse(["format", path, "--size", "1000", "--block-size", "1024"], out _) != null, "Groesse kein Vielfaches von 512 abgelehnt");
        Expect(FormatRequest.TryParse(["format", path, "--size", "-512", "--block-size", "1024"], out _) != null, "negative Groesse abgelehnt");
        Expect(FormatRequest.TryParse(["format", path, "--size"], out _) != null, "Option ohne Wert abgelehnt");
        Expect(FormatRequest.TryParse(["format", path, "--grösse", "1"], out _) != null, "unbekannte Option abgelehnt");
        long before = new FileInfo(path).Length;
        Expect(Throws(() => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite).Dispose()), "vorhandenes Ziel wird nicht ueberschrieben");
        Expect(new FileInfo(path).Length == before, "vorhandenes Ziel unveraendert");
        File.Delete(path);
        Directory.Delete(directory);
        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IOException) { return true; }
    }
}

/// <summary>512-Byte-Sektoren auf einer Datei; Grenzen wie die Gerätetreiber pruefen.</summary>
internal sealed class FileBlockDevice(FileStream file, string name) : IBlockDevice
{
    public ulong BlockSize => 512;
    public ulong BlockCount => (ulong)file.Length / 512;
    public string Name => name;

    public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data)
    {
        Check(blockNo, blockCount, data.Length);
        file.Position = (long)(blockNo * 512);
        file.ReadExactly(data[..(int)(blockCount * 512)]);
    }

    public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
    {
        Check(blockNo, blockCount, data.Length);
        file.Position = (long)(blockNo * 512);
        file.Write(data[..(int)(blockCount * 512)]);
    }

    public void Flush() => file.Flush();

    private void Check(ulong blockNo, ulong blockCount, int length)
    {
        if (blockNo > BlockCount || blockCount > BlockCount - blockNo || (ulong)length < blockCount * 512)
            throw new ArgumentOutOfRangeException(nameof(blockNo));
    }
}
