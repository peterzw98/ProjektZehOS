namespace ProjektZeh.Shell;

/// <summary>
/// Ausgabeziel fuer Konsole und spaetere Terminalfenster.
/// </summary>
internal interface ITextOutput
{
    void WriteLine(string text);
    void Clear();
}
