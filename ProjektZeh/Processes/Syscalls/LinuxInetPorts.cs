using System.Collections.Generic;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V27: Porttabelle für AF_INET nur auf Loopback, je Lauf (liegt in <see cref="LinuxUnixNames"/>, damit alle Prozesse eines Laufs sie teilen
/// wie das Loopback-Netz eines Linux-Rechners). TCP und UDP haben getrennte Ports wie bei Linux. Ein Eintrag gehört dem gebundenen Socket
/// (bind, automatisch bei listen, connect oder sendto) bis zu dessen letztem close; angenommene Verbindungen tragen den Port des Lauschenden,
/// ohne eigenen Eintrag.
/// <para>
/// Abweichung: SO_REUSEADDR/SO_REUSEPORT erlauben kein zweites bind auf einen belegten Port, und es gibt kein TIME_WAIT (ein Port ist nach
/// dem close sofort wieder frei). Die Adresse (0.0.0.0 oder 127.x.y.z) gehört nicht zum Schlüssel: zwei Sockets auf 127.0.0.1 und 127.0.0.2
/// mit demselben Port schließen sich hier aus, bei Linux nicht.
/// </para>
/// <para>Sperren: <see cref="_gate"/> ist eine Blattsperre; darin wird keine Socket-Sperre genommen.</para>
/// </summary>
internal sealed class LinuxInetPorts
{
    /// <summary>Linux net.ipv4.ip_local_port_range (Vorgabe 32768 bis 60999).</summary>
    public const int EphemeralFirst = 32768, EphemeralLast = 60999;

    private const long EADDRINUSE = 98;

    private readonly object _gate = new();
    private readonly Dictionary<int, object> _tcp = new();
    private readonly Dictionary<int, object> _udp = new();
    private int _next = EphemeralFirst - 1;

    private Dictionary<int, object> Table(bool udp) => udp ? _udp : _tcp;

    /// <summary>
    /// Bindet <paramref name="port"/> (1 bis 65535) an <paramref name="owner"/>; 0 vergibt einen freien Port aus dem kurzlebigen Bereich.
    /// Liefert den Port oder -EADDRINUSE (Bereich erschöpft ebenso, wie Linux' inet_csk_get_port).
    /// </summary>
    public long Bind(bool udp, int port, object owner)
    {
        lock (_gate)
        {
            Dictionary<int, object> t = Table(udp);
            if (port != 0)
            {
                if (t.ContainsKey(port))
                {
                    return -EADDRINUSE;
                }

                t[port] = owner;
                return port;
            }

            int span = EphemeralLast - EphemeralFirst + 1;
            for (int i = 0; i < span; i++)
            {
                _next = _next >= EphemeralLast ? EphemeralFirst : _next + 1;
                if (!t.ContainsKey(_next))
                {
                    t[_next] = owner;
                    return _next;
                }
            }

            return -EADDRINUSE;
        }
    }

    public object? Find(bool udp, int port)
    {
        lock (_gate)
        {
            return Table(udp).TryGetValue(port, out object? owner) ? owner : null;
        }
    }

    /// <summary>
    /// Gibt <paramref name="port"/> frei, wenn er <paramref name="owner"/> gehört (letztes close, UDP-disconnect). O(1): Der Socket kennt seinen
    /// Port, die Tabelle wird nicht durchsucht; ein inzwischen fremder Eintrag bleibt stehen.
    /// </summary>
    public void Release(bool udp, int port, object owner)
    {
        lock (_gate)
        {
            Dictionary<int, object> t = Table(udp);
            if (t.TryGetValue(port, out object? o) && ReferenceEquals(o, owner))
            {
                t.Remove(port);
            }
        }
    }
}
