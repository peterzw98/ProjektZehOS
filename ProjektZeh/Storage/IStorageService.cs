using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

/// <summary>
/// Einrichtung und Einbindung des virtuellen Datenlaufwerks.
/// </summary>
internal interface IStorageService
{
    ResultObject<StorageDeviceInfo[]> GetDevices();
    ResultErr InitializeTestDisk(string? deviceName, bool confirmLarge, bool confirmOverwrite);
    ResultErr Mount(string? deviceName);
    /// <summary>Boot: bindet nach DataDiskSelector.Decide den Bestand auf sata0 oder genau eine ZEHOSDATA-Platte unter /data ein.</summary>
    ResultErr MountData(out string? setupBlockedReason);
    /// <summary>Wechselt ausdruecklich auf die ZEHOSDATA-Partition des benannten Laufwerks.</summary>
    ResultErr UseDataDisk(string? deviceName);
    ResultErr Unmount();
}
