using System;
using System.Threading;

namespace ProjektZeh.Network;

/// <summary>
/// E6-C.11 (#390): IPv4-Adresse fuer Statusleser, die <c>CosmosNetworkService.Gate</c> nicht abwarten duerfen (Desktop-Statuszeile,
/// Netzwerkautomatik). Cosmos liest <c>NetworkAdapter.IPConfig</c> ueber <c>IPConfig.Get</c> aus einer <c>List</c> ohne Sperre, die
/// <c>IPConfig.Set</c> (DHCP, <c>net static</c>) unter der Stapelsperre aendert; ein Leser ohne Gate kann auf einer zweiten CPU
/// mitten in den Austausch fallen (Enumerator wirft oder sieht eine halbe Liste). Gelesen wird deshalb nur, wer Gate sofort bekommt;
/// sonst gilt der zuletzt unter Gate veroeffentlichte Wert. Ein Schreiber haelt Gate ohnehin und veroeffentlicht vor dem Freigeben.
/// </summary>
internal sealed class NetworkAddressSnapshot
{
    private readonly object gate;
    // Unveraenderlicher string, per Volatile.Write veroeffentlicht: ein Leser sieht den alten oder den neuen Wert, nie einen halben.
    private string? published;

    public NetworkAddressSnapshot(object gate) => this.gate = gate;

    public string? Published => Volatile.Read(ref published);

    /// <summary>Frisch unter Gate, wenn es frei ist oder dieser Thread es schon haelt (Monitor ist rekursiv); sonst ohne Warten der letzte Wert.</summary>
    public string? Read(Func<string?> readLocked)
    {
        if (!Monitor.TryEnter(gate)) return Published;
        try { return PublishLocked(readLocked); }
        finally { Monitor.Exit(gate); }
    }

    /// <summary>Liest unter dem gehaltenen Gate und veroeffentlicht das Ergebnis; wirft das Lesen, bleibt der alte Wert stehen.</summary>
    public string? PublishLocked(Func<string?> readLocked)
    {
        if (!Monitor.IsEntered(gate)) throw new InvalidOperationException("Netzstatus nur unter Gate lesen.");
        string? value = readLocked();
        Volatile.Write(ref published, value);
        return value;
    }
}
