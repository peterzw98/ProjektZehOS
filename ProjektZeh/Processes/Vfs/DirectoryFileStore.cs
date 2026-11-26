using System;
using System.Collections.Generic;
using System.IO;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Processes.Vfs;

/// <summary>
/// Abfragen, die System.IO nicht sicher beantwortet: Art eines Eintrags OHNE Folgen eines Symlinks, Linkziel, Link anlegen.
/// Cosmos folgt Symlinks in jedem Pfadteil (siehe <see cref="PathGuard"/>), deshalb liefert der Kernel eine eigene Fassung
/// (<c>CosmosLinuxDiskLinks</c>), der Host <see cref="HostLinuxDiskLinks"/>. Aufrufe laufen schon unter <see cref="StorageLock"/>
/// und werfen bei Speicherfehlern (<see cref="FileErrors.IsFileError"/>).
/// </summary>
internal interface ILinuxDiskLinks
{
    /// <summary>
    /// Art des Eintrags <paramref name="name"/> in <paramref name="parent"/> ohne Folgen. <paramref name="found"/> ist der Name, unter
    /// dem das Laufwerk den Eintrag führt: FAT findet einen Eintrag auch über seinen 8.3-Kurznamen ("_ZEHOS~1.DB" für
    /// ".zehos-security.db"), dann unterscheidet sich <paramref name="found"/> vom angefragten Namen.
    /// </summary>
    public LinuxNodeKind KindStorageLocked(string parent, string name, out string found);

    public string ReadLinkStorageLocked(string full);

    /// <summary>false, wenn das Laufwerk keine Symlinks kann (FAT); das Dateisystem meldet dann -EPERM wie Linux.</summary>
    public bool CreateSymlinkStorageLocked(string full, string target);
}

/// <summary>Host-Fassung über .NET (lstat-Semantik von <see cref="FileSystemInfo"/> unter Unix). Nur für Hosttests belegt.</summary>
internal sealed class HostLinuxDiskLinks : ILinuxDiskLinks
{
    public LinuxNodeKind KindStorageLocked(string parent, string name, out string found)
    {
        found = name; // Linux-Hosts suchen exakt; es gibt keinen zweiten Namen
        var info = new FileInfo(parent + "/" + name);
        FileAttributes attributes = info.Attributes;
        if ((int)attributes == -1)
        {
            return LinuxNodeKind.Missing;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return LinuxNodeKind.Symlink;
        }

        return (attributes & FileAttributes.Directory) != 0 ? LinuxNodeKind.Directory : LinuxNodeKind.File;
    }

    public string ReadLinkStorageLocked(string full) =>
        new FileInfo(full).LinkTarget ?? throw new IOException("Kein Symlink.");

    public bool CreateSymlinkStorageLocked(string full, string target)
    {
        File.CreateSymbolicLink(full, target);
        return true;
    }
}

/// <summary>
/// Prozesswurzel als Ordner eines eingebundenen Laufwerks (Datenplatte). Jeder Zugriff läuft unter <see cref="StorageLock"/>, auch
/// Lesen und Schreiben auf offenen Dateien. Namen, die ein FAT- oder Cosmos-Pfad anders deuten könnte (":", "\\", Steuerzeichen,
/// abschließender Punkt oder Leerzeichen, Systemnamen ".zehos-"), gibt es hier nicht: Abfragen melden sie als fehlend, Anlegen
/// lehnt sie mit -EINVAL ab, Listen lassen sie weg. Ein Treffer über einen anderen Namen als den angefragten (FAT-Kurzname, siehe
/// <see cref="ILinuxDiskLinks.KindStorageLocked"/>) gilt als fehlend; Anlegen und Umbenennen auf einen solchen Namen sind -EEXIST,
/// damit nie ein vorhandener Eintrag über seinen Zweitnamen überschrieben wird.
/// <para>
/// Wachstum: Eine Datei wächst in Schritten von höchstens <see cref="StorageLock.ChunkBytes"/>, jeder unter einer eigenen Sperre
/// (Cosmos-FAT nullt Lücken sofort, ohne Sparse), und alles, was dieser Speicher wachsen lässt, zählt gegen
/// <see cref="QuotaBytes"/> (-ENOSPC).
/// </para>
/// <para>
/// Rechte: Ordner 0755, Dateien 0644 (schreibgeschützte 0444); FAT kennt keine Unix-Rechte, ein Ausführungsbit fehlt deshalb.
/// Umbenennen über ein vorhandenes Verzeichnis entfernt es erst und ist damit nicht atomar.
/// </para>
/// </summary>
internal sealed class DirectoryFileStore : ILinuxFileStore
{
    public const long DefaultMaxFileBytes = 1L << 30;

