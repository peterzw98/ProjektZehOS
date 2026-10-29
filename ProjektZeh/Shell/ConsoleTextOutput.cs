using System;

namespace ProjektZeh.Shell;

/// <summary>
/// Schreibt auf die Cosmos-Konsole.
/// </summary>
internal sealed class ConsoleTextOutput : ITextOutput
{
    /// <summary>
    /// Schreibt eine Textzeile.
    /// </summary>
    /// <param name="text"></param>
    public void WriteLine(string text)
    {
        Console.WriteLine(text);
    }

    /// <summary>
    /// Leert die Konsole.
    /// </summary>
    public void Clear()
    {
        Console.Clear();
    }
}
