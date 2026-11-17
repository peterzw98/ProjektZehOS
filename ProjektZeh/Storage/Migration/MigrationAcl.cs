using System;
using System.Collections.Generic;
using System.Text;
using ProjektZeh.Security;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Migration;

/// <summary>
/// Rein textuelle Umschreibung der Kontendatei (Spec E4.4 §3): Kontozeilen byteidentisch, ACL-Pfade ueber die Planzuordnung.
/// Bewusst ohne <see cref="FileAccessControl"/>, weil dessen Path.GetFullPath auf Windows-Hosts andere Pfade liefert (huang P2-1).
/// </summary>
internal static class MigrationAcl
{
    private const string AclHeader = "ZEHOS-SECURITY-1";

    private sealed class Entry(string owner)
    {
        public string Owner { get; } = owner;
        public SortedDictionary<string, string> Grants { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Kontonamen aus der Kontendatei; die vollstaendige Pruefung der Konten bleibt beim AccountService.</summary>
    public static ResultObject<string[]> ReadAccountNames(string securityText)
    {
        var split = Split(securityText);
        if (!split.IsOK()) return ResultObjectFactory.CreateFailure<string[]>(split.Status);
        var names = new List<string>();
        foreach (string line in split.Value!.Accounts[AccountService.Header.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('|');
            if (parts.Length != 4) return Corrupt<string[]>("Kontozeile ungueltig.");
            names.Add(parts[1]);
        }
        return ResultObjectFactory.CreateSuccess(names.ToArray());
    }

    /// <summary>Liefert die neue Kontendatei; Hinweise (verworfene Eintraege) gehen an <paramref name="log"/>.</summary>
    public static ResultObject<string> Rewrite(string securityText, MigrationPlan plan, Action<string> log)
    {
        var split = Split(securityText);
        if (!split.IsOK()) return ResultObjectFactory.CreateFailure<string>(split.Status);
        var parsed = ParseAcl(split.Value!.Acl);
        if (!parsed.IsOK()) return ResultObjectFactory.CreateFailure<string>(parsed.Status);

        var mapped = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (path, entry) in parsed.Value!)
        {
            if (!plan.TryMapSource(path, out string target))
            {
                log($"Rechteeintrag ohne Datei verworfen: {path}");
                continue;
            }
            // Systemdaten sind fuer den Dateidienst gesperrt (Paket 4.2); dort gibt es keine Rechteeintraege.
            if (KernelPath.IsAtOrBelow(target, "/etc", StringComparison.Ordinal) || KernelPath.IsAtOrBelow(target, "/var", StringComparison.Ordinal))
            {
                log($"Rechteeintrag fuer Systemdaten verworfen: {path}");
                continue;
            }
            // Zwei Schreibweisen derselben FAT-Datei haette schon der Kernel abgelehnt; hier ist das ein Schaden.
            if (!mapped.TryAdd(target, entry)) return Corrupt<string>($"Doppelter Rechteeintrag fuer {target}.");
        }
        if (!mapped.ContainsKey("/")) return Corrupt<string>("Rechteeintrag der Wurzel fehlt.");

        // Neue Verzeichnisse (/srv, /srv/legacy-data, .config/zehos ...) erben vom naechsten abgebildeten Elterneintrag, sonst
        // scheitert Check an der Elternkette (huang P2-2). Eltern stehen vor ihren Kindern, daher reicht ein Durchlauf.
        foreach (string directory in plan.Directories)
        {
            if (mapped.ContainsKey(directory) || !plan.IsCreatedDirectory(directory) ||
                KernelPath.IsAtOrBelow(directory, "/etc", StringComparison.Ordinal) || KernelPath.IsAtOrBelow(directory, "/var", StringComparison.Ordinal)) continue;
            string? parent = KernelPath.Parent(directory);
            while (parent != null && !mapped.ContainsKey(parent)) parent = KernelPath.Parent(parent);
            var inherited = mapped[parent ?? "/"];
            var copy = new Entry(inherited.Owner);
            foreach (var grant in inherited.Grants) copy.Grants[grant.Key] = grant.Value;
            mapped[directory] = copy;
        }

        var text = new StringBuilder(split.Value!.Accounts).Append(AccountService.Separator).Append(AclHeader).Append('\n');
        foreach (var (path, entry) in mapped)
        {
            text.Append("P|").Append(Encode(path)).Append('|').Append(entry.Owner).Append('\n');
            foreach (var grant in entry.Grants)
                text.Append("G|").Append(Encode(path)).Append('|').Append(grant.Key).Append('|').Append(grant.Value).Append('\n');
        }
        return ResultObjectFactory.CreateSuccess(text.ToString());
    }

    /// <summary>
    /// Desktopdatei: ein absoluter Hintergrundpfad wird ueber den Plan abgebildet, ohne Zuordnung geleert und protokolliert
    /// (huang P3 a). Relative Pfade gelten weiter vom Home aus. Alle anderen Zeilen bleiben unveraendert.
    /// </summary>
    public static string RewriteDesktop(string text, MigrationPlan plan, Action<string> log)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int separator = lines[i].IndexOf('=');
            if (separator < 0 || lines[i][..separator].Trim() != "image") continue;
            string image = lines[i][(separator + 1)..].Trim();
            if (!image.StartsWith('/')) continue;
            if (plan.TryMapSource(image, out string target) && target != "/") lines[i] = "image=" + target;
            else
            {
                log($"Hintergrundbild ohne Ziel, Einstellung geleert: {image}");
                lines[i] = "image=";
            }
        }
        return string.Join('\n', lines);
    }