    /// <summary>
    /// Kontingent je Wurzel: so viele Bytes darf ein Prozess die Platte über diesen Speicher netto wachsen lassen. 64 MiB reichen für
    /// die Testprogramme und js (schreibt nichts) und halten ein bösartiges Programm unter einem kleinen Teil der Datenplatte;
    /// vorhandene Dateien (Pakete) zählen nicht, Löschen und Kürzen geben frei.
    /// </summary>
    public const long DefaultQuotaBytes = 64L << 20;

    private const uint DirectoryMode = 0x1ED, FileMode = 0x1A4, ReadOnlyFileMode = 0x124;
    private readonly string _root;
    private readonly ILinuxDiskLinks _links;

    // Offene Einträge je Speicherpfad: Cosmos schiebt das Entfernen einer offenen Datei bis zum letzten Schließen auf
    // (VfsManager.Paths.cs MarkPendingIfOpen), sie belegt so lange weiter Platz. Die Gutschrift fürs Kontingent hängt deshalb am
    // Eintrag, nicht am Namen, und kommt erst beim letzten Schließen. Zugriff nur unter StorageLock.
    private readonly Dictionary<string, OpenEntry> _openEntries;

    // Entfernte, noch offene Einträge je Pfad. Cosmos lässt ihren Namen bis zum letzten Schließen stehen (MarkPendingIfOpen,
    // VfsManager.Paths.cs:210-229): ein zweites unlink gelänge und schriebe erneut gut. Wie bei Linux gilt der Name deshalb sofort als
    // fehlend; Anlegen unter ihm ist -EEXIST, solange das Laufwerk ihn noch führt. Zugriff nur unter StorageLock.
    private readonly Dictionary<string, OpenEntry> _pendingPaths;

    /// <summary>Ein offener Eintrag; mehrere Deskriptoren auf dieselbe Datei teilen ihn.</summary>
    private sealed class OpenEntry
    {
        public int OpenCount;

        /// <summary>Entfernt oder ersetzt, solange offen: beim letzten Schließen die dann belegte Länge gutschreiben.</summary>
        public bool Detached;
    }

    private DirectoryFileStore(string root, ILinuxDiskLinks links, StringComparison nameComparison, long maxFileBytes, long quotaBytes)
    {
        _root = root;
        _links = links;
        NameComparison = nameComparison;
        MaxFileBytes = maxFileBytes;
        QuotaBytes = quotaBytes;
        StringComparer comparer = nameComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _openEntries = new Dictionary<string, OpenEntry>(comparer);
        _pendingPaths = new Dictionary<string, OpenEntry>(comparer);
    }

    /// <summary>Anzahl der offenen Einträge (Tests).</summary>
    internal int OpenEntryCount => _openEntries.Count;

    /// <summary>Anzahl der entfernten, noch offenen Pfade (Tests).</summary>
    internal int PendingPathCount => _pendingPaths.Count;

    private OpenEntry AttachStorageLocked(string path)
    {
        if (!_openEntries.TryGetValue(path, out OpenEntry? entry))
        {
            entry = new OpenEntry();
            _openEntries[path] = entry;
        }

        entry.OpenCount++;
        return entry;
    }

    /// <summary>
    /// Ein Eintrag verschwindet unter seinem Namen (unlink, ersetzt durch rename). Ist er offen, wird er abgelöst und die Gutschrift
    /// auf das letzte Schließen verschoben; sonst gleich gutschreiben.
    /// </summary>
    private void DetachStorageLocked(string path, long length)
    {
        if (_openEntries.Remove(path, out OpenEntry? entry))
        {
            entry.Detached = true;
            _pendingPaths[path] = entry;
            return;
        }

        GrownBytes -= length;
    }

    /// <summary>
    /// Ist <paramref name="path"/> belegt? Auch ein Zweitname (8.3) und der stehende Name einer entfernten, offenen Datei. Führt das
    /// Laufwerk einen solchen entfernten Namen nicht mehr (Linux-Host entfernt sofort), wird er frei.
    /// </summary>
    private bool OccupiedStorageLocked(string path)
    {
        if (RawKindStorageLocked(path, out _) != LinuxNodeKind.Missing)
        {
            return true;
        }

        _pendingPaths.Remove(path); // die abgelöste Datei bleibt bis zum letzten Schließen gebucht, nur der Name ist frei
        return false;
    }

