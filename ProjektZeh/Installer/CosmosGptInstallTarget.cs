using System;
using System.IO;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Ext2;
using ProjektZeh.Storage.Migration;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Installer;

/// <summary>
/// Plattenzugriffe des GPT-Installers (Spec 7a-S4) fuer Cosmos 3.0.97. Reihenfolge und Pruefungen dazwischen liegen in
/// <see cref="GptInstallJob"/> und sind dort auf dem Host getestet; hier stehen nur die einzelnen Cosmos-Aufrufe.
/// </summary>
internal sealed class CosmosGptInstallTarget(IBlockDevice device, string espFileSystem, string rootFileSystem, string espMount) : IGptInstallTarget
{
    internal const string RootMount = "/mnt/install-root";
    private const string EspLabel = "ZEHOSBOOT";
    private Partition? esp;
    private Partition? root;
    private bool espMounted;
    private bool rootMounted;

    public ResultErr WriteLayout(GptTable table) => Try(() =>
    {
        GptLayoutWriter.Write(device.BlockCount, table, (block, data) => device.WriteBlock(block, (ulong)data.Length / device.BlockSize, data.ToArray()), device.Flush);
        StorageManager.RescanPartitions(device);
        var verified = GptLayout.Verify(device.BlockSize, device.BlockCount, device.ReadBlock);
        if (!verified.IsOK()) return verified.Status;
        if (verified.Value!.DiskGuid != table.DiskGuid) return Failed("Zurueckgelesene GPT traegt eine andere Disk-GUID.");
        // Cosmos liest GPT ohne CRC-Pruefung; die Zuordnung erfolgt deshalb ueber die geplanten Startsektoren.
        var partitions = StorageManager.GetPartitions(device);
        if (partitions.Count != 2) return Failed($"Nach dem Schreiben {partitions.Count} statt 2 Partitionen erkannt.");
        foreach (Partition partition in partitions)
        {
            if (partition.StartSector == table.Entries[0].FirstLba) esp = partition;
            else if (partition.StartSector == table.Entries[1].FirstLba) root = partition;
        }
        return esp != null && root != null ? ResultErr.CreateSuccess() : Failed("ESP oder Root nicht an den geplanten Startsektoren gefunden.");
    });

    public ResultErr FormatEsp() => Try(() =>
    {
        // Cosmos 3.0.97 liest die ESP mit 4-KiB-Clustern; groessere Cluster sind nicht verlaesslich.
        if (!VfsManager.TryFormat(espFileSystem, esp!, new FatFormatOptions { Type = FatType.Fat32, SectorsPerCluster = 8, VolumeLabel = EspLabel }))
            return Failed("FAT32-Formatierung der EFI-Partition fehlgeschlagen.");
        if (!VfsManager.TryMount(espFileSystem, esp!, MountFlags.None, espMount, out _))
            return Failed("Die EFI-Partition konnte nicht eingebunden werden.");
        espMounted = true;
        return ResultErr.CreateSuccess();
    });

    // Der Plug leitet auf CosmosExt2Formatter.FormatForKernel um (4096 Byte, sparse_super); GptInstallJob prueft das danach.
    public ResultErr FormatRoot() => Try(() =>
        VfsManager.TryFormat(rootFileSystem, root!, null) ? ResultErr.CreateSuccess() : Failed("ext2-Formatierung der Root-Partition fehlgeschlagen."));

    public ResultObject<byte[]> ReadRootSuperblock()
    {
        try
        {
            var superblock = new byte[1024];
            new CosmosRootVolume(root!, null).ReadSuperblock(superblock);
            return ResultObjectFactory.CreateSuccess(superblock);
        }
        catch (Exception exception) { return ResultObjectFactory.CreateFromException<byte[]>(exception); }
    }

