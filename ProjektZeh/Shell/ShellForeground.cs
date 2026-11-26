using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <summary>Vordergrundprogramm der Shell (Ladybird V3: run laeuft neben der Ereignisschleife, Eingabe ueber Enter-Zeilen).</summary>
internal sealed partial class ShellService
{
    private IShellForeground? foreground;

    // Zeilen und Spalten des Fensters in einem Wert (Zeilen oben), damit der Programmthread nie ein halb geschriebenes Paar liest.
    // 0: kein Fenster hat eine Größe gemeldet.
    private int windowSize;

    /// <summary>Meldet das Fenster (TerminalApp bei jeder Größenänderung); Programme lesen sie mit TIOCGWINSZ.</summary>
    public void SetWindowSize(int rows, int columns)
    {
        int packed = (System.Math.Clamp(rows, 1, 0xffff) << 16) | System.Math.Clamp(columns, 1, 0xffff);
        System.Threading.Volatile.Write(ref windowSize, packed);
    }

    /// <summary>Fenstergröße in Zeichen; ohne Meldung 24 x 80 wie ein frisches Linux-Terminal. Darf von jedem Thread kommen.</summary>
    public (ushort Rows, ushort Columns) WindowSize
    {
        get
        {
            int packed = System.Threading.Volatile.Read(ref windowSize);
            return packed == 0 ? ((ushort)24, (ushort)80) : ((ushort)((uint)packed >> 16), (ushort)(packed & 0xffff));
        }
    }

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
