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
        // E6-C.12: Eintrag und Zeilenende in einem Aufruf. Cosmos sperrt die serielle Schnittstelle je Aufruf
        // (Serial.PortScope, Cosmos.Kernel.Core/IO/Serial.cs); zwischen zwei Aufrufen koennte die Zeile einer anderen
        // CPU oder eines anderen Threads landen. Die Senke laeuft ausserhalb der SystemLog-Sperre.
        Log.WriteString(entry.ToString() + "\n");
    }
}
