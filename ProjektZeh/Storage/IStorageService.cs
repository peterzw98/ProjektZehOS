using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

/// <summary>
/// Einrichtung und Einbindung des virtuellen Datenlaufwerks.
/// </summary>
internal interface IStorageService
{
    ResultObject<StorageDeviceInfo[]> GetDevices();
    ResultErr InitializeTestDisk(string? deviceName);
    ResultErr Mount(string? deviceName);
    ResultErr Unmount();
}