    /// <summary>Umbenennen einer Datei: ihr offener oder entfernter, offener Eintrag wandert mit (Ordner mit solchen Einträgen darunter benennt <see cref="RenameStorageLocked"/> nicht um).</summary>
    private void MoveEntriesStorageLocked(string from, string to)
    {
        MoveKeys(_openEntries, from, to);
        MoveKeys(_pendingPaths, from, to);
    }

    private void MoveKeys(Dictionary<string, OpenEntry> table, string from, string to)
    {
        var moved = new List<KeyValuePair<string, OpenEntry>>();
        foreach (KeyValuePair<string, OpenEntry> entry in table)
        {
            if (string.Equals(entry.Key, from, NameComparison) || entry.Key.StartsWith(from + "/", NameComparison))
            {
                moved.Add(entry);
            }
        }

        foreach (KeyValuePair<string, OpenEntry> entry in moved)
        {
            table.Remove(entry.Key);
            table[string.Concat(to, entry.Key.AsSpan(from.Length))] = entry.Value;
        }
    }

    /// <summary>
    /// Ein Deskriptor des Eintrags schließt. Beim letzten: ein abgelöster Eintrag gibt die beim Schließen belegte Länge frei, ein
    /// noch benannter verlässt nur die Tabelle (seine Länge bleibt belegt, die Datei besteht weiter).
    /// </summary>
    private void ReleaseStorageLocked(OpenEntry entry, long lengthAtClose)
    {
        if (--entry.OpenCount > 0)
        {
            return;
        }

        if (entry.Detached)
        {
            GrownBytes -= lengthAtClose; // genau einmal: der Eintrag ist in keiner Tabelle mehr benannt
            RemoveByReference(_pendingPaths, entry);
            return;
        }

        RemoveByReference(_openEntries, entry);
    }

    private static void RemoveByReference(Dictionary<string, OpenEntry> table, OpenEntry entry)
    {
        foreach (KeyValuePair<string, OpenEntry> pair in table)
        {
            if (ReferenceEquals(pair.Value, entry))
            {
                table.Remove(pair.Key);
                return;
            }
        }
    }

    /// <summary>Kontingent für das Nettowachstum (siehe <see cref="DefaultQuotaBytes"/>).</summary>
    public long QuotaBytes { get; }

    /// <summary>Bisheriges Nettowachstum; kann durch Löschen vorhandener Dateien negativ werden.</summary>
    public long GrownBytes { get; private set; }

    /// <summary>Anzahl der Wachstumsschritte (je höchstens <see cref="StorageLock.ChunkBytes"/> unter einer Sperre); Diagnose und Tests.</summary>
    public int GrowSteps { get; private set; }

    public StringComparison NameComparison { get; }

    /// <summary>Größte Datei; ein Schreiben dahinter ist -EFBIG (sonst legte ein lseek weit nach vorn die Platte voll).</summary>
    public long MaxFileBytes { get; }

    /// <summary>
    /// Öffnet <paramref name="root"/> (absolut, ohne abschließendes '/') als Prozesswurzel und legt den Ordner bei Bedarf an.
    /// Der Pfad kommt vom Kernel, nicht vom Programm.
    /// </summary>
    public static ResultObject<DirectoryFileStore> Open(string root, ILinuxDiskLinks links, StringComparison nameComparison,
        long maxFileBytes = DefaultMaxFileBytes, long quotaBytes = DefaultQuotaBytes)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (string.IsNullOrEmpty(root) || root[0] != '/' || root.EndsWith('/') || root.Contains("/../", StringComparison.Ordinal) ||
            root.EndsWith("/..", StringComparison.Ordinal))
        {
            return ResultObjectFactory.CreateFailure<DirectoryFileStore>(ResultCode.InvalidArgument, "Die Prozesswurzel ist kein absoluter Ordnerpfad.");
        }

