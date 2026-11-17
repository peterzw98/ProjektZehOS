using System;
using System.Buffers.Binary;
using ProjektZeh.Shared;

namespace ProjektZeh.Network;

/// <summary>
/// SNTPv4-Client-Pakete nach RFC 4330 (Etappe 12.2, Spec 2026-11-17-tls-12-2-audit.md §2.3), rein und ohne Netzwerk.
/// Nicht authentifiziert: Schutz bietet nur, dass die Antwort den zufaelligen eigenen Sendezeitstempel als Originate
/// zurueckgeben muss; ein Angreifer auf dem Pfad kann trotzdem antworten.
/// </summary>
internal static class Sntp
{
    public const int Port = 123;
    public const int PacketBytes = 48;
    private const int OriginateOffset = 24;
    private const int TransmitOffset = 40;
    private static readonly DateTime Era0 = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Era1 = new(2036, 2, 7, 6, 28, 16, DateTimeKind.Utc);

    /// <summary>
    /// Anfrage im Client-Modus mit <paramref name="nonce"/> (8 Zufallsbytes) als Sendezeitstempel. RFC 4330 erlaubt einen
    /// beliebigen Wert; ein zufaelliger macht blind gefaelschte Antworten praktisch unmoeglich.
    /// </summary>
    public static byte[] CreateRequest(ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != 8) throw new ArgumentException("Der Sendezeitstempel umfasst 8 Byte.", nameof(nonce));
        var packet = new byte[PacketBytes];
        packet[0] = 0x23; // LI 0, Version 4, Modus 3 (Client)
        nonce.CopyTo(packet.AsSpan(TransmitOffset));
        return packet;
    }

    /// <summary>Server-Sendezeit einer gueltigen Antwort auf die Anfrage mit <paramref name="nonce"/>.</summary>
    public static ResultObject<DateTime> ParseResponse(ReadOnlySpan<byte> response, ReadOnlySpan<byte> nonce)
    {
        if (response.Length < PacketBytes) return Invalid("Antwort zu kurz.");
        int leap = response[0] >> 6, version = (response[0] >> 3) & 7, mode = response[0] & 7;
        if (leap == 3) return Invalid("Server nicht synchronisiert (LI 3).");
        if (version is not (3 or 4)) return Invalid("Unbekannte NTP-Version.");
        if (mode != 4) return Invalid("Keine Serverantwort (Modus " + mode + ").");
        if (response[1] is 0 or > 15) return Invalid("Ungueltiges Stratum (Kiss-o'-Death oder unsynchronisiert).");
        if (!response.Slice(OriginateOffset, 8).SequenceEqual(nonce)) return Invalid("Antwort passt nicht zur Anfrage.");
        uint seconds = BinaryPrimitives.ReadUInt32BigEndian(response[TransmitOffset..]);
        uint fraction = BinaryPrimitives.ReadUInt32BigEndian(response[(TransmitOffset + 4)..]);
        if (seconds == 0 && fraction == 0) return Invalid("Sendezeit fehlt.");
        // RFC 4330 Abschnitt 3: Ist das oberste Bit 0, liegt die Zeit nach 2036 (Era 1).
        DateTime era = (seconds & 0x80000000) != 0 ? Era0 : Era1;
        long ticks = (long)seconds * TimeSpan.TicksPerSecond + (long)((ulong)fraction * TimeSpan.TicksPerSecond >> 32);
        return ResultObjectFactory.CreateSuccess(era.AddTicks(ticks));
    }

    private static ResultObject<DateTime> Invalid(string message) =>
        ResultObjectFactory.CreateFailure<DateTime>(ResultCode.Failure, "cause='SntpInvalid' message='" + message + "'");
}
