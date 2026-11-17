using System;
using System.Collections.Generic;
using ProjektZeh.Security;

namespace ProjektZeh.Storage.Migration;

/// <summary>Wie der Inhalt einer Datei auf das Ziel kommt.</summary>
internal enum PlanContent { Copy, SecurityDatabase, DesktopSettings }

internal sealed record PlanFile(string Source, string Target, long Size, string Sha256, PlanContent Content);

/// <summary>
/// Zuordnung Legacy -> Root (Spec E4.4 §2), rein und ohne Dateizugriff. Quellnamen werden wie auf FAT ohne Gross-/Kleinschreibung
/// verglichen, Ziele Ordinal; Zielnamen stammen aus der Verzeichnisauflistung, nur das Home-Verzeichnis traegt den Kontonamen,
/// weil das Root-Profil es ueber <see cref="SystemPaths.Home"/> und Ordinal findet. Konflikte verhindern jeden Schreibzugriff.
/// </summary>
internal sealed class MigrationPlan
{
    private const long BlockBytes = 4096;
    // Root ist ext2 ohne large_file (Gate, E4.0 Schritt g): Dateien ab 2 GiB passen nicht. ext2 speichert Namen mit hoechstens
    // 255 Byte; FAT erlaubt 255 UTF-16-Zeichen, die in UTF-8 laenger sein koennen. Beides muss vor dem Schreiben auffallen.
    internal const long MaximumFileBytes = (1L << 31) - 1;
    internal const int MaximumNameBytes = 255;
    // Dateien entstehen ueber "<name>" + Temp-Endung (IMigrationTarget.WriteFile); auch dieser Name muss auf ext2 passen.
    internal static readonly int MaximumFileNameBytes = MaximumNameBytes - System.Text.Encoding.UTF8.GetByteCount(IMigrationTarget.TemporarySuffix);
    private const string LegacyDataDirectory = "/srv/legacy-data";
    private readonly Dictionary<string, string> sourceToTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> sourceDirectoryTargets = new(StringComparer.Ordinal);
    private readonly List<string> directories = [];
    private readonly HashSet<string> directorySet = new(StringComparer.Ordinal);
    private readonly List<PlanFile> files = [];
    private readonly List<string> conflicts = [];
    private readonly List<string> log = [];
    private readonly string legacyRoot;

    private MigrationPlan(string legacyRoot) => this.legacyRoot = legacyRoot;

    /// <summary>Alle anzulegenden Zielverzeichnisse, Eltern vor Kindern; Installerverzeichnisse ueberspringt der Ablauf.</summary>
    public IReadOnlyList<string> Directories => directories;
    public IReadOnlyList<PlanFile> Files => files;
    public IReadOnlyList<string> Conflicts => conflicts;
    /// <summary>Ausschluesse und Hinweise, die der Befehl anzeigt.</summary>
    public IReadOnlyList<string> Log => log;
    public long RequiredBytes { get; private set; }
    public long RequiredInodes { get; private set; }

