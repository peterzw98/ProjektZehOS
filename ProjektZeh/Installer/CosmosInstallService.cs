using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.FileSystem.Ext2;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Installer;

internal sealed class CosmosInstallService : IInstallService, IInstallPlatform
{
    private const string FileSystemName = "zehos-setup-fat";
    private const string RootFileSystemName = "zehos-setup-ext2";
    private readonly InstallService service;
    private readonly Func<ResultErr> restart;
    private readonly string installMount;
    private bool registered;
    private bool rootRegistered;
    // Die Vollpruefung dauert spuerbar; sie laeuft pro Platte nur einmal, bis diese Platte beschrieben wird.
    private readonly Dictionary<string, bool> blankDisks = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="gptEnabled">Freischaltung des GPT-Wegs aus Kernel.cs (-p:ZehOSGptInstaller).</param>
    /// <param name="installMount">Einhaengepunkt der ESP waehrend der GPT-Installation (SystemPaths.InstallMount).</param>
    public CosmosInstallService(string version, Func<ResultErr> restart, bool gptEnabled = false, string installMount = "/data")
    {
        this.restart = restart;
        this.installMount = installMount;
        service = new InstallService(this, ReadPayload("ZehOS.Payload.Efi"), ReadPayload("ZehOS.Payload.Kernel", inverted: true), version,
            gptEnabled, CosmosSaltSource.Fill);
    }

    public string ProductVersion => service.ProductVersion;
    public ResultObject<InstallDisk[]> GetDisks() => service.GetDisks();
    public ResultObject<IInstallJob> Begin(string diskName, bool eraseExisting) => service.Begin(diskName, eraseExisting);
    public ResultObject<IInstallJob> BeginGpt(string diskName, bool eraseExisting) => service.BeginGpt(diskName, eraseExisting);
    public ResultErr Restart() => restart();

    public ResultObject<InstallCandidate[]> GetCandidates()
    {
        try
        {
            var candidates = new List<InstallCandidate>();
            foreach (IBlockDevice device in StorageManager.Devices)
            {
                // Die Datenplatte ZEHOSDATA ist nie Installationsziel und erscheint nicht in der Liste.
                // Nicht lesbar heisst nicht sicher: ein solches Geraet wird nicht angeboten, die Liste bleibt nutzbar.
                try { if (StorageLock.Run(() => CosmosDataDisk.HasZehosDataLabel(device))) continue; }
                catch (Exception) { continue; }
                candidates.Add(new(new InstallDisk(device.Name, checked(device.BlockCount * device.BlockSize),
                    StorageManager.GetPartitions(device).Count, CachedIsBlank(device), device.BlockSize), IsInstallMedium(device)));
            }
            return ResultObjectFactory.CreateSuccess(candidates.ToArray());
        }
        catch (Exception exception) { return ResultObjectFactory.CreateFromException<InstallCandidate[]>(exception); }
    }

