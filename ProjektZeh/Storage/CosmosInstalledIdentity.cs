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
/// eingehaengtes /data bewertet wird. Ohne /data gibt es nichts zu bewerten und keine Mutation (null).
/// </summary>
internal static class CosmosInstalledIdentity
{
    private const string DataMount = "/data";

    public static IdentityResult? EvaluateData()
    {
        Partition? partition = null;
        foreach (VfsMount mount in VfsManager.Mounts)
            if (string.Equals(mount.MountPoint, DataMount, StringComparison.Ordinal)) partition = mount.Partition;
        if (partition is null) return null;

        IBlockDevice host = partition.Host;
        ResultObject<GptTable>? table;
        try
        {
            table = Gpt.IsGpt(host) ? GptLayout.Verify(host.BlockSize, host.BlockCount, host.ReadBlock) : null;
        }
        catch (Exception exception) when (exception is IOException or ArgumentOutOfRangeException)
        {
            table = GptLayout.Failure("TableIoError", "GPT nicht lesbar: " + exception.Message);
        }
        return InstalledIdentity.Evaluate(ReadMarker(), table, entry => RootGate(host, entry));
    }

    /// <summary>Fuer AccountService.MutationBlockedReason.</summary>
    public static string? MutationBlockedReason() => EvaluateData()?.BlockedReason;

    private static string? ReadMarker()
    {
        string path = Path.Combine(DataMount, InstallMarker.RelativePath);
        try
        {
            if (!File.Exists(path)) return null;
            // Groessere Dateien gar nicht erst lesen; ein leerer Text ist fuer Parse ungueltig (=> Unclear).
            if (new FileInfo(path).Length > InstallMarker.MaximumBytes) return string.Empty;
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>ext2-Superblock (Byte 1024..2047 der Partition) durch das Feature-Gate; IOException geht an Evaluate.</summary>
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
