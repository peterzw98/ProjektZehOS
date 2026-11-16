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
    private int targetWidth;
    private int targetHeight;
    private long left;
    private long top;

    public void SetViewport(int width, int height, int imageWidth, int imageHeight)
    {
        frameWidth = imageWidth;
        frameHeight = imageHeight;
        (targetWidth, targetHeight) = WasmImageFit.Fit(width, height, imageWidth, imageHeight);
        left = ((long)width - targetWidth) / 2;
        top = ((long)height - targetHeight) / 2;
    }

    public void Handle(InputEvent value)
    {
        switch (value.Type)
        {
            case InputEventType.Reset:
                Reset();
                return;
            case InputEventType.KeyDown:
                // Nur gemeldete Druecke gelten als gehalten; ihr Loslassen wird nie verworfen, damit keine
                // Taste haengen bleibt. Die Warteschlange bleibt durch maximal 256 Tastencodes begrenzt.
                if (!value.IsRepeat && keys.Count < LegacyCapacity && held.Add(value.KeyCode))
                    keys.Enqueue(0x100 | value.KeyCode);
                Enqueue(new(WasmInputEventType.KeyDown, value.KeyCode));
                return;
            case InputEventType.KeyUp:
                if (!value.IsRepeat && held.Remove(value.KeyCode))
                    keys.Enqueue(value.KeyCode);
                Enqueue(new(WasmInputEventType.KeyUp, value.KeyCode));
                return;
            case InputEventType.Text:
                Text(value.Character);
                return;
        }

        if (targetWidth == 0) return;
        long x = (long)value.X - left;
        long y = (long)value.Y - top;
        bool inside = x >= 0 && y >= 0 && x < targetWidth && y < targetHeight;
        // Erst begrenzen, dann abbilden: Positionen weit ausserhalb duerfen im long-Produkt nicht ueberlaufen.
        int pixelX = WasmImageFit.SourceIndex(Math.Clamp(x, 0, targetWidth - 1L), targetWidth, frameWidth);
        int pixelY = WasmImageFit.SourceIndex(Math.Clamp(y, 0, targetHeight - 1L), targetHeight, frameHeight);
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
            keys.Enqueue(code);
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
