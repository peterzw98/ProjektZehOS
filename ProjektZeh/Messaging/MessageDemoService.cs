using System;
using System.Threading;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Messaging;

internal sealed class MessageDemoService
{
    private readonly BackgroundTaskService tasks;
    private MessageQueue<int>? queue;
    private OrderedTransfer accounts = new();
    private int producerId;
    private int consumerId;
    private int active;
    private int produced;
    private int consumed;
    private int emptyWaits;
    private int fullWaits;
    private int invalid;

    public MessageDemoService(BackgroundTaskService tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        this.tasks = tasks;
    }

    public ResultErr Start(bool slowProducer)
    {
        if (Interlocked.CompareExchange(ref active, 2, 0) != 0)
            return ResultErr.CreateFailure(ResultCode.Failure, "Die Nachrichtenvorfuehrung laeuft bereits.");
        var buffer = new MessageQueue<int>(3);
        queue = buffer;
        accounts = new OrderedTransfer();
        produced = consumed = emptyWaits = fullWaits = invalid = 0;
        producerId = consumerId = 0;
        var producer = tasks.Start("messages-producer", context =>
        {
            try
            {
                for (int i = 1; i <= 24; i++)
                {
                    if (context.Token.WaitHandle.WaitOne(slowProducer ? 200 : 1)) break;
                    var result = buffer.TrySend(i);
                    if (result == MessageQueueResult.Full)
                    {
                        Interlocked.Increment(ref fullWaits);
                        result = buffer.Send(i, Timeout.Infinite, context.Token);
                    }
                    if (result != MessageQueueResult.Success) break;
                    accounts.Transfer(true);
                    context.Report(Interlocked.Increment(ref produced));
                }
            }
            catch
            {
                buffer.Close();
                Interlocked.Decrement(ref active);
                throw;
            }
            // Cosmos 3.0.94 überspringt verschachteltes finally bei OCE; erwarteter Abbruch bleibt ein normaler Rückweg.
            buffer.Close();
            Interlocked.Decrement(ref active);
        });
        if (!producer.IsOK())
        {
            buffer.Close();
            Volatile.Write(ref active, 0);
            return producer.Status;
        }
        producerId = producer.Value;
        var consumer = tasks.Start("messages-consumer", context =>
        {
            try
            {
                int expected = 1;
                while (true)
                {
                    var result = buffer.TryReceive(out int value);
                    if (result == MessageQueueResult.Empty)
                    {
                        Interlocked.Increment(ref emptyWaits);
                        result = buffer.Receive(out value, Timeout.Infinite, context.Token);
                    }
                    if (result != MessageQueueResult.Success) break;
                    if (value != expected++) Interlocked.Increment(ref invalid);
                    accounts.Transfer(false);
                    context.Report(Interlocked.Increment(ref consumed));
                    if (context.Token.WaitHandle.WaitOne(slowProducer ? 1 : 200)) break;
                }
            }
            catch { Interlocked.Decrement(ref active); throw; }
            Interlocked.Decrement(ref active);
        });
        if (!consumer.IsOK())
        {
            Interlocked.Decrement(ref active);
            buffer.Close();
            tasks.Cancel(producerId);
            return consumer.Status;
        }
        consumerId = consumer.Value;
        return ResultErr.CreateSuccess();
    }

    public void Close() => queue?.Close();

    public void Stop()
    {
        Close();
        if (producerId != 0) tasks.Cancel(producerId);
        if (consumerId != 0) tasks.Cancel(consumerId);
    }

    public string Status() => $"messages: active={Volatile.Read(ref active)} produced={Volatile.Read(ref produced)} " +
        $"consumed={Volatile.Read(ref consumed)} empty-waits={Volatile.Read(ref emptyWaits)} full-waits={Volatile.Read(ref fullWaits)} " +
        $"order-errors={Volatile.Read(ref invalid)} buffered={queue?.Count ?? 0}/3 lock-total={accounts.Total}";
}
