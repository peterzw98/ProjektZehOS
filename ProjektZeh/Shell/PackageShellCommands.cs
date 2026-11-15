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
/// Shellsitzung; das Ergebnis liefert <c>pkg status</c>, wie bei den Netzwerkbefehlen.
/// </summary>
internal sealed class PackageShellCommands(PackageStore store, Func<string, IPackageSource> remote,
    Func<string, ResultObject<string>> resolvePath, BackgroundTaskService tasks, Func<bool> isAdministrator, Action<string>? log = null)
{
    private const string Usage = "pkg list|source [<https-URL>]|update|install <id>|install-local <Verzeichnis>|upgrade [<id>]|remove <id>|status|cancel";
    private readonly object gate = new();
    private bool running;
    private int taskId;
    private string? result;

    public ShellCommand[] Commands => [new("pkg", Usage, "Installiert und verwaltet signierte App-Pakete.", true, Execute)];

    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        string command = arguments.Length > 0 ? arguments[0] : "";
        int count = arguments.Length - 1;
        switch (command)
        {
            case "list" when count == 0: return List(output);
            case "status" when count == 0:
                lock (gate) output.WriteLine(running ? "Paketauftrag laeuft." : result ?? "Kein Paketauftrag.");
                return ResultErr.CreateSuccess();
            case "cancel" when count == 0:
                lock (gate)
                {
                    if (!running) return ResultErr.CreateFailure(ResultCode.Failure, "Kein Paketauftrag laeuft.");
                    var cancelled = tasks.Cancel(taskId);
                    if (cancelled.IsOK()) output.WriteLine("Abbruch angefordert; Status mit pkg status pruefen.");
                    return cancelled;
                }
            case "source" when count == 0:
                var source = store.ReadSource();
                if (!source.IsOK()) return source.Status;
                output.WriteLine(source.Value ?? "Keine Paketquelle gesetzt.");
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
                if (written.IsOK()) output.WriteLine("Paketquelle gesetzt; Katalog mit pkg update laden.");
                return written;
            case "remove":
                var removed = store.Remove(argument);
                if (removed.IsOK()) output.WriteLine($"Paket {argument} entfernt.");
                return removed;
            case "install-local":
                var directory = resolvePath(argument);
                if (!directory.IsOK()) return directory.Status;
                return Start(output, token => Describe(store.Install(new DirectoryPackageSource(directory.Value!), "", null, token), "installiert"));
            case "install":
                if (!PackageManifest.IsValidId(argument)) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltige Paketkennung.");
                return Start(output, token => InstallFromCatalog(argument, token));
            case "upgrade":
                return Start(output, token => Upgrade(argument, token));
            default:
                return Start(output, Update);
        }
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
        else if (catalog.Value == null) lines.Add("Kein Katalog geladen: pkg source <URL>, dann pkg update.");
        foreach (string line in lines) output.WriteLine(line);
        if (lines.Count == 0) output.WriteLine("Keine Pakete.");
        return ResultErr.CreateSuccess();
    }

    private string Update(CancellationToken token)
    {
        var url = RequireSource();
        if (!url.IsOK()) return url.Status.Message;
        var source = remote(url.Value!);
        var catalog = source.Read("catalog", PackageCatalog.MaximumBytes, token);
        if (!catalog.IsOK()) return "Katalog nicht geladen: " + catalog.Status.Message;
        var signature = source.Read("catalog.sig", PackageTrust.SignatureBytes, token);
        if (!signature.IsOK()) return "Katalog nicht geladen: " + signature.Status.Message;
        var accepted = store.AcceptCatalog(catalog.Value!, signature.Value!);
        return accepted.IsOK()
            ? $"Katalog geladen (Generation {accepted.Value!.Generation}, {accepted.Value.Entries.Length} Pakete)."
            : accepted.Status.Message;
    }

    private string InstallFromCatalog(string id, CancellationToken token)
    {
        var url = RequireSource();
        if (!url.IsOK()) return url.Status.Message;
        var catalog = store.ReadCatalog();
        if (!catalog.IsOK()) return catalog.Status.Message;
        var entry = catalog.Value?.Find(id);
        if (entry == null) return catalog.Value == null ? "Kein Katalog geladen; zuerst pkg update." : $"Paket {id} ist nicht im Katalog.";
        return Describe(store.Install(remote(url.Value!), $"{entry.Id}/{entry.Version}/", entry.ManifestSha256, token), "installiert");
    }

    // Jedes Paket einzeln; ein Fehler bei einem Paket haelt die uebrigen nicht auf.
    private string Upgrade(string id, CancellationToken token)
    {
        var url = RequireSource();
        if (!url.IsOK()) return url.Status.Message;
        var catalog = store.ReadCatalog();
        if (!catalog.IsOK() || catalog.Value == null) return catalog.IsOK() ? "Kein Katalog geladen; zuerst pkg update." : catalog.Status.Message;
        var installed = store.ReadInstalled();
        if (!installed.IsOK()) return installed.Status.Message;
        var report = new StringBuilder();
        foreach (var package in installed.Value!)
        {
            if (id.Length > 0 && package.Manifest.Id != id) continue;
            var entry = catalog.Value.Find(package.Manifest.Id);
            if (entry == null || entry.Version.CompareTo(package.Manifest.Version) <= 0) continue;
            string line = Describe(store.Install(remote(url.Value!), $"{entry.Id}/{entry.Version}/", entry.ManifestSha256, token), "aktualisiert");
            report.Append(report.Length == 0 ? "" : "\n").Append(line);
        }
        return report.Length > 0 ? report.ToString() : "Alle Pakete sind aktuell.";
    }

    private ResultObject<string> RequireSource()
    {
        var source = store.ReadSource();
        if (source.IsOK() && source.Value == null) return ResultObjectFactory.CreateFailure<string>(ResultCode.Failure, "Keine Paketquelle gesetzt; zuerst pkg source <https-URL>.");
        return source;
    }

    private static string Describe(ResultObject<PackageManifest> installed, string verb) =>
        installed.IsOK() ? $"{installed.Value!.Id} {installed.Value.Version} {verb}." : installed.Status.Message;

    private ResultErr Start(ITextOutput output, Func<CancellationToken, string> work)
    {
        lock (gate)
        {
            if (running) return ResultErr.CreateFailure(ResultCode.Failure, "Ein Paketauftrag dieser Sitzung laeuft bereits.");
            running = true;
            result = null;
            var started = tasks.Start("pkg", context => Run(work, context.Token));
            if (!started.IsOK()) { running = false; return started.Status; }
            taskId = started.Value;
            output.WriteLine("Paketauftrag gestartet; Ergebnis mit pkg status abrufen.");
            return ResultErr.CreateSuccess();
        }
    }

    private void Run(Func<CancellationToken, string> work, CancellationToken token)
    {
        string text;
        try { text = work(token); }
        catch (OperationCanceledException) { text = "Paketauftrag abgebrochen."; }
        catch (Exception exception) { text = "Paketauftrag fehlgeschlagen: " + exception.Message; }
        // Seriell mitprotokolliert, damit Installationen ohne Bildschirm nachvollziehbar bleiben.
        log?.Invoke(text);
        lock (gate) { result = text; running = false; }
    }

    public void Close()
    {
        lock (gate) if (running) tasks.Cancel(taskId);
    }
}
