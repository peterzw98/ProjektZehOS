using System;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

/// <summary>Liest <paramref name="blockCount"/> Sektoren ab <paramref name="block"/> in <paramref name="buffer"/>.</summary>
internal delegate void BlockReader(ulong block, ulong count, Span<byte> buffer);

/// <summary>
/// Leerpruefung vor disk init als reine Funktion. Bis <see cref="FullScanLimit"/> wird alles gelesen; darueber nur
/// Kopf, Ende und je 4 KiB alle 64 MiB, und nur nach ausdruecklicher Bestaetigung. Kopf und Ende decken MBR, GPT samt
/// Sicherungskopie, FAT-/NTFS-/LUKS-Bootsektor und den ext2-Superblock (Offset 0x438) ab.
/// </summary>
internal static class DiskEmptyCheck
{
    internal const ulong FullScanLimit = 4UL * 1024 * 1024 * 1024;
    internal const ulong EdgeBytes = 64UL * 1024 * 1024;
    internal const ulong SampleStep = 64UL * 1024 * 1024;
    internal const ulong SampleBytes = 4096;
    private const ulong BatchBytes = 1024UL * 1024;

    /// <summary>
    /// shortcut: ueber 4 GiB bleiben Daten zwischen den Stichproben unentdeckt (nur die ersten 4 KiB je 64 MiB werden gelesen);
    /// bei Bedarf dichter pruefen, die Vollpruefung kostet beim AHCI-Treiber (4 KiB je Befehl) Minuten je 32 GiB.
    /// </summary>
    public static ResultErr Check(ulong blockSize, ulong blockCount, bool confirmedLarge, BlockReader read)
    {
        if (blockSize == 0 || blockCount > ulong.MaxValue / blockSize)
        {
            return Failure("UnsupportedDiskSize", "Die Plattengroesse ist nicht pruefbar.");
        }

        ulong totalBytes = blockCount * blockSize;
        if (totalBytes <= FullScanLimit)
        {
            return ScanRange(blockSize, 0, blockCount, read);
        }

        if (!confirmedLarge)
        {
            return Failure("LargeDiskNeedsConfirmation",
                "Platten ueber 4 GiB werden nur stichprobenartig auf Leere geprueft; zur Bestaetigung 'disk init <Name> --yes-large' verwenden.");
        }

        ulong edgeBlocks = EdgeBytes / blockSize;
        var result = ScanRange(blockSize, 0, edgeBlocks, read);
        if (!result.IsOK()) return result;
        result = ScanRange(blockSize, blockCount - edgeBlocks, blockCount, read);
        if (!result.IsOK()) return result;
        ulong sampleBlocks = Math.Max(1, SampleBytes / blockSize);
        var sample = new byte[sampleBlocks * blockSize];
        for (ulong offset = SampleStep; offset < totalBytes; offset += SampleStep)
        {
            ulong block = offset / blockSize;
            ulong count = Math.Min(sampleBlocks, blockCount - block);
            var span = sample.AsSpan(0, (int)(count * blockSize));
            read(block, count, span);
            if (span.IndexOfAnyExcept((byte)0) >= 0) return NotEmpty();
        }

        return ResultErr.CreateSuccess();
    }

    private static ResultErr ScanRange(ulong blockSize, ulong first, ulong end, BlockReader read)
    {
        ulong batchBlocks = Math.Max(1, BatchBytes / blockSize);
        var buffer = new byte[batchBlocks * blockSize];
        for (ulong block = first; block < end; block += batchBlocks)
        {
            // Die Plattengroesse ist nicht durch den Stapel teilbar; der letzte Stapel wird gekuerzt.
            ulong count = Math.Min(batchBlocks, end - block);
            var span = buffer.AsSpan(0, (int)(count * blockSize));
            read(block, count, span);
            if (span.IndexOfAnyExcept((byte)0) >= 0) return NotEmpty();
        }

        return ResultErr.CreateSuccess();
    }

    private static ResultErr NotEmpty() => Failure("DiskNotEmpty", "Das Laufwerk enthaelt Daten; es wird nicht formatiert.");

    private static ResultErr Failure(string cause, string message) =>
        ResultErr.CreateFailure(ResultCode.Failure, $"cause='{cause}' message='{message}'");
}
