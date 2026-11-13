using System;

namespace ProjektZeh.Scheduling;

internal sealed class RoundRobinQueue<T>(int capacity) where T : class
{
    private readonly T?[] items = new T[capacity];
    public int Count { get; private set; }
    public T? At(int index) => index >= 0 && index < Count ? items[index] : null;
    public bool Add(T item) => Insert(item, false);
    public bool AddFirst(T item) => Insert(item, true);

    private bool Insert(T item, bool first)
    {
        for (int i = 0; i < Count; i++) if (ReferenceEquals(items[i], item)) return true;
        if (Count == items.Length) return false;
        int index = first ? 0 : Count;
        for (int i = Count; i > index; i--) items[i] = items[i - 1];
        items[index] = item;
        Count++;
        return true;
    }

    public void Remove(T item)
    {
        for (int i = 0; i < Count; i++)
        {
            if (!ReferenceEquals(items[i], item)) continue;
            for (int j = i; j < Count - 1; j++) items[j] = items[j + 1];
            items[--Count] = null;
            return;
        }
    }

    public T? Take()
    {
        T? item = At(0);
        if (item != null) Remove(item);
        return item;
    }
}

internal sealed class SchedulerTimeSlice
{
    private readonly ulong quantum;
    private ulong remaining;
    public SchedulerTimeSlice(ulong quantum) { this.quantum = quantum; remaining = quantum; }
    public void Reset() => remaining = quantum;
    public bool Charge(ulong elapsed)
    {
        remaining = elapsed >= remaining ? 0 : remaining - elapsed;
        return remaining == 0;
    }
}
