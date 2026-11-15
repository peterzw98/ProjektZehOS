using System;
using System.Collections.Generic;
using ProjektZeh.Input;

namespace ProjektZeh.Runtime.Wasm;

internal enum WasmInputEventType { KeyDown = 1, KeyUp, Text, PointerMove, PointerDown, PointerUp, Scroll, Reset }
internal readonly record struct WasmInputEvent(WasmInputEventType Type, int Code = 0, int X = 0, int Y = 0);

/// <summary>Eingabe im Framekoordinatensystem; die Sitzung synchronisiert alle Zugriffe mit ihrer Sperre.</summary>
internal sealed class WasmInputBuffer
{
    private const int Capacity = 128;
    private const int LegacyCapacity = 64;
    private readonly WasmInputEvent[] events = new WasmInputEvent[Capacity];
    private readonly Queue<int> keys = new();
    private readonly HashSet<byte> held = new();
    private int head;
    private int count;
    private char highSurrogate;
    private int frameWidth;
    private int frameHeight;
    private int scale;
    private long left;
    private long top;

    public void SetViewport(int width, int height, int imageWidth, int imageHeight)
    {
        frameWidth = imageWidth;
        frameHeight = imageHeight;
        scale = width > 0 && height > 0 && imageWidth > 0 && imageHeight > 0
            ? Math.Max(1, Math.Min(width / imageWidth, height / imageHeight)) : 0;
        left = ((long)width - (long)imageWidth * scale) / 2;
        top = ((long)height - (long)imageHeight * scale) / 2;
    }

    public void Handle(InputEvent value)
    {
        switch (value.Type)
        {
            case InputEventType.Reset:
                Reset();
                return;
            case InputEventType.KeyDown:
                if (!value.IsRepeat && held.Add(value.KeyCode) && keys.Count < LegacyCapacity)
                    keys.Enqueue(0x100 | value.KeyCode);
                Enqueue(new(WasmInputEventType.KeyDown, value.KeyCode));
                return;
            case InputEventType.KeyUp:
                if (!value.IsRepeat && held.Remove(value.KeyCode) && keys.Count < LegacyCapacity)
                    keys.Enqueue(value.KeyCode);
                Enqueue(new(WasmInputEventType.KeyUp, value.KeyCode));
                return;
            case InputEventType.Text:
                Text(value.Character);
                return;
        }

        if (scale == 0) return;
        long x = (long)value.X - left;
        long y = (long)value.Y - top;
        bool inside = x >= 0 && y >= 0 && x < (long)frameWidth * scale && y < (long)frameHeight * scale;
        int pixelX = (int)Math.Clamp(x / scale, 0, frameWidth - 1L);
        int pixelY = (int)Math.Clamp(y / scale, 0, frameHeight - 1L);
        switch (value.Type)
        {
            case InputEventType.PointerMove:
                Enqueue(new(WasmInputEventType.PointerMove, 0, pixelX, pixelY));
                break;
            case InputEventType.PointerDown when inside:
                Enqueue(new(WasmInputEventType.PointerDown, (int)value.Button, pixelX, pixelY));
                break;
            case InputEventType.PointerUp:
                // Ein Loslassen ausserhalb des Bildes beendet weiterhin einen begonnenen Ziehvorgang.
                Enqueue(new(WasmInputEventType.PointerUp, (int)value.Button, pixelX, pixelY));
                break;
            case InputEventType.PointerScroll when inside:
                Enqueue(new(WasmInputEventType.Scroll, value.ScrollDelta, pixelX, pixelY));
                break;
        }
    }

    public void Reset()
    {
        foreach (byte code in held)
            if (keys.Count < LegacyCapacity) keys.Enqueue(code);
        held.Clear();
        head = count = 0;
        highSurrogate = '\0';
        Enqueue(new(WasmInputEventType.Reset));
    }

    public int PollKey() => keys.Count > 0 ? keys.Dequeue() : -1;

    public bool TryRead(out WasmInputEvent value)
    {
        if (count == 0) { value = default; return false; }
        value = events[head];
        head = (head + 1) % Capacity;
        count--;
        return true;
    }

    private void Text(char character)
    {
        if (highSurrogate != '\0')
        {
            char previous = highSurrogate;
            highSurrogate = '\0';
            if (char.IsLowSurrogate(character))
            {
                Enqueue(new(WasmInputEventType.Text, 0x10000 + ((previous - 0xd800) << 10) + character - 0xdc00));
                return;
            }
            Enqueue(new(WasmInputEventType.Text, 0xfffd));
        }
        if (char.IsHighSurrogate(character)) highSurrogate = character;
        else Enqueue(new(WasmInputEventType.Text, char.IsLowSurrogate(character) ? 0xfffd : character));
    }

    private void Enqueue(WasmInputEvent value)
    {
        int last = (head + count - 1 + Capacity) % Capacity;
        if (count > 0 && value.Type == WasmInputEventType.PointerMove && events[last].Type == value.Type)
        {
            events[last] = value;
            return;
        }
        if (count == Capacity)
        {
            // Nach verlorenen Ereignissen darf die App keine Taste oder Maustaste als gehalten behalten.
            head = count = 0;
            events[count++] = new(WasmInputEventType.Reset);
        }
        events[(head + count) % Capacity] = value;
        count++;
    }
}
