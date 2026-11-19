#if ZEHOS_STORAGE_RATE_PROBE
using System;
using System.Diagnostics;
using System.IO;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

/// <summary>
/// Messprobe (-p:ZehOSStorageRateProbe=true, Issue #542) fuer die Schreib-/Leserate im Gast; Gegenstueck ist
/// scripts/test-storage-rate-guest.py. Misst je Schritt die Gesamtzeit und getrennt davon die Zeit in
/// IBlockDevice.ReadBlock/WriteBlock (Aufrufe, Sektoren, Summe), damit Treiber- und Dateisystemanteil getrennt sichtbar
/// sind. Platten: sata1 genau 512 MiB, erstes MiB leer (Rohmessung, dann ext2), sata2 genau 520 MiB mit FAT32 vom Host.
/// Nur Messung; nie in normale Builds.
/// </summary>
internal static class CosmosStorageRateProbe
{
    private const ulong Ext2DiskSize = 512UL * 1024 * 1024;
    private const ulong FatDiskSize = 520UL * 1024 * 1024;
    private const int BigFileBytes = 256 * 1024 * 1024;
    private const int ChunkBytes = 1024 * 1024;
    private const int SmallFileBytes = 64 * 1024;
    private const int SmallFileCount = 1000;
    // Rohmessung im hinteren Bereich der ext2-Platte, bevor sie formatiert wird.
    private const ulong RawOffsetBytes = 256UL * 1024 * 1024;
    private const int RawBytes = 64 * 1024 * 1024;
    private const int RawSmallBytes = 8 * 1024 * 1024;

    private static string step = "select";

    public static bool Run()
    {
        try
        {
            Write($"start cpus={Environment.ProcessorCount} freq={Stopwatch.Frequency}");
            IBlockDevice ext2Raw = FindDevice(Ext2DiskSize);
            IBlockDevice fatRaw = FindDevice(FatDiskSize);
            var ext2Disk = new TimedBlockDevice(ext2Raw);
            var fatDisk = new TimedBlockDevice(fatRaw);

            step = "check";
            byte[] prefix = new byte[1024 * 1024];
            ext2Disk.ReadBlock(0, (ulong)prefix.Length / ext2Disk.BlockSize, prefix);
            if (!Array.TrueForAll(prefix, value => value == 0))
                throw new InvalidOperationException($"{ext2Raw.Name}: erstes MiB nicht leer; nichts geschrieben");

            RawRuns(ext2Disk);

            step = "ext2-format";
            Check(VfsManager.RegisterFileSystem("zehos-rate-ext2", new Ext2FileSystemType(ext2Disk)), "Registrierung ext2 abgelehnt");
            Measure("ext2", "format", 0, ext2Disk, () => Check(CosmosExt2Formatter.FormatForKernel(ext2Disk, null), "FormatForKernel abgelehnt"));
            Check(VfsManager.TryMount("zehos-rate-ext2", "", MountFlags.None, "/rate-ext2", out _), "TryMount ext2 abgelehnt");
            FileRuns("ext2", "/rate-ext2", ext2Disk);
            Check(VfsManager.TryUnmount("/rate-ext2"), "TryUnmount ext2 abgelehnt");

            step = "fat-mount";
            Check(VfsManager.RegisterFileSystem("zehos-rate-fat", new FatFileSystemType(fatDisk)), "Registrierung FAT abgelehnt");
            Check(VfsManager.TryMount("zehos-rate-fat", "", MountFlags.None, "/rate-fat", out _), "TryMount FAT abgelehnt");
            FileRuns("fat", "/rate-fat", fatDisk);
            Check(VfsManager.TryUnmount("/rate-fat"), "TryUnmount FAT abgelehnt");

            Write("DONE");
            return true;
        }
        catch (Exception exception)
        {
            Write($"{step} FAIL {exception.GetType().Name}: {exception.Message}");
            Write("DONE");
            return false;
        }
    }

    private static IBlockDevice FindDevice(ulong size)
    {
        step = $"select-{size >> 20}MiB";
        IBlockDevice? found = null;
        foreach (IBlockDevice device in StorageManager.Devices)
        {
            if (device.BlockSize != 512 || device.BlockCount * device.BlockSize != size)
                continue;
            if (found is not null)
                throw new InvalidOperationException("Plattengroesse nicht eindeutig");
            found = device;
        }

        return found ?? throw new InvalidOperationException("Platte fehlt");
    }

    // Treibergrundlinie ohne Dateisystem: grosse Aufrufe (1 MiB) und 4-KiB-Aufrufe wie ein Dateisystem mit 4-KiB-Bloecken.
    private static void RawRuns(TimedBlockDevice disk)
    {
        byte[] buffer = Pattern(ChunkBytes, 7);
        ulong blocksPerChunk = (ulong)ChunkBytes / disk.BlockSize;
        ulong start = RawOffsetBytes / disk.BlockSize;
        Measure("raw", "write-1MiB-calls", RawBytes, disk, () =>
        {
            for (ulong i = 0; i < (ulong)(RawBytes / ChunkBytes); i++)
                disk.WriteBlock(start + i * blocksPerChunk, blocksPerChunk, buffer);
            disk.Flush();
        });
        Measure("raw", "read-1MiB-calls", RawBytes, disk, () =>
        {
            for (ulong i = 0; i < (ulong)(RawBytes / ChunkBytes); i++)
                disk.ReadBlock(start + i * blocksPerChunk, blocksPerChunk, buffer);
        });
        ulong blocksPer4K = 4096 / disk.BlockSize;
        Measure("raw", "write-4KiB-calls", RawSmallBytes, disk, () =>
        {
            for (ulong i = 0; i < (ulong)(RawSmallBytes / 4096); i++)
                disk.WriteBlock(start + i * blocksPer4K, blocksPer4K, buffer.AsSpan(0, 4096));
            disk.Flush();
        });
        Measure("raw", "read-4KiB-calls", RawSmallBytes, disk, () =>
        {
            for (ulong i = 0; i < (ulong)(RawSmallBytes / 4096); i++)
                disk.ReadBlock(start + i * blocksPer4K, blocksPer4K, buffer.AsSpan(0, 4096));
        });
    }

