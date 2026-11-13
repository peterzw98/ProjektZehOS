using ProjektZeh.Apps.Terminal;
using ProjektZeh.Input;
using ProjektZeh.Runtime;
using ProjektZeh.Security;
using ProjektZeh.Shell;
using ProjektZeh.UI;

namespace ProjektZeh.Graphics;

/// <summary>Verbindet den Desktop mit der Identitaet und Shell der Textkonsole.</summary>
internal sealed record DesktopSession(AccountService Accounts, ShellService Shell, UserSession Identity);

/// <summary>
/// Anmeldefenster des Desktops; verwendet denselben Ablauf wie die Textshell.
/// </summary>
internal sealed class LoginScreen
{
    private const int SecondaryText = unchecked((int)0xffa0afc1);
    private readonly DesktopSession session;
    private readonly LoginFlow login;
    private readonly TerminalBuffer messages;
    private readonly UiPainter painter;
    private readonly UiTextBox input;
    private UiRect bounds;

    public LoginScreen(DesktopSession session, UiPainter painter, int width, int height)
    {
        this.session = session;
        this.painter = painter;
        messages = new(680 / painter.GlyphWidth, 8);
        login = new(session.Accounts, session.Shell, messages);
        input = new(128, painter: painter) { Focused = true };
        Resize(width, height);
        input.Submitted = Submit;
    }

    public void Resize(int width, int height)
    {
        int line = painter.GlyphHeight;
        bounds = new((width - 720) / 2, (height - 7 * line - 56) / 2, 720, 7 * line + 56);
        input.Bounds = new(bounds.X + 20, bounds.Y + 3 * line + 24, 680, line + 12);
    }

    public void Handle(InputEvent value)
    {
        if (login.IsActive && !login.IsBusy) input.Handle(value);
    }

    /// <summary>Uebernimmt fertige Kennwortpruefungen, solange das Anmeldefenster angezeigt wird.</summary>
    public void Update() => session.Shell.Poll(messages);

    /// <summary>
    /// Meldet direkt ab, damit eine laufende oder offene Kennwortabfrage der Textshell das Abmelden nicht verhindert.
    /// Laufende Berechnungen pruefen beim Uebernehmen erneut die Berechtigung.
    /// </summary>
    public void Logout()
    {
        session.Shell.CancelSecretInput();
        session.Identity.Logout();
    }

    public void Reset()
    {
        login.Reset();
        input.SetText("");
        messages.Clear();
    }

    private void Submit()
    {
        var result = login.Submit(input.Text);
        input.SetText("");
        if (!result.IsOK()) messages.WriteLine(result.Message);
    }

    public void Render(PixelSurface surface)
    {
        int line = painter.GlyphHeight;
        int x = bounds.X + 20;
        UiPainter.Frame(surface, bounds, 0, 0, true);
        if (!login.IsActive)
        {
            painter.Text(surface, "ZehOS - Konten gesperrt", x, bounds.Y + 16, 680);
            painter.Text(surface, "Sicherheits-Recovery-Build erforderlich.", x, bounds.Y + 16 + line, 680, SecondaryText);
            painter.Text(surface, "Esc: Textshell, log zeigt die Ursache.", x, bounds.Y + 16 + 2 * line, 680, SecondaryText);
            return;
        }
        input.Masked = login.IsSecret;
        painter.Text(surface, $"ZehOS - {login.Title}", x, bounds.Y + 16, 680);
        if (session.Accounts.NeedsSetup) painter.Text(surface, "Noch kein Benutzer vorhanden.", x, bounds.Y + 16 + line, 680, SecondaryText);
        painter.Text(surface, login.IsBusy ? "Kennwort wird geprueft ..." : login.Prompt, x, bounds.Y + 2 * line + 20, 680);
        input.Draw(surface, painter, 0, 0, true);
        // Lange Meldungen umbrechen im Puffer; die letzten zwei Zeilen zeigen das Ergebnis.
        for (int index = System.Math.Max(0, messages.Count - 2); index < messages.Count; index++)
            painter.Text(surface, messages.GetLine(index), x, bounds.Y + (4 + index - System.Math.Max(0, messages.Count - 2)) * line + 44, 680, SecondaryText);
    }
}
