using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class ClassRunQueuesTests
{
    private const ulong Tick = ClassRunQueues<FakeThread>.TickNs;

    private sealed class FakeThread(string name)
    {
        public ClassThreadState State { get; } = new();
        public override string ToString() => name;
    }

    private static ClassRunQueues<FakeThread> Queues(int capacity = 8) => new(capacity, t => t.State);

    private static FakeThread Make(string name, ThreadClass threadClass = ThreadClass.Normal, ulong remaining = 0)
    {
        var thread = new FakeThread(name);
        thread.State.Class = threadClass;
        thread.State.RemainingNs = remaining;
        return thread;
    }

    [TestMethod]
    public void Ready_SameThreadTwice_QueuedOnce()
    {
        var queues = Queues();
        var a = Make("a");
        queues.Ready(a);
        queues.Ready(a);
        Assert.AreEqual(1, queues.Count);
    }

    [TestMethod]
    public void Ready_FullClass_ReturnsFalseAndKeepsOrder()
    {
        var queues = Queues(2);
        FakeThread a = Make("a"), b = Make("b"), c = Make("c");
        Assert.IsTrue(queues.Ready(a));
        Assert.IsTrue(queues.Ready(b));
        Assert.IsFalse(queues.Ready(c));
        Assert.AreSame(a, queues.Pick(null));
        Assert.AreSame(b, queues.Pick(null));
    }

    [TestMethod]
    public void Pick_PrefersHigherClass()
    {
        var queues = Queues();
        FakeThread n = Make("n"), b = Make("b", ThreadClass.Background), i = Make("i", ThreadClass.Interactive);
        queues.Ready(n);
        queues.Ready(b);
        queues.Ready(i);
        Assert.AreSame(i, queues.Pick(null));
        Assert.AreSame(n, queues.Pick(null));
        Assert.AreSame(b, queues.Pick(null));
        Assert.IsNull(queues.Pick(null));
    }

    [TestMethod]
    public void Pick_SetsSliceForClass()
    {
        var queues = Queues();
        FakeThread i = Make("i", ThreadClass.Interactive), b = Make("b", ThreadClass.Background);
        queues.Ready(i);
        queues.Ready(b);
        queues.Pick(null);
        Assert.AreEqual(Tick, i.State.RemainingNs);
        queues.Pick(null);
        Assert.AreEqual(4 * Tick, b.State.RemainingNs);
    }

    [TestMethod]
    public void Pick_LowerClassWaiting_KeepsRunning()
    {
        var queues = Queues();
        FakeThread n = Make("n", remaining: 5_000_000), b = Make("b", ThreadClass.Background);
        queues.Ready(b);
        Assert.AreSame(n, queues.Pick(n));
        Assert.AreEqual(1, queues.Count);
    }

    [TestMethod]
    public void Pick_SameClassWaiting_Switches()
    {
        var queues = Queues();
        FakeThread n1 = Make("n1", remaining: 5_000_000), n2 = Make("n2");
        queues.Ready(n2);
        Assert.AreSame(n2, queues.Pick(n1));
    }

    [TestMethod]
    public void Yielded_PreemptedByHigherClass_ReturnsToFrontWithRemainder()
    {
        var queues = Queues();
        FakeThread n1 = Make("n1", remaining: 5_000_000), n2 = Make("n2"), i = Make("i", ThreadClass.Interactive);
        queues.Ready(n2);
        queues.Ready(i);
        Assert.AreSame(i, queues.Pick(n1));
        queues.Yielded(n1);
        Assert.AreSame(n1, queues.Pick(null));
        Assert.AreEqual(5_000_000UL, n1.State.RemainingNs);
        Assert.AreSame(n2, queues.Pick(null));
    }

    [TestMethod]
    public void Yielded_AfterSliceEnd_GoesToBackWithFreshSlice()
    {
        var queues = Queues();
        FakeThread n1 = Make("n1", remaining: 2 * Tick), n2 = Make("n2"), n3 = Make("n3");
        queues.Ready(n2);
        queues.Ready(n3);
        Assert.IsFalse(queues.Tick(n1, Tick));
        Assert.IsTrue(queues.Tick(n1, Tick));
        Assert.AreSame(n2, queues.Pick(n1));
        queues.Yielded(n1);
        Assert.AreSame(n3, queues.At(0));
        Assert.AreSame(n1, queues.At(1));
        Assert.AreEqual(0UL, n1.State.RemainingNs);
    }

    [TestMethod]
    public void Pick_ThreadWokenWhileRunning_DoesNotRefillSlice()
    {
        var queues = Queues();
        var n1 = Make("n1", remaining: Tick);
        queues.Ready(n1);
        Assert.AreSame(n1, queues.Pick(null));
        Assert.AreEqual(Tick, n1.State.RemainingNs);
        var n2 = Make("n2");
        queues.Ready(n2);
        Assert.IsTrue(queues.Tick(n1, Tick));
    }

    [TestMethod]
    public void Tick_SliceNotExpired_ReturnsFalse()
    {
        var queues = Queues();
        var n = Make("n", remaining: 2 * Tick);
        Assert.IsFalse(queues.Tick(n, Tick));
        Assert.AreEqual(Tick, n.State.RemainingNs);
    }

    [TestMethod]
    public void Tick_AloneAfterSlice_RefillsAndKeepsCpu()
    {
        var queues = Queues();
        var n = Make("n", remaining: Tick);
        Assert.IsFalse(queues.Tick(n, Tick));
        Assert.IsFalse(queues.Tick(n, 2 * Tick));
        Assert.AreEqual(2 * Tick, n.State.RemainingNs);
    }

    [TestMethod]
    public void Tick_OnlyLowerClassWaiting_KeepsCpuUntilStarvationLimit()
    {
        var queues = Queues();
        FakeThread n = Make("n", remaining: Tick), b = Make("b", ThreadClass.Background);
        queues.Ready(b);
        for (int slice = 1; slice < ClassRunQueues<FakeThread>.StarvationLimit; slice++)
        {
            Assert.IsFalse(queues.Tick(n, n.State.RemainingNs), $"Scheibe {slice}");
        }
        Assert.IsTrue(queues.Tick(n, n.State.RemainingNs));
        Assert.AreSame(b, queues.Pick(n));
        queues.Yielded(n);
        // Die Zählung beginnt neu: b läuft, n wartet, danach hungert nichts.
        Assert.AreSame(n, queues.Pick(b));
    }

    [TestMethod]
    public void Starvation_NormalBeforeBackground()
    {
        var queues = Queues();
        FakeThread i = Make("i", ThreadClass.Interactive, Tick), n = Make("n"), b = Make("b", ThreadClass.Background);
        queues.Ready(n);
        queues.Ready(b);
        for (int slice = 1; slice < ClassRunQueues<FakeThread>.StarvationLimit; slice++) Assert.IsFalse(queues.Tick(i, i.State.RemainingNs));
        Assert.IsTrue(queues.Tick(i, i.State.RemainingNs));
        Assert.AreSame(n, queues.Pick(i));
        queues.Yielded(i);
        n.State.RemainingNs = 0;
        Assert.IsTrue(queues.Tick(n, Tick));
        Assert.AreSame(b, queues.Pick(n));
        queues.Yielded(n);
        b.State.RemainingNs = 0;
        Assert.IsTrue(queues.Tick(b, Tick));
        Assert.AreSame(i, queues.Pick(b));
    }

    [TestMethod]
    public void Tick_CurrentAlsoQueued_NotCountedAsWaiterAndMovedToBack()
    {
        var queues = Queues();
        FakeThread n1 = Make("n1", remaining: Tick), n2 = Make("n2");
        queues.Ready(n1);
        queues.Ready(n2);
        Assert.IsTrue(queues.Tick(n1, Tick));
        Assert.AreSame(n2, queues.At(0));
        Assert.AreSame(n1, queues.At(1));
        // Allein in der eigenen Queue zu stehen ist kein Grund zum Wechsel.
        var alone = Queues();
        var n3 = Make("n3", remaining: Tick);
        alone.Ready(n3);
        Assert.IsFalse(alone.Tick(n3, Tick));
    }

    [TestMethod]
    public void AlwaysYields_PreemptedAtFirstTickWhenAnyoneWaits()
    {
        var queues = Queues();
        var main = Make("main", remaining: 2 * Tick);
        main.State.AlwaysYields = true;
        Assert.IsFalse(queues.Tick(main, Tick));
        queues.Ready(Make("n"));
        Assert.IsTrue(queues.Tick(main, Tick));
    }

    [TestMethod]
    public void AlwaysYields_NeverKeptByPick()
    {
        var queues = Queues();
        var main = Make("main", remaining: 2 * Tick);
        main.State.AlwaysYields = true;
        var b = Make("b", ThreadClass.Background);
        queues.Ready(b);
        Assert.AreSame(b, queues.Pick(main));
    }

    [TestMethod]
    public void MainLoopAndBusyWorker_KeepTheShareOfTheFormerScheduler()
    {
        var queues = Queues();
        var main = Make("main");
        main.State.AlwaysYields = true;
        var worker = Make("worker");
        queues.Ready(main);
        queues.Ready(worker);
        var current = queues.Pick(null)!;
        int mainTicks = 0;
        for (int tick = 0; tick < 300; tick++)
        {
            if (ReferenceEquals(current, main)) mainTicks++;
            if (!queues.Tick(current, Tick)) continue;
            var next = queues.Pick(current)!;
            if (!ReferenceEquals(next, current)) queues.Yielded(current);
            current = next;
        }
        Assert.AreEqual(100, mainTicks);
    }

    [TestMethod]
    public void SetClass_QueuedThread_MovesBetweenQueues()
    {
        var queues = Queues();
        var n = Make("n");
        queues.Ready(n);
        Assert.IsTrue(queues.SetClass(n, ThreadClass.Background));
        Assert.AreEqual(0, queues.CountOf(ThreadClass.Normal));
        Assert.AreEqual(1, queues.CountOf(ThreadClass.Background));
    }

    [TestMethod]
    public void SetClass_TargetFull_KeepsThreadWhereItWas()
    {
        var queues = Queues(1);
        FakeThread n = Make("n"), b = Make("b", ThreadClass.Background);
        queues.Ready(n);
        queues.Ready(b);
        Assert.IsFalse(queues.SetClass(n, ThreadClass.Background));
        Assert.AreEqual(ThreadClass.Normal, n.State.Class);
        Assert.AreEqual(1, queues.CountOf(ThreadClass.Normal));
    }

    [TestMethod]
    public void Blocked_RemovesFromAnyClass()
    {
        var queues = Queues();
        FakeThread i = Make("i", ThreadClass.Interactive), n = Make("n"), b = Make("b", ThreadClass.Background);
        queues.Ready(i);
        queues.Ready(n);
        queues.Ready(b);
        queues.Blocked(n);
        Assert.AreEqual(2, queues.Count);
        Assert.AreSame(i, queues.Pick(null));
        Assert.AreSame(b, queues.Pick(null));
    }

    [TestMethod]
    public void PickFailed_ReturnsToFront()
    {
        var queues = Queues();
        FakeThread n1 = Make("n1"), n2 = Make("n2");
        queues.Ready(n1);
        queues.Ready(n2);
        Assert.AreSame(n1, queues.Pick(null));
        queues.PickFailed(n1);
        Assert.AreSame(n1, queues.Pick(null));
    }

    [TestMethod]
    public void PeekStealable_SkipsThreadsThatMayNotMove()
    {
        var queues = Queues();
        FakeThread i = Make("i", ThreadClass.Interactive), n = Make("n");
        queues.Ready(i);
        queues.Ready(n);
        Assert.AreSame(n, queues.PeekStealable(t => !ReferenceEquals(t, i)));
        Assert.AreEqual(2, queues.Count);
    }

    [TestMethod]
    public void ThreadWithoutState_IsNormalWithoutSlice()
    {
        var queues = new ClassRunQueues<object>(4, _ => null);
        var a = new object();
        Assert.IsTrue(queues.Ready(a));
        Assert.AreEqual(1, queues.CountOf(ThreadClass.Normal));
        Assert.AreSame(a, queues.Pick(null));
        Assert.IsFalse(queues.Tick(a, Tick));
    }

    [TestMethod]
    public void Waiting_PublishedAfterEveryQueueChange()
    {
        var queues = Queues();
        FakeThread a = Make("a"), b = Make("b");
        queues.Ready(a);
        queues.Ready(b);
        Assert.AreEqual(2, queues.Waiting);
        Assert.AreSame(a, queues.Pick(null));
        Assert.AreEqual(1, queues.Waiting);
        queues.Yielded(a);
        Assert.AreEqual(2, queues.Waiting);
        queues.Blocked(b);
        Assert.AreEqual(1, queues.Waiting);
        Assert.IsTrue(queues.SetClass(a, ThreadClass.Background));
        Assert.AreEqual(1, queues.Waiting);
        Assert.AreSame(a, queues.Pick(null));
        queues.PickFailed(a);
        Assert.AreEqual(1, queues.Waiting);
        // Tick mit Umsetzen ans Ende: die Zahl bleibt gleich.
        var c = Make("c", remaining: Tick);
        queues.Ready(c);
        queues.Ready(Make("d"));
        int before = queues.Waiting;
        Assert.IsTrue(queues.Tick(c, Tick));
        Assert.AreEqual(before, queues.Waiting);
        Assert.AreEqual(queues.Count, queues.Waiting);
    }

    [TestMethod]
    public void Starvation_CounterResetsWhenClassBecomesEmpty()
    {
        var queues = Queues();
        FakeThread n = Make("n", remaining: Tick), b1 = Make("b1", ThreadClass.Background), b2 = Make("b2", ThreadClass.Background);
        queues.Ready(b1);
        for (int slice = 1; slice < ClassRunQueues<FakeThread>.StarvationLimit; slice++) Assert.IsFalse(queues.Tick(n, n.State.RemainingNs));
        queues.Blocked(b1);
        queues.Ready(b2);
        // b2 ist neu bereit und erbt die sieben Scheiben von b1 nicht.
        Assert.IsFalse(queues.Tick(n, n.State.RemainingNs));
    }
}
