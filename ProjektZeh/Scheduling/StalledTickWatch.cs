namespace ProjektZeh.Scheduling;

/// <summary>
/// #727: Sicht einer CPU auf die Tickzähler der anderen. VMware Workstation stellt einen abgelaufenen LAPIC-Timer erst beim
/// nächsten Austritt der virtuellen CPU zu; eine CPU, die nur rechnet, bekam unter Linux-Host 7.3 bis über 1 s keinen Tick und
/// wurde nicht verdrängt. Eine CPU, die ihre Ticks bekommt (eine haltende tritt aus), meldet eine fremde CPU zum Anstoßen, wenn
/// diese einen Thread rechnet und ihr Zähler über <see cref="StallTicks"/> eigene Ticks stillsteht. Alle CPUs ticken mit
/// derselben Periode; zwei eigene Ticks ohne fremden gibt es sonst nur bei langen maskierten Abschnitten oder einer vom Host
/// verdrängten vCPU, und dann kostet es nur einen überflüssigen IPI. Feste Größe, keine Allokation nach dem Konstruktor (läuft
/// im Timer-Interrupt). Nur von der beobachtenden CPU mit maskierten Interrupts benutzt, daher ohne Sperre.
/// </summary>
internal sealed class StalledTickWatch(int cpuCount)
{
    public const int StallTicks = 2;
    private readonly uint[] seen = new uint[cpuCount];
    private readonly int[] still = new int[cpuCount];

    public int CpuCount => seen.Length;

    /// <summary>Ein eigener Tick: true, wenn <paramref name="cpu"/> jetzt angestoßen werden soll.</summary>
    public bool Observe(int cpu, uint ticks, bool runsThread)
    {
        if (ticks != seen[cpu] || !runsThread)
        {
            // Eine CPU auf ihrem Idle-Thread hält und bekommt ihren Tick selbst.
            seen[cpu] = ticks;
            still[cpu] = 0;
            return false;
        }
        if (++still[cpu] < StallTicks) return false;
        // Danach erst nach weiteren StallTicks erneut: ein Anstoß je StallTicks Tickperioden und steckender CPU.
        still[cpu] = 0;
        return true;
    }
}
