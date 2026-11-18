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
    /// <summary>Nur bis Etappe 5: Idle-Thread von Cosmos 3.0.97 (Hauptschleife), der nie blockiert.</summary>
    public bool AlwaysYields;
}
