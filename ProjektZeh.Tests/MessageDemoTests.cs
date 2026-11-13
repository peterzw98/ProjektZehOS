using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Messaging;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MessageDemoTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Start_SlowProducerOrConsumer_ProcessesEveryNumberAndExposesWaits(bool slowProducer)
    {
        var tasks = new BackgroundTaskService();
        var demo = new MessageDemoService(tasks);
        try
        {
            Assert.IsTrue(demo.Start(slowProducer).IsOK());
            Assert.IsFalse(demo.Start(slowProducer).IsOK());
            Assert.IsTrue(SpinWait.SpinUntil(() => demo.Status().Contains("active=0", StringComparison.Ordinal), 5000));
            string state = demo.Status();
            StringAssert.Contains(state, "produced=24 consumed=24");
            StringAssert.Contains(state, "order-errors=0 buffered=0/3 lock-total=2000");
            string key = slowProducer ? "empty-waits=0" : "full-waits=0";
            Assert.IsFalse(state.Contains(key, StringComparison.Ordinal), state);
        }
        finally { demo.Stop(); Assert.IsTrue(tasks.StopAll(2000)); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CloseOrStop_RunningDemo_TerminatesBothTasks(bool stop)
    {
        var tasks = new BackgroundTaskService();
        var demo = new MessageDemoService(tasks);
        try
        {
            Assert.IsTrue(demo.Start(true).IsOK());
            if (stop) demo.Stop(); else demo.Close();
            Assert.IsTrue(SpinWait.SpinUntil(() => demo.Status().Contains("active=0", StringComparison.Ordinal), 2000));
        }
        finally { demo.Stop(); Assert.IsTrue(tasks.StopAll(2000)); }
    }
}
