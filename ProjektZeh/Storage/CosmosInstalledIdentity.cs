using System;
using System.IO;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.Storage;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Storage;

/// <summary>
/// Cosmos-Anbindung von InstalledIdentity (7a Schritt 3b): liest bei jedem Aufruf die Platte hinter dem aktuell als /data
/// eingehaengten Volume neu (Marker auf der ESP, GPT ueber GptLayout.Verify, Root-Superblock), damit auch ein per disk use
/// eingehaengtes /data bewertet wird. Ohne /data gibt es nichts zu bewerten (null, nicht gesperrt): Ersteinrichtung und
/// Recovery scheitern ohne eingehaengtes /data ohnehin. Jede unerwartete Ausnahme ergibt Unclear (fail-closed).
/// </summary>
internal static class CosmosInstalledIdentity
{
    private const string DataMount = "/data";

    public static IdentityResult? EvaluateData() => Evaluate(DataMount);

    /// <summary>Wie <see cref="EvaluateData"/>, fuer die unter <paramref name="mountPoint"/> eingehaengte ESP (4.3: /boot/efi).</summary>
    public static IdentityResult? Evaluate(string mountPoint)
    {
        try
        {
            // Mounttabelle, GPT, Marker und Root-Superblock als ein kurzer Abschnitt: kleine Lesezugriffe, ein konsistenter Stand.
            return StorageLock.Run(() => EvaluateMountedStorageLocked(mountPoint));
        }
        catch (Exception exception)
        {
            return new(InstalledState.Unclear, $"cause='IdentityCheckFailed' message='Identitaetspruefung fehlgeschlagen: {exception.GetType().Name}. Keine Ersteinrichtung.'");
        }
    }

    private static IdentityResult? EvaluateMountedStorageLocked(string mountPoint)
    {
        Partition? partition = null;
        foreach (VfsMount mount in VfsManager.Mounts)
            if (string.Equals(mount.MountPoint, mountPoint, StringComparison.Ordinal)) partition = mount.Partition;
        if (partition is null) return null;

        IBlockDevice host = partition.Host;
        ResultObject<GptTable>? table;
        try
        {
            table = Gpt.IsGpt(host) ? GptLayout.Verify(host.BlockSize, host.BlockCount, host.ReadBlock) : null;
        }
        catch (Exception exception) when (exception is IOException or ArgumentOutOfRangeException)
        {
            table = GptLayout.Failure("TableIoError", "GPT nicht lesbar: " + exception.GetType().Name);
        }
        return InstalledIdentity.Evaluate(ReadMarkerStorageLocked(mountPoint), table, entry => RootGate(host, entry));
    }

    /// <summary>Fuer AccountService.MutationBlockedReason.</summary>
    public static string? MutationBlockedReason() => EvaluateData()?.BlockedReason;

    /// <summary>Markertext der ESP unter <paramref name="mountPoint"/>; null = Datei fehlt, leer = unlesbar (ergibt Unclear).</summary>
    internal static string? ReadMarker(string mountPoint) => StorageLock.Run(() => ReadMarkerStorageLocked(mountPoint));

    private static string? ReadMarkerStorageLocked(string mountPoint)
    {
        // Rohes System.IO ohne Pfadwaechter: die ESP ist FAT und kennt keine Symlinks.
        string path = Path.Combine(mountPoint, InstallMarker.RelativePath);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            // Groessere Dateien gar nicht erst lesen; ein leerer Text ist fuer Parse ungueltig (=> Unclear).
            if (stream.Length > InstallMarker.MaximumBytes) return string.Empty;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nur "fehlt" darf Legacy ergeben; jeder andere Lesefehler ist unklar.
            return string.Empty;
        }
    }

    /// <summary>
    /// ext2-Superblock (Byte 1024..2047 der Partition) durch das Feature-Gate; IOException geht an Evaluate. Laeuft nur aus
    /// <see cref="EvaluateMountedStorageLocked"/>, also unter der Speicher-Sperre.
    /// </summary>
    private static RootCheck RootGate(IBlockDevice host, GptEntry root)
    {
        var superblock = new byte[1024];
        try
        {
            host.ReadBlock(root.FirstLba + 1024 / host.BlockSize, 1024 / host.BlockSize, superblock);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new IOException("Root-Superblock ausserhalb der Platte.", exception);
        }
        return InstalledIdentity.CheckRoot(superblock);
    }
}
