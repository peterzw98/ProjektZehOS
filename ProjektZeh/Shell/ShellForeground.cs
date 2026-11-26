using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <summary>Vordergrundprogramm der Shell (Ladybird V3: run laeuft neben der Ereignisschleife, Eingabe ueber Enter-Zeilen).</summary>
internal sealed partial class ShellService
{
    private IShellForeground? foreground;

    /// <summary>true, solange ein Programm im Vordergrund laeuft; Eingabezeilen gehen dann an das Programm.</summary>
    public bool HasForeground => foreground != null;

    /// <summary>Eingabeaufforderung des Programms (angefangene Ausgabezeile) oder null ohne Vordergrundprogramm.</summary>
    public string? ForegroundPrompt => foreground?.Prompt;

    /// <summary>Meldet ein gestartetes Programm an; seine Ausgabe holt <see cref="Poll"/> ab.</summary>
    internal ResultErr AttachForeground(IShellForeground program)
    {
        if (closed) return ResultErr.CreateFailure(ResultCode.Failure, "Sitzung geschlossen.");
        if (foreground != null) return ResultErr.CreateFailure(ResultCode.Failure, "In dieser Sitzung laeuft schon ein Programm.");
        foreground = program;
        return ResultErr.CreateSuccess();
    }

    /// <summary>Strg+D: Eingabeende fuer das Vordergrundprogramm; false ohne Programm.</summary>
    public bool EndForegroundInput()
    {
        if (closed || foreground == null) return false;
        foreground.EndInput();
        return true;
    }

    private void InterruptForeground(ITextOutput output)
    {
        var program = foreground!;
        if (!program.IsInterrupted)
        {
            output.WriteLine("^C");
            program.Interrupt();
            return;
        }
        // Zweiter Abbruch: Das Programm haengt an einer Stelle ohne Abbruchpruefung. Das Fenster wird wieder frei; ein neues run
        // geht erst, wenn das Programm doch endet.
        program.Detach();
        foreground = null;
        output.WriteLine("Programm reagiert nicht; Terminal wieder frei.");
    }

    private bool PollForeground(ITextOutput output)
    {
        var program = foreground;
        if (closed || program == null) return false;
        bool written = program.Drain(output);
        if (program.IsDone) foreground = null;
        return written;
    }

    private void CloseForeground()
    {
        var program = foreground;
        foreground = null;
        if (program == null) return;
        // Fenster zu: Programm beenden, seine Ausgaben verfallen.
        program.Interrupt();
        program.Detach();
    }
}