    public ResultErr PrepareRoot(Guid diskGuid) => Try(() =>
    {
        if (!VfsManager.TryMount(rootFileSystem, root!, MountFlags.None, RootMount, out VfsMount? mount))
            return Failed("Root konnte nicht schreibbar eingehaengt werden.");
        rootMounted = true;
        var volume = new CosmosRootVolume(root!, mount);
        var opened = RootVolumeState.Open(volume);
        if (!opened.IsOK()) return opened;
        // Migration und Installer verwenden dieselbe Liste, damit eine Wiederholung keine fremden Daten loescht.
        foreach (string entry in LayoutMigration.InstallerEntries)
            if (!string.Equals(entry, LayoutMarker.Path, StringComparison.Ordinal))
                Directory.CreateDirectory(RootMount + entry);
        File.WriteAllText(RootMount + LayoutMarker.Path, new LayoutMarker(LayoutState.Fresh, diskGuid).Serialize());
        var closed = RootVolumeState.Close(volume);
        if (!closed.IsOK()) return closed;
        if (!VfsManager.TryUnmount(RootMount)) return Failed("Root konnte nicht ausgehaengt werden.");
        rootMounted = false;
        // Aushaengen schreibt den Superblock zuletzt; GptInstallJob liest ihn danach ein zweites Mal.
        device.Flush();
        return ResultErr.CreateSuccess();
    });

    public ResultErr WriteFileChunk(string path, byte[] data, int offset, int count, bool first, bool last)
    {
        FileStream? stream = null;
        Exception? failure = null;
        try
        {
            string destination = espMount + path;
            if (first) Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            stream = new FileStream(destination, first ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 1);
            stream.Write(data, offset, count);
            stream.Flush(true);
        }
        catch (Exception exception) { failure = exception; }
        try { stream?.Dispose(); }
        catch (Exception exception)
        { return ResultErr.CreateFromException(failure == null ? exception : new AggregateException(failure, exception)); }
        return failure == null ? ResultErr.CreateSuccess() : ResultErr.CreateFromException(failure);
    }

    public ResultErr FlushEsp() => Try(() =>
    {
        if (!VfsManager.TryGetMount(espMount, out VfsMount? mount) || !mount.Superblock.SuperOperations.Sync(mount.Superblock))
            return Failed("Sync der EFI-Partition fehlgeschlagen.");
        device.Flush();
        return ResultErr.CreateSuccess();
    });

    public ResultErr WriteMarker(string text) => Try(() =>
    {
        string path = Path.Combine(espMount, InstallMarker.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Temp + Rename: ein Teilschreiben hinterlaesst nie einen Marker unter dem endgueltigen Namen.
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, true);
        return ResultErr.CreateSuccess();
    });

    public ResultObject<string> ReadMarker()
    {
        try { return ResultObjectFactory.CreateSuccess(File.ReadAllText(Path.Combine(espMount, InstallMarker.RelativePath))); }
        catch (Exception exception) { return ResultObjectFactory.CreateFromException<string>(exception); }
    }

    public ResultErr Complete() => Try(() =>
    {
        if (!VfsManager.TryUnmount(espMount)) return Failed("EFI-Partition konnte nicht ausgehaengt werden.");
        espMounted = false;
        device.Flush();
        return ResultErr.CreateSuccess();
    });

    // Haengt Root vor ESP aus und loescht nichts; scheitern beide, nennt die Meldung beide (Spec 7a-S4, Abschnitt 4).
    public ResultErr Abort()
    {
        string errors = "";
        if (rootMounted)
        {
            try { if (VfsManager.TryUnmount(RootMount)) rootMounted = false; else errors += "Root nicht ausgehaengt. "; }
            catch (Exception exception) { errors += "Root: " + exception.Message + " "; }
        }
        if (espMounted)
        {
            try { if (VfsManager.TryUnmount(espMount)) espMounted = false; else errors += "EFI-Partition nicht ausgehaengt. "; }
            catch (Exception exception) { errors += "EFI-Partition: " + exception.Message + " "; }
        }
        try { device.Flush(); }
        catch (Exception exception) { errors += "Flush: " + exception.Message; }
        return errors.Length == 0 ? ResultErr.CreateSuccess() : Failed(errors.Trim());
    }

    private static ResultErr Try(Func<ResultErr> action)
    {
        try { return action(); }
        catch (Exception exception) { return ResultErr.CreateFromException(exception); }
    }

    private static ResultErr Failed(string message) => ResultErr.CreateFailure(ResultCode.Failure, message);
}
