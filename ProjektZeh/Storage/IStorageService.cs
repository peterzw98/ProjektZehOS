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
    /// <summary>Boot: bindet einen Bestand auf sata0 unter /data ein; eine ZEHOSDATA-Platte nie automatisch.</summary>
    ResultErr MountData();
    /// <summary>Wechselt ausdruecklich auf die ZEHOSDATA-Partition des benannten Laufwerks.</summary>
    ResultErr UseDataDisk(string? deviceName);
    ResultErr Unmount();
}
