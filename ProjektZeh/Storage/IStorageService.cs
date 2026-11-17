using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

/// <summary>
/// Einrichtung und Einbindung des virtuellen Datenlaufwerks.
/// </summary>
internal interface IStorageService
{
    ResultObject<StorageDeviceInfo[]> GetDevices();
    ResultErr InitializeTestDisk(string? deviceName, bool confirmLarge);
    ResultErr Mount(string? deviceName);
    /// <summary>Waehlt die Datenpartition per Label ZEHOSDATA (Rueckfall sata0) und bindet sie unter /data ein.</summary>
    ResultErr MountData();
    ResultErr Unmount();
}
