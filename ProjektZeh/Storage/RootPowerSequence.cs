using System;
using System.Collections.Generic;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

/// <summary>Plattenzugriffe der Energieaktion im Root-Profil; der Cosmos-Adapter haelt die Speicher-Sperre selbst.</summary>
internal interface IRootPowerPlatform
{
    /// <summary>Grund, warum gerade nicht ausgeschaltet werden darf (laufende Paket-App), sonst null.</summary>
    string? BusyReason();
    /// <summary>Sperrt alle Schreiber ohne zu warten; false, wenn gerade einer schreibt.</summary>
    bool TryBlockWrites();
    void ReleaseWrites();
    /// <summary>Persistente Mounts ausser "/" und den virtuellen, in Einhaengereihenfolge.</summary>
    IReadOnlyList<string> PersistentMounts();
    bool IsMounted(string mountPoint);
    /// <summary>Sync und Device.Flush; ein Fehler kommt als Ausnahme oder false.</summary>
    bool Sync(string mountPoint);
    /// <summary>Haengt aus und merkt sich, wie der Mount wieder einzuhaengen ist.</summary>
    bool Unmount(string mountPoint);
    bool Remount(string mountPoint);
    ResultErr CloseRoot();
    ResultErr ReopenRoot();
}

/// <summary>
/// Spec 7b §2, Schritte 4-7 und die Ruecknahmetabelle. Fail-closed: jeder Fehler vor der eigentlichen Energieaktion stellt
/// Mounts und Root wieder her und laesst die Shell bedienbar. Die Schreibsperre faellt erst nach einem gelungenen Reopen.
/// </summary>
internal sealed class RootPowerSequence(IRootPowerPlatform platform)
{
    private readonly List<string> unmounted = [];

    /// <summary>Root bleibt schreibgesperrt, weil ein Reopen scheiterte; ein spaeteres Prepare versucht es erneut.</summary>
    public bool RootWriteLocked { get; private set; }

    public ResultErr Prepare()
    {
        if (platform.BusyReason() is { } busy)
            return Failed("PowerBusy", busy + " zuerst schliessen, dann erneut ausfuehren.");
        // Nach gescheitertem Reopen haelt diese Instanz die Sperre noch; sie bleibt bis zum Ende dieses Laufs gesetzt.
        if (RootWriteLocked)
        {
            var reopened = Reopen();
            if (!reopened.IsOK()) return reopened;
        }
        else if (!platform.TryBlockWrites())
            return Failed("PowerBusy", "Ein Schreibvorgang laeuft noch; Energieaktion danach erneut ausfuehren.");

        // unmounted wird nicht geleert: ein frueher gescheitertes Wiedereinhaengen wird bei jeder Ruecknahme erneut versucht.
        List<string> mounts;
        // Ohne diesen Fang bliebe die Root-Sperre nach einer Ausnahme haengen, weil prepare dann als gescheitert gilt (Review P3-c).
        try { mounts = new List<string>(platform.PersistentMounts()); }
        catch (Exception exception) { return RollBack(Failed("PowerUnmountFailed", "Mounts nicht lesbar (" + exception.Message + ")."), reopen: false); }
        // Zusatzplatten in umgekehrter Einhaengereihenfolge, /boot/efi immer zuletzt (Spec 7b §2.5).
        mounts.Reverse();
        int efi = mounts.IndexOf(SystemPaths.Root.UpdateRoot);
        if (efi >= 0) { mounts.RemoveAt(efi); mounts.Add(SystemPaths.Root.UpdateRoot); }
        foreach (string mount in mounts)
        {
            string? failure;
            try { failure = platform.Sync(mount) ? platform.Unmount(mount) ? null : "Aushaengen" : "Sync"; }
            catch (Exception exception) { failure = exception.Message; }
            // Ein Mount, der beim Aushaengen warf, ist schon entfernt und muss mit zurueck (peter-review P2-3).
            bool stillMounted;
            try { stillMounted = platform.IsMounted(mount); }
            catch (Exception exception) { stillMounted = false; failure ??= exception.Message; }
            if (!stillMounted) unmounted.Add(mount);
            if (failure != null)
                return RollBack(Failed("PowerUnmountFailed", $"{mount} nicht ausgehaengt ({failure})."), reopen: false);
        }

        var closed = platform.CloseRoot();
        // Close setzt s_state bei einem Fehler nach dem Schreiben selbst zurueck; Reopen sichert das vor dem Freigeben ab.
        return closed.IsOK() ? closed : RollBack(closed, reopen: true);
    }

    /// <summary>Die Energieaktion kehrte zurueck: erst Reopen, dann Mounts, zuletzt die Schreiber wieder freigeben.</summary>
    public ResultErr Restore() => RollBack(ResultErr.CreateSuccess(), reopen: true);

    // Reihenfolge laut Spec §2: Reopen, Mounts zurueck, erst dann Schreiber frei. Scheitert Reopen, bleibt die Sperre
    // fuer alle Root-Schreiber (fail-closed); Zusatzplatten und /boot/efi werden trotzdem zum Lesen eingehaengt.
    private ResultErr RollBack(ResultErr failure, bool reopen)
    {
        string notes = "";
        if (reopen)
        {
            var reopened = Reopen();
            if (!reopened.IsOK()) notes += " " + reopened.Message;
        }

        for (int i = unmounted.Count - 1; i >= 0; i--)
        {
            string mount = unmounted[i];
            string? error = null;
            bool back;
            try { back = platform.IsMounted(mount) || platform.Remount(mount); }
            catch (Exception exception) { back = false; error = exception.Message; }
            if (back) { unmounted.RemoveAt(i); continue; }
            notes += mount == SystemPaths.Root.UpdateRoot ? $" {mount} nicht eingehaengt, Updates gesperrt." : $" {mount} nicht eingehaengt.";
            if (error != null) notes += $" ({error})";
        }
        if (!RootWriteLocked) platform.ReleaseWrites();
        if (notes.Length == 0) return failure;
        return failure.IsOK() ? Failed("PowerRestoreIncomplete", notes.Trim())
            : ResultErr.CreateFailure(failure.Code, failure.Message + notes);
    }

    private ResultErr Reopen()
    {
        var reopened = platform.ReopenRoot();
        RootWriteLocked = !reopened.IsOK();
        return RootWriteLocked ? Failed("RootWriteLocked", "Root schreibgesperrt, bitte neu starten (" + reopened.Message + ").") : reopened;
    }

    private static ResultErr Failed(string cause, string message) =>
        ResultErr.CreateFailure(ResultCode.Failure, $"cause='{cause}' message='{message}'");
}
