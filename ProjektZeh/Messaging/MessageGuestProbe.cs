#if ZEHOS_THREAD_PROBE
using System;
using System.Threading;
using Cosmos.Kernel.System.Diagnostics;

namespace ProjektZeh.Messaging;

internal static class MessageGuestProbe
{
    public static void Run()
    {
        var queue = new MessageQueue<int>(2);
        Check(queue.TryReceive(out _) == MessageQueueResult.Empty, "empty");
        queue.TrySend(1);
        queue.TrySend(2);
        Check(queue.TrySend(3) == MessageQueueResult.Full, "full");
        Check(queue.Send(3, 20) == MessageQueueResult.TimedOut, "send timeout");
        queue.TryReceive(out _);
        queue.TryReceive(out _);
        Check(queue.Receive(out _, 20) == MessageQueueResult.TimedOut, "receive timeout");
        var accounts = new OrderedTransfer();
        int produced = 0;
        int consumed = 0;
        int invalid = 0;
        var producer = new Thread(() =>
        {
            for (int i = 1; i <= 100; i++)
            {
                if (queue.Send(i, 5000) != MessageQueueResult.Success) { Interlocked.Increment(ref invalid); break; }
                accounts.Transfer(true);
                Interlocked.Increment(ref produced);
            }
            queue.Close();
        });
        var consumer = new Thread(() =>
        {
            int expected = 1;
            while (true)
            {
                var result = queue.Receive(out int number, 5000);
                if (result == MessageQueueResult.Closed) break;
                if (result != MessageQueueResult.Success) { Interlocked.Increment(ref invalid); break; }
                if (number != expected++) Interlocked.Increment(ref invalid);
                accounts.Transfer(false);
                Interlocked.Increment(ref consumed);
            }
        });
        consumer.Start();
        producer.Start();
        Check(producer.Join(10000) && consumer.Join(10000), "worker join");
        Check(produced == 100 && consumed == 100 && invalid == 0, "numbered delivery");
        Check(accounts.Total == 2000, "lock order");
        Check(queue.Receive(out _, 0) == MessageQueueResult.Closed, "closed");
        CheckWaiters(false, false);
        CheckWaiters(true, false);
        CheckWaiters(false, true);
        CheckWaiters(true, true);
        Log.WriteString("MESSAGES_PROBE PASS numbered=100 consumed=100 duplicates=0 order=ok close/cancel/timeout/locks=ok\n");
    }

    private static void CheckWaiters(bool sending, bool cancel)
    {
        var queue = new MessageQueue<int>(1);
        if (sending) queue.TrySend(1);
        using var cancellation = new CancellationTokenSource();
        MessageQueueResult result = MessageQueueResult.Success;
        using var entered = new ManualResetEvent(false);
        var thread = new Thread(() =>
        {
            entered.Set();
            result = sending ? queue.Send(2, 5000, cancellation.Token) : queue.Receive(out _, 5000, cancellation.Token);
        });
        thread.Start();
        Check(entered.WaitOne(2000), "waiter entered");
        Thread.Sleep(30);
        if (cancel) cancellation.Cancel(); else queue.Close();
        Check(thread.Join(2000), "waiter join");
        Check(result == (cancel ? MessageQueueResult.Cancelled : MessageQueueResult.Closed), "waiter result");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("messages: " + name);
    }
}
#endif