    public ResultObject<IInstallTarget> OpenTarget(string diskName)
    {
        try
        {
            foreach (IBlockDevice device in StorageManager.Devices)
            {
                if (!string.Equals(device.Name, diskName, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsInstallMedium(device)) return Failure("Das Installationsmedium darf nicht beschrieben werden.");
                if (StorageLock.Run(() => CosmosDataDisk.HasZehosDataLabel(device))) return Failure("Die Datenplatte ZEHOSDATA darf nicht ueberschrieben werden, auch nicht mit --erase.");
                if (device.BlockSize != 512 || device.BlockCount < InstallService.MinimumDiskSize / 512 || device.BlockCount - 2048 > uint.MaxValue)
                    return Failure("Unterstuetzt werden Platten ab 128 MiB mit 512-Byte-Sektoren und hoechstens 2 TiB.");
                // Mountpruefung und Registrierung als ein Abschnitt, damit dazwischen niemand einhaengt.
                var refused = StorageLock.Run(() => PrepareTargetStorageLocked(device));
                if (refused != null) return refused;
                blankDisks.Remove(device.Name);
                return ResultObjectFactory.CreateSuccess<IInstallTarget>(new CosmosInstallTarget(device, FileSystemName));
            }
            return ResultObjectFactory.CreateFailure<IInstallTarget>(ResultCode.NotFound, "Ziellaufwerk nicht gefunden.");
        }
        catch (Exception exception) { return ResultObjectFactory.CreateFromException<IInstallTarget>(exception); }
    }

    public ResultObject<IGptInstallTarget> OpenGptTarget(string diskName)
    {
        try
        {
            foreach (IBlockDevice device in StorageManager.Devices)
            {
                if (!string.Equals(device.Name, diskName, StringComparison.OrdinalIgnoreCase)) continue;
                // Wie OpenTarget, zusaetzlich beide Installationsmountpunkte; nichts davon schreibt (Spec 7a-S4, Abschnitt 2).
                if (IsInstallMedium(device)) return GptFailure("Das Installationsmedium darf nicht beschrieben werden.");
                if (StorageLock.Run(() => CosmosDataDisk.HasZehosDataLabel(device))) return GptFailure("Datenplatte/installiertes System, nicht als Ziel erlaubt.");
                // Mountpruefung und Registrierung als ein Abschnitt, damit dazwischen niemand einhaengt.
                var refused = StorageLock.Run(() => PrepareGptTargetStorageLocked(device));
                if (refused != null) return refused;
                blankDisks.Remove(device.Name);
                return ResultObjectFactory.CreateSuccess<IGptInstallTarget>(new CosmosGptInstallTarget(device, FileSystemName, RootFileSystemName, installMount));
            }
            return ResultObjectFactory.CreateFailure<IGptInstallTarget>(ResultCode.NotFound, "Ziellaufwerk nicht gefunden.");
        }
        catch (Exception exception) { return ResultObjectFactory.CreateFromException<IGptInstallTarget>(exception); }
    }

    /// <summary>Fehlerergebnis oder null, wenn Ziel und beide Mountpunkte frei und beide Dateisysteme registriert sind.</summary>
    private ResultObject<IGptInstallTarget>? PrepareGptTargetStorageLocked(IBlockDevice device)
    {
        foreach (var mount in VfsManager.Mounts)
            if (mount.Partition != null && ReferenceEquals(mount.Partition.Host, device))
                return GptFailure("Das Ziellaufwerk ist eingebunden. Vor der Installation aushaengen.");
        if (VfsManager.TryGetMount(installMount, out _) || VfsManager.TryGetMount(CosmosGptInstallTarget.RootMount, out _))
            return GptFailure("Installations-Mountpunkt belegt.");
        if (!registered)
        {
            if (!VfsManager.RegisterFileSystem(FileSystemName, new FatFileSystemType()))
                return GptFailure("Das Installer-Dateisystem konnte nicht registriert werden.");
            registered = true;
        }
        if (!rootRegistered)
        {
            // Ohne Geraet: Format und Mount bekommen die Partition ueber VfsManager, der Plug erzwingt das 4-KiB-Profil.
            if (!VfsManager.RegisterFileSystem(RootFileSystemName, new Ext2FileSystemType()))
                return GptFailure("Das Root-Dateisystem konnte nicht registriert werden.");
            rootRegistered = true;
        }
        return null;
    }

    private static ResultObject<IGptInstallTarget> GptFailure(string message) => ResultObjectFactory.CreateFailure<IGptInstallTarget>(ResultCode.Failure, message);

    /// <summary>Fehlerergebnis oder null, wenn das Ziel ausgehaengt und das Installer-Dateisystem registriert ist.</summary>
    private ResultObject<IInstallTarget>? PrepareTargetStorageLocked(IBlockDevice device)
    {
        foreach (var mount in VfsManager.Mounts)
            if (mount.Partition != null && ReferenceEquals(mount.Partition.Host, device))
                return Failure("Das Ziellaufwerk ist eingebunden. Vor der Installation aushaengen.");
        if (VfsManager.TryGetMount("/data", out _)) return Failure("Der Installations-Mountpunkt /data ist bereits belegt.");
        if (!registered)
        {
            if (!VfsManager.RegisterFileSystem(FileSystemName, new FatFileSystemType()))
                return Failure("Das Installer-Dateisystem konnte nicht registriert werden.");
            registered = true;
        }
        return null;
    }

    private static byte[] ReadPayload(string name, bool inverted = false)
    {
#if ZEHOS_INSTALLER
        using Stream stream = typeof(CosmosInstallService).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Installer-Payload fehlt: " + name);
        byte[] bytes = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new EndOfStreamException("Installer-Payload ist unvollstaendig.");
            offset += count;
        }
        if (inverted)
            for (int i = 0; i < bytes.Length; i++) bytes[i] ^= 0xff;
        return bytes;
#else
        throw new InvalidOperationException("Die Installationsdateien sind nur im Installer-Build enthalten.");
#endif
    }

    private bool CachedIsBlank(IBlockDevice device)
    {
        if (!blankDisks.TryGetValue(device.Name, out bool blank)) blankDisks[device.Name] = blank = IsBlank(device);
        return blank;
    }

    /// <summary>
    /// Leer gilt nur eine vollstaendig gelesene Platte ohne ein einziges gesetztes Byte.
    /// shortcut: ueber 256 MiB wird nicht gelesen (die Oberflaeche stuende sonst lange still) und deshalb immer die Loeschbestaetigung verlangt; bei Bedarf Hintergrundpruefung ergaenzen.
    /// </summary>
    private static bool IsBlank(IBlockDevice device)
    {
        const ulong batchBlocks = 128;
        if (device.BlockCount * device.BlockSize > 256UL * 1024 * 1024) return false;
        var buffer = new byte[batchBlocks * device.BlockSize];
        for (ulong block = 0; block < device.BlockCount; block += batchBlocks)
        {
            ulong count = Math.Min(batchBlocks, device.BlockCount - block);
            // Sperre je Lesestapel (64 KiB bei 512-Byte-Sektoren), nicht fuer die ganze Platte.
            StorageLock.Run(() => device.ReadBlock(block, count, buffer));
            for (ulong index = 0; index < count * device.BlockSize; index++)
                if (buffer[index] != 0) return false;
        }
        return true;
    }

