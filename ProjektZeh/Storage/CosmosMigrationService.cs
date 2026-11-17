#if ZEHOS_ROOT_MIGRATION
using System;
using System.IO;
using System.Threading;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Ext2;
using ProjektZeh.Storage.Migration;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Storage;

/// <summary>
/// Cosmos-Anbindung von <c>system migrate</c> (Etappe 4.5, Spec storage-etappe4-design §3), nur mit -p:ZehOSRootMigration=true.
/// Das Ziel wird vor dem ersten Schreibzugriff ausschliesslich ueber Lese-Wrapper geprueft (ESP-Identitaet Healthy, Root-Marker
/// fresh/migrating); erst dann wird Root unter /mnt/target beschreibbar eingehaengt und per s_state als offen markiert.
/// Die Quelle /data liest die Migration ueber <see cref="MigrationSourceSwap"/> nur lesend.
/// </summary>
internal sealed class CosmosMigrationService(CosmosStorageService storage) : IMigrationService
{
    internal const string TargetMount = "/mnt/target";
    private const string TargetEfiMount = "/mnt/target-efi";
    private const string SourceMount = "/data";
    private static readonly CosmosSwitchableFileSystem readOnly = new("zehos-migration-ro");
    private static readonly CosmosSwitchableFileSystem writable = new("zehos-migration-rw");

    /// <summary>
    /// Gesetzt, wenn /data nach einer Migration nicht zurueckgehaengt werden konnte; der Quelllease bleibt dann bis zum Neustart
    /// gehalten. Die Energieaktion braucht das, um den empfohlenen Neustart trotz gehaltener Sperre zuzulassen.
    /// </summary>
    internal static string? StrandedReason { get; private set; }

