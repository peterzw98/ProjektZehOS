using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

/// <summary>Verhalten der Speicher-Sperre (Diagnoselauf 13/14): Ausschluss, Reentranz, Deadlock-Waechter, Diagnose.</summary>
[TestClass]
[DoNotParallelize]
public sealed class StorageLockTests
{
    [TestCleanup]
    public void Reset()
    {
        StorageLock.Report = null;
        StorageLock.SlowThreshold = TimeSpan.FromSeconds(2);
    }

    [TestMethod]
    public void Run_IsReentrant()
    {
        int value = StorageLock.Run(() => StorageLock.Run(() => StorageLock.IsHeldByCurrentThread ? 7 : 0));
        Assert.AreEqual(7, value);
        Assert.IsFalse(StorageLock.IsHeldByCurrentThread);
    }

    [TestMethod]
    public void Run_ExcludesOtherThreads()
    {
        int inside = 0, overlaps = 0;
        var threads = new List<Thread>();
        for (int index = 0; index < 4; index++)
        {
            threads.Add(new Thread(() =>
            {
                for (int round = 0; round < 200; round++)
                    StorageLock.Run(() =>
                    {
                        if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlaps);
                        Thread.SpinWait(50);
                        Interlocked.Decrement(ref inside);
                    });
            }));
        }

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
        Assert.AreEqual(0, overlaps);
    }

    [TestMethod]
    public void Run_ReleasesOnException()
    {
        Assert.ThrowsException<IOException>(() => StorageLock.Run(() => throw new IOException("x")));
        Assert.IsFalse(StorageLock.IsHeldByCurrentThread);
        var other = new Thread(() => StorageLock.Run(() => { }));
        other.Start();
        Assert.IsTrue(other.Join(5000), "Sperre nach Ausnahme nicht freigegeben");
    }

    [TestMethod]
    public void EnsureNotHeld_ThrowsOnlyUnderLock()
    {
        StorageLock.EnsureNotHeld("ausserhalb");
        var exception = Assert.ThrowsException<InvalidOperationException>(() => StorageLock.Run(() => StorageLock.EnsureNotHeld("Block")));
        StringAssert.Contains(exception.Message, "Block");
    }

    [TestMethod]
    public void Report_NamesSlowHolder()
    {
        var reports = new List<string>();
        StorageLock.Report = reports.Add;
        StorageLock.SlowThreshold = TimeSpan.FromMilliseconds(20);
        StorageLock.Run(() => Thread.Sleep(60));
        StorageLock.Run(() => { });
        Assert.AreEqual(1, reports.Count);
        StringAssert.Contains(reports[0], "StorageLockTests.Report_NamesSlowHolder");
    }

    [TestMethod]
    public void ReadExactly_ReadsInChunksUnderLock()
    {
        byte[] source = new byte[StorageLock.ChunkBytes * 2 + 17];
        new Random(5).NextBytes(source);
        var stream = new LockCheckingStream(source);
        byte[] target = new byte[source.Length];
        StorageLock.ReadExactly(stream, target);
        CollectionAssert.AreEqual(source, target);
        Assert.IsTrue(stream.Reads >= 3);
        Assert.AreEqual(0, stream.UnlockedReads);
        Assert.IsTrue(stream.LargestRead <= StorageLock.ChunkBytes);
    }

    private sealed class LockCheckingStream(byte[] data) : MemoryStream(data)
    {
        public int Reads { get; private set; }
        public int UnlockedReads { get; private set; }
        public int LargestRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            if (!StorageLock.IsHeldByCurrentThread) UnlockedReads++;
            LargestRead = Math.Max(LargestRead, count);
            return base.Read(buffer, offset, count);
        }
    }
}
