using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using ProjektZeh.Packages;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <summary>
/// Paketbefehle der Shell. Aendernde Befehle sind Administratoren vorbehalten und laufen als Hintergrundauftrag je
/// Shellsitzung; das Ergebnis holt die Ereignisschleife ab, wie bei den Netzwerkbefehlen.
/// </summary>
internal sealed class PackageShellCommands(PackageStore store, Func<string, IPackageSource> remote,
    Func<string, ResultObject<string>> resolvePath, BackgroundTaskService tasks, Func<bool> isAdministrator,
    Action<string>? log = null, Func<int>? getSessionGeneration = null, PackageService2? native = null)
{
    private const string Usage = "pkg list|source [<https-URL>]|update|install <id>|install-local <Verzeichnis>|upgrade [<id>]|remove <id>|cancel";
    private const string NativeUsage = "pkg native list|resolve <pfad>|update [--from <Verzeichnis>]|install [--from <Verzeichnis>] <id>...|upgrade [--from <Verzeichnis>]|remove <id>|remove --unused";
    private readonly ShellJob job = new(tasks, "pkg", "Paketauftrag", log, getSessionGeneration);
    private readonly object progressGate = new();
    private PackageProgress? progress;
    private string? lastProgress;
    private int progressGeneration;
    private bool closed;

    public string? TakeResult()
    {
        string? text = job.TakeResult();
        if (text != null) lock (progressGate) progress = null;
        return text;
    }

    public string? TakeProgress()
    {
        PackageProgress? value;
        lock (progressGate)
        {
            value = progressGeneration == (getSessionGeneration?.Invoke() ?? 0) ? progress : null;
            progress = null;
        }
        if (value == null) return null;
        string text;
        if (value.TotalBytes > 0)
        {
            int percent = (int)(value.CompletedBytes * 100 / value.TotalBytes);
            string bar = new string('#', percent / 5).PadRight(20, '-');
            text = $"{value.Phase} {value.Path} [{bar}] {percent}% {value.CompletedBytes}/{value.TotalBytes} B";
        }
        else text = $":: {value.Phase} {value.Path}";
        if (text == lastProgress) return null;
        lastProgress = text;
        return text;
    }

    private void ReportProgress(PackageProgress value)
    {
        lock (progressGate) if (!closed) progress = value;
    }

    public ShellCommand[] Commands => [new("pkg", native != null ? Usage + "|native ..." : Usage,
        native != null ? "Installiert und verwaltet signierte App-Pakete; pkg native verwaltet native Pakete (Format 2)." : "Installiert und verwaltet signierte App-Pakete.",
        true, Execute)];

    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        string command = arguments.Length > 0 ? arguments[0] : "";
        int count = arguments.Length - 1;
        switch (command)
        {
            case "list" when count == 0: return List(output);
            case "native" when native != null: return ExecuteNative(arguments[1..], output);
            case "cancel" when count == 0:
                return job.Cancel(output);
            case "source" when count == 0:
                var source = store.ReadSource();
                if (!source.IsOK()) return source.Status;
                output.WriteLine(source.Value!);
                return ResultErr.CreateSuccess();
        }
        if (!store.HasTrust) return ResultErr.CreateFailure(ResultCode.Failure, "Dieser Build enthaelt keinen Paketschluessel; Pakete sind deaktiviert.");
        bool known = command switch
        {
            "source" or "install" or "install-local" or "remove" => count == 1,
            "update" => count == 0,
            "upgrade" => count <= 1,
            _ => false
        };
        if (!known) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: " + Usage);
        if (!isAdministrator()) return ResultErr.CreateFailure(ResultCode.Failure, "pkg " + command + " erfordert Administratorrechte.");
        string argument = count == 1 ? arguments[1] : "";
        switch (command)
        {
            case "source":
                var written = store.WriteSource(argument);
                if (written.IsOK()) output.WriteLine("Paketquelle gesetzt.");
                return written;
            case "remove":
                var removed = store.Remove(argument);
                if (removed.IsOK()) output.WriteLine($"Paket {argument} entfernt.");
                return removed;
            case "install-local":
                var directory = resolvePath(argument);
                if (!directory.IsOK()) return directory.Status;
                return Start(output, token => Describe(store.Install(new DirectoryPackageSource(directory.Value!), "", null, token, ReportProgress), "installiert"));
            case "install":
                if (!PackageManifest.IsValidId(argument)) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltige Paketkennung.");
                return Start(output, token => InstallFromCatalog(argument, token));
            case "upgrade":
                return Start(output, token => Upgrade(argument, token));
            default:
                return Start(output, token => DescribeCatalog(LoadCatalog(token)));
        }
    }

    // Paketbestand v2 (N3-B1). Quelle ist dieselbe HTTPS-Quelle wie v1 (catalog2 liegt neben catalog) oder mit --from ein
    // lokales Verzeichnis im Layout von package2.py; vertraut wird in beiden Faellen nur der Signatur, nicht der Herkunft.
    private ResultErr ExecuteNative(string[] arguments, ITextOutput output)
    {
        string command = arguments.Length > 0 ? arguments[0] : "";
        var rest = new List<string>(arguments.Length);
        for (int index = 1; index < arguments.Length; index++) rest.Add(arguments[index]);
        switch (command)
        {
            case "list" when rest.Count == 0: return NativeList(output);
            case "resolve" when rest.Count == 1: return NativeResolve(rest[0], output);
        }
        string? from = null;
        if (rest.Count >= 2 && rest[0] == "--from")
        {
            from = rest[1];
            rest.RemoveRange(0, 2);
        }
        bool known = command switch
        {
            "update" or "upgrade" => rest.Count == 0,
            "install" => rest.Count >= 1,
            "remove" => rest.Count == 1 && from == null,
            _ => false
        };
        if (!known) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: " + NativeUsage);
        if (!store.HasTrust) return ResultErr.CreateFailure(ResultCode.Failure, "Dieser Build enthaelt keinen Paketschluessel; Pakete sind deaktiviert.");
        if (!isAdministrator()) return ResultErr.CreateFailure(ResultCode.Failure, "pkg native " + command + " erfordert Administratorrechte.");
        foreach (string id in command == "install" ? rest : [])
            if (!PackageManifest.IsValidId(id)) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltige Paketkennung.");
        if (command == "remove" && rest[0] != "--unused" && !PackageManifest.IsValidId(rest[0]))
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltige Paketkennung.");
        IPackageSource? source = null;
        if (command != "remove")
        {
            var opened = NativeSource(from);
            if (!opened.IsOK()) return opened.Status;
            source = opened.Value!;
        }
        string[] ids = [.. rest];
        return command switch
        {
            "update" => Start(output, token => DescribeCatalog2(native!.Update(source!, token))),
            "install" => Start(output, token => DescribeChange(native!.Install(source!, ids, token, ReportProgress), "installiert")),
            "upgrade" => Start(output, token => DescribeChange(native!.Upgrade(source!, token, ReportProgress), "aktualisiert")),
            _ => Start(output, token => DescribeChange(ids[0] == "--unused" ? native!.RemoveUnused(token) : native!.Remove(ids[0], token), "entfernt")),
        };
    }

    private ResultObject<IPackageSource> NativeSource(string? from)
    {
        if (from != null)
        {
            var directory = resolvePath(from);
            return directory.IsOK() ? ResultObjectFactory.CreateSuccess<IPackageSource>(new DirectoryPackageSource(directory.Value!))
                : ResultObjectFactory.CreateFailure<IPackageSource>(directory.Status);
        }
        var url = store.ReadSource();
        return url.IsOK() ? ResultObjectFactory.CreateSuccess(remote(url.Value!)) : ResultObjectFactory.CreateFailure<IPackageSource>(url.Status);
    }

    // Synchrone Ausgaben gehen zusaetzlich ins Protokoll (wie Auftragsergebnisse), damit Gastproben sie seriell sehen.
    private void Emit(ITextOutput output, string line)
    {
        output.WriteLine(line);
        log?.Invoke(line);
    }

    private ResultErr NativeList(ITextOutput output)
    {
        var active = native!.CurrentIndex();
        if (!active.IsOK()) return active.Status;
        var catalog = native.ReadCatalog();
        int lines = 0;
        foreach (var package in active.Value!.Packages)
        {
            var available = catalog.IsOK() ? catalog.Value?.Find(package.Manifest.Id) : null;
            string update = available != null && available.Version.CompareTo(package.Manifest.Version) > 0 ? $", Update {available.Version}" : "";
            Emit(output, $"v2 {package.Manifest.Id} {package.Manifest.Version} {(package.Requested ? "angefordert" : "automatisch")}{update}");
            lines++;
        }
        if (catalog.IsOK() && catalog.Value != null)
            foreach (var entry in catalog.Value.Entries)
                if (Array.FindIndex(active.Value.Packages, package => package.Manifest.Id == entry.Id) < 0)
                {
                    Emit(output, $"v2 {entry.Id} {entry.Version} verfuegbar");
                    lines++;
                }
        if (!catalog.IsOK()) Emit(output, catalog.Status.Message);
        else if (catalog.Value == null) Emit(output, "Noch kein Katalog v2 geladen; pkg native update.");
        if (lines == 0) Emit(output, "v2: keine Pakete (Index " + active.Value.Generation + ").");
        return ResultErr.CreateSuccess();
    }

    private ResultErr NativeResolve(string path, ITextOutput output)
    {
        var index = native!.CurrentIndex();
        if (!index.IsOK()) return index.Status;
        var entry = index.Value!.Resolve(path);
        if (!entry.IsOK())
        {
            Emit(output, "v2 resolve: " + entry.Status.Message);
            return entry.Status;
        }
        var value = entry.Value!;
        Emit(output, value.Kind == PackageIndexKind.Directory ? $"v2 resolve {value.Path}: Ordner"
            : $"v2 resolve {value.Path}: {value.PackageId} {value.Version}, {value.File!.Size} B, {(value.Executable ? "ausfuehrbar" : "nicht ausfuehrbar")}, Index {index.Value.Generation}");
        return ResultErr.CreateSuccess();
    }

    private static string DescribeCatalog2(ResultObject<PackageCatalog2> catalog) => catalog.IsOK()
        ? $"Katalog v2 geladen (Generation {catalog.Value!.Generation}, {catalog.Value.Entries.Length} Pakete)."
        : "Katalog v2 nicht geladen: " + catalog.Status.Message;

    private static string DescribeChange(ResultObject<PackageChange2> change, string verb)
    {
        if (!change.IsOK()) return "v2: " + change.Status.Message;
        if (!change.Value!.Changed) return "v2: nichts zu tun.";
        var names = new List<string>();
        foreach (var entry in change.Value.Staged) names.Add(entry.Id + " " + entry.Version);
        names.AddRange(change.Value.Removed);
        return names.Count == 0 ? "v2: Bestand gewechselt." : $"v2 {verb}: {string.Join(", ", names)}.";
    }

    private ResultErr List(ITextOutput output)
    {
        var installed = store.ReadInstalled();
        if (!installed.IsOK()) return installed.Status;
        var catalog = store.ReadCatalog();
        var lines = new List<string>();
        foreach (var package in installed.Value!)
        {
            var available = catalog.IsOK() ? catalog.Value?.Find(package.Manifest.Id) : null;
            string update = available != null && available.Version.CompareTo(package.Manifest.Version) > 0 ? $", Update {available.Version}" : "";
            lines.Add($"{package.Manifest.Id} {package.Manifest.Version} installiert ({package.Manifest.Name}{update})");
        }
        if (catalog.IsOK() && catalog.Value != null)
            foreach (var entry in catalog.Value.Entries)
                if (Array.FindIndex(installed.Value!, package => package.Manifest.Id == entry.Id) < 0) lines.Add($"{entry.Id} {entry.Version} verfuegbar");
        if (!catalog.IsOK()) lines.Add(catalog.Status.Message);
        else if (catalog.Value == null) lines.Add("Noch kein Katalog geladen; pkg install <id> oder pkg upgrade laden ihn automatisch.");
        foreach (string line in lines) output.WriteLine(line);
        if (lines.Count == 0) output.WriteLine("Keine Pakete.");
        return ResultErr.CreateSuccess();
    }

    private ResultObject<PackageCatalog> LoadCatalog(CancellationToken token)
    {
        var url = store.ReadSource();
        if (!url.IsOK()) return ResultObjectFactory.CreateFailure<PackageCatalog>(url.Status);
        var source = remote(url.Value!);
        ReportProgress(new("Paketliste laden", "catalog", 0, 0));
        var catalog = source.Read("catalog", PackageCatalog.MaximumBytes, token);
        if (!catalog.IsOK()) return ResultObjectFactory.CreateFailure<PackageCatalog>(catalog.Status);
        ReportProgress(new("Katalogsignatur laden", "catalog.sig", 0, 0));
        var signature = source.Read("catalog.sig", PackageTrust.SignatureBytes, token);
        if (!signature.IsOK()) return ResultObjectFactory.CreateFailure<PackageCatalog>(signature.Status);
        ReportProgress(new("Katalog pruefen", "", 0, 0));
        return store.AcceptCatalog(catalog.Value!, signature.Value!);
    }

    private static string DescribeCatalog(ResultObject<PackageCatalog> catalog) => catalog.IsOK()
        ? $"Katalog geladen (Generation {catalog.Value!.Generation}, {catalog.Value.Entries.Length} Pakete)."
        : "Katalog nicht geladen: " + catalog.Status.Message;

    private string InstallFromCatalog(string id, CancellationToken token)
    {
        var url = store.ReadSource();
        if (!url.IsOK()) return url.Status.Message;
        var catalog = LoadCatalog(token);
        if (!catalog.IsOK()) return DescribeCatalog(catalog);
        var entry = catalog.Value?.Find(id);
        if (entry == null) return $"Paket {id} ist nicht im Katalog.";
        return Describe(store.Install(remote(url.Value!), $"{entry.Id}/{entry.Version}/", entry.ManifestSha256, token, ReportProgress), "installiert");
    }

    // Jedes Paket einzeln; ein Fehler bei einem Paket haelt die uebrigen nicht auf.
    private string Upgrade(string id, CancellationToken token)
    {
        var url = store.ReadSource();
        if (!url.IsOK()) return url.Status.Message;
        var catalog = LoadCatalog(token);
        if (!catalog.IsOK()) return DescribeCatalog(catalog);
        var installed = store.ReadInstalled();
        if (!installed.IsOK()) return installed.Status.Message;
        var report = new StringBuilder();
        foreach (var package in installed.Value!)
        {
            token.ThrowIfCancellationRequested();
            if (id.Length > 0 && package.Manifest.Id != id) continue;
            var entry = catalog.Value!.Find(package.Manifest.Id);
            if (entry == null || entry.Version.CompareTo(package.Manifest.Version) <= 0) continue;
            string line = Describe(store.Install(remote(url.Value!), $"{entry.Id}/{entry.Version}/", entry.ManifestSha256, token, ReportProgress), "aktualisiert");
            report.Append(report.Length == 0 ? "" : "\n").Append(line);
        }
        return report.Length > 0 ? report.ToString() : "Alle Pakete sind aktuell.";
    }

    private static string Describe(ResultObject<PackageManifest> installed, string verb) =>
        installed.IsOK() ? $"{installed.Value!.Id} {installed.Value.Version} {verb}." : installed.Status.Message;

    private ResultErr Start(ITextOutput output, Func<CancellationToken, string> work)
    {
        lock (progressGate)
        {
            var started = job.Start("pkg", output, work);
            if (started.IsOK())
            {
                progress = null;
                lastProgress = null;
                progressGeneration = getSessionGeneration?.Invoke() ?? 0;
            }
            return started;
        }
    }

    public void Close()
    {
        lock (progressGate) { closed = true; progress = null; }
        job.Close();
    }
}
