using System;
using System.Collections.Generic;
using System.IO;
using ProjektZeh.Shared;
using System.Runtime.CompilerServices;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.Network;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime;

/// <summary>
/// Liest Systeminformationen fuer den Taskmanager ueber Cosmos. Nur lesend: es werden weder Laufwerke
/// eingebunden noch Netzwerkadapter konfiguriert.
/// </summary>
internal sealed class CosmosSystemInfo(IStorageService storage, string version)
{
    private const string PageAllocatorType = "Cosmos.Kernel.Core.Memory.PageAllocator, Cosmos.Kernel.Core";
    private const ulong PageSize = 4096;

    public TaskManagerSection[] Sections =>
    [
        new("Datentraeger", ReadStorage),
        new("Netzwerk", ReadNetwork),
        new("System", () => [version, "Speicherverwaltung: Cosmos-Seitenverwalter, Seiten zu 4 KiB",
            "Grafik: " + (Cosmos.Kernel.System.Graphics.Canvas.GetFullScreen().Name ?? "unbekannt")])
    ];

    // Der Seitenverwalter ist in Cosmos 3.0.94 intern; gelesen werden nur seine Zaehler.
    public static (ulong Total, ulong Free) PhysicalMemory() => (TotalPages(null) * PageSize, FreePages(null) * PageSize);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "TotalPageCount")]
    private static extern ref ulong TotalPages([UnsafeAccessorType(PageAllocatorType)] object? owner);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "get_FreePageCount")]
    private static extern ulong FreePages([UnsafeAccessorType(PageAllocatorType)] object? owner);

    public static ResultObject<(long TotalBytes, long AvailableBytes)> DiskSpace(string path)
    {
        string? mount = path;
        while (mount != null)
        {
            if (VfsManager.TryStatFs(mount, out var stats))
                return ResultObjectFactory.CreateSuccess(((long)(stats.Blocks * stats.BlockSize), (long)(stats.AvailableBlocks * stats.BlockSize)));
            mount = Path.GetDirectoryName(mount);
        }
        return ResultObjectFactory.CreateFailure<(long, long)>(ResultCode.NotFound, "Keine Dateisystemstatistik fuer diesen Pfad verfuegbar.");
    }

    public static string MemoryText()
    {
        var memory = PhysicalMemory();
        return $"Speicher (Bytes): gesamt {memory.Total}, belegt {memory.Total - memory.Free}, frei {memory.Free}";
    }
    private string[] ReadStorage()
    {
        var lines = new List<string>
        {
            VfsManager.TryGetMount("/data", out _) ? "/data: eingebunden" : "/data: nicht eingebunden (disk mount sata0)"
        };
        var devices = storage.GetDevices();
        if (!devices.IsOK()) lines.Add("Laufwerke nicht lesbar: " + devices.Status.Message);
        else if (devices.Value!.Length == 0) lines.Add("Keine Laufwerke erkannt.");
        else
            foreach (var device in devices.Value!)
                lines.Add($"{device.Name}: {device.SizeBytes / 1048576} MiB, {device.PartitionCount} Partition(en)");
        return [.. lines];
    }

    private static string[] ReadNetwork()
    {
        if (!NetworkManager.IsEnabled) return ["Netzwerkunterstuetzung ist in diesem Build deaktiviert."];
        if (!NetworkManager.IsInitialized) return ["Cosmos-Netzwerkdienst ist nicht initialisiert.", "Internet: nicht verfuegbar"];
        var lines = new List<string> { $"Erkannte Adapter: {NetworkManager.DeviceCount}" };
        for (int index = 0; index < NetworkManager.DeviceCount; index++)
        {
            var adapter = NetworkManager.GetAdapter(index);
            if (!adapter.IsValid) continue;
            lines.Add($"{adapter.Name ?? "Adapter " + index}: MAC {adapter.MacAddress?.ToString() ?? "-"}");
            lines.Add($"  Verbindung: {(adapter.LinkUp ? "ja" : "nein")}, bereit: {(adapter.Ready ? "ja" : "nein")}");
            lines.Add($"  IPv4: {adapter.IPConfig?.Address.ToString() ?? "keine Adresse"}");
        }
        lines.Add("ZehOS startet noch keinen DHCP-/Internetdienst.");
        return [.. lines];
    }
}
