using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem.Ext2;

// format IMAGE --size BYTES --block-size N [--label L]: neues sparse Image anlegen und mit dem Cosmos-Ext2Formatter
// formatieren. Ergebnis als eine JSON-Zeile auf stdout.
if (args.Length < 1 || args[0] != "format" || args.Length < 2)
{
    Console.Error.WriteLine("Aufruf: format IMAGE --size BYTES --block-size 1024|2048|4096 [--label L]");
    return 2;
}
string image = args[1];
long size = 0;
uint blockSize = 1024;
string label = "";
for (int i = 2; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--size": size = long.Parse(args[i + 1]); break;
        case "--block-size": blockSize = uint.Parse(args[i + 1]); break;
        case "--label": label = args[i + 1]; break;
        default: Console.Error.WriteLine("Unbekannte Option " + args[i]); return 2;
    }
}
if (size <= 0 || size % 512 != 0) { Console.Error.WriteLine("--size muss ein positives Vielfaches von 512 sein."); return 2; }

// Nie ein vorhandenes Image ueberschreiben (AGENTS.md: Testdaten schuetzen).
using var file = new FileStream(image, FileMode.CreateNew, FileAccess.ReadWrite);
file.SetLength(size);
var device = new FileBlockDevice(file, Path.GetFileName(image));
var format = typeof(Ext2FormatOptions).Assembly.GetType("Cosmos.Kernel.System.FileSystem.Ext2.Ext2Formatter", true)!
    .GetMethod("Format", BindingFlags.Public | BindingFlags.Static)!;
var clock = Stopwatch.StartNew();
bool ok = false;
string? error = null;
try { ok = (bool)format.Invoke(null, [device, new Ext2FormatOptions { BlockSize = blockSize, VolumeLabel = label }])!; }
catch (TargetInvocationException exception) { error = exception.InnerException?.ToString() ?? exception.ToString(); }
file.Flush();
Console.WriteLine(JsonSerializer.Serialize(new { ok, size, blockSize, elapsedMs = clock.ElapsedMilliseconds, error }));
return ok ? 0 : 1;

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