    private sealed record Parts(string Accounts, string Acl);

    private static ResultObject<Parts> Split(string text)
    {
        int split = text.IndexOf(AccountService.Separator, StringComparison.Ordinal);
        if (!text.StartsWith(AccountService.Header, StringComparison.Ordinal) || split < AccountService.Header.Length)
            return Corrupt<Parts>("Kopf oder #ACL-Trenner fehlt.");
        return ResultObjectFactory.CreateSuccess(new Parts(text[..split], text[(split + AccountService.Separator.Length)..]));
    }

    private static ResultObject<List<(string Path, Entry Entry)>> ParseAcl(string acl)
    {
        string[] lines = acl.Split('\n');
        if (lines[0] != AclHeader) return Corrupt<List<(string, Entry)>>("ACL-Kopf fehlt.");
        var entries = new List<(string, Entry)>();
        var byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            string[] parts = lines[i].Split('|');
            if (parts.Length < 3 || !TryDecode(parts[1], out string path) || parts[2].Length == 0)
                return Corrupt<List<(string, Entry)>>($"ACL-Zeile {i + 1} ungueltig.");
            if (parts[0] == "P" && parts.Length == 3)
            {
                var entry = new Entry(parts[2]);
                if (!byPath.TryAdd(path, entry)) return Corrupt<List<(string, Entry)>>($"Doppelter Pfad in ACL-Zeile {i + 1}.");
                entries.Add((path, entry));
            }
            else if (parts[0] == "G" && parts.Length == 4 && int.TryParse(parts[3], out int value) && value is >= 1 and <= 3 &&
                byPath.TryGetValue(path, out var owner))
                owner.Grants[parts[2]] = parts[3];
            else return Corrupt<List<(string, Entry)>>($"ACL-Zeile {i + 1} ungueltig.");
        }
        return ResultObjectFactory.CreateSuccess(entries);
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static bool TryDecode(string value, out string path)
    {
        path = "";
        try
        {
            path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static ResultObject<T> Corrupt<T>(string message) =>
        ResultObjectFactory.CreateFailure<T>(ResultCode.Failure, $"cause='SecurityDatabaseInvalid' message='{message}'");
}
