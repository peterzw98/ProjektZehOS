using System.Runtime.CompilerServices;
using Cosmos.Kernel.System.Input;
using Cosmos.Kernel.System.Input.Layouts;

namespace ProjektZeh.Input;

internal sealed class CosmosInputService
{
    private readonly KeyboardLayout layout = new DEStandardLayout();
    private bool capsLock;
    private bool numLock;

    public CosmosInputService()
    {
        KeyboardManager.SetLayout(layout);
        CosmosKeyboardCapture.Initialize();
    }

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
                bool altGr = input.IsKeyDown((byte)PhysicalKey.AltGr);
                bool control = input.IsKeyDown((byte)PhysicalKey.Control);
                bool shift = input.IsKeyDown((byte)PhysicalKey.LeftShift) || input.IsKeyDown((byte)PhysicalKey.RightShift);
                // AltGr hat eine eigene Zeichenspalte und darf nicht durch CapsLock oder Shift verschwinden.
                var key = ConvertScanCode(layout, raw.KeyCode, control || altGr, shift && !altGr,
                    input.IsKeyDown((byte)PhysicalKey.Alt) || altGr, numLock, capsLock && !altGr);
                character = GermanKeyboard.Translate(raw.KeyCode, key?.KeyChar ?? '\0', shift, capsLock, control, altGr);
            }
            input.ReportKey(raw.KeyCode, released, character);
        }

        // Der Maus-Interrupt setzt X, Y und Tasten einzeln; ohne Maske mischt ein Paket dazwischen zwei Meldungen.
        int x, y, scroll;
        PointerButtons buttons = PointerButtons.None;
#pragma warning disable COSMOS0001, NAOT0007 // Die in 3.0.94 gepruefte Core-Interruptmaske ist fuer diesen kurzen Eingabeschutz erforderlich.
        var mask = Cosmos.Kernel.Core.Scheduler.SchedulerManager.MaskInterrupts();
#pragma warning restore COSMOS0001, NAOT0007
        try
        {
            x = MouseManager.X;
            y = MouseManager.Y;
            scroll = MouseManager.ScrollDelta;
            MouseManager.ResetScrollDelta();
            if (MouseManager.LeftButton) buttons |= PointerButtons.Left;
            if (MouseManager.RightButton) buttons |= PointerButtons.Right;
            if (MouseManager.MiddleButton) buttons |= PointerButtons.Middle;
        }
        finally { mask.Dispose(); }
        input.ReportPointer(x, y, buttons, scroll);
    }

    // Verwendet die vorhandene Layoutuebersetzung, die in Cosmos 3.0.94 noch intern ist.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ConvertScanCode")]
    private static extern KeyEvent? ConvertScanCode(KeyboardLayout layout, byte scanCode,
        bool control, bool shift, bool alt, bool numLock, bool capsLock);
}
