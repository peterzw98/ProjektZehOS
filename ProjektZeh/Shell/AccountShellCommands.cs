using System;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Shell;

internal sealed class AccountShellCommands(AccountService accounts, UserSession session, FileService files,
    Action<Func<string, Func<Func<ResultErr>>>> beginSecret, bool allowRecovery)
{
    public ShellCommand[] Commands =>
    [
        new("whoami", "whoami", "Zeigt die aktuelle Identitaet.", false, WhoAmI),
        new("login", "login <Name>", "Meldet nur diese Sitzung mit geschuetzter Kennworteingabe an.", true, Login),
        new("logout", "logout", "Meldet nur diese Sitzung ab.", false, Logout),
        new("user", "user list|add <Name> admin|user", "Verwaltet Konten als Administrator.", true, User),
        new("grant", "grant <Pfad> <Name> none|r|rw", "Vergibt Rechte an eigenen Eintraegen.", true, Grant),
        new("security", "security adopt <Pfad> <Name>|recover <Adminname>", "Ordnet bestehende Dateien zu; Recovery nur im expliziten Sicherheits-Recovery-Build.", true, Security)
    ];

    private ResultErr WhoAmI(string[] arguments, ITextOutput output)
    {
        output.WriteLine(session.UserId == null ? "Nicht angemeldet." : $"{session.UserName} ({session.UserId}) {(session.IsAdministrator ? "admin" : "user")}; Home: {session.HomeDirectory}");
        return ResultErr.CreateSuccess();
    }

    private ResultErr Login(string[] arguments, ITextOutput output)
    {
        if (arguments.Length != 1) return Invalid();
        if (!accounts.IsReady) return FileAccessControl.Denied();
        string name = arguments[0];
        Prompt(output, password =>
        {
            var work = accounts.LoginWork(session, name, password);
            return () =>
            {
                var complete = work();
                return () =>
                {
                    var result = complete();
                    if (result.IsOK()) files.ResetDirectory(session.HomeDirectory!);
                    return result;
                };
            };
        });
        return ResultErr.CreateSuccess();
    }

    private ResultErr Logout(string[] arguments, ITextOutput output)
    {
        session.Logout();
        files.ResetDirectory(accounts.Access.RootDirectory);
        return ResultErr.CreateSuccess();
    }

    private ResultErr User(string[] arguments, ITextOutput output)
    {
        if (!accounts.IsAdmin(session)) return FileAccessControl.Denied();
        if (arguments.Length == 1 && arguments[0] == "list")
        {
            foreach (string user in accounts.ListUsers(session)) output.WriteLine(user);
            return ResultErr.CreateSuccess();
        }
        if (arguments.Length != 3 || arguments[0] != "add" || arguments[2] is not ("admin" or "user")) return Invalid();
        string name = arguments[1];
        bool administrator = arguments[2] == "admin";
        Prompt(output, password => accounts.CreateUserWork(session, name, administrator, password));
        return ResultErr.CreateSuccess();
    }

    private ResultErr Grant(string[] arguments, ITextOutput output)
    {
        if (arguments.Length != 3) return Invalid();
        FilePermission permission;
        switch (arguments[2])
        {
            case "none": permission = FilePermission.None; break;
            case "r": permission = FilePermission.Read; break;
            case "rw": permission = FilePermission.Read | FilePermission.Write; break;
            default: return Invalid();
        }
        var path = files.ResolvePath(arguments[0]);
        return path.IsOK() ? accounts.Grant(session, path.Value!, arguments[1], permission) : path.Status;
    }

    private ResultErr Security(string[] arguments, ITextOutput output)
    {
        if (arguments.Length == 2 && arguments[0] == "recover")
        {
            if (!allowRecovery) return ResultErr.CreateFailure(ResultCode.Failure, "Recovery erfordert einen ausdruecklich gestarteten Sicherheits-Recovery-Build.");
            output.WriteLine("Recovery ersetzt Konten/Rechte; bestehende Dateien bleiben bis zu ihrer ausdruecklichen Zuordnung gesperrt.");
            string name = arguments[1];
            Prompt(output, password =>
            {
                var work = accounts.RecoveryWork(session, name, password);
                return () =>
                {
                    var complete = work();
                    return () =>
                    {
                        var result = complete();
                        if (result.IsOK()) files.ResetDirectory(session.HomeDirectory!);
                        return result;
                    };
                };
            });
            return ResultErr.CreateSuccess();
        }
        if (arguments.Length != 3 || arguments[0] != "adopt") return Invalid();
        var path = files.ResolvePath(arguments[1]);
        return path.IsOK() ? accounts.Adopt(session, path.Value!, arguments[2]) : path.Status;
    }

    private void Prompt(ITextOutput output, Func<string, Func<Func<ResultErr>>> prepare)
    {
        output.WriteLine("Kennwort (12–128 Zeichen; Eingabe verborgen, kein Verlauf):");
        beginSecret(prepare);
    }

    private static ResultErr Invalid() => ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltige Argumente. Hilfe: help.");
}
