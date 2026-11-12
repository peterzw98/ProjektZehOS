using System;

namespace ProjektZeh.Input;

internal sealed class InputService
{
    public const int QueueCapacity = 256;
    private readonly InputEventQueue events = new(QueueCapacity);
    private readonly bool[] pressed = new bool[128];
    private readonly int width;
    private readonly int height;

    public int PointerX { get; private set; }
    public int PointerY { get; private set; }
    public PointerButtons Buttons { get; private set; }
    public int PressedKeyCount { get; private set; }
    public long DroppedEvents { get; private set; }

    public InputService(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        this.width = width;
        this.height = height;
        PointerX = width / 2;
        PointerY = height / 2;
    }

    public bool IsKeyDown(byte key) => key < pressed.Length && pressed[key];

    public void ReportKey(byte code, bool released, char character = '\0')
    {
        if (code == 0 || code >= pressed.Length) return;
        bool wasPressed = pressed[code];
        pressed[code] = !released;
        if (wasPressed != !released) PressedKeyCount += released ? -1 : 1;
        if (!Publish(new(released ? InputEventType.KeyUp : InputEventType.KeyDown,
                code, IsRepeat: !released && wasPressed, IsTextKey: !released && !char.IsControl(character)))) return;
        if (!released && !char.IsControl(character)) Publish(new(InputEventType.Text, Character: character));
    }

    public void ReportPointer(int x, int y, PointerButtons buttons)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        if (x != PointerX || y != PointerY)
        {
            PointerX = x;
            PointerY = y;
            if (!Publish(new(InputEventType.PointerMove, X: x, Y: y))) return;
        }
        PointerButtons changed = Buttons ^ buttons;
        Buttons = buttons;
        for (var bit = 1; bit <= 4; bit <<= 1)
        {
            var button = (PointerButtons)bit;
            if ((changed & button) != 0 && !Publish(new(
                    (buttons & button) != 0 ? InputEventType.PointerDown : InputEventType.PointerUp,
                    X: x, Y: y, Button: button))) return;
        }
    }

    public bool TryRead(out InputEvent value) => events.TryDequeue(out value);

    public void Reset(long droppedEvents)
    {
        DroppedEvents += droppedEvents;
        Array.Clear(pressed);
        PressedKeyCount = 0;
        Buttons = PointerButtons.None;
        events.Clear();
        events.TryEnqueue(new(InputEventType.Reset));
    }

    private bool Publish(InputEvent value)
    {
        if (events.TryEnqueue(value)) return true;
        // Auch verlorene Freigaben duerfen keine gedrueckten Tasten hinterlassen.
        Reset(QueueCapacity + 1);
        return false;
    }
}
