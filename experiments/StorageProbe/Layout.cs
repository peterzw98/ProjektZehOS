using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem.Ext2;
using ProjektZeh.Storage.Partitioning;

/// <summary>
/// 7a Schritt 2 (Spec 2026-11-17-storage-7a-hostprobe.md): partitioniert ein neues sparse Image mit dem produktiven
/// GptLayoutWriter und formatiert die Root-Partition mit dem Kernelprofil des korrigierten ext2-Formatters. Die
/// Tabellenbytes prueft hier nur GptLayout.Verify; die Gegenpruefung mit fremden Werkzeugen macht
/// scripts/test-installer-layout.py.
/// </summary>
internal static class LayoutCommand
{
    private const string Usage = "Aufruf: layout IMAGE --size BYTES [--guid-seed HEX] | verify IMAGE";

    internal static int Layout(string[] args)
    {
        if (args.Length is not (4 or 6) || args[2] != "--size" || (args.Length == 6 && args[4] != "--guid-seed") ||
            !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size % 512 != 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        byte[]? seed = null;
        if (args.Length == 6)
        {
            try { seed = Convert.FromHexString(args[5]); }
            catch (FormatException) { Console.Error.WriteLine("--guid-seed muss Hex sein."); return 2; }
            if (seed.Length == 0) { Console.Error.WriteLine("--guid-seed darf nicht leer sein."); return 2; }
        }

        // Grenzen vor dem Anlegen: eine verweigerte Groesse hinterlaesst keine Datei.
        if (!GptLayout.SupportedGeometry(GptLayout.SectorSize, (ulong)size / GptLayout.SectorSize))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, size, cause = "UnsupportedDiskSize" }));
            return 1;
        }
        // Wie format: ohne nachgewiesene sparse Anlage schreibt SetLength unter Windows die volle Groesse (bis 128 GiB).
        // Nach der Geometriepruefung, damit abgelehnte Groessen auf allen Hosts dieselbe Ursache melden.
        if (OperatingSystem.IsWindows() && size > FormatRequest.WindowsMaximum)
        {
            Console.Error.WriteLine("Unter Windows hoechstens 64 MiB, bis sparse Anlage dort geprueft ist; layout braucht mindestens 32 GiB.");
            return 2;
        }

        FileStream file;
        try { file = new FileStream(args[1], FileMode.CreateNew, FileAccess.ReadWrite); }
        catch (IOException exception) { Console.Error.WriteLine("Image nicht angelegt: " + exception.Message); return 3; }
        using (file)
        {
            file.SetLength(size);
            var device = new FileBlockDevice(file, Path.GetFileName(args[1]));
            // Fester Seed nur fuer reproduzierbare Laeufe der Probe; SHA-512 liefert 64 Byte, der Writer braucht 48.
            Action<byte[]> random = seed is null ? bytes => RandomNumberGenerator.Fill(bytes) : bytes => SHA512.HashData(seed).AsSpan(0, bytes.Length).CopyTo(bytes);
            var written = GptLayoutWriter.Write(GptLayout.SectorSize, device.BlockCount, random,
                (block, data) => device.WriteBlock(block, (ulong)data.Length / GptLayout.SectorSize, data), device.Flush);
            if (!written.IsOK())
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, size, cause = written.Status.Message }));
                return 1;
            }

            GptEntry esp = written.Value!.Entries[0], root = written.Value.Entries[1];
            var window = new WindowBlockDevice(device, root.FirstLba, root.LastLba - root.FirstLba + 1);
            bool formatted = ProjektZeh.Storage.Ext2.CosmosExt2Formatter.FormatForKernel(window, new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = "zehos-root" });
            device.Flush();
            var verified = GptLayout.Verify(GptLayout.SectorSize, device.BlockCount, device.ReadBlock);
            bool ok = formatted && verified.IsOK();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok,
                size,
                diskGuid = written.Value.DiskGuid,
                esp = new { first = esp.FirstLba, last = esp.LastLba, unique = esp.Unique },
                root = new { first = root.FirstLba, last = root.LastLba, unique = root.Unique },
                formatted,
                verify = verified.IsOK() ? "ok" : verified.Status.Message,
            }));
            return ok ? 0 : 1;
        }
    }

    internal static int Verify(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine(Usage); return 2; }
        FileStream file;
        try { file = new FileStream(args[1], FileMode.Open, FileAccess.Read); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, verify = "io", error = exception.Message }));
            return 3;
        }
        using var _ = file;
        var device = new FileBlockDevice(file, Path.GetFileName(args[1]));
        var verified = GptLayout.Verify(GptLayout.SectorSize, device.BlockCount, device.ReadBlock);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = verified.IsOK(), verify = verified.IsOK() ? "ok" : verified.Status.Message }));
        return verified.IsOK() ? 0 : 1;
    }
}

/// <summary>Partitionsfenster ueber einem Geraet; Grenzen wie FileBlockDevice, damit der Formatter nie hinausschreibt.</summary>
internal sealed class WindowBlockDevice(IBlockDevice inner, ulong first, ulong count) : IBlockDevice
{
    public ulong BlockSize => inner.BlockSize;
    public ulong BlockCount => count;
    public string Name => inner.Name + "@" + first.ToString(CultureInfo.InvariantCulture);

    public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data)
    {
        Check(blockNo, blockCount);
        inner.ReadBlock(first + blockNo, blockCount, data);
    }

    public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
    {
        Check(blockNo, blockCount);
        inner.WriteBlock(first + blockNo, blockCount, data);
    }

    public void Flush() => inner.Flush();

    private void Check(ulong blockNo, ulong blockCount)
    {
        if (blockNo > count || blockCount > count - blockNo) throw new ArgumentOutOfRangeException(nameof(blockNo));
    }
}
