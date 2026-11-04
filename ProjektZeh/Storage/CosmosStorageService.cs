using System;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

/// <summary>
/// Verbindet Cosmos mit einem FAT-Datenlaufwerk unter /data.
/// </summary>
internal sealed class CosmosStorageService : IStorageService
{
    private const string FileSystemName = "zehos-fat";
    private const string MountPoint = "/data";
    private const ulong TestDiskSize = 64UL * 1024 * 1024;
    private const ulong PartitionStart = 2048;
    private bool registered;

    /// <summary>
    /// Beschreibt die von Cosmos erkannten Laufwerke.
    /// </summary>
    public ResultObject<StorageDeviceInfo[]> GetDevices()
    {
        try
        {
            var devices = StorageManager.Devices;
            var descriptions = new StorageDeviceInfo[devices.Count];
            for (int index = 0; index < devices.Count; index++)
            {
                IBlockDevice device = devices[index];
                descriptions[index] = new StorageDeviceInfo(device.Name,
                    device.BlockCount * device.BlockSize, StorageManager.GetPartitions(device).Count);
            }

            return ResultObjectFactory.CreateSuccess(descriptions);
        }
        catch (Exception exception)
        {
            return ResultObjectFactory.CreateFromException<StorageDeviceInfo[]>(exception);
        }
    }

    /// <summary>
    /// Erstellt MBR und FAT32 ausschliesslich auf einer vollstaendig leeren 64-MiB-Testplatte.
    /// </summary>
    public ResultErr InitializeTestDisk(string? deviceName)
    {
        var found = FindDevice(deviceName);
        if (!found.IsOK())
        {
            return found.Status;
        }

        try
        {
            IBlockDevice device = found.Value!;
            if (device.BlockSize != 512)
            {
                return Failure("UnsupportedSectorSize", "Die Testplatte benoetigt 512-Byte-Sektoren.");
            }

            if (device.BlockCount != TestDiskSize / device.BlockSize)
            {
                return Failure("UnsupportedDiskSize", "Die Testplatte muss genau 64 MiB gross sein.");
            }

            if (StorageManager.GetPartitions(device).Count != 0)
            {
                return Failure("ExistingPartitions", "Das Laufwerk hat bereits Partitionen; es wird nicht formatiert.");
            }

            var blank = CheckEmptyDisk(device);
            if (!blank.IsOK())
            {
                return blank;
            }

            var registration = RegisterFileSystem();
            if (!registration.IsOK())
            {
                return registration;
            }

            Mbr.Create(device);
            if (!PartitionManager.Create(device, PartitionStart, device.BlockCount - PartitionStart, 0x0C, Guid.Empty))
            {
                return Failure("PartitionCreationRefused", "Cosmos hat das Anlegen der Partition abgelehnt.");
            }

            StorageManager.RescanPartitions(device);
            var partitions = StorageManager.GetPartitions(device);
            if (partitions.Count != 1)
            {
                return Failure("UnexpectedPartitionCount", "Nach der Einrichtung wurde nicht genau eine Partition erkannt.");
            }

            var options = new FatFormatOptions
            {
                Type = FatType.Fat32,
                VolumeLabel = "ZEHOS"
            };
            if (!VfsManager.TryFormat(FileSystemName, partitions[0], options))
            {
                return Failure("FormatRefused", "Cosmos hat die FAT32-Formatierung abgelehnt.");
            }

            device.Flush();
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    /// <summary>
    /// Bindet die einzige Partition des benannten Laufwerks ein, ohne sie zu formatieren.
    /// </summary>
    public ResultErr Mount(string? deviceName)
    {
        var found = FindDevice(deviceName);
        if (!found.IsOK())
        {
            return found.Status;
        }

        try
        {
            var registration = RegisterFileSystem();
            if (!registration.IsOK())
            {
                return registration;
            }

            var partitions = StorageManager.GetPartitions(found.Value!);
            if (partitions.Count != 1)
            {
                return Failure("UnexpectedPartitionCount", "Zum Einbinden wird genau eine Datenpartition erwartet.");
            }

            if (!VfsManager.TryMount(FileSystemName, partitions[0], MountFlags.None, MountPoint, out _))
            {
                return Failure("MountRefused", "Cosmos hat das Einbinden unter /data abgelehnt.");
            }

            return ResultErr.CreateSuccess();
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    /// <summary>
    /// Schreibt ausstehende Dateisystemdaten und haengt /data aus.
    /// </summary>
    public ResultErr Unmount()
    {
        try
        {
            if (!VfsManager.TryUnmount(MountPoint))
            {
                return Failure("UnmountRefused", "Cosmos hat das Aushaengen von /data abgelehnt.");
            }

            return ResultErr.CreateSuccess();
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    private static ResultObject<IBlockDevice> FindDevice(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return ResultObjectFactory.CreateFailure<IBlockDevice>(ResultCode.InvalidArgument,
                "cause='InvalidDeviceName' message='Ein Laufwerksname muss angegeben werden.'");
        }

        var devices = StorageManager.Devices;
        foreach (IBlockDevice device in devices)
        {
            if (string.Equals(device.Name, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return ResultObjectFactory.CreateSuccess(device);
            }
        }

        return ResultObjectFactory.CreateFailure<IBlockDevice>(ResultCode.NotFound,
            $"cause='DeviceNotFound' message='Laufwerk nicht gefunden: {deviceName}'");
    }

    private ResultErr RegisterFileSystem()
    {
        if (registered)
        {
            return ResultErr.CreateSuccess();
        }

        if (!VfsManager.RegisterFileSystem(FileSystemName, new FatFileSystemType()))
        {
            return Failure("RegistrationRefused", "Cosmos hat die Registrierung von zehos-fat abgelehnt.");
        }

        registered = true;
        return ResultErr.CreateSuccess();
    }

    private static ResultErr CheckEmptyDisk(IBlockDevice device)
    {
        const ulong batchBlocks = 128;
        var buffer = new byte[batchBlocks * device.BlockSize];
        for (ulong block = 0; block < device.BlockCount; block += batchBlocks)
        {
            // Die festgelegte Testplattengroesse ist durch die Stapelgroesse teilbar.
            device.ReadBlock(block, batchBlocks, buffer);
            foreach (byte value in buffer)
            {
                if (value != 0)
                {
                    return Failure("DiskNotEmpty", "Das Laufwerk enthaelt Daten; es wird nicht formatiert.");
                }
            }
        }

        return ResultErr.CreateSuccess();
    }

    private static ResultErr Failure(string cause, string message)
    {
        return ResultErr.CreateFailure(ResultCode.Failure, $"cause='{cause}' message='{message}'");
    }
}
