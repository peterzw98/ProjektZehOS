using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Migration;

/// <summary>Ein Eintrag der Quelle: Pfad relativ zu /data mit '/' und dem tatsaechlichen Namen der Verzeichnisauflistung.</summary>
internal sealed record InventoryEntry(string RelativePath, bool IsDirectory, long Size, string? Sha256, long LastWriteTicks);

/// <summary>
/// Inventar der Legacy-Quelle (Spec E4.4 §1): liest alle Dateien und Verzeichnisse unter der Quellwurzel nur ueber System.IO.
/// Jeder Lesefehler bricht ab, damit nie ein unvollstaendiges Inventar migriert wird.
/// </summary>
internal sealed class MigrationInventory
{
    /// <summary>shortcut: feste Grenze gegen den Kernel-Heap (FAT erlaubt Millionen Dateien); anheben nach Messung von G6.</summary>
    public const int MaximumEntries = 100_000;

    private MigrationInventory(List<InventoryEntry> entries)
    {
        Entries = entries;
        Hash = ComputeHash(entries);
    }

    /// <summary>Eltern vor Kindern, je Verzeichnis Ordinal sortiert.</summary>
    public IReadOnlyList<InventoryEntry> Entries { get; }
    /// <summary>SHA-256 ueber Pfade, Arten, Groessen und Inhaltspruefsummen; Grundlage von Journalkopf und Root-Marker.</summary>
    public string Hash { get; }

    public static ResultObject<MigrationInventory> Read(string sourceRoot, int maximumEntries = MaximumEntries)
    {
        var listed = List(sourceRoot, maximumEntries);
        if (!listed.IsOK()) return ResultObjectFactory.CreateFailure<MigrationInventory>(listed.Status);
        var entries = listed.Value!;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsDirectory) continue;
            var hash = HashFile(sourceRoot, entries[i].RelativePath);
            if (!hash.IsOK()) return ResultObjectFactory.CreateFailure<MigrationInventory>(hash.Status);
            entries[i] = entries[i] with { Sha256 = hash.Value };
        }
        return ResultObjectFactory.CreateSuccess(new MigrationInventory(entries));
    }

    /// <summary>
    /// Schritt 8: vergleicht Verzeichnisliste, Groessen und Zeitstempel mit diesem Inventar und hasht nur abweichende Dateien
    /// neu (sonst wuerde die Platte ein weiteres Mal vollstaendig gelesen, huang P3 c). Jede Abweichung ist ein Fehler.
    /// </summary>
    public ResultErr VerifyUnchanged(string sourceRoot)
    {
        var listed = List(sourceRoot, MaximumEntries);
        if (!listed.IsOK()) return listed.Status;
        var current = listed.Value!;
        if (current.Count != Entries.Count) return Changed("Anzahl der Eintraege");
        for (int i = 0; i < current.Count; i++)
        {
            InventoryEntry before = Entries[i], now = current[i];
            if (!string.Equals(before.RelativePath, now.RelativePath, StringComparison.Ordinal) || before.IsDirectory != now.IsDirectory ||
                before.Size != now.Size) return Changed(now.RelativePath);
            if (before.IsDirectory || before.LastWriteTicks == now.LastWriteTicks) continue;
            var hash = HashFile(sourceRoot, now.RelativePath);
            if (!hash.IsOK()) return hash.Status;
            if (!string.Equals(hash.Value, before.Sha256, StringComparison.Ordinal)) return Changed(now.RelativePath);
        }
        return ResultErr.CreateSuccess();
    }

    /// <summary>Oeffnet eine Quelldatei zum Lesen (Kopieren in Schritt 6).</summary>
    public static string HostPath(string sourceRoot, string relativePath) =>
        Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static ResultObject<List<InventoryEntry>> List(string sourceRoot, int maximumEntries)
    {
        var entries = new List<InventoryEntry>();
        var pending = new Stack<(DirectoryInfo Directory, string Relative)>();
        try
        {
            pending.Push((new DirectoryInfo(sourceRoot), ""));
            while (pending.Count > 0)
            {
                var (directory, relative) = pending.Pop();
                foreach (var child in directory.EnumerateFileSystemInfos())
                {
                    // Symlinks und Mountpunkte sind auf FAT nicht vorgesehen; fail-closed statt ihnen zu folgen.
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                        return Failure("SourceUnsupported", $"Verknuepfung in der Quelle: {relative}{child.Name}");
                    if (entries.Count >= maximumEntries)
                        return Failure("InventoryTooLarge", $"Mehr als {maximumEntries} Eintraege in der Quelle; Migration nicht begonnen.");
                    string path = relative + child.Name;
                    if (child is DirectoryInfo subdirectory)
                    {
                        entries.Add(new InventoryEntry(path, true, 0, null, 0));
                        pending.Push((subdirectory, path + "/"));
                    }
                    else
                    {
                        var file = (FileInfo)child;
                        entries.Add(new InventoryEntry(path, false, file.Length, null, file.LastWriteTimeUtc.Ticks));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Failure("SourceReadFailed", "Quelle nicht lesbar: " + exception.Message);
        }
        // Ordinal ueber den ganzen Pfad: ein Elternpfad ist Praefix seiner Kinder und steht damit immer vor ihnen.
        entries.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return ResultObjectFactory.CreateSuccess(entries);
    }

    private static ResultObject<string> HashFile(string sourceRoot, string relativePath)
    {
        try
        {
            using var stream = File.OpenRead(HostPath(sourceRoot, relativePath));
            return ResultObjectFactory.CreateSuccess(Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ResultObjectFactory.CreateFailure<string>(ResultCode.Failure,
                $"cause='SourceReadFailed' message='Quelldatei nicht lesbar: {relativePath}: {exception.Message}'");
        }
    }

    private static string ComputeHash(List<InventoryEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries)
            text.Append(entry.IsDirectory ? 'D' : 'F').Append(' ').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.RelativePath)))
                .Append(' ').Append(entry.Size.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(entry.Sha256 ?? "-").Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static ResultErr Changed(string what) =>
        ResultErr.CreateFailure(ResultCode.Failure, $"cause='SourceChanged' message='Quelle waehrend der Migration geaendert: {what}'");

    private static ResultObject<List<InventoryEntry>> Failure(string cause, string message) =>
        ResultObjectFactory.CreateFailure<List<InventoryEntry>>(ResultCode.Failure, $"cause='{cause}' message='{message}'");
}
