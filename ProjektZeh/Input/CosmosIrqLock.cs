using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Cosmos.Kernel.Core.Scheduler;

namespace ProjektZeh.Input;

/// <summary>
/// Sperre fuer Daten, die ein Interrupt und ein Thread teilen. Maskiert Interrupts und dreht dann, bis sie frei ist.
/// In ihr gilt ohne Ausnahme: keine Allokation, kein Protokoll, keine zweite Sperre, nur wenige Zeilen.
/// </summary>
/// <remarks>
/// Die Maske allein schuetzt nur gegen Interrupts derselben CPU; erst die Sperre schuetzt auch gegen eine zweite CPU
/// (Etappe 4, E4-D). Auf einer CPU kann sie nach der Maske nie belegt sein, das Verhalten bleibt dort wie bisher.
/// Nicht wiedereintrittsfaehig: wer sie haelt, darf keinen Pfad aufrufen, der sie erneut nimmt (Selbstblockade).
/// Als veraenderliches statisches Feld neben dem Puffer halten; ein readonly-Feld wuerde je Aufruf kopiert.
/// Eine aeussere Sperre ist zulaessig, wenn die Reihenfolge immer gleich bleibt: Der Tablettbericht kann unter der
/// DeviceLock des USB-Controllers laufen (USB-Sperre, dann diese); einen umgekehrten Weg gibt es nicht.
/// </remarks>
internal struct CosmosIrqLock
{
    private int taken;

#pragma warning disable COSMOS0001, NAOT0007 // Die gepruefte Core-Interruptmaske ist fuer diesen kurzen Eingabeschutz erforderlich.
    [UnscopedRef]
    public Scope Enter()
    {
        var mask = SchedulerManager.MaskInterrupts();
        // Erst maskieren, dann drehen: sonst koennte der eigene Interrupt die Sperre auf dieser CPU nie mehr bekommen.
        while (Interlocked.CompareExchange(ref taken, 1, 0) != 0) X86Base.Pause();
        return new Scope(ref taken, mask);
    }

    internal ref struct Scope
    {
        private ref int taken;
        private InterruptMaskScope mask;
        // Ein zweites Dispose gaebe sonst eine Sperre frei, die inzwischen ein anderer haelt (wie IrqLockScope in Cosmos).
        private bool disposed;

        internal Scope(ref int taken, InterruptMaskScope mask)
        {
            this.taken = ref taken;
            this.mask = mask;
        }

        /// <summary>Gibt erst die Sperre frei und stellt dann die Maske wieder her.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Volatile.Write(ref taken, 0);
            mask.Dispose();
        }
    }
#pragma warning restore COSMOS0001, NAOT0007
}
