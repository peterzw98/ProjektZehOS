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
    // Schreiber (Interrupt) und Leser (Hauptschleife) nehmen dieselbe Sperre; auf mehreren CPUs reicht die Maske nicht.
    private static CosmosIrqLock bufferLock;

    internal static void Initialize()
    {
        events.Clear();
        dropped = 0;
    }

    // shortcut: Cosmos 3.0.94 verwirft Break-Ereignisse; bei einer oeffentlichen Rohereignis-API diesen Plug entfernen.
    public static void ReportScanCode(byte scanCode, bool released)
    {
        using (bufferLock.Enter())
        {
            if (!events.TryEnqueue(new(released ? InputEventType.KeyUp : InputEventType.KeyDown, scanCode))) dropped++;
        }
    }

    internal static bool TryRead(out InputEvent value, out long lost)
    {
        using (bufferLock.Enter())
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
