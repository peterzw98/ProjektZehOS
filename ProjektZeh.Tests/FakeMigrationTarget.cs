using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Tests;

/// <summary>
/// Hostfake der Root-Partition: Kernelpfade liegen in einem Temp-Verzeichnis, damit auch PackageStore den Ergebnisbaum lesen
/// kann. Jede Operation wird protokolliert; <see cref="FailWhen"/> laesst eine Operation mit IOException scheitern (Absturz),
/// <see cref="RefuseWhen"/> mit einem Fehlerergebnis (etwa ENOSPC).
/// </summary>
internal sealed class FakeMigrationTarget : IMigrationTarget, IDisposable
{
    public FakeMigrationTarget(Guid disk)
    {
        Root = Path.Combine(Path.GetTempPath(), "zehos-migration-target-" + Guid.NewGuid().ToString("N"));
        foreach (string directory in new[] { "/boot", "/boot/efi", "/mnt", "/proc", "/sys", "/dev", "/etc", "/etc/zehos" })
            Directory.CreateDirectory(Host(directory));
        File.WriteAllText(Host(LayoutMarker.Path), new LayoutMarker(LayoutState.Fresh, disk).Serialize());
    }

    public string Root { get; }
    public List<string> Operations { get; } = [];
    public Func<string, bool>? FailWhen { get; set; }
    public Func<string, bool>? RefuseWhen { get; set; }
    /// <summary>Absturz nach vollstaendiger Temp-Datei, vor dem Rename (bleibt als Rest liegen).</summary>
    public Func<string, bool>? CrashBeforeRenameWhen { get; set; }
    public long Bytes { get; set; } = long.MaxValue / 2;
    public long Inodes { get; set; } = long.MaxValue / 2;

    public string Host(string kernelPath) => kernelPath == "/" ? Root : Path.Combine(Root, kernelPath[1..].Replace('/', Path.DirectorySeparatorChar));

    public ResultErr CreateDirectory(string path) => Do("mkdir " + path, () => Directory.CreateDirectory(Host(path)));

    public ResultErr WriteFile(string path, Stream content) => Do("write " + path, () =>
    {
        string temporary = Host(path) + IMigrationTarget.TemporarySuffix;
        using (var file = File.Create(temporary)) content.CopyTo(file);
        if (CrashBeforeRenameWhen?.Invoke("write " + path) == true) throw new IOException("Simulierter Abbruch vor dem Rename: " + path);
        File.Move(temporary, Host(path), true);
    });

    public ResultErr AppendJournal(string line, bool flush) => Do("journal " + line.TrimEnd('\n'), () => File.AppendAllText(Host(MigrationJournal.Path), line));

    public ResultErr Delete(string path)
    {
        string host = Host(path);
        if (!File.Exists(host) && !Directory.Exists(host)) return ResultErr.CreateFailure(ResultCode.NotFound, "fehlt");
        return Do("delete " + path, () =>
        {
            if (Directory.Exists(host)) Directory.Delete(host);
            else File.Delete(host);
        });
    }

    public ResultObject<Stream> ReadBack(string path)
    {
        if (!File.Exists(Host(path))) return ResultObjectFactory.CreateFailure<Stream>(ResultCode.NotFound, "fehlt");
        return ResultObjectFactory.CreateSuccess<Stream>(File.OpenRead(Host(path)));
    }

    public ResultObject<string[]> List()
    {
        var paths = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            paths.Add("/" + Path.GetRelativePath(Root, entry).Replace(Path.DirectorySeparatorChar, '/'));
        return ResultObjectFactory.CreateSuccess(paths.ToArray());
    }

    public ResultObject<long> FreeBytes() => ResultObjectFactory.CreateSuccess(Bytes);
    public ResultObject<long> FreeInodes() => ResultObjectFactory.CreateSuccess(Inodes);
    public ResultErr Sync() => Do("sync", () => { });

    /// <summary>Baum mit Pruefsummen, fuer den Vergleich zweier Laeufe.</summary>
    public SortedDictionary<string, string> Snapshot()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in List().Value!)
            result[path] = File.Exists(Host(path)) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Host(path)))) : "dir";
        return result;
    }

    public LayoutMarker Marker() => LayoutMarker.Parse(File.ReadAllText(Host(LayoutMarker.Path))).Value!;

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }

    private ResultErr Do(string operation, Action action)
    {
        if (FailWhen?.Invoke(operation) == true) throw new IOException("Simulierter Abbruch: " + operation);
        if (RefuseWhen?.Invoke(operation) == true) return ResultErr.CreateFailure(ResultCode.Failure, "cause='NoSpace' message='Simuliert: " + operation + "'");
        action();
        Operations.Add(operation);
        return ResultErr.CreateSuccess();
    }
}
