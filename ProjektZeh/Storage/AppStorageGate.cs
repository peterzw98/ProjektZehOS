using System;

namespace ProjektZeh.Storage;

/// <summary>
/// Gemeinsame Schreibsperre fuer den App-Speicher /save (Etappe 13.4). Apps schreiben auf dem Wasm-Worker, waehrend der
/// praeemptive Scheduler andere Threads weiterlaufen laesst; Aushaengen und Energieaktionen duerfen deshalb nicht mitten in
/// ein Ersetzen (Temp, Sicherung, Rename) fallen. <see cref="Block"/> sperrt neue Schreiber und wartet auf einen laufenden;
/// danach melden schreibende App-Aufrufe EBUSY, Lesen bleibt. Die Migration (4.5) kann dieselbe Sperre nutzen.
/// </summary>
internal static class AppStorageGate
{
    private static readonly object gate = new();
    private static bool blocked;

    public static bool IsBlocked
    {
        get { lock (gate) return blocked; }
    }

    /// <summary>Fuehrt eine Plattenoperation einer App aus; ist gesperrt, liefert sie <paramref name="busy"/> ohne Zugriff.</summary>
    public static int Run(Func<int> operation, int busy)
    {
        lock (gate)
            return blocked ? busy : operation();
    }

    /// <summary>Sperrt neue Schreiber; kehrt erst zurueck, wenn ein laufender fertig ist.</summary>
    public static void Block()
    {
        lock (gate) blocked = true;
    }

    public static void Release()
    {
        lock (gate) blocked = false;
    }
}
