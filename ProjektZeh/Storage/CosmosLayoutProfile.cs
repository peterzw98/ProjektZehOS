#if ZEHOS_ROOT_PROFILE
using System;
using System.IO;
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

/// <summary>Ergebnis der Profilwahl; RootVolume nur im schreibbaren Root-Fall (fuer s_state beim Herunterfahren).</summary>
internal sealed record CosmosLayoutSelection(LayoutDecision Decision, IRootVolume? RootVolume, Guid DiskGuid = default);

/// <summary>
/// Cosmos-Anbindung der Profilwahl (Etappe 4.3, Spec storage-etappe4-design Abschnitt 2, Schritte 0-4), nur mit
/// -p:ZehOSRootProfile=true. Ergibt sich kein Root-Fall, ist danach nichts eingehaengt und der Kernel nimmt die
/// Etappe-2/7a-Auswahl (ESP als /data); die Entscheidung liefert dann nur den zusaetzlichen Sperrgrund.
/// </summary>
internal static class CosmosLayoutProfile
{
    internal const string EfiMount = "/boot/efi";
    private const string SystemDevice = "sata0";
    private const string EfiFileSystem = "zehos-efi";
    private const string RootFileSystem = "zehos-root";
    private static bool efiRegistered;

    public static CosmosLayoutSelection Select(Action<string> note)
    {
        var legacy = new CosmosLayoutSelection(new LayoutDecision(LayoutOutcome.Legacy, false, false, null), null);
        // 0. Nur ein gueltiges 7a-Layout auf sata0 fuehrt zu /boot/efi; alles andere bleibt bei der bisherigen Auswahl.
        IBlockDevice? device = FindDevice();
        if (device is null || !Gpt.IsGpt(device)) return legacy;
        var table = GptLayout.Verify(device.BlockSize, device.BlockCount, device.ReadBlock);
        if (!table.IsOK()) return legacy;
        Partition? esp = CosmosGptPartitions.Find(device, table.Value!, GptLayout.EspType, null);
        if (esp is null) return legacy;

        // 1. ESP direkt unter /boot/efi, ESP-Marker lesen, Identitaet wie 7a.
        if (!MountEfi(esp)) return legacy;
        IdentityResult? identity = CosmosInstalledIdentity.Evaluate(EfiMount);
        Partition? root = null;
        if (identity is { State: InstalledState.Healthy or InstalledState.RootUnclean })
        {
            var marker = InstallMarker.Parse(CosmosInstalledIdentity.ReadMarker(EfiMount) ?? string.Empty);
            if (marker.IsOK()) root = CosmosGptPartitions.Find(device, table.Value!, GptLayout.LinuxFileSystemType, marker.Value!.RootGuid);
        }

        // 2. Root nur lesend pruefen: Root-Marker und Kontendatei.
        ResultObject<LayoutMarker>? layout = null;
        bool hasAccounts = false;
        var readOnly = root is null ? null : new CosmosReadOnlyExt2FileSystem(root);
        if (readOnly != null && readOnly.TryMount(out _))
        {
            try
            {
                string probe = CosmosReadOnlyExt2FileSystem.MountPoint;
                layout = LayoutMarker.ReadFile(probe + LayoutMarker.Path);
                hasAccounts = File.Exists(probe + SystemPaths.Root.SecurityDirectory + "/" + AccountService.MetadataFile);
            }
            finally
            {
                // 3. RO-Zugriff schliessen, bevor geschrieben eingehaengt wird.
                VfsManager.TryUnmount(CosmosReadOnlyExt2FileSystem.MountPoint);
            }
        }
        else if (identity is { State: InstalledState.Healthy or InstalledState.RootUnclean })
        {
            identity = new(InstalledState.Unclear, "cause='RootNotReadable' message='Root-Partition nicht lesbar. Keine Ersteinrichtung.'");
        }

        var decision = LayoutProfile.Decide(identity, layout, hasAccounts, table.Value!.DiskGuid);
        note($"Profil: {decision.Outcome}" + (decision.BlockedReason is null ? string.Empty : " " + decision.BlockedReason));
        switch (decision.Outcome)
        {
            case LayoutOutcome.Root:
                return MountRootWritable(root!, decision, table.Value.DiskGuid, note);
            case LayoutOutcome.RootReadOnly when readOnly!.TryMount("/", out _):
                return new(decision, null);
            case LayoutOutcome.RootReadOnly:
                return FallBack(new(LayoutOutcome.Blocked, false, false, "cause='RootMountFailed' message='Root nicht lesbar einhaengbar. Keine Ersteinrichtung.'"));
            default:
                return FallBack(decision);
        }
    }