    public ResultObject<MigrationReport> Run(string diskName, Action<string> log, CancellationToken cancellation)
    {
        try
        {
            return RunChecked(diskName, log, cancellation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure("MigrationIoFailed", exception.Message);
        }
        // Treiberfehler anderer Art erst nach Close/Unmount im finally; die Hintergrundaufgabe meldet sie als Ergebnis (Review P3-c).
        catch (Exception exception)
        {
            log($"Fehler: {exception.GetType().Name}: {exception.Message}");
            return Failure("MigrationFailed", exception.GetType().Name + ": " + exception.Message);
        }
    }

    private ResultObject<MigrationReport> RunChecked(string diskName, Action<string> log, CancellationToken cancellation)
    {
        Partition? source = CosmosStorageService.WritableDataPartition();
        if (source is null) return Failure("SourceMissing", "/data ist nicht beschreibbar eingehaengt.");
        IBlockDevice? device = FindDevice(diskName);
        if (device is null) return Failure("DeviceNotFound", "Laufwerk nicht gefunden: " + diskName);
        if (ReferenceEquals(device, source.Host)) return Failure("TargetIsSource", "Das Ziel ist das Laufwerk mit /data.");
        foreach (Partition partition in StorageManager.GetPartitions(device))
            if (VfsManager.IsPartitionMounted(partition)) return Failure("TargetMounted", "Eine Partition des Ziels ist eingehaengt.");

        if (!Gpt.IsGpt(device)) return Failure("TargetNotGpt", "Das Ziel hat kein ZehOS-Layout (keine GPT).");
        var table = GptLayout.Verify(device.BlockSize, device.BlockCount, device.ReadBlock);
        if (!table.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(table.Status);
        var root = CheckIdentity(device, table.Value!);
        if (!root.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(root.Status);
        var state = CheckRootMarker(root.Value!);
        if (!state.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(state.Status);
        log($"Ziel {device.Name}: Identitaet Healthy, Root-Marker {(state.Value == LayoutState.Fresh ? "fresh" : "migrating")}; Root wird unter {TargetMount} eingehaengt.");
        log("Hinweis: /data ist waehrend der Migration nur lesbar und beim Umhaengen kurz nicht erreichbar.");
        return MigrateMounted(root.Value!, source, log, cancellation);
    }

    /// <summary>ESP nur lesend: Identitaet muss Healthy sein; der Installationsmarker nennt die Root-Partition.</summary>
    private static ResultObject<Partition> CheckIdentity(IBlockDevice device, GptTable table)
    {
        Partition? esp = CosmosGptPartitions.Find(device, table, GptLayout.EspType, null);
        if (esp is null) return Failure<Partition>("TargetNoEsp", "Das Ziel hat keine ESP.");
        var reader = new CosmosReadOnlyFileSystem(new FatFileSystemType(new CosmosReadOnlyBlockDevice(esp)));
        if (!readOnly.TryMount(reader, esp, MountFlags.ReadOnly, TargetEfiMount))
            return Failure<Partition>("TargetEspUnreadable", "ESP des Ziels nicht lesbar.");
        IdentityResult? identity;
        string? text;
        try
        {
            identity = CosmosInstalledIdentity.Evaluate(TargetEfiMount);
            text = CosmosInstalledIdentity.ReadMarker(TargetEfiMount);
        }
        finally
        {
            // Ohne Aushaengen bliebe ein fremder Mount stehen; dann lieber gar nicht weiter.
            if (!VfsManager.TryUnmount(TargetEfiMount)) throw new IOException("cause='TargetEspBusy' message='ESP des Ziels liess sich nicht aushaengen.'");
        }

        if (identity is not { State: InstalledState.Healthy })
            return Failure<Partition>("TargetNotHealthy", identity?.Reason ?? "Keine ZehOS-Installation auf dem Ziel.");
        var marker = InstallMarker.Parse(text ?? string.Empty);
        if (!marker.IsOK()) return ResultObjectFactory.CreateFailure<Partition>(marker.Status);
        Partition? root = CosmosGptPartitions.Find(device, table, GptLayout.LinuxFileSystemType, marker.Value!.RootGuid);
        return root is null ? Failure<Partition>("TargetNoRoot", "Root-Partition des Ziels fehlt.") : ResultObjectFactory.CreateSuccess(root);
    }

    /// <summary>Root nur lesend: ready wird abgelehnt, bevor ein Schreibmount Superblock oder s_state anfasst.</summary>
    private static ResultObject<LayoutState> CheckRootMarker(Partition root)
    {
        if (!readOnly.TryMount(new CosmosReadOnlyExt2FileSystem(root), root, MountFlags.ReadOnly, TargetMount))
            return Failure<LayoutState>("TargetRootUnreadable", "Root des Ziels nicht lesbar.");
        ResultObject<LayoutMarker>? marker;
        try
        {
            marker = LayoutMarker.ReadFile(TargetMount + LayoutMarker.Path);
        }
        finally
        {
            if (!VfsManager.TryUnmount(TargetMount)) throw new IOException("cause='TargetRootBusy' message='Root des Ziels liess sich nicht aushaengen.'");
        }

        if (marker is null) return Failure<LayoutState>("TargetMarkerMissing", "Root-Marker des Ziels fehlt.");
        if (!marker.IsOK()) return ResultObjectFactory.CreateFailure<LayoutState>(marker.Status);
        if (marker.Value!.State == LayoutState.Ready) return Failure<LayoutState>("TargetReady", "Ziel ist bereits eingerichtet.");
        return ResultObjectFactory.CreateSuccess(marker.Value.State);
    }

    private ResultObject<MigrationReport> MigrateMounted(Partition root, Partition source, Action<string> log, CancellationToken cancellation)
    {
        if (!writable.TryMount(new Ext2FileSystemType(root), root, MountFlags.None, TargetMount) || !VfsManager.TryGetMount(TargetMount, out VfsMount? mount))
            return Failure("TargetMountFailed", "Root des Ziels nicht schreibbar einhaengbar.");
        var volume = new CosmosRootVolume(root, mount);
        // s_state "nicht sauber" vor dem ersten Dateischreiber (Spec Abschnitt 2); ein Abbruch hinterlaesst damit ein unklares Ziel.
        var opened = RootVolumeState.Open(volume);
        if (!opened.IsOK())
        {
            if (!VfsManager.TryUnmount(TargetMount)) log("Fehler: Ziel liess sich nicht aushaengen.");
            return ResultObjectFactory.CreateFailure<MigrationReport>(opened);
        }

        var swap = new MigrationSourceSwap(AppStorageGate.TryLockSource, () => AppStorageGate.BusyDescription,
            CosmosStorageService.UnmountData, () => CosmosStorageService.MountDataReadOnly(source),
            CosmosStorageService.UnmountData, () => storage.MountDataWritable(source), log);
        var target = new FileMigrationTarget(TargetMount, () => FreeSpace(stats => stats.AvailableBlocks * stats.BlockSize),
            () => FreeSpace(stats => stats.FreeInodes),
            () => volume.Sync() ? ResultErr.CreateSuccess() : ResultErr.CreateFailure(ResultCode.Failure, "cause='SyncFailed' message='Sync des Ziels fehlgeschlagen.'"),
            CosmosPathGuard.For(TargetMount));
        ResultObject<MigrationReport> result;
        ResultErr closed;
        bool unmounted;
        try
        {
            result = LayoutMigration.Run(SourceMount, target, swap.Acquire, log, cancellation: cancellation);
        }
        finally
        {
            closed = RootVolumeState.Close(volume);
            unmounted = VfsManager.TryUnmount(TargetMount);
        }

        return Combine(result, closed, unmounted, swap.RestoreFailure, log);
    }

    /// <summary>Nachlaufende Fehler machen ein erfolgreiches Kopieren nicht unsichtbar: Ergebnis und Folgezustand in einer Meldung.</summary>
    private static ResultObject<MigrationReport> Combine(ResultObject<MigrationReport> result, ResultErr closed, bool unmounted,
        string? restoreFailure, Action<string> log)
    {
        string done = result.IsOK() ? $"Migration kopiert ({result.Value!.Files} Dateien, Ziel ready), aber " : string.Empty;
        if (!closed.IsOK()) log("Fehler: Ziel nicht sauber geschlossen: " + closed.Message);
        if (!unmounted) log("Fehler: Ziel liess sich nicht aushaengen.");
        if (restoreFailure != null)
        {
            StrandedReason = restoreFailure;
            return Failure("SourceRestoreFailed", done + restoreFailure + (result.IsOK() ? string.Empty : " Migration: " + result.Status.Message));
        }
        if (!result.IsOK()) return result;
        if (!closed.IsOK()) return Failure("TargetCloseFailed", done + "das Ziel ist nicht sauber geschlossen: " + closed.Message);
        if (!unmounted) return Failure("TargetUnmountFailed", done + "das Ziel liess sich nicht aushaengen.");
        return result;
    }

    private static ResultObject<long> FreeSpace(Func<VfsStatFs, ulong> select)
    {
        if (!VfsManager.TryStatFs(TargetMount, out VfsStatFs stats))
            return Failure<long>("TargetStatFailed", "Keine Dateisystemstatistik fuer das Ziel.");
        ulong value = select(stats);
        return ResultObjectFactory.CreateSuccess(value > long.MaxValue ? long.MaxValue : (long)value);
    }

    private static IBlockDevice? FindDevice(string name)
    {
        foreach (IBlockDevice device in StorageManager.Devices)
            if (string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase)) return device;
        return null;
    }

    private static ResultObject<MigrationReport> Failure(string cause, string message) => Failure<MigrationReport>(cause, message);

    private static ResultObject<T> Failure<T>(string cause, string message) =>
        ResultObjectFactory.CreateFailure<T>(ResultCode.Failure, $"cause='{cause}' message='{message}'");
}
#endif
