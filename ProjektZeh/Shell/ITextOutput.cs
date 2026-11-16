namespace ProjektZeh.Shell;

/// <summary>
/// Ausgabeziel fuer Konsole und spaetere Terminalfenster.
/// </summary>
internal interface ITextOutput
{
    void WriteLine(string text);
    void Clear();
}

/// <summary>Ersetzt die zuletzt geschriebene Fortschrittszeile, solange keine normale Ausgabe dazwischenliegt.</summary>
internal interface IProgressOutput : ITextOutput
{
    void WriteProgress(string text);
}
