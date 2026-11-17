using System;
using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.SystemServices;

/// <summary>
/// Kernelzeit = RTC + Versatz aus einem Zeitabgleich (Etappe 12.2, Spec 2026-11-17-tls-12-2-audit.md §2.3). Die RTC selbst
/// wird nie beschrieben; nach einem Neustart gilt wieder sie, bis der naechste Abgleich gelingt. TLS, Uhranzeige und die
/// Wasm-Uhr lesen <see cref="UtcNow"/>, damit alle dieselbe Zeit sehen.
/// </summary>
internal static class SystemClock
{
    private static long offsetTicks;
    private static int synchronized;

    public static DateTime UtcNow => DateTime.UtcNow.AddTicks(Interlocked.Read(ref offsetTicks));

    /// <summary>Ob seit dem Start ein Abgleich uebernommen wurde.</summary>
    public static bool IsSynchronized => Volatile.Read(ref synchronized) != 0;

    /// <summary>Uebernimmt einen nach <see cref="ClockCorrection.Offset"/> geprueften Versatz.</summary>
    public static void Apply(TimeSpan offset)
    {
        Interlocked.Exchange(ref offsetTicks, offset.Ticks);
        Volatile.Write(ref synchronized, 1);
    }
}

/// <summary>Reine Regeln fuer Plausibilitaet und Uebernahme einer Netzwerkzeit.</summary>
internal static class ClockCorrection
{
    /// <summary>Hoechster Rueckschritt gegenueber einer plausiblen RTC; begrenzt, wie alt ein gefaelschter Abgleich ein abgelaufenes Zertifikat machen kann.</summary>
    public static readonly TimeSpan MaximumBackward = TimeSpan.FromMinutes(5);
    public const int PlausibleYears = 15;

    /// <summary>UTC, nicht vor dem Stand des Wurzelbundles und hoechstens 15 Jahre danach.</summary>
    public static bool IsPlausible(DateTime utc, DateTime lowerBound) =>
        utc.Kind == DateTimeKind.Utc && utc >= lowerBound && utc <= lowerBound.AddYears(PlausibleYears);

    /// <summary>
    /// Versatz, der die RTC (<paramref name="rtcUtc"/>) auf die Netzwerkzeit bringt. Massgeblich fuer Plausibilitaet und
    /// Rueckschritt ist die aktuelle Kernelzeit (<paramref name="currentUtc"/>, RTC plus bisherigem Versatz), damit ein
    /// spaeterer Abgleich eine fruehere Vorwaertskorrektur nicht weiter als 5 Minuten zuruecknimmt. Eine unplausible Zeit
    /// wird vollstaendig korrigiert; dann kann ein Angreifer auf dem Pfad jede Zeit ab dem Bundle-Stand setzen (Spec §2.3).
    /// </summary>
    public static ResultObject<TimeSpan> Offset(DateTime rtcUtc, DateTime currentUtc, DateTime networkUtc, DateTime lowerBound)
    {
        if (!IsPlausible(networkUtc, lowerBound))
            return Refused("NetworkTimeImplausible", "Netzwerkzeit " + networkUtc.ToString("u") + " liegt ausserhalb des plausiblen Bereichs.");
        TimeSpan step = networkUtc - currentUtc;
        if (IsPlausible(currentUtc, lowerBound) && step < -MaximumBackward)
            return Refused("BackwardStepRefused", "Netzwerkzeit liegt " + (-step).ToString(@"d\.hh\:mm\:ss") + " hinter der Uhr; mehr als 5 Minuten Rueckschritt werden nicht uebernommen.");
        return ResultObjectFactory.CreateSuccess(networkUtc - rtcUtc);
    }

    private static ResultObject<TimeSpan> Refused(string cause, string message) =>
        ResultObjectFactory.CreateFailure<TimeSpan>(ResultCode.Failure, "cause='" + cause + "' message='" + message + "'");
}
