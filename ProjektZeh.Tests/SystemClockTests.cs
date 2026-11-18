using System;
using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Network;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Tests;

/// <summary>Etappe 12.2 PR C (Spec 2026-11-17-tls-12-2-audit.md §2.3): Bundle-Untergrenze, SNTP-Pakete, Versatzregel.</summary>
[DoNotParallelize]
[TestClass]
public sealed class SystemClockTests
{
    private static readonly byte[] Nonce = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly DateTime Bound = new(2026, 9, 25, 3, 12, 1, DateTimeKind.Utc);
    private static readonly DateTime Server = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] Response(DateTime time, byte first = 0x24, byte stratum = 2, byte[]? originate = null)
    {
        var packet = new byte[Sntp.PacketBytes];
        packet[0] = first;
        packet[1] = stratum;
        (originate ?? Nonce).CopyTo(packet, 24);
        TimeSpan since = time - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(40), (uint)(long)since.TotalSeconds);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(44), 0x80000000); // eine halbe Sekunde
        return packet;
    }

    [TestMethod]
    public void BundleDateIsReadFromTheShippedHeader()
    {
        Assert.AreEqual(Bound, TlsRoots.MinimumTime);
        Assert.AreEqual(new DateTime(2026, 9, 5, 3, 12, 1, DateTimeKind.Utc),
            TlsRoots.ParseBundleDate(Encoding.ASCII.GetBytes("##\n## Certificate data from Mozilla as of: Sat Sep  5 03:12:01 2026 GMT\r\n")),
            "Perl-gmtime fuellt einstellige Tage mit Leerzeichen auf");
        foreach (string broken in new[] { "kein Kopf\n", "## Certificate data from Mozilla as of: Fri Foo 25 03:12:01 2026 GMT\n",
            "## Certificate data from Mozilla as of: Fri Sep 25 03:12 2026 GMT\n", "## Certificate data from Mozilla as of: Fri Sep 25 03:12:01 2026" })
            Assert.ThrowsException<InvalidOperationException>(() => TlsRoots.ParseBundleDate(Encoding.ASCII.GetBytes(broken)), broken);
    }

    [TestMethod]
    public void TlsClockMustNotPrecedeTheBundle()
    {
        Assert.ThrowsException<InvalidOperationException>(() => TlsTransport.CheckClock(Bound.AddSeconds(-1)));
        Assert.ThrowsException<InvalidOperationException>(() => TlsTransport.CheckClock(Bound.AddYears(15).AddSeconds(1)));
        Assert.ThrowsException<InvalidOperationException>(() => TlsTransport.CheckClock(DateTime.SpecifyKind(Server, DateTimeKind.Local)));
        TlsTransport.CheckClock(Bound);
        TlsTransport.CheckClock(Server);
    }

    [TestMethod]
    public void RequestIsAClientPacketCarryingTheNonce()
    {
        byte[] request = Sntp.CreateRequest(Nonce);
        Assert.AreEqual(48, request.Length);
        Assert.AreEqual(0x23, request[0], "LI 0, Version 4, Modus 3");
        CollectionAssert.AreEqual(Nonce, request[40..48]);
        Assert.ThrowsException<ArgumentException>(() => Sntp.CreateRequest(new byte[7]));
    }

    [TestMethod]
    public void ValidResponseYieldsTheServerTime()
    {
        var parsed = Sntp.ParseResponse(Response(Server), Nonce);
        Assert.IsTrue(parsed.IsOK(), parsed.Status.Message);
        Assert.AreEqual(Server.AddSeconds(0.5), parsed.Value);
        Assert.AreEqual(DateTimeKind.Utc, parsed.Value.Kind);

        // Nach 2036 beginnt Era 1: das oberste Bit der Sekunden ist dann 0.
        var era1 = Response(new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual(new DateTime(2040, 1, 1, 0, 0, 0, 500, DateTimeKind.Utc), Sntp.ParseResponse(era1, Nonce).Value);
        Assert.IsTrue(Sntp.ParseResponse(Response(Server, first: 0x1C), Nonce).IsOK(), "Version 3 wird angenommen");

        // Letzte Sekunde von Era 0 (oberstes Bit gesetzt) und erste von Era 1.
        var lastEra0 = Response(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        BinaryPrimitives.WriteUInt32BigEndian(lastEra0.AsSpan(40), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(lastEra0.AsSpan(44), 0);
        Assert.AreEqual(new DateTime(2036, 2, 7, 6, 28, 15, DateTimeKind.Utc), Sntp.ParseResponse(lastEra0, Nonce).Value);
        BinaryPrimitives.WriteUInt32BigEndian(lastEra0.AsSpan(40), 0);
        BinaryPrimitives.WriteUInt32BigEndian(lastEra0.AsSpan(44), 0xC0000000);
        Assert.AreEqual(new DateTime(2036, 2, 7, 6, 28, 16, 750, DateTimeKind.Utc), Sntp.ParseResponse(lastEra0, Nonce).Value, "Era 1 mit 0,75 s");
    }

    [TestMethod]
    public void InvalidResponsesAreRejected()
    {
        Assert.IsFalse(Sntp.ParseResponse(new byte[47], Nonce).IsOK(), "zu kurz");
        Assert.IsFalse(Sntp.ParseResponse(Response(Server, first: 0xE4), Nonce).IsOK(), "LI 3");
        Assert.IsFalse(Sntp.ParseResponse(Response(Server, first: 0x23), Nonce).IsOK(), "Modus 3 statt 4");
        Assert.IsFalse(Sntp.ParseResponse(Response(Server, first: 0x14), Nonce).IsOK(), "Version 2");
        Assert.IsFalse(Sntp.ParseResponse(Response(Server, stratum: 0), Nonce).IsOK(), "Kiss-o'-Death");
        Assert.IsFalse(Sntp.ParseResponse(Response(Server, stratum: 16), Nonce).IsOK(), "unsynchronisiert");
        Assert.IsFalse(Sntp.ParseResponse(Response(Server, originate: [9, 9, 9, 9, 9, 9, 9, 9]), Nonce).IsOK(), "fremde Antwort");
        var zero = Response(Server);
        Array.Clear(zero, 40, 8);
        Assert.IsFalse(Sntp.ParseResponse(zero, Nonce).IsOK(), "Sendezeit fehlt");
    }

    [TestMethod]
    public void PlausibleClockMayGoForwardButOnlyFiveMinutesBack()
    {
        DateTime rtc = Server;
        Assert.AreEqual(TimeSpan.FromDays(3), ClockCorrection.Offset(rtc, rtc, rtc.AddDays(3), Bound).Value);
        Assert.AreEqual(TimeSpan.FromMinutes(-5), ClockCorrection.Offset(rtc, rtc, rtc.AddMinutes(-5), Bound).Value);
        var refused = ClockCorrection.Offset(rtc, rtc, rtc.AddMinutes(-6), Bound);
        Assert.IsFalse(refused.IsOK());
        StringAssert.Contains(refused.Status.Message, "BackwardStepRefused");
    }

    [TestMethod]
    public void LaterSyncCannotUndoAnEarlierForwardCorrection()
    {
        // RTC zwei Tage nach; der erste Abgleich stellt vor. Ein zweiter (gefaelschter) darf nur 5 Minuten hinter die
        // aktuelle Kernelzeit, nicht zurueck auf die RTC.
        DateTime rtc = Server.AddDays(-2), current = Server;
        StringAssert.Contains(ClockCorrection.Offset(rtc, current, Server.AddDays(-1), Bound).Status.Message, "BackwardStepRefused");
        Assert.AreEqual(Server.AddMinutes(-4) - rtc, ClockCorrection.Offset(rtc, current, Server.AddMinutes(-4), Bound).Value, "Versatz bleibt relativ zur RTC");
    }

    [TestMethod]
    public void ImplausibleClockIsFullyCorrectedButNeverToAnImplausibleTime()
    {
        var rtc2020 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(Server - rtc2020, ClockCorrection.Offset(rtc2020, rtc2020, Server, Bound).Value, "RTC vor dem Bundle");
        var rtcFuture = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(Server - rtcFuture, ClockCorrection.Offset(rtcFuture, rtcFuture, Server, Bound).Value, "RTC weit in der Zukunft darf zurueck");
        var rtcJustTooLate = Bound.AddYears(15).AddDays(1);
        Assert.AreEqual(Server - rtcJustTooLate, ClockCorrection.Offset(rtcJustTooLate, rtcJustTooLate, Server, Bound).Value, "knapp ueber 15 Jahre gilt als unplausibel");
        StringAssert.Contains(ClockCorrection.Offset(Server, Server, Bound.AddDays(-1), Bound).Status.Message, "NetworkTimeImplausible");
        StringAssert.Contains(ClockCorrection.Offset(rtc2020, rtc2020, Bound.AddYears(16), Bound).Status.Message, "NetworkTimeImplausible");
    }
}
