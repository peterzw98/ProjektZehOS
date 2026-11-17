using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Security;

internal sealed class FileAccessControl
{
    private const string Header = "ZEHOS-SECURITY-1";
    private readonly Dictionary<string, FilePermissions> entries;
    // Etappe 3: Vergleich je Verwaltungswurzel (ext2 Ordinal, FAT OrdinalIgnoreCase) fuer jede Schluessel- und Praefixpruefung.
    private readonly StringComparison comparison;
    public string RootDirectory { get; }
    internal StringComparison Comparison => comparison;
    /// <summary>Prueft Symlinks und Mountgrenzen (Pfad, creating) vor jeder Rechteentscheidung; null nur ohne Dateisystem (Hosttests).</summary>
    internal Func<string, bool, ResultErr>? PathGuard { get; }
    public bool IsReady { get; private set; }
    internal Func<ResultErr>? Persist { get; set; }

    /// <summary>FAT-Verhalten wie bisher: OrdinalIgnoreCase, ohne Pfadwaechter.</summary>
    public FileAccessControl(string rootDirectory) : this(rootDirectory, StringComparison.OrdinalIgnoreCase) { }

    /// <summary>
    /// Vergleich je Verwaltungswurzel: Ordinal auf ext2, OrdinalIgnoreCase auf FAT. Gilt fuer Eintraege, Restore, Contains,
    /// Verschieben und jede Praefix- und Elternpruefung. <paramref name="pathGuard"/> prueft vor jeder Entscheidung Symlinks
    /// und Mountgrenzen; seine Ablehnung oder Ausnahme sperrt, auch fuer Administratoren.
    /// </summary>
    public FileAccessControl(string rootDirectory, StringComparison comparison, Func<string, bool, ResultErr>? pathGuard = null)
    {
        // TrimEndingDirectorySeparator laesst die Wurzel ("/", "C:\") mit ihrem Trenner stehen.
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        this.comparison = comparison;
        entries = new(StringComparer.FromComparison(comparison));
        PathGuard = pathGuard;
    }

    /// <summary>Wendet den Waechter an; eine Ausnahme gilt als Ablehnung.</summary>
    internal ResultErr Guard(string path, bool creating) => RunGuard(PathGuard, path, creating);