        try
        {
            StorageLock.Run(() => Directory.CreateDirectory(root));
            return ResultObjectFactory.CreateSuccess(new DirectoryFileStore(root, links, nameComparison, maxFileBytes, quotaBytes));
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<DirectoryFileStore>(exception);
        }
    }

    public long Lookup(string path, out LinuxStoreNode node)
    {
        node = new LinuxStoreNode(LinuxNodeKind.Missing, 0, 0, 0);
        if (path.Length == 0)
        {
            node = new LinuxStoreNode(LinuxNodeKind.Directory, 4096, DirectoryMode, 0);
            return 0;
        }

        if (!ValidPath(path))
        {
            return 0; // ein solcher Name kann hier nicht existieren
        }

        try
        {
            using (StorageLock.Enter())
            {
                node = LookupStorageLocked(path);
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    private LinuxStoreNode LookupStorageLocked(string path)
    {
        LinuxNodeKind kind = KindStorageLocked(path);
        switch (kind)
        {
            case LinuxNodeKind.File:
                var info = new FileInfo(Full(path));
                uint mode = (info.Attributes & FileAttributes.ReadOnly) != 0 ? ReadOnlyFileMode : FileMode;
                return new LinuxStoreNode(kind, info.Length, mode, ToNanos(info.LastWriteTimeUtc));
            case LinuxNodeKind.Directory:
                return new LinuxStoreNode(kind, 4096, DirectoryMode, 0);
            case LinuxNodeKind.Symlink:
                return new LinuxStoreNode(kind, 0, LinuxMode.PermissionMask, 0);
            default:
                return new LinuxStoreNode(LinuxNodeKind.Missing, 0, 0, 0);
        }
    }

    /// <summary>Art des Eintrags unter genau diesem Namen; ein Treffer über einen Zweitnamen (8.3) gilt als fehlend.</summary>
    private LinuxNodeKind KindStorageLocked(string path)
    {
        if (_pendingPaths.ContainsKey(path))
        {
            return LinuxNodeKind.Missing; // entfernt, nur noch offen: wie Linux nicht mehr unter dem Namen zu finden
        }

        LinuxNodeKind kind = RawKindStorageLocked(path, out bool alias);
        return alias ? LinuxNodeKind.Missing : kind;
    }

    /// <summary>Art des Eintrags, wie das Laufwerk ihn findet; <paramref name="alias"/>, wenn es ihn unter einem anderen Namen führt.</summary>
    private LinuxNodeKind RawKindStorageLocked(string path, out bool alias)
    {
        int slash = path.LastIndexOf('/');
        string parent = slash < 0 ? _root : string.Concat(_root, "/", path.AsSpan(0, slash));
        string name = path.Substring(slash + 1);
        LinuxNodeKind kind = _links.KindStorageLocked(parent, name, out string found);
        alias = kind != LinuxNodeKind.Missing && !SameName(found, name, NameComparison);
        return kind;
    }

    /// <summary>
    /// Gleicher Name im Sinn des Laufwerks: ordinal, bei einem Laufwerk ohne Groß-/Kleinschreibung ordinal ohne sie. Ein anderer
    /// Name (Kurzname statt Langname) ist nie gleich.
    /// </summary>
    internal static bool SameName(string? found, string requested, StringComparison comparison) =>
        string.Equals(found, requested, comparison == StringComparison.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public long ReadLink(string path, out string target)
    {
        target = string.Empty;
        if (!ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        try
        {
            using (StorageLock.Enter())
            {
                if (KindStorageLocked(path) != LinuxNodeKind.Symlink)
                {
                    return -LinuxErrno.ENOENT; // auch ein Treffer über einen Zweitnamen
                }

                target = _links.ReadLinkStorageLocked(Full(path));
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long List(string path, out string[] names)
    {
        names = Array.Empty<string>();
        if (path.Length > 0 && !ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        var result = new List<string>();
        try
        {
            using (StorageLock.Enter())
            {
                foreach (string entry in Directory.GetFileSystemEntries(Full(path)))
                {
                    string name = Path.GetFileName(entry);
                    if (ValidName(name) && !_pendingPaths.ContainsKey(path.Length == 0 ? name : path + "/" + name))
                    {
                        result.Add(name);
                    }
                }
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }

        result.Sort(StringComparer.Ordinal);
        names = result.ToArray();
        return 0;
    }

    public long OpenFile(string path, bool write, out ILinuxStoreFile? file)
    {
        file = null;
        if (!ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        try
        {
            using (StorageLock.Enter())
            {
                // Erneut ohne Folgen prüfen: zwischen Auflösung und Öffnen könnte ein anderer Weg einen Symlink hingelegt haben.
                if (KindStorageLocked(path) != LinuxNodeKind.File)
                {
                    return -LinuxErrno.ENOENT;
                }

                // Ohne Puffer (1 Byte): Schreibfehler zeigen sich beim write, nicht erst beim Schließen, das keinen Fehler melden kann.
                var stream = new FileStream(Full(path), System.IO.FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1);
                file = new DiskFile(this, stream, AttachStorageLocked(path));
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long CreateFile(string path, uint mode, out ILinuxStoreFile? file)
    {
        file = null;
        if (!ValidPath(path))
        {
            return -LinuxErrno.EINVAL;
        }

        try
        {
            using (StorageLock.Enter())
            {
                // CreateNew meldet einen belegten Namen nur als allgemeine IOException; vorher ohne Folgen nachsehen. Auch ein
                // Zweitname zählt als belegt: sonst landete das Anlegen auf dem vorhandenen Eintrag.
                if (OccupiedStorageLocked(path))
                {
                    return -LinuxErrno.EEXIST;
                }

                var stream = new FileStream(Full(path), System.IO.FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1);
                file = new DiskFile(this, stream, AttachStorageLocked(path));
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long CreateDirectory(string path, uint mode)
    {
        if (!ValidPath(path))
        {
            return -LinuxErrno.EINVAL;
        }

        try
        {
            using (StorageLock.Enter())
            {
                if (OccupiedStorageLocked(path))
                {
                    return -LinuxErrno.EEXIST; // auch ein Zweitname (8.3) oder der stehende Name einer entfernten, offenen Datei
                }

                Directory.CreateDirectory(Full(path));
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long CreateSymlink(string path, string target)
    {
        if (!ValidPath(path))
        {
            return -LinuxErrno.EINVAL;
        }

        try
        {
            using (StorageLock.Enter())
            {
                if (OccupiedStorageLocked(path))
                {
                    return -LinuxErrno.EEXIST; // auch ein Zweitname (8.3) oder der stehende Name einer entfernten, offenen Datei
                }

                return _links.CreateSymlinkStorageLocked(Full(path), target) ? 0 : -LinuxErrno.EPERM;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long DeleteFile(string path)
    {
        if (!ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        try
        {
            using (StorageLock.Enter())
            {
                LinuxNodeKind kind = KindStorageLocked(path);
                if (kind == LinuxNodeKind.Missing)
                {
                    return -LinuxErrno.ENOENT; // File.Delete meldet einen fehlenden Eintrag nicht
                }

                if (kind == LinuxNodeKind.Directory)
                {
                    return -LinuxErrno.EISDIR;
                }

                long freed = kind == LinuxNodeKind.File ? new FileInfo(Full(path)).Length : 0;
                File.Delete(Full(path));
                DetachStorageLocked(path, freed);
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long DeleteDirectory(string path)
    {
        if (!ValidPath(path))
        {
            return -LinuxErrno.ENOENT;
        }

        try
        {
            using (StorageLock.Enter())
            {
                LinuxNodeKind kind = KindStorageLocked(path);
                if (kind != LinuxNodeKind.Directory)
                {
                    return kind == LinuxNodeKind.Missing ? -LinuxErrno.ENOENT : -LinuxErrno.ENOTDIR;
                }

                if (HoldsPendingStorageLocked(path))
                {
                    return -LinuxErrno.ENOTEMPTY; // ein entfernter, offener Eintrag steht dort bis zum letzten Schließen noch (Cosmos)
                }

                Directory.Delete(Full(path), false);
                return 0;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    public long Rename(string from, string to)
    {
        if (!ValidPath(from))
        {
            return -LinuxErrno.ENOENT;
        }

        if (!ValidPath(to))
        {
            return -LinuxErrno.EINVAL;
        }

        try
        {
            using (StorageLock.Enter())
            {
                return RenameStorageLocked(from, to);
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Errno(exception);
        }
    }

    private long RenameStorageLocked(string from, string to)
    {
        string source = Full(from), target = Full(to);
        LinuxNodeKind kind = KindStorageLocked(from);
        if (kind == LinuxNodeKind.Missing)
        {
            return -LinuxErrno.ENOENT;
        }

        // Ziel nur unter seinem eigenen Namen: rename("/langername", "/LANGER~1") träfe denselben Eintrag über seinen Kurznamen,
        // File.Move mit Überschreiben könnte ihn dann verlieren.
        LinuxNodeKind existing = RawKindStorageLocked(to, out bool targetAlias);
        if (targetAlias)
        {
            return -LinuxErrno.EEXIST;
        }

        if (_pendingPaths.ContainsKey(to))
        {
            // Ziel ist der stehende Name einer entfernten, offenen Datei: Cosmos schöbe sie als "<name>.~replace" beiseite und die
            // Gutschrift käme doppelt. Solange das Laufwerk den Namen führt, belegt; sonst ist er frei.
            if (existing != LinuxNodeKind.Missing)
            {
                return -LinuxErrno.EEXIST;
            }

            _pendingPaths.Remove(to);
        }

        long replaced = existing == LinuxNodeKind.File ? new FileInfo(target).Length : 0;

        if (kind == LinuxNodeKind.Symlink)
        {
            // File.Move prüft unter .NET das Linkziel und scheitert an hängenden Links: Link neu anlegen, dann den alten entfernen
            // (nicht atomar, wie das Ersetzen eines Verzeichnisses).
            string linkTarget = _links.ReadLinkStorageLocked(source);
            if (existing == LinuxNodeKind.Directory)
            {
                Directory.Delete(target, false);
            }
            else if (existing != LinuxNodeKind.Missing)
            {
                File.Delete(target);
            }

            if (!_links.CreateSymlinkStorageLocked(target, linkTarget))
            {
                return -LinuxErrno.EPERM; // kann nicht vorkommen, wenn schon ein Link da ist; sonst nichts verändert außer dem ersetzten Ziel
            }

            File.Delete(source);
            if (existing != LinuxNodeKind.Missing)
            {
                DetachStorageLocked(to, replaced);
            }

            return 0;
        }

        if (kind != LinuxNodeKind.Directory)
        {
            File.Move(source, target, true);
            if (existing != LinuxNodeKind.Missing)
            {
                DetachStorageLocked(to, replaced);
            }

            MoveEntriesStorageLocked(from, to);
            return 0;
        }

        // Cosmos zieht beim Umbenennen nur die Handles des umbenannten Eintrags selbst mit (MoveOpenHandles, VfsManager.Paths.cs:369-383,
        // Pin 5790fa74), nicht die seiner Kinder; deren OpenedPath bleibt alt. Ein aufgeschobenes Entfernen löschte dann beim letzten
        // Schließen den alten Pfad (eine fremde, neue Datei dort), und unlink unter dem neuen Pfad fände den offenen Handle nicht
        // (MarkPendingIfOpen vergleicht OpenedPath, :425) und gäbe die Cluster unter ihm frei. Deshalb: Ordner mit offenen oder entfernten,
        // noch offenen Einträgen darunter nicht umbenennen.
        if (HoldsOpenBelowStorageLocked(from) || HoldsPendingStorageLocked(from))
        {
            return -LinuxErrno.EBUSY;
        }

        if (existing == LinuxNodeKind.Directory)
        {
            if (HoldsPendingStorageLocked(to))
            {
                return -LinuxErrno.ENOTEMPTY; // leer wirkend, aber ein entfernter, offener Eintrag steht dort noch
            }

            Directory.Delete(target, false); // leer, vom Dateisystem geprüft
        }

        Directory.Move(source, target);
        return 0;
    }

    private string Full(string path) => path.Length == 0 ? _root : _root + "/" + path;

    /// <summary>Jeder Teil muss ein auf dem Laufwerk eindeutiger Name sein.</summary>
    private static bool ValidPath(string path)
    {
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/')
            {
                continue;
            }

            if (!ValidName(path.Substring(start, i - start)))
            {
                return false;
            }

            start = i + 1;
        }

        return true;
    }

    /// <summary>Ist unter <paramref name="directory"/> eine Datei offen?</summary>
    private bool HoldsOpenBelowStorageLocked(string directory)
    {
        string prefix = directory + "/";
        foreach (string open in _openEntries.Keys)
        {
            if (open.StartsWith(prefix, NameComparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Steht unter <paramref name="directory"/> noch ein entfernter, offener Eintrag, den das Laufwerk führt?</summary>
    private bool HoldsPendingStorageLocked(string directory)
    {
        string prefix = directory + "/";
        foreach (string pending in _pendingPaths.Keys)
        {
            if (pending.StartsWith(prefix, NameComparison) && RawKindStorageLocked(pending, out _) != LinuxNodeKind.Missing)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <see cref="PathComponent.IsValid"/> (kein ".", "..", ':', '\\', abschließender Punkt, ".zehos-"), keine Steuerzeichen und kein
    /// "&lt;name&gt;.~replace" von Cosmos.
    /// </summary>
    private static bool ValidName(string name)
    {
        // "<name>.~replace" legt Cosmos für ein ersetztes, noch offenes Ziel bis zum letzten Schließen an (DropDisplacedEntry): kein Name
        // des Programms, nicht sichtbar und nicht anlegbar.
        if (!PathComponent.IsValid(name) || name.EndsWith(".~replace", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c < ' ' || c == '\u007f')
            {
                return false;
            }
        }

        return true;
    }

    private static long ToNanos(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) * 100;

    internal static long Errno(Exception exception) => exception switch
    {
        // Platte voll: .NET unter Linux trägt errno 28 als HResult, Windows ERROR_DISK_FULL/ERROR_HANDLE_DISK_FULL (0x70/0x27, auch als
        // 0x8007xxxx). Cosmos (Pin 5790fa74) setzt ConvertErrorPalToPlatform als Identität ein (Cosmos.Kernel.Plugs/System/IO/
        // InteropSysFilePlug.cs:336), der HResult ist dort also der PAL-Wert selbst: PalError.ENOSPC = 0x10034 und EFBIG = 0x10016
        // (Interop.Error.cs:37 und :23), gemeldet von FileDescriptorTable.cs:248. Unbekannte Werte bleiben -EIO.
        IOException { HResult: 0x10034 or unchecked((int)0x80070070) or unchecked((int)0x80070027) } => -LinuxErrno.ENOSPC,
        IOException { HResult: 0x10016 } => -LinuxErrno.EFBIG,
        // Nackte Codes je nach Wirt: Windows-Fehlernummern nur unter Windows (unter Linux ist 0x27 = 39 ENOTEMPTY), errno nur sonst.
        IOException { HResult: 0x70 or 0x27 } when OperatingSystem.IsWindows() => -LinuxErrno.ENOSPC,
        IOException { HResult: 28 } when !OperatingSystem.IsWindows() => -LinuxErrno.ENOSPC,
        IOException { HResult: 27 } when !OperatingSystem.IsWindows() => -LinuxErrno.EFBIG,
        FileNotFoundException or DirectoryNotFoundException => -LinuxErrno.ENOENT,
        UnauthorizedAccessException => -LinuxErrno.EACCES,
        PathTooLongException => -LinuxErrno.ENAMETOOLONG,
        IOException => -LinuxErrno.EIO,
        _ => -LinuxErrno.EINVAL,
    };

    /// <summary>
    /// Offene Datei auf dem Laufwerk; jede Stream-Operation unter der Sperre, höchstens <see cref="StorageLock.ChunkBytes"/> je
    /// Abschnitt. Wachsen (Lücke vor einem Schreiben, ftruncate nach oben) läuft in Schritten über <see cref="GrowStorage"/>.
    /// </summary>
    private sealed class DiskFile : ILinuxStoreFile
    {
        private readonly DirectoryFileStore _store;
        private readonly OpenEntry _entry;
        private FileStream? _stream;

        public DiskFile(DirectoryFileStore store, FileStream stream, OpenEntry entry)
        {
            _store = store;
            _stream = stream;
            _entry = entry;
        }

        public long Length()
        {
            if (_stream is null)
            {
                return -LinuxErrno.EBADF;
            }

            try
            {
                using (StorageLock.Enter())
                {
                    return _stream.Length;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }

        public long ReadAt(long offset, Span<byte> dst)
        {
            if (_stream is null)
            {
                return -LinuxErrno.EBADF;
            }

            if (offset < 0)
            {
                return -LinuxErrno.EINVAL;
            }

            Span<byte> part = dst.Slice(0, Math.Min(dst.Length, StorageLock.ChunkBytes));
            try
            {
                using (StorageLock.Enter())
                {
                    if (offset >= _stream.Length)
                    {
                        return 0;
                    }

                    _stream.Position = offset;
                    return _stream.Read(part);
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }

        public long WriteAt(long offset, ReadOnlySpan<byte> src)
        {
            if (_stream is null)
            {
                return -LinuxErrno.EBADF;
            }

            if (offset < 0)
            {
                return -LinuxErrno.EINVAL;
            }

            if (src.Length == 0)
            {
                return 0;
            }

            if (offset >= _store.MaxFileBytes)
            {
                return -LinuxErrno.EFBIG;
            }

            long length = Length();
            if (length < 0)
            {
                return length;
            }

            if (offset > length && offset - length + 1 > _store.QuotaBytes - _store.GrownBytes)
            {
                return -LinuxErrno.ENOSPC; // Lücke und mindestens ein Byte passen nicht: nichts anlegen
            }

            if (offset > length)
            {
                // Lücke erst in Schritten anlegen (Cosmos-FAT nullt sie sofort), dann schreiben.
                long grown = Grow(length, offset);
                if (grown < 0)
                {
                    return grown;
                }

                length = offset;
            }

            int count = (int)Math.Min(Math.Min(src.Length, _store.MaxFileBytes - offset), StorageLock.ChunkBytes);
            long growth = Math.Max(0, offset + count - length);
            long room = _store.QuotaBytes - _store.GrownBytes;
            if (growth > room)
            {
                // Wie bei voller Platte: kürzen, ganz ohne Platz -ENOSPC.
                count -= (int)(growth - Math.Max(0, room));
                if (count <= 0)
                {
                    return -LinuxErrno.ENOSPC;
                }

                growth = Math.Max(0, offset + count - length);
            }

            ReadOnlySpan<byte> part = src.Slice(0, count);
            try
            {
                using (StorageLock.Enter())
                {
                    _stream.Position = offset;
                    _stream.Write(part);
                    _store.GrownBytes += growth;
                    return part.Length;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }

        public long SetLength(long length)
        {
            if (_stream is null)
            {
                return -LinuxErrno.EBADF;
            }

            if (length < 0)
            {
                return -LinuxErrno.EINVAL;
            }

            if (length > _store.MaxFileBytes)
            {
                return -LinuxErrno.EFBIG;
            }

            long current = Length();
            if (current < 0)
            {
                return current;
            }

            if (length > current)
            {
                long grown = Grow(current, length);
                return grown < 0 ? grown : 0;
            }

            try
            {
                using (StorageLock.Enter())
                {
                    _stream.SetLength(length);
                    _store.GrownBytes -= current - length;
                    return 0;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return Errno(exception);
            }
        }

        /// <summary>
        /// Verlängert von <paramref name="from"/> auf <paramref name="to"/> in Schritten von höchstens <see cref="StorageLock.ChunkBytes"/>,
        /// jeder Schritt unter einer eigenen Sperre, damit andere Plattenzugriffe dazwischen drankommen. Prüft vorher das Kontingent
        /// für das ganze Wachstum (-ENOSPC, nichts geschrieben). Ein Fehler mitten drin lässt die bis dahin gewachsene Länge stehen.
        /// </summary>
        private long Grow(long from, long to) => _store.GrowStorage(_stream!, from, to);

        public long Close()
        {
            FileStream? stream = _stream;
            _stream = null;
            if (stream is null)
            {
                return 0;
            }

            // Schließen kann noch schreiben; scheitert das, ist die Datei trotzdem zu (wie close unter Linux nach -EIO).
            using (StorageLock.Enter())
            {
                long lengthAtClose = LengthForCreditStorageLocked(stream);
                try
                {
                    stream.Dispose();
                    return 0;
                }
                catch (Exception exception) when (FileErrors.IsFileError(exception))
                {
                    return Errno(exception);
                }
                finally
                {
                    _store.ReleaseStorageLocked(_entry, lengthAtClose);
                }
            }
        }

        /// <summary>Belegte Länge für die Gutschrift; ist sie nicht lesbar, 0 (dann keine Gutschrift, das Kontingent bleibt vorsichtig belegt).</summary>
        private static long LengthForCreditStorageLocked(FileStream stream)
        {
            try
            {
                return stream.Length;
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return 0;
            }
        }
    }

    /// <summary>Siehe <see cref="DiskFile"/>: Wachstum in Schritten mit Kontingent.</summary>
    private long GrowStorage(FileStream stream, long from, long to) => GrowInSteps(stream.SetLength, from, to);

    /// <summary>
    /// Verlängert über <paramref name="setLength"/> von <paramref name="from"/> auf <paramref name="to"/>, je Schritt höchstens
    /// <see cref="StorageLock.ChunkBytes"/> unter einer eigenen Sperre. Vorher Prüfung des Kontingents für das ganze Wachstum (-ENOSPC,
    /// nichts verändert). Scheitert ein Schritt (etwa Platte voll), wird die Datei auf <paramref name="from"/> zurückgesetzt und das
    /// Kontingent zurückgebucht; maßgeblich bleibt der Fehler des Schritts.
    /// </summary>
    internal long GrowInSteps(Action<long> setLength, long from, long to)
    {
        if (to - from > QuotaBytes - GrownBytes)
        {
            return -LinuxErrno.ENOSPC;
        }

        long current = from;
        while (current < to)
        {
            long next = Math.Min(to, current + StorageLock.ChunkBytes);
            try
            {
                using (StorageLock.Enter())
                {
                    setLength(next);
                    GrownBytes += next - current;
                    GrowSteps++;
                }
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                long error = Errno(exception);
                if (current > from)
                {
                    _ = RollBack(setLength, from, current);
                }

                return error;
            }

            current = next;
        }

        return 0;
    }

    /// <summary>
    /// Setzt nach einem gescheiterten Wachstumsschritt auf <paramref name="from"/> zurück. Scheitert auch das, bleibt die Datei bei
    /// <paramref name="reached"/> und das Kontingent dafür gebucht (stimmt mit der Platte); der Aufrufer meldet den Fehler des
    /// Wachstumsschritts, nicht diesen. false in diesem Fall (Tests).
    /// </summary>
    private bool RollBack(Action<long> setLength, long from, long reached)
    {
        try
        {
            using (StorageLock.Enter())
            {
                setLength(from);
                GrownBytes -= reached - from;
                return true;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return false;
        }
    }
}
