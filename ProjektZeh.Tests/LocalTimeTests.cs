using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class LocalTimeTests
{
    [DataTestMethod]
    // 2026: letzter Sonntag im März ist der 29., im Oktober der 25.
    [DataRow("2026-03-29T00:59:59", "2026-03-29T01:59:59", "MEZ")]
    [DataRow("2026-03-29T01:00:00", "2026-03-29T03:00:00", "MESZ")]
    [DataRow("2026-10-25T00:59:59", "2026-10-25T02:59:59", "MESZ")]
    [DataRow("2026-10-25T01:00:00", "2026-10-25T02:00:00", "MEZ")]
    [DataRow("2026-03-22T12:00:00", "2026-03-22T13:00:00", "MEZ")]
    [DataRow("2026-11-16T14:40:00", "2026-11-16T15:40:00", "MEZ")]
    [DataRow("2026-12-31T23:30:00", "2027-01-01T00:30:00", "MEZ")]
    [DataRow("2028-02-28T23:30:00", "2028-02-29T00:30:00", "MEZ")]
    [DataRow("2028-02-29T23:30:00", "2028-03-01T00:30:00", "MEZ")]
    // 2028: letzter Sonntag im März ist der 26.
    [DataRow("2028-03-26T01:00:00", "2028-03-26T03:00:00", "MESZ")]
    public void FromUtc_AppliesEuSummerTimeRule(string utc, string expected, string zone)
    {
        var (time, actualZone) = LocalTime.FromUtc(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), time);
        Assert.AreEqual(zone, actualZone);
    }
}
