using System.Runtime.CompilerServices;
using Cosmos.Kernel.System.Input;
using Cosmos.Kernel.System.Input.Layouts;

namespace ProjektZeh.Input;

internal sealed class CosmosInputService
{
    private readonly KeyboardLayout fallbackLayout = new USStandardLayout();
    private bool capsLock;
    private bool numLock;

    public CosmosInputService() => CosmosKeyboardCapture.Initialize();

    public static void SetScreenSize(int width, int height) => MouseManager.SetScreenSize(width, height);

    public void Poll(InputService input)
    {
        // Ein Eingabesturm darf Aktualisieren und Zeichnen nicht verdraengen.
        for (var count = 0; count < 64; count++)
        {
            bool available = CosmosKeyboardCapture.TryRead(out var raw, out long lost);
            if (lost != 0)
            {
                input.Reset(lost + InputService.QueueCapacity);
                capsLock = numLock = false;
            }
            if (!available) break;
            bool released = raw.Type == InputEventType.KeyUp;
            if (!released && !input.IsKeyDown(raw.KeyCode))
            {
                if (raw.KeyCode == (byte)PhysicalKey.CapsLock) capsLock = !capsLock;
                if (raw.KeyCode == (byte)PhysicalKey.NumLock) numLock = !numLock;
            }
            char character = '\0';
            if (!released)
            {
                var key = ConvertScanCode(KeyboardManager.GetLayout() ?? fallbackLayout, raw.KeyCode,
                    input.IsKeyDown((byte)PhysicalKey.Control),
                    input.IsKeyDown((byte)PhysicalKey.LeftShift) || input.IsKeyDown((byte)PhysicalKey.RightShift),
                    input.IsKeyDown((byte)PhysicalKey.Alt), numLock, capsLock);
                character = key?.KeyChar ?? '\0';
            }
            input.ReportKey(raw.KeyCode, released, character);
        }

        // Der Maus-Interrupt setzt X, Y und Tasten einzeln; ohne Maske mischt ein Paket dazwischen zwei Meldungen.
        int x, y;
        PointerButtons buttons = PointerButtons.None;
#pragma warning disable COSMOS0001, NAOT0007 // Die in 3.0.94 gepruefte Core-Interruptmaske ist fuer diesen kurzen Eingabeschutz erforderlich.
        var mask = Cosmos.Kernel.Core.Scheduler.SchedulerManager.MaskInterrupts();
#pragma warning restore COSMOS0001, NAOT0007
        try
        {
            x = MouseManager.X;
            y = MouseManager.Y;
            if (MouseManager.LeftButton) buttons |= PointerButtons.Left;
            if (MouseManager.RightButton) buttons |= PointerButtons.Right;
            if (MouseManager.MiddleButton) buttons |= PointerButtons.Middle;
        }
        finally { mask.Dispose(); }
        input.ReportPointer(x, y, buttons);
    }

    // Verwendet die vorhandene Layoutuebersetzung, die in Cosmos 3.0.94 noch intern ist.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ConvertScanCode")]
    private static extern KeyEvent? ConvertScanCode(KeyboardLayout layout, byte scanCode,
        bool control, bool shift, bool alt, bool numLock, bool capsLock);
}
