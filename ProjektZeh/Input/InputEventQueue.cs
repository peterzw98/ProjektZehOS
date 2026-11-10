using System;

namespace ProjektZeh.Input;

internal sealed class InputEventQueue
{
    private readonly InputEvent[] events;
    private int head;
    private int count;

    public InputEventQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        events = new InputEvent[capacity];
    }

    public bool TryEnqueue(InputEvent value)
    {
        if (count == events.Length) return false;
        events[(head + count) % events.Length] = value;
        count++;
        return true;
    }

    public bool TryDequeue(out InputEvent value)
    {
        value = default;
        if (count == 0) return false;
        value = events[head];
        head = (head + 1) % events.Length;
        count--;
        return true;
    }

    public void Clear() => head = count = 0;
}
