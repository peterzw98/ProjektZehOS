using System;
using System.Collections.Generic;
using Cosmos.Kernel.System;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.Timers;
using ProjektZeh.Shared;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Virtual;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Runtime;

/// <summary>
/// Erzeuger der /proc- und /sys-Dateien im Kernel (Etappe 5b, Paket E5b.3): liest je Aufruf Cosmos-Werte als Kopien und
/// uebergibt sie an <see cref="SystemInfoSnapshot"/>, das Grenzen, Maskierung und Format festlegt. Quellen laut Matrix
/// docs/superpowers/specs/2026-11-17-system-files-inventory.md (Cosmos 3.0.97).
/// </summary>
internal sealed class CosmosSystemFiles(IStorageService storage, string version, Func<long> elapsedMilliseconds, SystemLog log)
{
    private const ulong PageSize = 4096;

    public ResultObject<byte[]> Produce(string path) => path switch
    {
        "/proc/version" => SystemInfoSnapshot.Version(version),
        // Ohne Zeitgeber ist die Stoppuhr nicht belegt (Matrix: TimerManager.IsInitialized).
        "/proc/uptime" => SystemInfoSnapshot.Uptime(TimerManager.IsInitialized ? elapsedMilliseconds() : null),
        "/proc/meminfo" => Memory(),
        "/proc/mounts" => SystemInfoSnapshot.Mounts(Mounts()),
        "/proc/log" => SystemInfoSnapshot.Log(log.GetEntries()),
        "/sys/kernel/components" => SystemInfoSnapshot.Components(Features(), Drivers()),
        "/sys/devices" => SystemInfoSnapshot.Devices(Devices()),
        "/sys/block" => Blocks(),
        _ => ResultObjectFactory.CreateFailure<byte[]>(ResultCode.NotFound, "Keine Systemdatei."),
    };

    private static ResultObject<byte[]> Memory()
    {
        var memory = CosmosSystemInfo.PhysicalMemory();
        return SystemInfoSnapshot.Memory(PageSize, memory.Total / PageSize, memory.Free / PageSize);
    }

    private static List<SystemInfoMount> Mounts()
    {
        // Cosmos ersetzt das Array bei Mountaenderungen; einmal lesen ergibt einen in sich gleichen Stand.
        var mounts = VfsManager.Mounts;
        var result = new List<SystemInfoMount>(Math.Min(mounts.Count, SystemInfoSnapshot.MaxRecords + 1));
        for (int index = 0; index < mounts.Count && result.Count <= SystemInfoSnapshot.MaxRecords; index++)
            result.Add(new(mounts[index].Name, mounts[index].Source, mounts[index].MountPoint, mounts[index].IsReadOnly));
        return result;
    }

    private static List<SystemInfoComponent> Features() =>
    [
        new("interrupts", KernelFeatures.Interrupts), new("uart", KernelFeatures.Uart), new("pci", KernelFeatures.Pci),
        new("timer", KernelFeatures.Timer), new("keyboard", KernelFeatures.Keyboard), new("mouse", KernelFeatures.Mouse),
        new("network", KernelFeatures.Network), new("scheduler", KernelFeatures.Scheduler), new("graphics", KernelFeatures.Graphics),
        new("storage", KernelFeatures.Storage), new("fat", KernelFeatures.Fat), new("usb", KernelFeatures.Usb),
        new("audio", KernelFeatures.Audio), new("finalizer", KernelFeatures.FinalizerThread),
    ];

    // Hoechstens MaxRecords + 1 lesen: mehr fuehrt im Snapshot ohnehin zum Fehler, ohne grosse Listen anzulegen.
    private static List<SystemInfoDriver> Drivers()
    {
        var result = new List<SystemInfoDriver>();
        for (int index = 0; index < DriverDiagnostics.DriverCount && result.Count <= SystemInfoSnapshot.MaxRecords; index++)
            if (DriverDiagnostics.TryGetDriver(index, out var driver)) result.Add(new(driver.Name, driver.Priority));
        return result;
    }

    /// <summary>Nur Pfad, Bus, Treiber und Zustand; LastFault, Offer-Gruende und Rohadressen bleiben draussen (Matrix).</summary>
    private static List<SystemInfoDevice> Devices()
    {
        var result = new List<SystemInfoDevice>();
        for (int index = 0; index < DriverDiagnostics.NodeCount && result.Count <= SystemInfoSnapshot.MaxRecords; index++)
            if (DriverDiagnostics.TryGetNode(index, out var node))
                result.Add(new(node.Path, node.BusName, node.DriverName ?? "-", node.State.ToString()));
        return result;
    }

    private ResultObject<byte[]> Blocks()
    {
        var devices = storage.GetDevices();
        if (!devices.IsOK()) return ResultObjectFactory.CreateFailure<byte[]>(devices.Status);
        var blocks = new List<SystemInfoBlock>(devices.Value!.Length);
        foreach (var device in devices.Value) blocks.Add(new(device.Name, device.SizeBytes, device.PartitionCount));
        return SystemInfoSnapshot.Blocks(blocks);
    }

    /// <summary>Typquelle fuer FileService (Spec §0.8): nur was Cosmos als regulaere Datei meldet; ohne Stat-Ergebnis ablehnen.</summary>
    public static bool IsRegularFile(string path) => VfsManager.TryStat(path, out var stat) && stat.IsRegularFile;
}
