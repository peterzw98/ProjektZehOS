using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Messaging;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MessageQueueTests
{
    [TestMethod]
    public void TrySendReceive_BoundedQueue_PreservesFifoAndReportsStates()
    {
        var queue = new MessageQueue<int>(2);
        Assert.AreEqual(MessageQueueResult.Empty, queue.TryReceive(out _));
        Assert.AreEqual(MessageQueueResult.Success, queue.TrySend(4));
        Assert.AreEqual(MessageQueueResult.Success, queue.TrySend(7));
        Assert.AreEqual(MessageQueueResult.Full, queue.TrySend(9));
        Assert.AreEqual(2, queue.Count);
        Assert.AreEqual(MessageQueueResult.Success, queue.TryReceive(out int first));
        Assert.AreEqual(4, first);
        queue.Close();
        Assert.AreEqual(MessageQueueResult.Closed, queue.TrySend(9));
        Assert.AreEqual(MessageQueueResult.Success, queue.TryReceive(out int second));
        Assert.AreEqual(7, second);
        Assert.AreEqual(MessageQueueResult.Closed, queue.TryReceive(out _));
    }

    [TestMethod]
    public void ConstructorAndWait_InvalidArguments_RejectThem()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new MessageQueue<int>(0));
        var queue = new MessageQueue<int>(1);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => queue.Receive(out _, -2));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => queue.Send(1, -2));
    }

    [TestMethod]
    public void ReceiveAndSend_UnsatisfiedCondition_TimeOut()
    {
        var queue = new MessageQueue<int>(1);
        Assert.AreEqual(MessageQueueResult.TimedOut, queue.Receive(out _, 20));
        queue.TrySend(1);
        Assert.AreEqual(MessageQueueResult.TimedOut, queue.Send(2, 20));
        Assert.AreEqual(1, queue.Count);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Wait_CloseOrCancellation_WakesSenderAndReceiver(bool sending, bool cancel)
    {
        var queue = new MessageQueue<int>(1);
        if (sending) queue.TrySend(1);
        using var token = new CancellationTokenSource();
        MessageQueueResult result = MessageQueueResult.Success;
        var thread = new Thread(() => result = sending
            ? queue.Send(2, Timeout.Infinite, token.Token)
            : queue.Receive(out _, Timeout.Infinite, token.Token));
        thread.Start();
        Assert.IsTrue(SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, 2000));
        if (cancel) token.Cancel(); else queue.Close();
        Assert.IsTrue(thread.Join(2000));
        Assert.AreEqual(cancel ? MessageQueueResult.Cancelled : MessageQueueResult.Closed, result);
    }

    [TestMethod]
    public void SendReceive_AlreadyCancelled_DoesNotMoveMessages()
    {
        var queue = new MessageQueue<int>(1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.AreEqual(MessageQueueResult.Cancelled, queue.Send(1, 0, cancelled.Token));
        Assert.AreEqual(0, queue.Count);
        queue.TrySend(4);
        Assert.AreEqual(MessageQueueResult.Cancelled, queue.Receive(out _, 0, cancelled.Token));
        Assert.AreEqual(1, queue.Count);
        Assert.AreEqual(MessageQueueResult.Success, queue.Receive(out int value, 0));
        Assert.AreEqual(4, value);
    }
    [TestMethod]
    public void SendReceive_ConcurrentWorkers_NoLossOrDuplicates()
    {
        const int perProducer = 300;
        var queue = new MessageQueue<int>(3);
        var seen = new int[perProducer * 2];
        int failures = 0;
        Thread Consumer() => new(() =>
        {
            while (true)
            {
                var result = queue.Receive(out int value, 5000);
                if (result == MessageQueueResult.Closed) return;
                if (result != MessageQueueResult.Success) { Interlocked.Increment(ref failures); return; }
                Interlocked.Increment(ref seen[value]);
            }
        });
        Thread Producer(int offset) => new(() =>
        {
            for (int i = 0; i < perProducer; i++)
                if (queue.Send(offset + i, 5000) != MessageQueueResult.Success) Interlocked.Increment(ref failures);
        });
        Thread[] consumers = [Consumer(), Consumer()];
        Thread[] producers = [Producer(0), Producer(perProducer)];
        foreach (var thread in consumers) thread.Start();
        foreach (var thread in producers) thread.Start();
        foreach (var thread in producers) Assert.IsTrue(thread.Join(10000));
        queue.Close();
        foreach (var thread in consumers) Assert.IsTrue(thread.Join(10000));
        Assert.AreEqual(0, failures);
        foreach (int count in seen) Assert.AreEqual(1, count);
    }
}
