using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Security;

internal sealed class AccountService
{
    private sealed record Account(string Id, string Name, bool Administrator, string PasswordHash);
    private const string Header = "ZEHOS-ACCOUNTS-1\n";
    private const string Separator = "#ACL\n";
    private readonly Dictionary<string, Account> accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UserSession> sessions = [];
    private readonly FileService metadata;
    private readonly PasswordHasher hasher;
    public FileAccessControl Access { get; }
    public bool IsReady => Access.IsReady && accounts.Count != 0;

    public AccountService(string root, PasswordHasher hasher)
    {
        Access = new FileAccessControl(root);
        metadata = new FileService(Access.RootDirectory);
        this.hasher = hasher;
        Access.Persist = Save;
    }

    public UserSession CreateSession()
    {
        var session = new UserSession();
        sessions.Add(session);
        return session;
    }

    public void ReleaseSession(UserSession session) { session.Logout(); sessions.Remove(session); }

    public ResultErr Load()
    {
        Lock();
        var read = metadata.ReadText(".zehos-security.db");
        if (!read.IsOK()) return read.Status;
        try
        {
            string text = read.Value!;
            int split = text.IndexOf(Separator, StringComparison.Ordinal);
            if (!text.StartsWith(Header, StringComparison.Ordinal) || split < Header.Length) throw new FormatException();
            foreach (string line in text[Header.Length..split].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|');
                if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out _) || !ValidName(parts[1]) ||
                    parts[2] is not ("admin" or "user") || !PasswordHasher.IsValid(parts[3]) || accounts.Count >= 32) throw new FormatException();
                foreach (var existing in accounts.Values)
                    if (existing.Id == parts[0]) throw new FormatException();
                accounts.Add(parts[1], new Account(parts[0], parts[1], parts[2] == "admin", parts[3]));
            }
            bool administrator = false;
            foreach (var account in accounts.Values) administrator |= account.Administrator;
            if (!administrator || !Access.Restore(text[(split + Separator.Length)..]).IsOK()) throw new FormatException();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var account in accounts.Values) ids.Add(account.Id);
            if (!Access.ValidateUsers(ids)) throw new FormatException();
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            Lock();
            return ResultErr.CreateFailure(ResultCode.Failure, "Konten/Rechte fehlen oder sind beschaedigt. Zugriff gesperrt; explizite Recovery erforderlich.");
        }
    }

    public Func<ResultErr> PrepareLogin(UserSession session, string name, string password)
        => LoginWork(session, name, password)();

    public Func<Func<ResultErr>> LoginWork(UserSession session, string name, string password)
    {
        Account? account = null;
        if (IsReady) accounts.TryGetValue(name, out account);
        return () =>
        {
            bool valid = account != null && PasswordHasher.Verify(password, account.PasswordHash);
            return () =>
            {
                if (!valid || !IsReady || !accounts.TryGetValue(name, out var current) || !ReferenceEquals(current, account)) return FileAccessControl.Denied();
                session.Login(account!.Id, account.Name, account.Administrator, Home(account.Name));
                return ResultErr.CreateSuccess();
            };
        };
    }

    public Func<ResultErr> PrepareCreateUser(UserSession actor, string name, bool administrator, string password)
        => CreateUserWork(actor, name, administrator, password)();

    public Func<Func<ResultErr>> CreateUserWork(UserSession actor, string name, bool administrator, string password)
    {
        if (!IsAdmin(actor) || !ValidName(name) || accounts.ContainsKey(name) || accounts.Count >= 32)
            return () => FileAccessControl.Denied;
        return () =>
        {
            string encoded = hasher.Hash(password);
            return () =>
            {
            if (!IsAdmin(actor) || accounts.ContainsKey(name) || accounts.Count >= 32 || Access.Contains(Home(name)) ||
                Directory.Exists(Home(name)) || File.Exists(Home(name))) return FileAccessControl.Denied();
            var account = new Account(Guid.NewGuid().ToString("N"), name, administrator, encoded);
            accounts.Add(name, account);
            Access.Assign(Home(name), account.Id);
            var saved = Save();
            if (!saved.IsOK()) return saved;
            return CreateHome(name);
            };
        };
    }

    public Func<ResultErr> PrepareRecovery(UserSession actor, string name, string password)
        => RecoveryWork(actor, name, password)();

    public Func<Func<ResultErr>> RecoveryWork(UserSession actor, string name, string password)
    {
        if (!ValidName(name)) return () => FileAccessControl.Denied;
        return () =>
        {
            string encoded = hasher.Hash(password);
            return () =>
            {
            if (File.Exists(Path.Combine(Access.RootDirectory, ".zehos-security.db")))
            {
                try
                {
                    // Auch beliebig beschaedigte Metadaten ohne das Shell-Textlimit erhalten.
                    File.Move(Path.Combine(Access.RootDirectory, ".zehos-security.db"),
                        Path.Combine(Access.RootDirectory, $".zehos-security-recovery-{Guid.NewGuid():N}.db"), false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { return ResultErr.CreateFromException(exception); }
            }
            Lock();
            Access.Initialize();
            var account = new Account(Guid.NewGuid().ToString("N"), name, true, encoded);
            accounts.Add(name, account);
            Access.Assign(Access.RootDirectory, account.Id);
            Access.Grant(Access.RootDirectory, "*", FilePermission.Read);
            string homes = Path.Combine(Access.RootDirectory, "home");
            Access.Assign(homes, account.Id);
            Access.Grant(homes, "*", FilePermission.Read);
            Access.Assign(Home(name), account.Id);
            var saved = Save();
            if (!saved.IsOK()) return saved;
            var created = CreateHome(name);
            if (!created.IsOK()) return created;
            actor.Login(account.Id, name, true, Home(name));
            return ResultErr.CreateSuccess();
            };
        };
    }

    public ResultErr Grant(UserSession actor, string path, string name, FilePermission permission)
    {
        if (!IsReady || actor.UserId == null || (!IsAdmin(actor) && !Access.IsOwner(path, actor.UserId)) ||
            !Access.Contains(path) || !accounts.TryGetValue(name, out var target)) return FileAccessControl.Denied();
        Access.Grant(path, target.Id, permission);
        return Save();
    }

    public ResultErr Adopt(UserSession actor, string path, string ownerName)
    {
        if (!IsAdmin(actor) || !Access.IsManagedPath(path) || Access.Contains(path) || !accounts.TryGetValue(ownerName, out var owner)) return FileAccessControl.Denied();
        try { _ = File.GetAttributes(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ResultErr.CreateFromException(exception); }
        Access.Assign(path, owner.Id);
        return Save();
    }

    public bool IsAdmin(UserSession actor) => IsReady && actor.IsAdministrator && actor.UserId != null;

    public string[] ListUsers(UserSession actor)
    {
        if (!IsAdmin(actor)) return [];
        var result = new List<string>();
        foreach (var account in accounts.Values) result.Add($"{account.Name} {account.Id} {(account.Administrator ? "admin" : "user")}");
        return result.ToArray();
    }

    public void Lock()
    {
        Access.Lock();
        accounts.Clear();
        foreach (var session in sessions) session.Logout();
    }

    private ResultErr Save()
    {
        var text = new StringBuilder(Header);
        foreach (var account in accounts.Values)
            text.Append(account.Id).Append('|').Append(account.Name).Append('|').Append(account.Administrator ? "admin" : "user").Append('|').Append(account.PasswordHash).Append('\n');
        text.Append(Separator).Append(Access.Serialize());
        var result = metadata.WriteText(".zehos-security.db", text.ToString());
        if (!result.IsOK()) Lock();
        return result;
    }

    private string Home(string name) => Path.Combine(Access.RootDirectory, "home", name);

    private ResultErr CreateHome(string name)
    {
        try { Directory.CreateDirectory(Home(name)); return ResultErr.CreateSuccess(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Lock(); return ResultErr.CreateFromException(exception); }
    }

    private static bool ValidName(string name)
    {
        if (name.Length is < 1 or > 32) return false;
        foreach (char character in name)
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) return false;
        return true;
    }
}
