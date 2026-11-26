namespace ProjektZeh.Scheduling;

/// <summary>Kleinere Zahl = höherer Vorrang.</summary>
internal enum ThreadClass : byte { Interactive = 0, Normal = 1, Background = 2 }

/// <summary>Zustand eines Threads für die Policy; der Adapter legt ihn in SchedulerThread.SchedulerData.</summary>
internal sealed class ClassThreadState
{
    public ThreadClass Class = ThreadClass.Normal;
    /// <summary>Rest der laufenden Zeitscheibe.</summary>
    public ulong RemainingNs;
    /// <summary>CPU der letzten Auswahl, -1 = noch nie.</summary>
    public int LastCpu = -1;
    /// <summary>#674 V2: Stopwatch-Zeitpunkt, seit dem der Thread auf LastCpu läuft.</summary>
    public long OnCpuSince;
    /// <summary>#674 V2: Stopwatch-Zeitpunkt des letzten Umzugs, 0 = nie.</summary>
    public long MovedAt;
#if ZEHOS_SMP_PROBE
    // Nur Diagnose #674 (diag/674-v2-messung, nie mergen): Umzuege und Antworten des freiwilligen Umzugs je Thread, damit der
    // Gastlauf Pendeln und den Zeitpunkt des Umzugs belegt statt aus Code und Zeitmodell. Geschrieben im IRQ bzw. unter den
    // Queue-Sperren; das Feld legt OnThreadCreate an (dort ist Allokation erlaubt), im IRQ nur Zuweisungen.
    public const int ProbeMoveLog = 8;
    /// <summary>Alle OnThreadMigrate (Ausgleich, Verdraengung, freiwillig).</summary>
    public int ProbeMigrations;
    /// <summary>Gefragt (SelectMoveTarget), abgelehnt nach Grund, vorgeschlagen.</summary>
    public int ProbeAsked, ProbeTooShortOnCpu, ProbeTooSoonAfterMove, ProbeSiblingIdle, ProbeNoFreeCore, ProbeProposed;
    /// <summary>Stopwatch-Zeitpunkt der ersten Frage, -1 = nie; belegte CPUs (laufend oder Wartende) bei der letzten Ablehnung NoFreeCore.</summary>
    public long ProbeFirstAsk = -1, ProbeBusyMaskAtNoFree;
    /// <summary>Die ersten ProbeMoveLog Umzuege: Zeitpunkt, von, nach.</summary>
    public readonly long[] ProbeMoveAt = new long[ProbeMoveLog];
    public readonly int[] ProbeMoveFrom = new int[ProbeMoveLog], ProbeMoveTo = new int[ProbeMoveLog];
#endif
}