    private static bool IsInstallMedium(IBlockDevice device)
    {
        // Der Installer wird als ISO ausgeliefert. Auch ein roh auf USB geschriebenes ISO behaelt CD001 bei Byte 32769.
        if (device.BlockSize != 512 && device.BlockSize != 2048) return true;
        ulong sector = 32768 / device.BlockSize;
        if (device.BlockCount <= sector) return false;
        byte[] descriptor = new byte[device.BlockSize];
        StorageLock.Run(() => device.ReadBlock(sector, 1, descriptor));
        return descriptor[1] == 'C' && descriptor[2] == 'D' && descriptor[3] == '0' && descriptor[4] == '0' && descriptor[5] == '1';
    }

    private static ResultObject<IInstallTarget> Failure(string message) => ResultObjectFactory.CreateFailure<IInstallTarget>(ResultCode.Failure, message);
}

internal sealed class CosmosInstallTarget(IBlockDevice device, string fileSystemName) : IInstallTarget
{
    private bool mounted;
    public ResultErr Partition()
    {
        try
        {
            return StorageLock.Run(PartitionStorageLocked);
        }
        catch (Exception exception) { return ResultErr.CreateFromException(exception); }
    }

    // Partitionstabelle als ein Abschnitt: Loeschen, MBR, Flush und Neueinlesen gehoeren zusammen.
    private ResultErr PartitionStorageLocked()
    {
        // Alte GPT-Kopien duerfen nach einem bestaetigten Loeschen den neuen MBR nicht ueberstimmen.
        byte[] zeros = new byte[2048 * 512];
        device.WriteBlock(0, 2048, zeros);
        device.WriteBlock(device.BlockCount - 33, 33, zeros.AsSpan(0, 33 * 512));
        Mbr.Create(device);
        if (!Mbr.AddPartition(device, 0, 0xEF, 2048, device.BlockCount - 2048))
            return Failed("Cosmos hat die EFI-Partition abgelehnt.");
        device.Flush();
        StorageManager.RescanPartitions(device);
        return ResultErr.CreateSuccess();
    }

    public ResultErr Format()
    {
        try
        {
            return StorageLock.Run(FormatStorageLocked);
        }
        catch (Exception exception) { return ResultErr.CreateFromException(exception); }
    }

    // Formatieren und Einhaengen als ein Abschnitt; das Formatieren laesst sich nicht stueckeln.
    private ResultErr FormatStorageLocked()
    {
        var partitions = StorageManager.GetPartitions(device);
        if (partitions.Count != 1) return Failed("Genau eine EFI-Partition erwartet.");
        if (!VfsManager.TryFormat(fileSystemName, partitions[0], new FatFormatOptions { Type = FatType.Fat32, VolumeLabel = "ZEHOS" }))
            return Failed("FAT32-Formatierung fehlgeschlagen.");
        if (!VfsManager.TryMount(fileSystemName, partitions[0], MountFlags.None, "/data", out _))
            return Failed("Die formatierte EFI-Partition konnte nicht eingebunden werden.");
        mounted = true;
        return ResultErr.CreateSuccess();
    }

    // Ein Stueck (InstallJob: hoechstens 64 KiB) mit Oeffnen, Schreiben, Flush und Schliessen als ein Abschnitt.
    public ResultErr WriteFileChunk(string path, byte[] data, int offset, int count, bool first, bool last) =>
        StorageLock.Run(() => WriteFileChunkStorageLocked(path, data, offset, count, first));

    private static ResultErr WriteFileChunkStorageLocked(string path, byte[] data, int offset, int count, bool first)
    {
        FileStream? stream = null;
        Exception? failure = null;
        try
        {
            string destination = "/data" + path;
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

    public ResultErr Abort() => mounted ? Complete() : ResultErr.CreateSuccess();

    public ResultErr Complete()
    {
        try
        {
            return StorageLock.Run(CompleteStorageLocked);
        }
        catch (Exception exception) { return ResultErr.CreateFromException(exception); }
    }

    private ResultErr CompleteStorageLocked()
    {
        if (!VfsManager.TryUnmount("/data")) return Failed("Installationsdateien konnten nicht ausgehaengt werden.");
        mounted = false;
        device.Flush();
        return ResultErr.CreateSuccess();
    }

    private static ResultErr Failed(string message) => ResultErr.CreateFailure(ResultCode.Failure, message);
}
