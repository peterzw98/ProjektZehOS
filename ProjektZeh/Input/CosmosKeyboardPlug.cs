using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.System.Input;

namespace ProjektZeh.Input;

[Plug(typeof(KeyboardManager))]
public static class CosmosKeyboardPlug
{
    [PlugMember]
    public static void HandleScanCode(byte scanCode, bool released) =>
        CosmosKeyboardCapture.ReportScanCode(scanCode, released);
}

public static class CosmosKeyboardCapture
{
    private static readonly InputEventQueue events = new(InputService.QueueCapacity);
    private static long dropped;

    internal static void Initialize()
    {
        events.Clear();
        dropped = 0;
    }

    // shortcut: Cosmos 3.0.94 verwirft Break-Ereignisse; bei einer oeffentlichen Rohereignis-API diesen Plug entfernen.
    public static void ReportScanCode(byte scanCode, bool released)
    {
        if (!events.TryEnqueue(new(released ? InputEventType.KeyUp : InputEventType.KeyDown, scanCode))) dropped++;
    }

    internal static bool TryRead(out InputEvent value, out long lost)
    {
#pragma warning disable COSMOS0001, NAOT0007 // Die in 3.0.94 gepruefte Core-Interruptmaske ist fuer diesen kurzen Eingabeschutz erforderlich.
        var mask = Cosmos.Kernel.Core.Scheduler.SchedulerManager.MaskInterrupts();
#pragma warning restore COSMOS0001, NAOT0007
        lost = dropped;
        if (lost != 0)
        {
            events.Clear();
            dropped = 0;
        }
        return events.TryDequeue(out value);
    }
}
