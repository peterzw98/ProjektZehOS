using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem.Ext2;

// format IMAGE --size BYTES --block-size N [--label L] [--formatter original|fixed]: neues sparse Image anlegen und
// mit dem unveraenderten Cosmos-Ext2Formatter (original, Standard) oder der korrigierten gemeinsamen Quelle
// ProjektZeh/Storage/Ext2/CosmosExt2Formatter.cs (fixed) formatieren. Ergebnis als eine JSON-Zeile auf stdout.
// selftest: Grenz- und Argumentpruefungen sowie Layoutpruefung der korrigierten Quelle, ohne e2fsck.
if (args.Length == 1 && args[0] == "selftest") return SelfTest.Run();
if (args.Length == 1 && args[0] == "readonly-selftest") return ReadOnlySelfTest.Run();
if (args.Length < 2 || args[0] != "format")
{
    Console.Error.WriteLine("Aufruf: format IMAGE --size BYTES --block-size 1024|2048|4096 [--label L] [--formatter original|fixed] | selftest | readonly-selftest");
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
    var options = new Ext2FormatOptions { BlockSize = request.BlockSize, VolumeLabel = request.Label };
    try { ok = request.Fixed ? ProjektZeh.Storage.Ext2.CosmosExt2Formatter.Format(device, options) : (bool)format.Invoke(null, [device, options])!; }
    catch (TargetInvocationException exception) { error = exception.InnerException?.ToString() ?? exception.ToString(); }
    catch (Exception exception) when (request.Fixed) { error = exception.ToString(); }
    file.Flush();
    Console.WriteLine(JsonSerializer.Serialize(new { ok, formatter = request.Fixed ? "fixed" : "original", size = request.Size, blockSize = request.BlockSize, elapsedMs = clock.ElapsedMilliseconds, error }));
    return ok ? 0 : 1;
}

internal sealed record FormatRequest(string Image, long Size, uint BlockSize, string Label, bool Fixed)
{
    // Ohne nachgewiesene sparse Anlage unter Windows wuerde SetLength die volle Groesse auf die Platte schreiben.
    internal const long WindowsMaximum = 64L * 1024 * 1024;

