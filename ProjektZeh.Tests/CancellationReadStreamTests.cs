using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class CancellationReadStreamTests
{
    [TestMethod]
    public void HashingChecksCancellationBetweenReadBlocks()
    {
        using var cancellation = new CancellationTokenSource();
        var inner = new CancelAfterRead(cancellation);
        using var guarded = new CancellationReadStream(inner, cancellation.Token);
        Assert.ThrowsException<OperationCanceledException>(() => SHA256.HashData(guarded));
        Assert.AreEqual(1, inner.Reads);
    }

    [TestMethod]
    public void CancellationBeforeReadingDoesNotTouchTheSourceAndDisposalClosesIt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var inner = new CancelAfterRead(cancellation);
        using (var guarded = new CancellationReadStream(inner, cancellation.Token))
            Assert.ThrowsException<OperationCanceledException>(() => guarded.Read(new byte[1], 0, 1));
        Assert.AreEqual(0, inner.Reads);
        Assert.IsFalse(inner.CanRead);
    }

    private sealed class CancelAfterRead(CancellationTokenSource cancellation) : MemoryStream(new byte[256 * 1024])
    {
        public int Reads { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            Reads++;
            cancellation.Cancel();
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            // MemoryStream.Read(Span) ruft bei abgeleiteten Typen selbst Read(byte[]) auf; sonst zaehlt der Fake doppelt.
            byte[] copy = new byte[buffer.Length];
            int read = Read(copy, 0, copy.Length);
            copy.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
    }
}
