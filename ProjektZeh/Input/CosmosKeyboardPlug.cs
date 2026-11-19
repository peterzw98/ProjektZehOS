using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.System.Input;
#pragma warning disable COSMOS0001 // IrqSpinLock (ZehOS-Patch irq-mask-scope, #473) ist als COSMOS0001 markiert; die Nutzung ist bewusst.

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
    // Schreiber (Interrupt) und Leser (Hauptschleife) nehmen dieselbe Sperre; auf mehreren CPUs reicht die Maske nicht.
    private static IrqSpinLock bufferLock;

    internal static void Initialize()
    {
        // Der Tastatur-Interrupt kann schon laufen; ohne Sperre setzte Clear den Kopf mitten in einem Einreihen zurueck.
        using (bufferLock.Acquire())
        {
            events.Clear();
            dropped = 0;
        }
    }

    // shortcut: Cosmos 3.0.94 verwirft Break-Ereignisse; bei einer oeffentlichen Rohereignis-API diesen Plug entfernen.
    public static void ReportScanCode(byte scanCode, bool released)
    {
        using (bufferLock.Acquire())
        {
            if (!events.TryEnqueue(new(released ? InputEventType.KeyUp : InputEventType.KeyDown, scanCode))) dropped++;
        }
    }

    internal static bool TryRead(out InputEvent value, out long lost)
    {
        using (bufferLock.Acquire())
        {
            lost = dropped;
            if (lost != 0)
            {
                events.Clear();
                dropped = 0;
            }
            return events.TryDequeue(out value);
        }
    }
}
#pragma warning restore COSMOS0001