    internal static string? TryParse(string[] args, out FormatRequest request)
    {
        request = new FormatRequest(args.Length > 1 ? args[1] : "", 0, 0, "", false);
        if ((args.Length - 2) % 2 != 0) return "Optionen muessen paarweise angegeben werden.";
        long size = 0;
        uint blockSize = 0;
        string label = "";
        bool fixedFormatter = false;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 2; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--size" when long.TryParse(args[i + 1], System.Globalization.NumberStyles.None, culture, out size): break;
                case "--block-size" when uint.TryParse(args[i + 1], System.Globalization.NumberStyles.None, culture, out blockSize): break;
                case "--label": label = args[i + 1]; break;
                case "--formatter" when args[i + 1] is "original" or "fixed": fixedFormatter = args[i + 1] == "fixed"; break;
                case "--formatter": return "--formatter muss original oder fixed sein.";
                case "--size" or "--block-size": return $"{args[i]}: keine gueltige Zahl.";
                default: return "Unbekannte Option " + args[i];
            }
        }
        if (size <= 0 || size % 512 != 0) return "--size muss ein positives Vielfaches von 512 sein.";
        if (blockSize is not (1024 or 2048 or 4096)) return "--block-size muss 1024, 2048 oder 4096 sein.";
        if (OperatingSystem.IsWindows() && size > WindowsMaximum) return "Unter Windows hoechstens 64 MiB, bis sparse Anlage dort geprueft ist.";
        request = new FormatRequest(args[1], size, blockSize, label, fixedFormatter);
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
        Expect(FormatRequest.TryParse(["format", path, "--size", "4194304", "--block-size", "4096", "--formatter", "fixed"], out var fixedRequest) == null && fixedRequest.Fixed, "--formatter fixed");
        Expect(FormatRequest.TryParse(["format", path, "--size", "4194304", "--block-size", "4096", "--formatter", "neu"], out _) != null, "unbekannter Formatter abgelehnt");
        long before = new FileInfo(path).Length;
        Expect(Throws(() => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite).Dispose()), "vorhandenes Ziel wird nicht ueberschrieben");
        Expect(new FileInfo(path).Length == before, "vorhandenes Ziel unveraendert");
        File.Delete(path);
        FixedLayout(directory, Expect);
        Directory.Delete(directory);
        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }

    // Fehlerfaelle der Cosmos-Quelle (fehlende Sicherungen, first_data_block, Padding) ohne e2fsck nachpruefen.
    // 16 MiB mit 1K-Bloecken: zwei Gruppen ab Block 1, Sicherung in Gruppe 1 ab Block 8193.
    private static void FixedLayout(string directory, Action<bool, string> expect)
    {
        string path = Path.Combine(directory, "fixed.img");
        byte[] image;
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            file.SetLength(16L * 1024 * 1024);
            bool ok = ProjektZeh.Storage.Ext2.CosmosExt2Formatter.Format(new FileBlockDevice(file, "fixed"), new Ext2FormatOptions { BlockSize = 1024 });
            expect(ok, "fixed: 16 MiB/1K formatiert");
            file.Flush();
        }
        image = File.ReadAllBytes(path);
        File.Delete(path);

        // Kernelprofil: 1K und 2K werden ohne einen einzigen Schreibzugriff abgelehnt, null formatiert mit 4K.
        string kernelPath = Path.Combine(directory, "kernel.img");
        using (var file = new FileStream(kernelPath, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            file.SetLength(8L * 1024 * 1024);
            var device = new FileBlockDevice(file, "kernel");
            bool refused = !ProjektZeh.Storage.Ext2.CosmosExt2Formatter.FormatForKernel(device, new Ext2FormatOptions { BlockSize = 1024 })
                && !ProjektZeh.Storage.Ext2.CosmosExt2Formatter.FormatForKernel(device, new Ext2FormatOptions { BlockSize = 2048 });
            expect(refused && device.Writes == 0, "kernel: 1K/2K ohne Schreibzugriff abgelehnt");
            bool formatted = ProjektZeh.Storage.Ext2.CosmosExt2Formatter.FormatForKernel(device, null);
            byte[] header = new byte[1024];
            device.ReadBlock(2, 2, header);
            expect(formatted && BitConverter.ToUInt32(header, 24) == 2, "kernel: null formatiert mit 4096-Byte-Bloecken");
        }
        File.Delete(kernelPath);

        // Ungueltige Geraete: Sektorgroesse 0, Ueberlauf von BlockCount * BlockSize, mehr als 128 GiB.
        foreach (var (sector, count, name) in new[] { (0UL, 1UL << 20, "Sektorgroesse 0"), (512UL, ulong.MaxValue, "Ueberlauf"), (512UL, (128UL << 30) / 512 + 1, "ueber 128 GiB") })
        {
            var fake = new CountingBlockDevice(sector, count);
            expect(!ProjektZeh.Storage.Ext2.CosmosExt2Formatter.Format(fake, new Ext2FormatOptions { BlockSize = 4096 }) && fake.Accesses == 0, "fixed: " + name + " ohne Zugriff abgelehnt");
        }
        uint U32(long offset) => BitConverter.ToUInt32(image, (int)offset);
        ushort U16(long offset) => BitConverter.ToUInt16(image, (int)offset);
        const long Primary = 1024, Backup = 8193L * 1024;
        expect(U32(Primary + 20) == 1 && U32(Primary + 32) == 8192, "fixed: first_data_block 1, 8192 Bloecke je Gruppe");
        expect((U32(Primary + 100) & 1) == 1, "fixed: sparse_super gesetzt");
        expect(U16(Backup + 56) == 0xEF53 && U16(Backup + 90) == 1, "fixed: Sicherungssuperblock in Gruppe 1");
        uint group1Bitmap = U32(2048 + 32);
        expect(group1Bitmap == 8193 + 2 && U32(Backup + 1024 + 32) == group1Bitmap, "fixed: Gruppe-1-Bitmap hinter Sicherung, Deskriptorkopie gleich");
        uint group0Bitmap = U32(2048);
        int lastBits = image[group0Bitmap * 1024 + 1023];
        int inodeTail = image[U32(2048 + 4) * 1024 + 1023];
        expect(lastBits == 0 && inodeTail == 0xFF, "fixed: volle Blockbitmap ohne, Inodebitmap mit Padding");
        // Gruppe 0 belegt Superblock, Deskriptor, 2 Bitmaps, 256 Inodetabellenbloecke und Wurzel: Bits 0..260.
        // Der unveraenderte Formatter setzt Bit 261 zusaetzlich (e2fsck "-134" beim 4-MiB-Fall).
        byte[] bitmap = image.AsSpan((int)(group0Bitmap * 1024), 1024).ToArray();
        expect((bitmap[260 / 8] & (1 << (260 % 8))) != 0 && (bitmap[261 / 8] & (1 << (261 % 8))) == 0, "fixed: Gruppe 0 Bits 0..260 belegt, 261 frei");
        expect(U32(Primary + 4) == 16384 && U16(2048 + 12) == 8192 - 261, "fixed: Blockzahl und freie Bloecke Gruppe 0");
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

    public int Writes { get; private set; }

    public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
    {
        Writes++;
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

/// <summary>Geraet ohne Speicher fuer Grenzfaelle; zaehlt jeden Zugriff.</summary>
internal sealed class CountingBlockDevice(ulong blockSize, ulong blockCount) : IBlockDevice
{
    public ulong BlockSize => blockSize;
    public ulong BlockCount => blockCount;
    public string Name => "zaehler";
    public int Accesses { get; private set; }
    public void ReadBlock(ulong blockNo, ulong count, Span<byte> data) => Accesses++;
    public void WriteBlock(ulong blockNo, ulong count, ReadOnlySpan<byte> data) => Accesses++;
    public void Flush() => Accesses++;
}
