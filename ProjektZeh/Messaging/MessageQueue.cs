using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ProjektZeh.Messaging;

internal enum MessageQueueResult
{
    Success,
    Empty,
    Full,
    Closed,
    Cancelled,
    TimedOut
}

internal sealed class MessageQueue<T>
{
    private readonly object gate = new();
    private readonly Queue<T> messages;
    private bool closed;

    public int Capacity { get; }
    public int Count { get { lock (gate) return messages.Count; } }

    public MessageQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        messages = new Queue<T>(capacity);
    }

    public MessageQueueResult TrySend(T message)
    {
        lock (gate)
        {
            if (closed) return MessageQueueResult.Closed;
            if (messages.Count == Capacity) return MessageQueueResult.Full;
            messages.Enqueue(message);
            Monitor.PulseAll(gate);
            return MessageQueueResult.Success;
        }
    }

    public MessageQueueResult TryReceive(out T message)
    {
        lock (gate)
        {
            message = default!;
            if (messages.Count == 0) return closed ? MessageQueueResult.Closed : MessageQueueResult.Empty;
            message = messages.Dequeue();
            Monitor.PulseAll(gate);
            return MessageQueueResult.Success;
        }
    }

    public MessageQueueResult Send(T message, int timeoutMilliseconds = Timeout.Infinite,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeoutMilliseconds);
        using var registration = cancellationToken.Register(WakeWaiters);
        long started = Stopwatch.GetTimestamp();
        lock (gate)
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested) return MessageQueueResult.Cancelled;
                if (closed) return MessageQueueResult.Closed;
                if (messages.Count < Capacity) return TrySend(message);
                if (!Wait(started, timeoutMilliseconds)) return MessageQueueResult.TimedOut;
            }
        }
    }

    public MessageQueueResult Receive(out T message, int timeoutMilliseconds = Timeout.Infinite,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeoutMilliseconds);
        using var registration = cancellationToken.Register(WakeWaiters);
        long started = Stopwatch.GetTimestamp();
        lock (gate)
        {
            message = default!;
            while (true)
            {
                if (cancellationToken.IsCancellationRequested) return MessageQueueResult.Cancelled;
                if (messages.Count > 0) return TryReceive(out message);
                if (closed) return MessageQueueResult.Closed;
                if (!Wait(started, timeoutMilliseconds)) return MessageQueueResult.TimedOut;
            }
        }
    }

    public void Close()
    {
        lock (gate)
        {
            closed = true;
            Monitor.PulseAll(gate);
        }
    }

    private bool Wait(long started, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds == Timeout.Infinite)
        {
            Monitor.Wait(gate);
            return true;
        }
        long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed >= timeoutMilliseconds) return false;
        // Bedingung und Signal teilen dieselbe Sperre: zwischen Prüfung und Warten geht kein Wecksignal verloren.
        Monitor.Wait(gate, timeoutMilliseconds - (int)elapsed);
        return true;
    }

    private void WakeWaiters()
    {
        lock (gate) Monitor.PulseAll(gate);
    }

    private static void ValidateTimeout(int timeoutMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMilliseconds, Timeout.Infinite);
    }
}
