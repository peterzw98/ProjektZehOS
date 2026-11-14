using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;
using ProjektZeh.Shared;

namespace ProjektZeh.Installer;

internal sealed class CosmosInstallService : IInstallService, IInstallPlatform
{
    private const string FileSystemName = "zehos-setup-fat";
    private readonly InstallService service;
    private readonly Func<ResultErr> restart;
    private bool registered;

    public CosmosInstallService(string version, Func<ResultErr> restart)
    {
        this.restart = restart;
        service = new InstallService(this, ReadPayload("ZehOS.Payload.Efi"), ReadPayload("ZehOS.Payload.Kernel"), version);
    }

    public string ProductVersion => service.ProductVersion;
    public ResultObject<InstallDisk[]> GetDisks() => service.GetDisks();
    public ResultObject<IInstallJob> Begin(string diskName, bool eraseExisting) => service.Begin(diskName, eraseExisting);
    public ResultErr Restart() => restart();

    public ResultObject<InstallCandidate[]> GetCandidates()
    {
        try
        {
            var candidates = new List<InstallCandidate>();
            foreach (IBlockDevice device in StorageManager.Devices)
            {
                candidates.Add(new(new InstallDisk(device.Name, checked(device.BlockCount * device.BlockSize),
                    StorageManager.GetPartitions(device).Count, IsBlank(device)), IsInstallMedium(device)));
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
                if (device.BlockSize != 512 || device.BlockCount < InstallService.MinimumDiskSize / 512 || device.BlockCount - 2048 > uint.MaxValue)
                    return Failure("Unterstuetzt werden Platten ab 128 MiB mit 512-Byte-Sektoren und hoechstens 2 TiB.");
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
                return ResultObjectFactory.CreateSuccess<IInstallTarget>(new CosmosInstallTarget(device, FileSystemName));
            }
            return ResultObjectFactory.CreateFailure<IInstallTarget>(ResultCode.NotFound, "Ziellaufwerk nicht gefunden.");
        }
        catch (Exception exception) { return ResultObjectFactory.CreateFromException<IInstallTarget>(exception); }
    }

    private static byte[] ReadPayload(string name)
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
        return bytes;
#else
        throw new InvalidOperationException("Die Installationsdateien sind nur im Installer-Build enthalten.");
#endif
    }

    /// <summary>
    /// Leer gilt nur eine vollstaendig gelesene Platte ohne ein einziges gesetztes Byte.
    /// shortcut: ueber 1 GiB wird nicht gelesen (dauert Minuten) und deshalb immer die Loeschbestaetigung verlangt; bei Bedarf Hintergrundpruefung ergaenzen.
    /// </summary>
    private static bool IsBlank(IBlockDevice device)
    {
        const ulong batchBlocks = 128;
        if (device.BlockCount * device.BlockSize > 1024UL * 1024 * 1024) return false;
        var buffer = new byte[batchBlocks * device.BlockSize];
        for (ulong block = 0; block < device.BlockCount; block += batchBlocks)
        {
            ulong count = Math.Min(batchBlocks, device.BlockCount - block);
            device.ReadBlock(block, count, buffer);
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
        device.ReadBlock(sector, 1, descriptor);
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
        catch (Exception exception) { return ResultErr.CreateFromException(exception); }
    }

    public ResultErr Format()
    {
        try
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
        catch (Exception exception) { return ResultErr.CreateFromException(exception); }
    }

    public ResultErr WriteFileChunk(string path, byte[] data, int offset, int count, bool first, bool last)
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
            if (!VfsManager.TryUnmount("/data")) return Failed("Installationsdateien konnten nicht ausgehaengt werden.");
            mounted = false;
            device.Flush();
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) { return ResultErr.CreateFromException(exception); }
    }

    private static ResultErr Failed(string message) => ResultErr.CreateFailure(ResultCode.Failure, message);
}
