using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Security;

internal sealed class FileAccessControl
{
    private const string Header = "ZEHOS-SECURITY-1";
    private readonly Dictionary<string, FilePermissions> entries = new(StringComparer.OrdinalIgnoreCase);
    public string RootDirectory { get; }
    public bool IsReady { get; private set; }
    internal Func<ResultErr>? Persist { get; set; }

    public FileAccessControl(string rootDirectory) => RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));

    internal void Initialize() { entries.Clear(); IsReady = true; }
    internal void Lock() { IsReady = false; entries.Clear(); }

    public ResultErr Check(UserSession session, string path, FilePermission permission, bool creating = false)
    {
        if (!IsReady || session.UserId == null || !IsManagedPath(path)) return Denied();
        if (entries.TryGetValue(path, out var rights))
        {
            if (!session.IsAdministrator && !rights.Allows(session.UserId, permission) && !rights.Allows("*", permission)) return Denied();
        }
        else if (!creating) return Denied();
        string? parent = Path.GetDirectoryName(path);
        while (parent != null && !string.Equals(path, RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            FilePermission parentPermission = creating ? FilePermission.Write : FilePermission.Read;
            if (!entries.TryGetValue(parent, out var parentRights) ||
                (!session.IsAdministrator && !parentRights.Allows(session.UserId, parentPermission) && !parentRights.Allows("*", parentPermission))) return Denied();
            creating = false;
            if (string.Equals(parent, RootDirectory, StringComparison.OrdinalIgnoreCase)) break;
            parent = Path.GetDirectoryName(parent);
        }
        return ResultErr.CreateSuccess();
    }

    public bool IsManagedPath(string path)
    {
        if (string.Equals(path, RootDirectory, StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        string relative = path[(RootDirectory.Length + 1)..];
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.StartsWith(".zehos-", StringComparison.OrdinalIgnoreCase) || part.IndexOfAny(['|', '\r', '\n', ':', '\\', '/', '\0']) >= 0) return false;
        }
        return true;
    }

    internal void Assign(string path, string ownerId)
    {
        if (!IsReady || !IsManagedPath(path) || string.IsNullOrEmpty(ownerId)) throw new ArgumentException("Ungueltige Rechtezuordnung.");
        entries[path] = new FilePermissions(ownerId);
    }

    internal void Grant(string path, string userId, FilePermission permission) => entries[path].Grant(userId, permission);
    internal bool IsOwner(string path, string userId) => entries.TryGetValue(path, out var rights) && rights.OwnerId == userId;
    internal bool Contains(string path) => entries.ContainsKey(path);
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
        if (!result.IsOK()) Lock();
        return result;
    }

    // Nimmt einen Eintrag zurueck, dessen Datei nie entstanden ist; ohne gespeicherte Rechte bleibt alles gesperrt.
    internal void ForgetCreated(string path)
    {
        if (entries.Remove(path) && !(Persist?.Invoke() ?? Denied()).IsOK()) Lock();
    }

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
                if (!IsManagedPath(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(parts[2])) throw new FormatException();
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
