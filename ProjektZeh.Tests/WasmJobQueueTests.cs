using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

/// <summary>
/// Warteschlange des Wasm-Workers (12.4-I1): Ohne Worker (Ruhestand nach CPU-Fehlern oder gescheiterter Ersatzstart) muss
/// jeder wartende Lauf sein Ende melden koennen, sonst bliebe sein Fenster auf "beschaeftigt" (Review senior, P3).
/// </summary>
[TestClass]
public sealed class WasmJobQueueTests
{
    [TestMethod]
    public void Dequeue_KeepsFifoOrder()
    {
        var queue = new WasmJobQueue();
        var order = new List<int>();
        queue.Enqueue(() => order.Add(1), null);
        queue.Enqueue(() => order.Add(2), (_, _, _) => { });
        queue.Dequeue()();
        queue.Dequeue()();
        CollectionAssert.AreEqual(new[] { 1, 2 }, order);
        Assert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public void Drain_ReturnsEveryWaitingCompletionInOrderWithoutRunningJobs()
    {
        var queue = new WasmJobQueue();
        bool ran = false;
        var ended = new List<(string Window, WasmStatus Status, string Message)>();
        queue.Enqueue(() => ran = true, (status, _, message) => ended.Add(("erste", status, message)));
        queue.Enqueue(() => ran = true, null); // Diagnosejob ohne Fenster
        queue.Enqueue(() => ran = true, (status, _, message) => ended.Add(("zweite", status, message)));

        var dropped = queue.Drain();
        Assert.AreEqual(0, queue.Count, "Warteschlange leer");
        Assert.AreEqual(2, dropped.Count, "nur Laeufe mit Fenster melden ein Ende");
        foreach (var completed in dropped) completed(WasmStatus.CpuFault, 0, "Ruhestand");

        Assert.IsFalse(ran, "verworfene Laeufe starten nie");
        Assert.AreEqual(2, ended.Count);
        Assert.AreEqual("erste", ended[0].Window);
        Assert.AreEqual(WasmStatus.CpuFault, ended[1].Status);
        Assert.AreEqual("Ruhestand", ended[1].Message);
    }

    [TestMethod]
    public void Enqueue_RejectsMissingJob() =>
        Assert.ThrowsException<ArgumentNullException>(() => new WasmJobQueue().Enqueue(null!, null));
}
