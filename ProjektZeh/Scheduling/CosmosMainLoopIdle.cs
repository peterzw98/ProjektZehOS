using System.Threading;

namespace ProjektZeh.Scheduling;

/// <summary>Leerlauf der Hauptschleife zwischen zwei Runden.</summary>
internal static class CosmosMainLoopIdle
{
    // Seit SMP-Phase 5b parkt Thread.Sleep den Thread (Monitor.Wait -> ConditionVariable.WaitTimeout -> MarkSleeping/Park)
    // und gibt die CPU sofort per Reschedule-Interrupt ab; der Tick weckt ihn wieder. Ein Yield hielte die Hauptschleife dagegen
    // auf der CPU: Sie hat Klasse Interaktiv mit Restbudget, PickNext waehlt sie erneut, und Normal und Hintergrund kaemen
    // nur ueber die Aushungerungsgrenze dran (Review #479, P1). Deshalb immer schlafen, auch wenn Worker bereit sind.
    public static void Wait() => Thread.Sleep(1);
}
