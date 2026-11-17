using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ProjektZeh.Security;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Migration;

/// <summary>Abgeschlossener Schritt des Ablaufs (Fortschrittsanzeige in 4.5; Tests greifen hier ein).</summary>
internal enum MigrationStep { Locked, Inventory, Planned, Checked, Marked, Copied, Verified, Ready }

internal sealed record MigrationReport(int Files, string SourceHash);

/// <summary>
/// Migration Legacy -> Root (Etappe 4 §3, Spec E4.4): liest die Quelle nur, schreibt ausschliesslich ueber
/// <see cref="IMigrationTarget"/> und setzt den Root-Marker erst als letzten Schreibzugriff auf "ready". Jeder Abbruch laesst
/// "fresh" oder "migrating" zurueck; das Ziel bootet dann nicht als eingerichtetes System.
/// </summary>
internal static class LayoutMigration
{
    /// <summary>Vom Installer auf Root angelegt (7a-S4, PreparingRoot); werden nie journalisiert oder geloescht.</summary>
    internal static readonly string[] InstallerEntries = ["/boot", "/boot/efi", "/mnt", "/proc", "/sys", "/dev", "/etc", "/etc/zehos", LayoutMarker.Path];

    public static ResultObject<MigrationReport> Run(string sourceRoot, IMigrationTarget target, Func<IDisposable> lockSource,
        Action<string> log, Action<MigrationStep>? progress = null)
    {
        try
        {
            // Schritt 1: Schreibsperre der Quelle; using gibt sie auch bei einer Ausnahme frei. Auch ein Fehler beim Erwerben
            // der Sperre wird zum Fehlerergebnis.
            using (lockSource())
                return RunLocked(sourceRoot, target, log, progress ?? (_ => { }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("MigrationIoFailed", exception.Message);
        }
    }

    private static ResultObject<MigrationReport> RunLocked(string sourceRoot, IMigrationTarget target, Action<string> log, Action<MigrationStep> progress)
    {
        progress(MigrationStep.Locked);
        var marker = ReadMarker(target);
        if (!marker.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(marker.Status);
        if (marker.Value!.State == LayoutState.Ready) return Failure("TargetReady", "Ziel ist bereits eingerichtet.");

        // Schritt 2: Inventar, nur lesen.
        var inventory = MigrationInventory.Read(sourceRoot);
        if (!inventory.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(inventory.Status);
        progress(MigrationStep.Inventory);

        // Schritt 3: Zuordnung und Umschreibung der Kontendatei, beides rein und vor jedem Schreibzugriff.
        string? securityText = null;
        foreach (var file in inventory.Value!.Entries)
            if (!file.IsDirectory && string.Equals(file.RelativePath, AccountService.MetadataFile, StringComparison.OrdinalIgnoreCase))
                securityText = File.ReadAllText(MigrationInventory.HostPath(sourceRoot, file.RelativePath), Encoding.UTF8);
        if (securityText == null) return Failure("SecurityDatabaseMissing", "Keine Kontendatei in der Quelle.");
        var names = MigrationAcl.ReadAccountNames(securityText);
        if (!names.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(names.Status);
        var plan = MigrationPlan.Create(inventory.Value, names.Value!, SystemPaths.Legacy, SystemPaths.Root);
        foreach (string line in plan.Log) log(line);
        if (plan.Conflicts.Count != 0)
        {
            foreach (string conflict in plan.Conflicts) log("Konflikt: " + conflict);
            return Failure("PlanConflict", $"{plan.Conflicts.Count} Konflikt(e); nichts geschrieben.");
        }
        var acl = MigrationAcl.Rewrite(securityText, plan, log);
        if (!acl.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(acl.Status);
        progress(MigrationStep.Planned);

        // Schritt 4: eine Wiederholung raeumt erst die eigenen Spuren weg, dann zaehlt der freie Platz.
        var cleaned = PrepareTarget(target, marker.Value.State, inventory.Value.Hash, log);
        if (!cleaned.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(cleaned);
        var space = CheckSpace(target, plan);
        if (!space.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(space);
        progress(MigrationStep.Checked);

        // Schritt 5: ab hier ist das Ziel "migrating" und bootet nicht als eingerichtetes System.
        var written = WriteMarker(target, marker.Value with { State = LayoutState.Migrating });
        if (!written.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(written);
        progress(MigrationStep.Marked);

        // Schritt 6/7: Verzeichnisse, dann Dateien; jede Absicht steht geflusht im Journal, bevor die Aktion beginnt.
        var step = target.AppendJournal(MigrationJournal.HeaderLine(inventory.Value.Hash));
        if (!step.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(step);
        foreach (string directory in plan.Directories)
        {
            if (IsInstallerEntry(directory)) continue;
            step = target.AppendJournal(MigrationJournal.DirectoryLine(directory));
            if (step.IsOK()) step = target.CreateDirectory(directory);
            if (!step.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(step);
        }
        foreach (var file in plan.Files)
        {
            step = CopyFile(sourceRoot, target, file, plan, acl.Value!, log);
            if (!step.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(step);
        }
        progress(MigrationStep.Copied);

        // Schritt 8: hat sich die Quelle trotz Sperre geaendert, ist die Kopie nicht konsistent.
        var unchanged = inventory.Value.VerifyUnchanged(sourceRoot);
        if (!unchanged.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(unchanged);
        progress(MigrationStep.Verified);

        // Schritt 9: erst alles Geschriebene dauerhaft machen, dann "ready" als letzter Schreibzugriff und erneut zweifach Sync
        // (Etappe 4 §2). Ohne die Schranke davor koennte nach einem Absturz "ready" auf der Platte stehen, gepufferte Dateien
        // aber nicht (wie 7a-S4 P2-1).
        written = target.Sync();
        if (written.IsOK()) written = target.Sync();
        if (!written.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(written);
        written = WriteMarker(target, marker.Value with { State = LayoutState.Ready, SourceHash = inventory.Value.Hash, Files = plan.Files.Count });
        if (written.IsOK()) written = target.Sync();
        if (written.IsOK()) written = target.Sync();
        if (!written.IsOK()) return ResultObjectFactory.CreateFailure<MigrationReport>(written);
        progress(MigrationStep.Ready);
        return ResultObjectFactory.CreateSuccess(new MigrationReport(plan.Files.Count, inventory.Value.Hash));
    }

    private static ResultErr CopyFile(string sourceRoot, IMigrationTarget target, PlanFile file, MigrationPlan plan, string securityText, Action<string> log)
    {
        byte[]? replaced = file.Content switch
        {
            PlanContent.SecurityDatabase => Encoding.UTF8.GetBytes(securityText),
            PlanContent.DesktopSettings => Encoding.UTF8.GetBytes(MigrationAcl.RewriteDesktop(
                File.ReadAllText(MigrationInventory.HostPath(sourceRoot, file.Source), Encoding.UTF8), plan, log)),
            _ => null,
        };
        var step = target.AppendJournal(MigrationJournal.WriteLine(file.Target));
        if (!step.IsOK()) return step;
        string expected;
        if (replaced != null)
        {
            expected = Convert.ToHexStringLower(SHA256.HashData(replaced));
            using var content = new MemoryStream(replaced, false);
            step = target.WriteFile(file.Target, content);
        }
        else
        {
            // Unveraenderte Dateien muessen dem Inventar entsprechen; eine Abweichung faellt beim Zuruecklesen auf.
            expected = file.Sha256;
            using var content = File.OpenRead(MigrationInventory.HostPath(sourceRoot, file.Source));
            step = target.WriteFile(file.Target, content);
        }
        if (!step.IsOK()) return step;

        var back = target.ReadBack(file.Target);
        if (!back.IsOK()) return back.Status;
        string actual;
        using (var stream = back.Value!) actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            return ResultErr.CreateFailure(ResultCode.Failure, $"cause='VerifyFailed' message='Pruefsumme nach dem Schreiben falsch: {file.Target}'");
        return target.AppendJournal(MigrationJournal.DoneLine(file.Target, actual));
    }

    /// <summary>
    /// Auf dem Ziel duerfen nur Installereintraege liegen, bei einer Wiederholung (§4) zusaetzlich alles, was das Journal nennt,
    /// und Temp-Dateien eines abgebrochenen <see cref="IMigrationTarget.WriteFile"/> zu einer journalisierten Datei oder dem
    /// Marker (auch bei "fresh": ein Absturz vor dem Rename von "migrating" laesst den alten Marker stehen). Sonst koennte eine spaetere Wiederholung fremde Daten loeschen. Bei einer Wiederholung werden genau diese Spuren
    /// entfernt (Verzeichnisse von unten nach oben); fehlende Pfade sind Absichten ohne Aktion.
    /// </summary>
    private static ResultErr PrepareTarget(IMigrationTarget target, LayoutState state, string sourceHash, Action<string> log)
    {
        bool repeating = state == LayoutState.Migrating;
        MigrationJournal? journal = null;
        if (repeating)
        {
            var read = ReadJournal(target);
            if (!read.IsOK()) return read.Status;
            journal = read.Value;
        }
        IReadOnlyList<string> files = journal?.Files ?? [], directories = journal?.Directories ?? [];
        // Die Planung erzeugt dort nie Pfade; ein solches Journal stammt nicht aus diesem Ablauf.
        foreach (var paths in new[] { files, directories })
            foreach (string path in paths)
                if (IsInstallerArea(path)) return Foreign(path);

        var known = new HashSet<string>(InstallerEntries, StringComparer.Ordinal) { "/" };
        var writable = new HashSet<string>(files, StringComparer.Ordinal) { LayoutMarker.Path };
        if (repeating)
        {
            known.Add(MigrationJournal.Path);
            known.UnionWith(files);
            known.UnionWith(directories);
        }
        var temporaries = new List<string>();
        var listed = target.List();
        if (!listed.IsOK()) return listed.Status;
        foreach (string path in listed.Value!)
        {
            if (known.Contains(path)) continue;
            if (path.EndsWith(IMigrationTarget.TemporarySuffix, StringComparison.Ordinal) &&
                writable.Contains(path[..^IMigrationTarget.TemporarySuffix.Length]))
            {
                temporaries.Add(path);
                continue;
            }
            return Foreign(path);
        }
        foreach (string path in temporaries)
        {
            var deleted = DeleteTolerant(target, path);
            if (!deleted.IsOK()) return deleted;
        }
        if (!repeating) return ResultErr.CreateSuccess();
        if (journal?.SourceHash is { } previous && previous != sourceHash) log("Quelle hat sich seit dem abgebrochenen Lauf geaendert.");

        foreach (string path in files)
        {
            var deleted = DeleteTolerant(target, path);
            if (!deleted.IsOK()) return deleted;
        }
        for (int i = directories.Count - 1; i >= 0; i--)
        {
            var deleted = DeleteTolerant(target, directories[i]);
            if (!deleted.IsOK()) return deleted;
        }
        return DeleteTolerant(target, MigrationJournal.Path);
    }

    private static ResultErr Foreign(string path) =>
        ResultErr.CreateFailure(ResultCode.Failure, $"cause='ForeignEntry' message='Fremder Eintrag auf dem Ziel: {path}; nur Neuinstallation moeglich.'");

    // Mountpunkte und Bootbereich des Installers; die Migration schreibt dort nie.
    private static bool IsInstallerArea(string path)
    {
        foreach (string area in new[] { "/boot", "/mnt", "/proc", "/sys", "/dev" })
            if (KernelPath.IsAtOrBelow(path, area, StringComparison.Ordinal)) return true;
        return false;
    }

    private static ResultErr DeleteTolerant(IMigrationTarget target, string path)
    {
        if (IsInstallerEntry(path)) return ResultErr.CreateSuccess();
        var result = target.Delete(path);
        return result.IsOK() || result.Code == ResultCode.NotFound ? ResultErr.CreateSuccess() : result;
    }

    private static ResultErr CheckSpace(IMigrationTarget target, MigrationPlan plan)
    {
        var bytes = target.FreeBytes();
        if (!bytes.IsOK()) return bytes.Status;
        var inodes = target.FreeInodes();
        if (!inodes.IsOK()) return inodes.Status;
        if (bytes.Value < plan.RequiredBytes)
            return ResultErr.CreateFailure(ResultCode.Failure, $"cause='NoSpace' message='Zu wenig Platz: {plan.RequiredBytes} Byte noetig, {bytes.Value} frei.'");
        if (inodes.Value < plan.RequiredInodes)
            return ResultErr.CreateFailure(ResultCode.Failure, $"cause='NoInodes' message='Zu wenig Inodes: {plan.RequiredInodes} noetig, {inodes.Value} frei.'");
        return ResultErr.CreateSuccess();
    }

    private static ResultObject<LayoutMarker> ReadMarker(IMigrationTarget target)
    {
        var text = ReadText(target, LayoutMarker.Path, LayoutMarker.MaximumBytes);
        if (!text.IsOK()) return ResultObjectFactory.CreateFailure<LayoutMarker>(text.Status);
        return LayoutMarker.Parse(text.Value!);
    }

    /// <summary>Kein Journal ist gueltig (Abbruch vor dem Kopf); der Wert ist dann <see langword="null"/>.</summary>
    private static ResultObject<MigrationJournal?> ReadJournal(IMigrationTarget target)
    {
        var text = ReadText(target, MigrationJournal.Path, int.MaxValue);
        if (text.Status.Code == ResultCode.NotFound) return ResultObjectFactory.CreateSuccess<MigrationJournal?>(null);
        if (!text.IsOK()) return ResultObjectFactory.CreateFailure<MigrationJournal?>(text.Status);
        var journal = MigrationJournal.Parse(text.Value!);
        return journal.IsOK() ? ResultObjectFactory.CreateSuccess<MigrationJournal?>(journal.Value) : ResultObjectFactory.CreateFailure<MigrationJournal?>(journal.Status);
    }

    private static ResultObject<string> ReadText(IMigrationTarget target, string path, int maximumBytes)
    {
        var stream = target.ReadBack(path);
        if (!stream.IsOK()) return ResultObjectFactory.CreateFailure<string>(stream.Status);
        using var content = stream.Value!;
        using var copy = new MemoryStream();
        content.CopyTo(copy);
        if (copy.Length > maximumBytes) return ResultObjectFactory.CreateFailure<string>(ResultCode.Failure, $"cause='TooLarge' message='{path} zu gross.'");
        // Ungueltige Bytes (etwa ein Nullbyte-Schwanz) werden ersetzt statt zu werfen; Marker und Journal pruefen selbst.
        return ResultObjectFactory.CreateSuccess(Encoding.UTF8.GetString(copy.GetBuffer(), 0, (int)copy.Length));
    }

    private static ResultErr WriteMarker(IMigrationTarget target, LayoutMarker marker)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(marker.Serialize()), false);
        return target.WriteFile(LayoutMarker.Path, content);
    }

    private static bool IsInstallerEntry(string path) => Array.IndexOf(InstallerEntries, path) >= 0;

    private static ResultObject<MigrationReport> Failure(string cause, string message) =>
        ResultObjectFactory.CreateFailure<MigrationReport>(ResultCode.Failure, $"cause='{cause}' message='{message}'");
}
