using System;
using System.IO;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage.Partitioning;

internal enum InstalledState
{
    /// <summary>Kein Marker und keine Root-Partition: Altinstallation oder Datenplatte, Verhalten wie bisher.</summary>
    Legacy,
    Healthy,
    RootMissing,
    RootDamaged,
    /// <summary>Root nur wegen s_state nicht sauber (Absturz ohne sauberes Aushaengen): nur lesend, offline e2fsck noetig.</summary>
    RootUnclean,
    /// <summary>Identitaet nicht sicher bestimmbar; sperrt grundsaetzlich.</summary>
    Unclear,
}

/// <param name="Reason">cause/message fuer Protokoll und Sperrmeldung.</param>
internal sealed record IdentityResult(InstalledState State, string Reason)
{
    /// <summary>Sperrgrund fuer Ersteinrichtung und security recover, sonst null.</summary>
    public string? BlockedReason => State is InstalledState.RootMissing or InstalledState.RootDamaged or InstalledState.RootUnclean or InstalledState.Unclear ? Reason : null;
}

/// <summary>Ergebnis der Root-Superblockpruefung; Unclean trennt "nur s_state" von unbekanntem RoCompat (Etappe 4, §2).</summary>
internal enum RootCheck
{
    Ok,
    Unclean,
    ReadOnly,
    Reject,
}

/// <summary>
/// Identitaet des installierten Systems (Spec 7a, Abschnitt 5, Tabelle) als reine Funktion. Eingaben: Markertext der
/// eingebundenen ESP (null = Datei fehlt), Ergebnis von GptLayout.Verify (null = Platte ohne GPT) und die Root-Pruefung,
/// die nur fuer den im Marker genannten Eintrag aufgerufen wird (einziger Plattenzugriff).
/// </summary>
internal static class InstalledIdentity
{
    /// <summary>
    /// Feature-Gate mit getrenntem Ausgang fuer s_state: meldet das Gate ReadOnly und waere derselbe Superblock mit sauberem
    /// Zustand Ok, ist nur s_state schuld (Unclean); sonst bleibt es ReadOnly (unbekanntes RoCompat).
    /// </summary>
    public static RootCheck CheckRoot(ReadOnlySpan<byte> superblock)
    {
        var decision = Ext2FeatureGate.Evaluate(superblock);
        if (decision == Ext2MountDecision.Ok) return RootCheck.Ok;
        if (decision == Ext2MountDecision.Reject) return RootCheck.Reject;
        byte[] clean = superblock[..1024].ToArray();
        BitConverter.TryWriteBytes(clean.AsSpan(58, 2), (ushort)1);
        return Ext2FeatureGate.Evaluate(clean) == Ext2MountDecision.Ok ? RootCheck.Unclean : RootCheck.ReadOnly;
    }

    public static IdentityResult Evaluate(string? markerText, ResultObject<GptTable>? table, Func<GptEntry, RootCheck> rootGate)
    {
        if (markerText is null)
        {
            if (table is null) return new(InstalledState.Legacy, Cause("NoInstallMarker", "Kein Installationsmarker, keine GPT."));
            if (!table.IsOK()) return Blocked(InstalledState.Unclear, "TableInvalid", "GPT ungueltig: " + table.Status.Message);
            foreach (var entry in table.Value!.Entries)
                if (entry.Type == GptLayout.LinuxFileSystemType)
                    return Blocked(InstalledState.Unclear, "RootWithoutMarker", "Root-Partition vorhanden, Installationsmarker fehlt.");
            return new(InstalledState.Legacy, Cause("NoInstallMarker", "Kein Installationsmarker, keine Root-Partition."));
        }

        var marker = InstallMarker.Parse(markerText);
        if (!marker.IsOK()) return Blocked(InstalledState.Unclear, "MarkerInvalid", "Installationsmarker unlesbar: " + marker.Status.Message);
        if (table is null || !table.IsOK())
            return Blocked(InstalledState.Unclear, "TableInvalid", "Marker vorhanden, aber GPT fehlt oder ist ungueltig.");
        if (table.Value!.DiskGuid != marker.Value!.DiskGuid)
            return Blocked(InstalledState.Unclear, "DiskMismatch", "Disk-GUID passt nicht zum Installationsmarker.");

        GptEntry? root = null;
        foreach (var entry in table.Value.Entries)
            if (entry.Unique == marker.Value.RootGuid) root = entry;
        if (root is null) return Blocked(InstalledState.RootMissing, "RootMissing", "Kein GPT-Eintrag mit der Root-GUID des Markers.");
        // Exakt wie im Marker (Lead-Entscheid 10:59): auch ein vergroessertes Root ist kein unterstuetzter Weg.
        if (root.Type != GptLayout.LinuxFileSystemType || root.FirstLba != marker.Value.RootFirstLba || root.LastLba - root.FirstLba + 1 != marker.Value.RootSectors)
            return Blocked(InstalledState.RootDamaged, "RootChanged", "Root-Partition weicht in Typ, Lage oder Groesse vom Marker ab.");

        RootCheck gate;
        try
        {
            gate = rootGate(root);
        }
        catch (IOException)
        {
            return Blocked(InstalledState.RootDamaged, "RootIoError", "E/A-Fehler beim Lesen des Root-Superblocks, Neustart versuchen.");
        }
        return gate switch
        {
            RootCheck.Ok => new(InstalledState.Healthy, Cause("Healthy", "Installiertes System vollstaendig.")),
            RootCheck.Unclean => Blocked(InstalledState.RootUnclean, "RootUnclean", "Root nicht sauber ausgehaengt, offline e2fsck noetig."),
            _ => Blocked(InstalledState.RootDamaged, "RootGate", $"ext2-Pruefung des Root ergab {gate}."),
        };
    }

    private static IdentityResult Blocked(InstalledState state, string cause, string message) => new(state, Cause(cause, message + " Keine Ersteinrichtung."));

    private static string Cause(string cause, string message) => $"cause='{cause}' message='{message}'";
}
