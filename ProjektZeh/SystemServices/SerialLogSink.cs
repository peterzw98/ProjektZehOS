using Cosmos.Kernel.System.Diagnostics;

namespace ProjektZeh.SystemServices;

/// <summary>
/// Schreibt Diagnosen direkt auf die serielle Cosmos-Ausgabe.
/// </summary>
internal sealed class SerialLogSink : ISystemLogSink
{
    /// <summary>
    /// Schreibt einen einzelnen Eintrag und beendet die Zeile.
    /// </summary>
    /// <param name="entry"></param>
    public void Write(SystemLogEntry entry)
    {
        Log.WriteString(entry.ToString());
        Log.WriteString("\n");
    }
}