    public static MigrationPlan Create(MigrationInventory inventory, IReadOnlyCollection<string> accountNames, SystemPaths legacy, SystemPaths root)
    {
        var plan = new MigrationPlan(legacy.AdminRoot);
        var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in accountNames)
        {
            if (!PathComponent.IsValid(name)) plan.conflicts.Add($"Kontoname ist keine gueltige Pfadkomponente: {name}");
            else accounts[name] = name;
        }

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceChildren = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in inventory.Entries)
        {
            int separator = entry.RelativePath.LastIndexOf('/');
            string parent = separator < 0 ? "" : entry.RelativePath[..separator];
            sourceChildren.TryGetValue(parent, out int children);
            sourceChildren[parent] = ++children;
            if (children == FileService.MaximumExt2DirectoryEntries + 1)
                plan.conflicts.Add($"cause='DirectoryEntryLimit' message='Quellverzeichnis {legacy.AdminRoot}/{parent} hat mehr als {FileService.MaximumExt2DirectoryEntries} Eintraege; vor der Migration aufteilen.'");
            string? target = plan.Map(entry, accounts, legacy, root, out PlanContent content);
            if (target == null) continue;
            if (!ValidTarget(target, root, !entry.IsDirectory))
            {
                plan.conflicts.Add($"Zielpfad ungueltig: {entry.RelativePath} -> {target}");
                continue;
            }
            if (!entry.IsDirectory && entry.Size > MaximumFileBytes)
            {
                plan.conflicts.Add($"Datei zu gross fuer Root (ext2 ohne large_file, hoechstens 2 GiB - 1): {entry.RelativePath}");
                continue;
            }
            if (!targets.TryAdd(target, entry.RelativePath))
            {
                plan.conflicts.Add($"Zwei Quellen auf dasselbe Ziel {target}: {targets[target]} und {entry.RelativePath}");
                continue;
            }
            plan.sourceToTarget[entry.RelativePath] = target;
            if (entry.IsDirectory) plan.sourceDirectoryTargets.Add(target);
            else plan.files.Add(new PlanFile(entry.RelativePath, target, entry.Size, entry.Sha256!, content));
        }

        var needed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string target in plan.sourceDirectoryTargets) AddWithParents(needed, target);
        foreach (var file in plan.files)
            if (KernelPath.Parent(file.Target) is { } parent) AddWithParents(needed, parent);
        // Erst nach allen Verzeichnissen pruefen: auch ein spaeter benoetigter Elternordner kann mit einer Datei kollidieren.
        foreach (var file in plan.files)
            if (needed.Contains(file.Target)) plan.conflicts.Add($"Datei und Verzeichnis auf demselben Ziel: {file.Target}");
        plan.directories.AddRange(needed);
        plan.directorySet.UnionWith(needed);

        // Umgelegte Dateien und erzeugte Eltern koennen mehrere Quellen in einem Zielordner zusammenfuehren.
        var targetEntries = new HashSet<string>(needed, StringComparer.Ordinal);
        foreach (var file in plan.files) targetEntries.Add(file.Target);
        targetEntries.UnionWith(LayoutMigration.InstallerEntries);
        targetEntries.Add(MigrationJournal.Path);
        // Der bereits vorhandene Marker wird zum Abschluss ueber eine zusaetzliche Tempdatei ersetzt.
        targetEntries.Add(LayoutMarker.Path + IMigrationTarget.TemporarySuffix);
        var targetChildren = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string target in targetEntries)
        {
            string parent = KernelPath.Parent(target)!;
            targetChildren.TryGetValue(parent, out int children);
            targetChildren[parent] = ++children;
            if (children == FileService.MaximumExt2DirectoryEntries + 1)
                plan.conflicts.Add($"cause='DirectoryEntryLimit' message='Zielverzeichnis {parent} benoetigt mehr als {FileService.MaximumExt2DirectoryEntries} Eintraege; vor der Migration aufteilen.'");
        }

        long bytes = plan.directories.Count * BlockBytes, inodes = plan.directories.Count + plan.files.Count;
        foreach (var file in plan.files) bytes += (file.Size + BlockBytes - 1) / BlockBytes * BlockBytes;
        // 10 % Reserve, mindestens 1 MiB bzw. 64 Inodes (Etappe 4 §3 Schritt 4); deckt auch Journal, Marker und die etwas
        // laengeren Pfade in der umgeschriebenen Rechtedatei.
        plan.RequiredBytes = bytes + Math.Max(bytes / 10, 1024 * 1024);
        plan.RequiredInodes = inodes + Math.Max(inodes / 10, 64);
        return plan;
    }

    /// <summary>Ziel eines Legacy-Kernelpfads ("/data/..."), Vergleich wie auf FAT; "/data" selbst wird "/".</summary>
    public bool TryMapSource(string legacyPath, out string target)
    {
        target = "";
        if (string.Equals(legacyPath, legacyRoot, StringComparison.OrdinalIgnoreCase)) { target = "/"; return true; }
        if (!legacyPath.StartsWith(legacyRoot + "/", StringComparison.OrdinalIgnoreCase)) return false;
        if (!sourceToTarget.TryGetValue(legacyPath[(legacyRoot.Length + 1)..], out string? mapped)) return false;
        target = mapped;
        return true;
    }

    /// <summary>Vom Plan erzeugt heisst: nicht im Quellinventar (huang 5a.3); nur solche Verzeichnisse erben Rechte.</summary>
    public bool IsCreatedDirectory(string target) => !sourceDirectoryTargets.Contains(target) && directorySet.Contains(target);

    private string? Map(InventoryEntry entry, Dictionary<string, string> accounts, SystemPaths legacy, SystemPaths root, out PlanContent content)
    {
        content = PlanContent.Copy;
        string relative = entry.RelativePath;
        string[] parts = relative.Split('/');
        foreach (string part in parts)
        {
            if (part.StartsWith(".zehos-", StringComparison.OrdinalIgnoreCase) &&
                (part.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)))
                return Exclude(relative, "temporaere Datei");
        }
        if (Is(parts[0], "boot")) return Exclude(relative, "gehoert zur alten ESP");

        if (parts.Length == 1 && !entry.IsDirectory)
        {
            if (Is(parts[0], AccountService.MetadataFile))
            {
                content = PlanContent.SecurityDatabase;
                return KernelPath.Combine(root.SecurityDirectory, parts[0]);
            }
            if (Is(parts[0], AccountService.CommandsFile)) return KernelPath.Combine(root.SecurityDirectory, parts[0]);
            if (parts[0].StartsWith(".zehos-security-recovery-", StringComparison.OrdinalIgnoreCase) && parts[0].EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                return KernelPath.Combine(root.SecurityDirectory, parts[0]);
            if (Is(KernelPath.Combine(legacy.AdminRoot, parts[0]), legacy.UpdateConfigPath)) return root.UpdateConfigPath;
        }
        if (Is(KernelPath.Combine(legacy.AdminRoot, parts[0]), legacy.PackagesDirectory)) return Below(root.PackagesDirectory, parts);
        if (Is(parts[0], LegacyName(legacy.AppStateDirectory, legacy))) return Below(root.AppStateDirectory, parts);

        if (Is(KernelPath.Combine(legacy.AdminRoot, parts[0]), legacy.HomeBase) && (entry.IsDirectory || parts.Length > 1))
        {
            if (parts.Length == 1) return root.HomeBase;
            if (accounts.TryGetValue(parts[1], out string? account) && (parts.Length > 2 || entry.IsDirectory))
            {
                string home = root.Home(account);
                if (parts.Length == 3 && !entry.IsDirectory && Is(KernelPath.Combine(legacy.Home(parts[1]), parts[2]), legacy.DesktopSettings(legacy.Home(parts[1]))))
                {
                    content = PlanContent.DesktopSettings;
                    return root.DesktopSettings(home);
                }
                return Below(home, parts, 2);
            }
            if (parts.Length == 2 && entry.IsDirectory) log.Add($"Home ohne Konto nach {LegacyDataDirectory}: {relative}");
        }

        foreach (string part in parts)
        {
            if (part.StartsWith(".zehos-", StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add($"Unbekannte Systemdatei: {relative}");
                return null;
            }
        }
        return LegacyDataDirectory + "/" + relative;
    }

    private string? Exclude(string relative, string reason)
    {
        log.Add($"Ausgeschlossen ({reason}): {relative}");
        return null;
    }

    private static string Below(string directory, string[] parts, int skip = 1)
    {
        string path = directory;
        for (int i = skip; i < parts.Length; i++) path = KernelPath.Combine(path, parts[i]);
        return path;
    }

    // Name des App-Ordners unter /data (".zehos-apps"), aus dem Legacy-Profil statt als zweite Konstante.
    private static string LegacyName(string legacyPath, SystemPaths legacy) => legacyPath[(legacy.AdminRoot.Length + 1)..];

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    // Systemziele (/etc/zehos, /var/lib/zehos) duerfen .zehos--Namen tragen, Nutzerziele nicht (huang 5a.1).
    internal static bool ValidTarget(string target, SystemPaths root, bool isFile)
    {
        if (!KernelPath.IsAbsoluteNormalized(target) || target == "/") return false;
        bool system = KernelPath.IsAtOrBelow(target, root.SecurityDirectory, StringComparison.Ordinal) ||
            KernelPath.IsAtOrBelow(target, KernelPath.Parent(root.PackagesDirectory)!, StringComparison.Ordinal);
        string[] parts = target[1..].Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            int limit = isFile && i == parts.Length - 1 ? MaximumFileNameBytes : MaximumNameBytes;
            if (!PathComponent.IsValid(parts[i], system) || System.Text.Encoding.UTF8.GetByteCount(parts[i]) > limit) return false;
        }
        // Eine Datei "x.zehos-tmp" neben "x" wuerde vom Temp-Namen beim Schreiben von "x" ueberschrieben.
        if (isFile && parts[^1].EndsWith(IMigrationTarget.TemporarySuffix, StringComparison.Ordinal)) return false;
        return true;
    }

    private static void AddWithParents(SortedSet<string> set, string directory)
    {
        for (string? path = directory; path != null && path != "/"; path = KernelPath.Parent(path))
            if (!set.Add(path)) break;
    }
}
