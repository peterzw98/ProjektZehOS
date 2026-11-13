using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Messaging;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MessageLockOrderTests
{
    [TestMethod]
    public void Transfer_OppositeDirections_CompleteWithoutDeadlockAndPreserveTotal()
    {
        var accounts = new OrderedTransfer();
        var first = new Thread(() => { for (int i = 0; i < 10000; i++) accounts.Transfer(true); });
        var second = new Thread(() => { for (int i = 0; i < 10000; i++) accounts.Transfer(false); });
        first.Start();
        second.Start();
        Assert.IsTrue(first.Join(5000));
        Assert.IsTrue(second.Join(5000));
        Assert.AreEqual(2000, accounts.Total);
    }
}
