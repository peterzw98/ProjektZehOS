using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Erstellt MBR und FAT32 ausschliesslich auf einer vollstaendig leeren Platte von 64 MiB bis 128 GiB; die Partition erhaelt das Label ZEHOSDATA.
    /// </summary>
    public ResultErr InitializeTestDisk(string? deviceName, bool confirmLarge, bool confirmOverwrite)
    {
        var found = FindDevice(deviceName);
        if (!found.IsOK())
        {
            return found.Status;
        }

        try
        {
            IBlockDevice device = found.Value!;
            var size = DataDiskSelector.CheckInitSize(device.BlockSize, device.BlockCount);
            if (!size.IsOK())
            {
                return size;
            }

            if (StorageManager.GetPartitions(device).Count != 0)
            {
                return Failure("ExistingPartitions", "Das Laufwerk hat bereits Partitionen; es wird nicht formatiert.");
            }

            var overwrite = DataDiskSelector.CheckOverwrite(ReadCandidates(), VfsManager.TryGetMount(MountPoint, out _), confirmOverwrite);
            if (!overwrite.IsOK())
            {
                return overwrite;
            }

            var clock = Stopwatch.StartNew();
            Progress("init start", clock);
            var blank = DiskEmptyCheck.Check(device.BlockSize, device.BlockCount, confirmLarge, device.ReadBlock, text => Progress(text, clock));
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
            Progress("mbr ok", clock);
            if (!PartitionManager.Create(device, PartitionStart, device.BlockCount - PartitionStart, 0x0C, Guid.Empty))
            {
                return Failure("PartitionCreationRefused", "Cosmos hat das Anlegen der Partition abgelehnt.");
            }

            Progress("partition ok", clock);
            StorageManager.RescanPartitions(device);
            var partitions = StorageManager.GetPartitions(device);
            if (partitions.Count != 1)
            {
                return Failure("UnexpectedPartitionCount", "Nach der Einrichtung wurde nicht genau eine Partition erkannt.");
            }

            Progress("format start", clock);
            var options = new FatFormatOptions
            {
                Type = FatType.Fat32,
                VolumeLabel = DataDiskSelector.DataLabel
            };
            if (!VfsManager.TryFormat(FileSystemName, partitions[0], options))
            {
                return Failure("FormatRefused", "Cosmos hat die FAT32-Formatierung abgelehnt.");
            }

            device.Flush();
            Progress("format ok", clock);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    // Fortschritt seriell: die Shell blockiert bei grossen Platten, der Bildschirm zeigt erst am Ende etwas.
    // t = Millisekunden seit Beginn von disk init: je Befehl gerechnet trennt das langsames Lesen von einem Hang.
    private static void Progress(string text, Stopwatch clock) =>
        Cosmos.Kernel.System.Diagnostics.Log.WriteString($"STORAGE {text} t={clock.ElapsedMilliseconds}ms\n");

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
            // Reihenfolge der Fehler wie bisher: AlreadyMounted, Registrierung, Partitionsanzahl, Mount.
            var prepared = PrepareMount();
            if (!prepared.IsOK())
            {
                return prepared;
            }

            if (StorageManager.GetPartitions(found.Value!).Count != 1)
            {
                return Failure("UnexpectedPartitionCount", "Zum Einbinden wird genau eine Datenpartition erwartet.");
            }

            return MountPartition(found.Value!, 0);
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    /// <summary>
    /// Boot-Einbindung: nur ein Bestand auf sata0 wird eingebunden. Eine ZEHOSDATA-Platte wird weder bevorzugt noch automatisch
    /// verwendet (disk use); bei Mehrdeutigkeit oder einem unbrauchbaren Bestand wird nichts eingebunden und seriell gemeldet.
    /// </summary>
    public ResultErr MountData()
    {
        try
        {
            var decision = DataDiskSelector.Decide(ReadCandidates());
            if (decision.Hint != null)
            {
                Note("Hinweis: " + decision.Hint);
            }

            if (decision.Action == DataDiskAction.Mount)
            {
                var found = FindDevice(decision.DeviceName);
                if (!found.IsOK())
                {
                    return found.Status;
                }

                var prepared = PrepareMount();
                return prepared.IsOK() ? MountPartition(found.Value!, decision.PartitionIndex) : prepared;
            }

            if (decision.Action != DataDiskAction.NoDataDisk)
            {
                // Ohne Mount gibt es keine Kontodatei und damit keine Admin-Ersteinrichtung (AccountService.Load).
                Note("Fehler: " + decision.Message + " Keine Ersteinrichtung.");
            }

            return ResultErr.CreateFailure(ResultCode.Failure, decision.Message);
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    /// <summary>
    /// Wechselt ausdruecklich auf die ZEHOSDATA-Partition des Laufwerks; ein eingebundenes /data wird vorher ausgehaengt.
    /// </summary>
    public ResultErr UseDataDisk(string? deviceName)
    {
        var found = FindDevice(deviceName);
        if (!found.IsOK())
        {
            return found.Status;
        }

        try
        {
            IBlockDevice device = found.Value!;
            var index = DataDiskSelector.FindLabelled(CosmosDataDisk.Describe(device));
            if (!index.IsOK())
            {
                return index.Status;
            }

            if (VfsManager.TryGetMount(MountPoint, out _))
            {
                if (!VfsManager.TryUnmount(MountPoint))
                {
                    return Failure("UnmountRefused", "Cosmos hat das Aushaengen von /data abgelehnt.");
                }

                Note("use: bisheriges /data ausgehaengt");
            }

            var prepared = PrepareMount();
            var mounted = prepared.IsOK() ? MountPartition(device, index.Value) : prepared;
            if (mounted.IsOK())
            {
                Note($"use ok {device.Name}");
            }

            return mounted;
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    private static List<DataDiskCandidate> ReadCandidates()
    {
        var candidates = new List<DataDiskCandidate>();
        foreach (IBlockDevice device in StorageManager.Devices)
        {
            // Ein unlesbares Geraet (CD/ATAPI, Lesefehler) darf die uebrigen nicht verhindern.
            try
            {
                candidates.Add(CosmosDataDisk.Describe(device));
            }
            catch (Exception exception)
            {
                Note($"{device.Name} uebersprungen: {exception.Message}");
            }
        }

        return candidates;
    }

    private static void Note(string text) => Cosmos.Kernel.System.Diagnostics.Log.WriteString("STORAGE " + text + "\n");

    private ResultErr PrepareMount()
    {
        if (VfsManager.TryGetMount(MountPoint, out _))
        {
            return Failure("AlreadyMounted", "Unter /data ist bereits ein Laufwerk eingebunden.");
        }

        return RegisterFileSystem();
    }

    private static ResultErr MountPartition(IBlockDevice device, int partitionIndex)
    {
        if (!VfsManager.TryMount(FileSystemName, StorageManager.GetPartitions(device)[partitionIndex], MountFlags.None, MountPoint, out _))
        {
            return Failure("MountRefused", "Cosmos hat das Einbinden unter /data abgelehnt.");
        }

        return ResultErr.CreateSuccess();
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

    private static ResultErr Failure(string cause, string message)
    {
        return ResultErr.CreateFailure(ResultCode.Failure, $"cause='{cause}' message='{message}'");
    }
}
