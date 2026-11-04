namespace ProjektZeh.Storage;

/// <summary>
/// Hardwareunabhaengige Beschreibung eines erkannten Laufwerks.
/// </summary>
internal sealed record StorageDeviceInfo(string Name, ulong SizeBytes, int PartitionCount);