    /// <summary>Gemeinsamer Aufruf fuer Rechtepruefung und Metadatenzugriff: ohne Waechter erlaubt, Ausnahme = Ablehnung.</summary>
    internal static ResultErr RunGuard(Func<string, bool, ResultErr>? guard, string path, bool creating)
    {
        if (guard == null) return ResultErr.CreateSuccess();
        try
        {
            return guard(path, creating);
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, $"cause='PathGuardFailed' message='Pfadpruefung fehlgeschlagen: {exception.GetType().Name}'");
        }
    }
    internal void Initialize() { entries.Clear(); IsReady = true; }
    internal void Lock() { IsReady = false; entries.Clear(); }

    public ResultErr Check(UserSession session, string path, FilePermission permission, bool creating = false)
    {
        if (!IsReady || session.UserId == null || !IsManagedPath(path)) return Denied();
        if (!Guard(path, creating).IsOK()) return Denied();
        if (entries.TryGetValue(path, out var rights))
        {
            if (!session.IsAdministrator && !rights.Allows(session.UserId, permission) && !rights.Allows("*", permission)) return Denied();
        }
        else if (!creating) return Denied();
        string? parent = Path.GetDirectoryName(path);
        while (parent != null && !string.Equals(path, RootDirectory, comparison))
        {
            FilePermission parentPermission = creating ? FilePermission.Write : FilePermission.Read;
            if (!entries.TryGetValue(parent, out var parentRights) ||
                (!session.IsAdministrator && !parentRights.Allows(session.UserId, parentPermission) && !parentRights.Allows("*", parentPermission))) return Denied();
            creating = false;
            if (string.Equals(parent, RootDirectory, comparison)) break;
            parent = Path.GetDirectoryName(parent);
        }
        return ResultErr.CreateSuccess();
    }

    public bool IsManagedPath(string path)
    {
        if (string.Equals(path, RootDirectory, comparison)) return true;
        // Fuer die Wurzel "/" ist das Praefix "/" selbst, nicht "//".
        string prefix = Path.EndsInDirectorySeparator(RootDirectory) ? RootDirectory : RootDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, comparison)) return false;
        string relative = path[prefix.Length..];
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        if (IsSystemDataPath(parts)) return false;
        foreach (string part in parts)
            if (!PathComponent.IsValid(part)) return false;
        return true;
    }

    /// <summary>
    /// Etappe 4.2: Systemdaten des Root-Profils (/etc/zehos, /var/lib/zehos) sind fuer den Dateidienst immer gesperrt, auch fuer
    /// Administratoren; nur die zustaendigen Dienste schreiben dort ueber eigene Dateidienste mit Pfadwaechter. Gilt nur, wenn die
    /// Verwaltungswurzel das Dateisystem selbst ist; unter /data (Legacy) bleibt jeder Pfad wie bisher verwaltbar.
    /// </summary>
    private bool IsSystemDataPath(string[] parts) =>
        string.Equals(RootDirectory, Path.GetPathRoot(RootDirectory), comparison) &&
        ((parts.Length >= 2 && string.Equals(parts[0], "etc", comparison) && string.Equals(parts[1], "zehos", comparison)) ||
         (parts.Length >= 3 && string.Equals(parts[0], "var", comparison) && string.Equals(parts[1], "lib", comparison) && string.Equals(parts[2], "zehos", comparison)));

    internal void Assign(string path, string ownerId)
    {
        if (!IsReady || !IsManagedPath(path) || string.IsNullOrEmpty(ownerId)) throw new ArgumentException("Ungueltige Rechtezuordnung.");
        entries[path] = new FilePermissions(ownerId);
    }

    internal void Grant(string path, string userId, FilePermission permission) => entries[path].Grant(userId, permission);
    internal bool IsOwner(string path, string userId) => entries.TryGetValue(path, out var rights) && rights.OwnerId == userId;
    internal bool Contains(string path) => entries.ContainsKey(path);
    /// <summary>Gibt Eintraege eines geloeschten Kontos an den neuen Eigentuemer und entfernt seine Freigaben.</summary>
    internal void RemoveUser(string userId, string newOwnerId)
    {
        foreach (string path in new List<string>(entries.Keys))
        {
            var rights = entries[path];
            if (rights.OwnerId != userId) { rights.Grant(userId, FilePermission.None); continue; }
            var replacement = new FilePermissions(newOwnerId);
            foreach (var grant in rights.Grants)
                if (grant.Key != userId && grant.Key != newOwnerId) replacement.Grant(grant.Key, grant.Value);
            entries[path] = replacement;
        }
    }

    internal bool ValidateUsers(HashSet<string> ids)
    {
        foreach (var entry in entries.Values)
        {
            if (!ids.Contains(entry.OwnerId)) return false;
            foreach (var grant in entry.Grants)
                if (grant.Key != "*" && !ids.Contains(grant.Key)) return false;
        }
        return true;
    }

    internal ResultErr RegisterCreated(UserSession session, string path)
    {
        if (Contains(path)) return ResultErr.CreateSuccess();
        Assign(path, session.UserId!);
        var result = Persist?.Invoke() ?? Denied();
        if (IsFull(result)) entries.Remove(path);
        else if (!result.IsOK()) Lock();
        return result;
    }

    // Nimmt einen Eintrag zurueck, dessen Datei nie entstanden ist; ohne gespeicherte Rechte bleibt alles gesperrt.
    internal void ForgetCreated(string path)
    {
        if (entries.Remove(path) && !(Persist?.Invoke() ?? Denied()).IsOK()) Lock();
    }

    // Zielrechte vor der Umbenennung speichern: Nach einem Abbruch bleibt die Datei geschuetzt.
    internal ResultErr PrepareMove(string source, string target)
    {
        if (!IsReady || !entries.TryGetValue(source, out var rights) || !IsManagedPath(target) || entries.ContainsKey(target)) return Denied();
        entries.Add(target, rights);
        var result = Persist?.Invoke() ?? Denied();
        if (IsFull(result)) entries.Remove(target);
        else if (!result.IsOK()) Lock();
        return result;
    }

    internal ResultErr RemoveEntry(string path)
    {
        if (!IsReady) return Denied();
        entries.Remove(path);
        var result = Persist?.Invoke() ?? Denied();
        if (!result.IsOK()) Lock();
        return result;
    }

    // Eine volle Rechtedatei ist ein erwarteter Grenzfall, kein Schaden: nur der neue Eintrag wird verworfen, die
    // gespeicherte Fassung bleibt unveraendert und das System bedienbar.
    internal const string FullMessage = "Rechtedatenbank voll; Datei nicht angelegt.";
    private static bool IsFull(ResultErr result) => !result.IsOK() && result.Message == FullMessage;

    public string Serialize()
    {
        var text = new StringBuilder(Header + "\n");
        foreach (var entry in entries)
        {
            text.Append("P|").Append(Encode(entry.Key)).Append('|').Append(entry.Value.OwnerId).Append('\n');
            foreach (var grant in entry.Value.Grants)
                text.Append("G|").Append(Encode(entry.Key)).Append('|').Append(grant.Key).Append('|').Append((int)grant.Value).Append('\n');
        }
        return text.ToString();
    }

    public ResultErr Restore(string text)
    {
        Lock();
        try
        {
            string[] lines = text.Split('\n');
            if (lines.Length < 2 || lines[0] != Header) return Denied();
            foreach (string line in lines.AsSpan(1))
            {
                if (line.Length == 0) continue;
                string[] parts = line.Split('|');
                if (parts.Length < 3) throw new FormatException();
                string path = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                if (!IsManagedPath(path) || !string.Equals(Path.GetFullPath(path), path, comparison) || string.IsNullOrEmpty(parts[2])) throw new FormatException();
                if (parts[0] == "P" && parts.Length == 3) entries.Add(path, new FilePermissions(parts[2]));
                else if (parts[0] == "G" && parts.Length == 4 && int.TryParse(parts[3], out int value) && value is >= 1 and <= 3)
                    entries[path].Grant(parts[2], (FilePermission)value);
                else throw new FormatException();
            }
            IsReady = entries.ContainsKey(RootDirectory);
            return IsReady ? ResultErr.CreateSuccess() : Denied();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or KeyNotFoundException)
        {
            Lock();
            return ResultErr.CreateFailure(ResultCode.Failure, "Konten-/Rechtemetadaten beschaedigt; Zugriff gesperrt.");
        }
    }

    internal static ResultErr Denied() => ResultErr.CreateFailure(ResultCode.Failure, "Zugriff verweigert. Anmeldung und gueltige Dateirechte erforderlich.");
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
