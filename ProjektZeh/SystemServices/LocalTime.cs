using System;

namespace ProjektZeh.SystemServices;

/// <summary>Rechnet die UTC-Hardwareuhr in die Ortszeit Europe/Berlin um.</summary>
/// <remarks>
/// Cosmos 3.0.94 liefert nur <see cref="DateTime.UtcNow"/> und keine Zeitzonendaten. Die RTC bleibt UTC,
/// weil die TLS-Datumsprüfung davon abhängt.
/// shortcut: feste Zone mit EU-Sommerzeitregel, bei Bedarf eine Zeitzoneneinstellung ergänzen.
/// </remarks>
internal static class LocalTime
{
    public static (DateTime Time, string Zone) FromUtc(DateTime utc)
    {
        // Die EU wechselt am letzten Sonntag im März und Oktober jeweils um 01:00 UTC.
        bool summer = utc >= LastSunday(utc.Year, 3).AddHours(1) && utc < LastSunday(utc.Year, 10).AddHours(1);
        var local = DateTime.SpecifyKind(utc.AddHours(summer ? 2 : 1), DateTimeKind.Unspecified);
        return (local, summer ? "MESZ" : "MEZ");
    }

    private static DateTime LastSunday(int year, int month)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(int)last.DayOfWeek);
    }
}
