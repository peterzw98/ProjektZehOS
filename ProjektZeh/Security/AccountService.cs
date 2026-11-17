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
    internal const string Header = "ZEHOS-ACCOUNTS-1\n";
    internal const string Separator = "#ACL\n";
    internal const string UnknownAccountHash =
        "pbkdf2-sha256$600000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    internal const string CommandsFile = ".zehos-commands.db";
    private const string CommandsHeader = "ZEHOS-COMMANDS-1\n";
    // Ein Rechteeintrag je Datei (rund 90 Zeichen): die allgemeine 64-KiB-Textgrenze sperrte das System nach wenigen
    // hundert Dateien. 1 Mi Zeichen reichen je nach Pfadlaenge fuer einige tausend bis rund 10000 Eintraege.
    private const int MaximumMetadataLength = 1024 * 1024;
    private static readonly string[] AdministrativeCommands = ["user", "security", "disk", "update", "chperm", "reboot", "shutdown"];
    private static readonly string[] UnrestrictableCommands = ["login", "logout", "chperm"];
    private readonly Dictionary<string, bool> administratorCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Account> accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UserSession> sessions = [];
    private readonly FileService metadata;
    private readonly PasswordHasher hasher;
    public FileAccessControl Access { get; }
    public bool IsReady => Access.IsReady && accounts.Count != 0;
    /// <summary>Nur eine tatsaechlich fehlende Kontodatei erlaubt die Ersteinrichtung im normalen Build.</summary>
    public bool NeedsSetup { get; private set; }

    /// <summary>
    /// Gesetzt, wenn die Auswahl der Datenplatte fehlgeschlagen oder mehrdeutig ist: dann gilt auch eine fehlende Kontodatei
    /// nicht als Ersteinrichtung, damit ein falsch gewaehltes oder leeres Laufwerk kein Konto erhaelt.
    /// </summary>
    public string? SetupBlockedReason { get; set; }

    /// <summary>
    /// Sperre bei Rootfehlern (Spec 7a, Abschnitt 5): wird bei jedem Laden und direkt vor jeder Mutation durch Ersteinrichtung
    /// oder Recovery neu ausgewertet. Anders als SetupBlockedReason setzt ein Laufwerkswechsel sie nicht zurueck; nur bei der
    /// Konstruktion setzbar.
    /// </summary>
    public Func<string?>? MutationBlockedReason { get; init; }

    /// <summary>Wirft die Pruefung, wird gesperrt (fail-closed); nur der Typname geht in die Meldung.</summary>
    private string? MutationBlocked()
    {
        try
        {
            return MutationBlockedReason?.Invoke();
        }
        catch (Exception exception)
        {
            return $"cause='IdentityCheckFailed' message='Identitaetspruefung fehlgeschlagen: {exception.GetType().Name}. Keine Ersteinrichtung.'";
        }
    }

    /// <summary>Dateiname der Konten- und Rechtedatei auf der Datenplatte.</summary>
    public const string MetadataFile = ".zehos-security.db";
    private readonly string securityDirectory;

    /// <summary>Liefert, ob /data eingebunden ist. Sicherer Standard: nicht eingebunden; Kernel und Tests setzen es ausdruecklich.</summary>
    public Func<bool> IsDataMounted { get; set; } = () => false;

    /// <summary>
    /// Die Ersteinrichtung wird nur beim Boot angeboten: Standard geschlossen, nur der Boot-Load oeffnet das Fenster
    /// (der Kernel schliesst es danach wieder), so auch im Installer-Build.
    /// </summary>
    public bool SetupWindowOpen { get; set; }

    /// <summary>
    /// Ersteinrichtung nur beim Boot, nur mit eingebundenem /data und nur ohne vorhandene Kontendatei. Eine fehlende Datei auf
    /// einer waehrend der Sitzung gewechselten oder gar nicht eingebundenen Platte ergibt Sperre statt Einrichtung.
    /// </summary>
    public static bool MaySetUp(bool mounted, bool atBoot, bool hasAccountsFile) => mounted && atBoot && !hasAccountsFile;
    public IReadOnlyDictionary<string, bool> AdministratorCommands => administratorCommands;

    public AccountService(string root, PasswordHasher hasher)
        : this(root, hasher, StringComparison.OrdinalIgnoreCase, null)
    {
    }

    /// <summary>Etappe 3: Vergleich und Pfadwaechter der Verwaltungswurzel gelten auch fuer die Kontendatei selbst.</summary>
    /// <param name="securityDirectory">
    /// Ort der Konten-, Rechte- und Recovery-Dateien (Etappe 4.3: /etc/zehos im Root-Profil); ohne Angabe die Verwaltungswurzel
    /// wie im Legacy-Profil (/data).
    /// </param>
    public AccountService(string root, PasswordHasher hasher, StringComparison comparison, Func<string, bool, ResultErr>? pathGuard,
        Func<string, int?>? directoryEntryLimit = null, string? securityDirectory = null)
    {
        Access = new FileAccessControl(root, comparison, pathGuard);
        this.securityDirectory = securityDirectory ?? Access.RootDirectory;
        metadata = new FileService(this.securityDirectory, pathGuard: pathGuard, directoryEntryLimit: directoryEntryLimit);
        this.hasher = hasher;
        // Neue Dateieintraege duerfen an einer vollen Rechtedatei scheitern, ohne alles zu sperren (siehe Save).
        Access.Persist = () => Save(lockWhenFull: false);
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
        NeedsSetup = false;
        var read = metadata.ReadText(MetadataFile, allowMissing: true, MaximumMetadataLength);
        if (!read.IsOK()) return read.Status;
        if (read.Value == null)
        {
            bool mounted = IsDataMounted();
            string? blocked = SetupBlockedReason ?? MutationBlocked();
            if (blocked != null || !MaySetUp(mounted, SetupWindowOpen, hasAccountsFile: false))
                return ResultErr.CreateFailure(ResultCode.Failure, "Ersteinrichtung gesperrt: " + (blocked ??
                    (!mounted ? "/data ist nicht eingebunden." : "Sie wird nur beim Boot angeboten.")));
        }
        if (read.Value == null)
        {
            NeedsSetup = true;
            return ResultErr.CreateFailure(ResultCode.NotFound, "Noch keine Konten vorhanden. Administrator bei der Anmeldung anlegen.");
        }
        var commands = metadata.ReadText(CommandsFile, allowMissing: true);
        if (!commands.IsOK()) return commands.Status;
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
            if (commands.Value != null) RestoreCommands(commands.Value);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            Lock();
            return ResultErr.CreateFailure(ResultCode.Failure, "Konten/Rechte fehlen oder sind beschaedigt. Zugriff gesperrt; explizite Recovery erforderlich.");
        }
    }

    /// <summary>Ersteinrichtung wie die Recovery, aber nur solange keine Kontodatei existiert.</summary>
    public Func<Func<ResultErr>> SetupWork(UserSession actor, string name, string password)
    {
        if (!NeedsSetup) return () => FileAccessControl.Denied;
        var work = RecoveryWork(actor, name, password);
        return () =>
        {
            var apply = work();
            return () => NeedsSetup ? apply() : FileAccessControl.Denied();
        };
    }

    /// <summary>Eigene Kennwoerter darf jeder angemeldete Benutzer aendern, fremde nur ein Administrator.</summary>
    public Func<Func<ResultErr>> PasswordWork(UserSession actor, string name, string password)
    {
        if (!IsReady || actor.UserId == null || !accounts.TryGetValue(name, out var account) ||
            (account.Id != actor.UserId && !IsAdmin(actor))) return () => FileAccessControl.Denied;
        return () =>
        {
            string encoded = hasher.Hash(password);
            return () =>
            {
                if (!IsReady || !accounts.TryGetValue(name, out var current) || !ReferenceEquals(current, account) ||
                    actor.UserId == null || (account.Id != actor.UserId && !IsAdmin(actor))) return FileAccessControl.Denied();
                accounts[name] = account with { PasswordHash = encoded };
                return Save();
            };
        };
    }

    /// <summary>Loescht ein fremdes Konto; seine Eintraege gehen an den ausfuehrenden Administrator.</summary>
    public ResultErr DeleteUser(UserSession actor, string name)
    {
        if (!IsAdmin(actor) || !accounts.TryGetValue(name, out var target)) return FileAccessControl.Denied();
        // Der Ausfuehrende ist selbst Administrator; damit bleibt immer mindestens einer erhalten.
        if (target.Id == actor.UserId)
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Das eigene Konto kann nicht geloescht werden.");
        accounts.Remove(name);
        Access.RemoveUser(target.Id, actor.UserId!);
        foreach (var session in sessions)
            if (session.UserId == target.Id) session.Logout();
        return Save();
    }

    /// <summary>
    /// Prueft die Befehlstabelle. Ohne geladene Konten bleiben Einrichtung, Recovery und Energieaktionen erreichbar;
    /// die Befehle selbst pruefen dann weiterhin ihre eigenen Voraussetzungen.
    /// </summary>
    public ResultErr Authorize(UserSession actor, string command)
    {
        if (!IsReady || !administratorCommands.TryGetValue(command, out bool administrator) || !administrator || IsAdmin(actor))
            return ResultErr.CreateSuccess();
        return ResultErr.CreateFailure(ResultCode.Failure, $"{command}: Zugriff verweigert, nur fuer Administratoren.");
    }

    public ResultErr SetCommandAccess(UserSession actor, string command, bool administrator)
    {
        if (!IsAdmin(actor)) return FileAccessControl.Denied();
        foreach (string fixedCommand in UnrestrictableCommands)
            if (string.Equals(fixedCommand, command, StringComparison.OrdinalIgnoreCase))
                return ResultErr.CreateFailure(ResultCode.InvalidArgument, $"{command} bleibt fest geregelt.");
        administratorCommands[command] = administrator;
        return SaveCommands();
    }

    public Func<ResultErr> PrepareLogin(UserSession session, string name, string password)
        => LoginWork(session, name, password)();

    public Func<Func<ResultErr>> LoginWork(UserSession session, string name, string password)
    {
        Account? account = null;
        if (IsReady) accounts.TryGetValue(name, out account);
        // Ein zwischenzeitliches An- oder Abmelden, etwa ueber das Desktopmenue, darf nicht nachtraeglich ueberschrieben werden.
        int generation = session.Generation;
        return () =>
        {
            // Unbekannte Namen rechnen gleich lange, damit die Antwortzeit keine Kontonamen verraet.
            bool matches = PasswordHasher.Verify(password, account?.PasswordHash ?? UnknownAccountHash);
            bool valid = account != null && matches;
            return () =>
            {
                if (!valid || !IsReady || !accounts.TryGetValue(name, out var current) || !ReferenceEquals(current, account) ||
                    session.Generation != generation) return FileAccessControl.Denied();
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
            // Erst Rechte und Kontozustand erneut pruefen (ohne Dateisystemzugriff), dann die Home-Topologie vor jeder
            // Pruefung mit System.IO (folgt Links) und vor dem Speichern.
            if (!IsAdmin(actor) || accounts.ContainsKey(name) || accounts.Count >= 32 || Access.Contains(Home(name)))
                return FileAccessControl.Denied();
            var topology = CheckHomeTopology(name);
            if (!topology.IsOK()) return topology;
            if (Directory.Exists(Home(name)) || File.Exists(Home(name))) return FileAccessControl.Denied();
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
            // Vor jeder Mutation, auch wenn die Platte erst nach dem Boot (disk use) eingebunden wurde.
            string? blocked = MutationBlocked();
            if (blocked != null) return ResultErr.CreateFailure(ResultCode.Failure, "Konten gesperrt: " + blocked);
            string metadataPath = Path.Combine(securityDirectory, MetadataFile);
            string recoveredPath = Path.Combine(securityDirectory, $".zehos-security-recovery-{Guid.NewGuid():N}.db");
            // Quelle, Ziel und Home-Topologie vor jeder Mutation pruefen (Etappe 3): nichts verschieben oder speichern,
            // wenn ein Teil davon ueber einen Symlink oder einen fremden Mount liefe.
            foreach (var guarded in new[] { Access.Guard(metadataPath, creating: true), Access.Guard(recoveredPath, creating: true), CheckHomeTopology(name) })
                if (!guarded.IsOK()) return guarded;
            if (File.Exists(metadataPath))
            {
                try
                {
                    // Auch beliebig beschaedigte Metadaten ohne das Shell-Textlimit erhalten.
                    File.Move(metadataPath, recoveredPath, false);
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
            if (saved.IsOK()) saved = SaveCommands();
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
        administratorCommands.Clear();
        foreach (string command in AdministrativeCommands) administratorCommands[command] = true;
        foreach (var session in sessions) session.Logout();
    }

    private ResultErr Save(bool lockWhenFull = true)
    {
        var text = new StringBuilder(Header);
        foreach (var account in accounts.Values)
            text.Append(account.Id).Append('|').Append(account.Name).Append('|').Append(account.Administrator ? "admin" : "user").Append('|').Append(account.PasswordHash).Append('\n');
        text.Append(Separator).Append(Access.Serialize());
        // Vorab pruefen: Die gespeicherte Datei bleibt unveraendert, und FileAccessControl verwirft nur den neuen
        // Eintrag. Andere Aenderungen (Konten, Freigaben) sperren weiterhin, weil sie sonst nur im Speicher stuenden.
        if (text.Length > MaximumMetadataLength)
        {
            if (lockWhenFull) Lock();
            return ResultErr.CreateFailure(ResultCode.Failure, FileAccessControl.FullMessage);
        }
        var result = metadata.WriteText(MetadataFile, text.ToString(), MaximumMetadataLength);
        if (!result.IsOK()) Lock();
        else NeedsSetup = false;
        return result;
    }

    private ResultErr SaveCommands()
    {
        var text = new StringBuilder(CommandsHeader);
        foreach (var entry in administratorCommands) text.Append(entry.Key).Append('|').Append(entry.Value ? "admin" : "*").Append('\n');
        var result = metadata.WriteText(CommandsFile, text.ToString());
        if (!result.IsOK()) Lock();
        return result;
    }

    private void RestoreCommands(string text)
    {
        if (!text.StartsWith(CommandsHeader, StringComparison.Ordinal)) throw new FormatException();
        foreach (string line in text[CommandsHeader.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('|');
            if (parts.Length != 2 || !ValidName(parts[0]) || parts[1] is not ("admin" or "*")) throw new FormatException();
            administratorCommands[parts[0]] = parts[1] == "admin";
        }
    }

    private string Home(string name) => Path.Combine(Access.RootDirectory, "home", name);

    /// <summary>home und home/&lt;name&gt; duerfen nur fehlen oder echte Verzeichnisse im Mount der Wurzel sein.</summary>
    private ResultErr CheckHomeTopology(string name)
    {
        var homes = DirectoryOrMissing(Path.Combine(Access.RootDirectory, "home"));
        if (!homes.IsOK()) return homes.Status;
        // Fehlt home, entstehen beide Stufen neu; CreateHome prueft jede Stufe vor dem Anlegen erneut.
        if (!homes.Value) return ResultErr.CreateSuccess();
        var home = DirectoryOrMissing(Home(name));
        return home.IsOK() ? ResultErr.CreateSuccess() : home.Status;
    }

    /// <summary>
    /// Nach dem Pfadwaechter: true fuer ein Verzeichnis, false fuer einen wirklich fehlenden Eintrag. Eine Datei oder ein
    /// anderer Eintrag an dieser Stelle wird abgelehnt; echte E/A-Fehler werden weitergegeben, nicht als "fehlt" gewertet.
    /// </summary>
    private ResultObject<bool> DirectoryOrMissing(string path)
    {
        var guarded = Access.Guard(path, creating: true);
        if (!guarded.IsOK()) return ResultObjectFactory.CreateFailure<bool>(guarded);
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0
                ? ResultObjectFactory.CreateSuccess(true)
                : ResultObjectFactory.CreateFailure<bool>(ResultCode.Failure, $"cause='HomeNotDirectory' message='Kein Verzeichnis: {path}'");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ResultObjectFactory.CreateSuccess(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ResultObjectFactory.CreateFromException<bool>(exception);
        }
    }

    /// <summary>Legt home und home/&lt;name&gt; stufenweise an, jede Stufe erst nach dem Pfadwaechter.</summary>
    private ResultErr CreateHome(string name)
    {
        try
        {
            foreach (string directory in new[] { Path.Combine(Access.RootDirectory, "home"), Home(name) })
            {
                var guarded = Access.Guard(directory, creating: true);
                if (!guarded.IsOK()) { Lock(); return guarded; }
                Directory.CreateDirectory(directory);
            }

            return ResultErr.CreateSuccess();
        }
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
