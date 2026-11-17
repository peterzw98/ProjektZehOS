using System;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Migration;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Storage;

internal enum LayoutOutcome
{
    /// <summary>Kein 7a-Layout: Etappe-2-Auswahl wie heute, ohne /boot/efi.</summary>
    Legacy,
    /// <summary>ext2 schreibbar auf /, ESP unter /boot/efi.</summary>
    Root,
    /// <summary>RootUnclean: ext2 nur ueber die RO-Komponente auf /, Anmeldung mit vorhandenen Konten, keine Schreibzugriffe.</summary>
    RootReadOnly,
    /// <summary>Kein Root-Fall trotz 7a-Layout: ESP als /data, Profil Legacy, Einrichtung und security recover gesperrt.</summary>
    Blocked,
}

/// <param name="SetupAllowed">Ersteinrichtung erlaubt (nur Root-Marker fresh ohne Kontendatei).</param>
/// <param name="MarkReadyOnSave">Root-Marker steht auf fresh: das naechste Speichern der Konten setzt state=ready als letzten Schritt.</param>
/// <param name="BlockedReason">cause/message fuer Protokoll und Sperre; null, wenn nichts gesperrt ist.</param>
internal sealed record LayoutDecision(LayoutOutcome Outcome, bool SetupAllowed, bool MarkReadyOnSave, string? BlockedReason)
{
    public PathProfile Profile => Outcome is LayoutOutcome.Root or LayoutOutcome.RootReadOnly ? PathProfile.Root : PathProfile.Legacy;
}

/// <summary>
/// Profilwahl beim Boot (Etappe 4.3, Spec docs/superpowers/specs/2026-11-17-storage-etappe4-design.md, Abschnitt 2) als reine
/// Funktion. Eingaben: Identitaet aus dem ESP-Marker (null = kein 7a-Layout auf sata0), Root-Marker (null = Datei fehlt), ob die
/// Kontendatei auf Root vorhanden ist, und die Disk-GUID der GPT, zu der der Root-Marker gehoeren muss. Das Feature-Gate des
/// Root ist bereits in der Identitaet enthalten (Healthy = Gate Ok, RootUnclean = nur s_state).
/// </summary>
internal static class LayoutProfile
{
    public static LayoutDecision Decide(IdentityResult? identity, ResultObject<LayoutMarker>? rootMarker, bool hasAccounts, Guid diskGuid)
    {
        if (identity is null || identity.State == InstalledState.Legacy) return new(LayoutOutcome.Legacy, false, false, null);
        if (identity.State is not (InstalledState.Healthy or InstalledState.RootUnclean))
            return new(LayoutOutcome.Blocked, false, false, identity.Reason);

        if (rootMarker is null) return Unclear("LayoutMarkerMissing", "Root-Marker fehlt.");
        if (!rootMarker.IsOK()) return Unclear("LayoutMarkerInvalid", "Root-Marker ungueltig: " + rootMarker.Status.Message);
        var marker = rootMarker.Value!;
        if (marker.DiskGuid != diskGuid) return Unclear("LayoutDiskMismatch", "Root-Marker gehoert zu einer anderen Platte.");

        if (identity.State == InstalledState.RootUnclean)
        {
            // Nur ein eingerichtetes Root darf trotz s_state lesend benutzt werden; jeder andere Zustand ist unklar.
            return marker.State == LayoutState.Ready
                ? new(LayoutOutcome.RootReadOnly, false, false, identity.Reason)
                : Unclear("UncleanNotReady", "Root nicht sauber ausgehaengt und nicht eingerichtet.");
        }

        return marker.State switch
        {
            LayoutState.Ready when hasAccounts => new(LayoutOutcome.Root, false, false, null),
            LayoutState.Ready => Unclear("AccountsMissing", "Root eingerichtet, aber Kontendatei fehlt."),
            LayoutState.Fresh => new(LayoutOutcome.Root, !hasAccounts, true, null),
            _ => Unclear("MigrationIncomplete", "Migration begonnen, nicht abgeschlossen."),
        };
    }

    private static LayoutDecision Unclear(string cause, string message) =>
        new(LayoutOutcome.Blocked, false, false, $"cause='{cause}' message='{message} Keine Ersteinrichtung.'");
}