    private static void FileRuns(string fs, string mount, TimedBlockDevice disk)
    {
        byte[] chunk = Pattern(ChunkBytes, 3);
        string big = mount + "/big.bin";
        Measure(fs, "write-256MiB", BigFileBytes, disk, () =>
        {
            using (var stream = new FileStream(big, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                for (int written = 0; written < BigFileBytes; written += ChunkBytes)
                    stream.Write(chunk, 0, ChunkBytes);
            }

            disk.Flush();
        });
        Measure(fs, "read-256MiB", BigFileBytes, disk, () =>
        {
            long total = 0;
            using (var stream = new FileStream(big, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
            {
                int read;
                while ((read = stream.Read(chunk, 0, ChunkBytes)) > 0)
                    total += read;
            }

            Check(total == BigFileBytes, $"gelesen {total} statt {BigFileBytes}");
        });

        string directory = mount + "/small";
        Directory.CreateDirectory(directory);
        byte[] small = Pattern(SmallFileBytes, 5);
        Measure(fs, "write-1000x64KiB", SmallFileBytes * SmallFileCount, disk, () =>
        {
            for (int i = 0; i < SmallFileCount; i++)
                File.WriteAllBytes(directory + "/f" + i.ToString() + ".bin", small);
            disk.Flush();
        });
        Measure(fs, "read-1000x64KiB", SmallFileBytes * SmallFileCount, disk, () =>
        {
            for (int i = 0; i < SmallFileCount; i++)
            {
                byte[] content = File.ReadAllBytes(directory + "/f" + i.ToString() + ".bin");
                Check(content.Length == SmallFileBytes, $"f{i}: {content.Length} Byte");
            }
        });
    }

    private static void Measure(string fs, string name, long bytes, TimedBlockDevice disk, Action action)
    {
        step = fs + "-" + name;
        TimedBlockDevice.Counters before = disk.Snapshot();
        long started = Stopwatch.GetTimestamp();
        action();
        long elapsed = Stopwatch.GetTimestamp() - started;
        TimedBlockDevice.Counters after = disk.Snapshot();

        long totalUs = TicksToUs(elapsed);
        long readUs = TicksToUs(after.ReadTicks - before.ReadTicks);
        long writeUs = TicksToUs(after.WriteTicks - before.WriteTicks);
        long flushUs = TicksToUs(after.FlushTicks - before.FlushTicks);
        long reads = after.Reads - before.Reads, readSectors = after.ReadSectors - before.ReadSectors;
        long writes = after.Writes - before.Writes, writeSectors = after.WriteSectors - before.WriteSectors;
        long deviceUs = readUs + writeUs + flushUs;
        // Ganzzahlig in KB/s (1000 Byte), weil Fliesskomma-Formatierung im Gast nicht belegt ist.
        long kbps = totalUs > 0 ? bytes * 1000 / totalUs : 0;
        Write($"{fs} {name} bytes={bytes} total_ms={totalUs / 1000} kBps={kbps} "
              + $"dev_read calls={reads} sectors={readSectors} spc={(reads > 0 ? readSectors / reads : 0)} ms={readUs / 1000} "
              + $"dev_write calls={writes} sectors={writeSectors} spc={(writes > 0 ? writeSectors / writes : 0)} ms={writeUs / 1000} "
              + $"flush ms={flushUs / 1000} fs_ms={(totalUs - deviceUs) / 1000}");
    }

    private static long TicksToUs(long ticks) => (long)((ulong)ticks * 1_000_000UL / (ulong)Stopwatch.Frequency);

    private static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 31 + seed + (i >> 12));
        return data;
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition)
            throw new InvalidOperationException(detail);
    }

    private static void Write(string message) => Log.WriteString($"STORAGE_RATE {message}\n");

    /// <summary>Zaehlt Aufrufe, Sektoren und Zeit je Richtung; nur ein Thread benutzt die Probe.</summary>
    private sealed class TimedBlockDevice : IBlockDevice
    {
        private readonly IBlockDevice inner;
        private Counters counters;

        public TimedBlockDevice(IBlockDevice inner) => this.inner = inner;

        public struct Counters
        {
            public long Reads, ReadSectors, ReadTicks, Writes, WriteSectors, WriteTicks, FlushTicks;
        }

        public ulong BlockCount => inner.BlockCount;
        public ulong BlockSize => inner.BlockSize;
        public string Name => inner.Name;

        public Counters Snapshot() => counters;

        public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data)
        {
            long started = Stopwatch.GetTimestamp();
            inner.ReadBlock(blockNo, blockCount, data);
            counters.ReadTicks += Stopwatch.GetTimestamp() - started;
            counters.Reads++;
            counters.ReadSectors += (long)blockCount;
        }

        public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
        {
            long started = Stopwatch.GetTimestamp();
            inner.WriteBlock(blockNo, blockCount, data);
            counters.WriteTicks += Stopwatch.GetTimestamp() - started;
            counters.Writes++;
            counters.WriteSectors += (long)blockCount;
        }

        public void Flush()
        {
            long started = Stopwatch.GetTimestamp();
            inner.Flush();
            counters.FlushTicks += Stopwatch.GetTimestamp() - started;
        }
    }
}
#endif