    /// <summary>s_state 1 beim Herunterfahren (Spec Abschnitt 2, Energieaktionen): erst /boot/efi aushaengen, dann Root schliessen.</summary>
    public static ResultErr Close(IRootVolume volume)
    {
        if (VfsManager.TryGetMount(EfiMount, out _) && !VfsManager.TryUnmount(EfiMount))
            return ResultErr.CreateFailure(ResultCode.Failure, "cause='EfiUnmountFailed' message='/boot/efi konnte nicht ausgehaengt werden.'");
        return RootVolumeState.Close(volume);
    }

    /// <summary>Root-Marker auf state=ready setzen (Temp, Rename, Sync); einmal nach dem ersten Speichern der Konten.</summary>
    public static ResultErr MarkReady(Guid diskGuid, IRootVolume volume)
    {
        try
        {
            string temporary = LayoutMarker.Path + ".tmp";
            File.WriteAllText(temporary, new LayoutMarker(LayoutState.Ready, diskGuid).Serialize());
            File.Move(temporary, LayoutMarker.Path, true);
            // Spec Etappe 4 §2: zweifacher Sync, dann Device.Flush (Cosmos schreibt den Superblock erst danach).
            for (int pass = 0; pass < 2; pass++)
                if (!volume.Sync()) return ResultErr.CreateFailure(ResultCode.Failure, "cause='SyncFailed' message='Root-Marker geschrieben, Sync fehlgeschlagen.'");
            volume.Flush();
            return ResultErr.CreateSuccess();
        }
        // Auch Rechte- und Pfadfehler aus File.Move duerfen nicht ueber accounts.Saved in den Speicherpfad laufen.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, $"cause='LayoutMarkerWrite' message='Root-Marker nicht geschrieben: {exception.Message}'");
        }
    }

    private static CosmosLayoutSelection MountRootWritable(Partition root, LayoutDecision decision, Guid diskGuid, Action<string> note)
    {
        if (!VfsManager.RegisterFileSystem(RootFileSystem, new Ext2FileSystemType(root))
            || !VfsManager.TryMount(RootFileSystem, "", MountFlags.None, "/", out VfsMount? mount))
            return FallBack(new(LayoutOutcome.Blocked, false, false, "cause='RootMountFailed' message='Root nicht schreibbar einhaengbar. Keine Ersteinrichtung.'"));
        var volume = new CosmosRootVolume(root, mount);
        // s_state 0 direkt nach dem Mount, vor jedem anderen Schreiber (Spec Abschnitt 2, Sauber-Kennzeichen).
        var opened = RootVolumeState.Open(volume);
        if (opened.IsOK()) return new(decision, volume, diskGuid);
        note("Root nicht als offen markierbar: " + opened.Message);
        VfsManager.TryUnmount("/");
        return FallBack(new(LayoutOutcome.Blocked, false, false, "cause='RootOpenFailed' message='s_state nicht setzbar. Keine Ersteinrichtung.'"));
    }

    /// <summary>4. Kein Root-Fall: /boot/efi aushaengen, damit die bisherige Auswahl die ESP unter /data einhaengt.</summary>
    private static CosmosLayoutSelection FallBack(LayoutDecision decision)
    {
        if (VfsManager.TryGetMount(EfiMount, out _)) VfsManager.TryUnmount(EfiMount);
        return new(decision, null);
    }

    private static bool MountEfi(Partition esp)
    {
        if (!efiRegistered)
        {
            if (!VfsManager.RegisterFileSystem(EfiFileSystem, new FatFileSystemType())) return false;
            efiRegistered = true;
        }

        return VfsManager.TryMount(EfiFileSystem, esp, MountFlags.None, EfiMount, out _);
    }

    private static IBlockDevice? FindDevice()
    {
        foreach (IBlockDevice device in StorageManager.Devices)
            if (string.Equals(device.Name, SystemDevice, StringComparison.Ordinal)) return device;
        return null;
    }
}
#endif
