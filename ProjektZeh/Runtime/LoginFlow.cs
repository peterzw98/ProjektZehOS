using System;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Runtime;

/// <summary>
/// Zeilenweise Anmeldung beziehungsweise Ersteinrichtung des Administrators fuer Textshell und Anmeldefenster.
/// Die Kennwortpruefung laeuft ueber <c>login</c> und <c>security setup</c> der Shell und damit ueber deren
/// verdeckte Eingabe und begrenzte Hintergrundberechnung.
/// </summary>
internal sealed class LoginFlow(AccountService accounts, ShellService shell, ITextOutput output)
{
    private string? name;
    private string? password;

    /// <summary>Gesperrte Konten bleiben der normalen Shell und damit der ausdruecklichen Recovery ueberlassen.</summary>
    public bool IsActive => accounts.IsReady || accounts.NeedsSetup;
    public bool IsSecret => name != null;
    public bool IsBusy => shell.IsBusy;
    public string Title => accounts.NeedsSetup ? "Administrator anlegen" : "Anmelden";
    public string Prompt => name == null ? "Benutzername: " : !accounts.NeedsSetup ? "Kennwort: " :
        password == null ? "Kennwort (12-128 Zeichen): " : "Kennwort wiederholen: ";

    public void Reset() => name = password = null;

    /// <summary>Uebernimmt die naechste Eingabe; der letzte Schritt startet die Kennwortpruefung im Hintergrund.</summary>
    public ResultErr Submit(string line)
    {
        if (name == null)
        {
            if (line.Length > 0) name = line;
            return ResultErr.CreateSuccess();
        }

        bool setup = accounts.NeedsSetup;
        if (setup && password == null)
        {
            password = line;
            return ResultErr.CreateSuccess();
        }

        string user = name;
        string? first = password;
        Reset();
        if (setup && !string.Equals(first, line, StringComparison.Ordinal))
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Die Kennwoerter stimmen nicht ueberein.");
        // Namen mit Leerzeichen oder Anfuehrungszeichen wuerden die Befehlszeile veraendern.
        foreach (char character in user)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))
                return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltiger Benutzername.");
        // Eine liegengebliebene Kennwortabfrage wuerde sonst den Anmeldebefehl als Kennwort uebernehmen.
        shell.CancelSecretInput();
        // Der Kennworthinweis des Befehls kaeme hier erst nach der bereits erfolgten Eingabe und entfaellt deshalb.
        var started = shell.ExecuteLine(setup ? $"security setup {user}" : $"login {user}", Discard.Instance);
        if (!started.IsOK()) return started;
        return shell.IsSecretInput ? shell.ExecuteLine(line, output) : FileAccessControl.Denied();
    }

    private sealed class Discard : ITextOutput
    {
        public static readonly Discard Instance = new();
        public void WriteLine(string text) { }
        public void Clear() { }
    }
}
