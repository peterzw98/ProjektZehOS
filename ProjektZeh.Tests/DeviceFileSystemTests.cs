using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Virtual;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DeviceFileSystemTests
{
    private static DeviceHandle Open(string name)
    {
        var result = DeviceFileSystem.Open(name);
        Assert.IsTrue(result.IsOK());
        return result.Value!;
    }

    [TestMethod]
    public void NamesAreNullAndZeroOnlyAndUnknownIsNotFound()
    {
        CollectionAssert.AreEqual(new[] { "null", "zero" }, new List<string>(DeviceFileSystem.Names));
        foreach (string name in new[] { "tty", "random", "", "NULL", "null/", "sda" })
        {
            var result = DeviceFileSystem.Open(name);
            Assert.IsFalse(result.IsOK(), name);
            Assert.IsNull(result.Value);
        }
    }

    [TestMethod]
    public void NullReadsEofAndAcceptsEveryWrite()
    {
        var handle = Open("null");
        var buffer = new byte[16];
        Array.Fill(buffer, (byte)7);
        Assert.AreEqual(0, handle.Read(buffer).Value);
        CollectionAssert.AreEqual(Enumerable16(7), buffer, "null darf den Puffer nicht veraendern");
        Assert.AreEqual(0, handle.Read(Span<byte>.Empty).Value);
        Assert.AreEqual(1000, handle.Write(new byte[1000]).Value);
        Assert.AreEqual(0, handle.Write(ReadOnlySpan<byte>.Empty).Value);
    }

    [TestMethod]
    public void ZeroFillsExactlyTheRequestedBuffer()
    {
        var handle = Open("zero");
        foreach (int length in new[] { 0, 1, 4095, 4096, 65535, 65536 })
        {
            var buffer = new byte[length + 2];
            Array.Fill(buffer, (byte)0xAA);
            Assert.AreEqual(length, handle.Read(buffer.AsSpan(1, length)).Value);
            Assert.AreEqual(0xAA, buffer[0], "Byte vor dem Fenster");
            Assert.AreEqual(0xAA, buffer[length + 1], "Byte nach dem Fenster");
            for (int index = 1; index <= length; index++)
                Assert.AreEqual(0, buffer[index]);
        }

        Assert.AreEqual(5, handle.Write(new byte[5]).Value);
    }

    [TestMethod]
    public void ZeroReadIsCappedAndTheRestIsAShortRead()
    {
        var handle = Open("zero");
        var buffer = new byte[DeviceHandle.MaxReadBytes + 4096];
        Array.Fill(buffer, (byte)0xAA);
        Assert.AreEqual(DeviceHandle.MaxReadBytes, handle.Read(buffer).Value);
        Assert.AreEqual(0, buffer[DeviceHandle.MaxReadBytes - 1]);
        Assert.AreEqual(0xAA, buffer[DeviceHandle.MaxReadBytes], "hinter der Grenze bleibt der Puffer unberuehrt");
        Assert.AreEqual(0xAA, buffer[^1]);
        // Ein int.MaxValue-Fenster wuerde ohne Grenze 2 GiB loeschen; mit der Grenze bleibt der Aufruf klein.
        Assert.AreEqual(DeviceHandle.MaxReadBytes, handle.Read(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
            ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(buffer), buffer.Length)).Value);
    }

    [TestMethod]
    public void ClosedHandleFailsAndDoesNotAffectOthers()
    {
        var first = Open("zero");
        var second = Open("zero");
        first.Close();
        first.Close();
        Assert.IsTrue(first.IsClosed);
        Assert.IsFalse(first.Read(new byte[4]).IsOK());
        Assert.IsFalse(first.Write(new byte[4]).IsOK());
        Assert.IsFalse(second.IsClosed);
        Assert.AreEqual(4, second.Read(new byte[4]).Value);
        Assert.AreNotSame(first, second);
    }

    [TestMethod]
    public void ParallelHandlesShareNoState()
    {
        var a = Open("null");
        var b = Open("zero");
        var c = Open("zero");
        var bufferB = new byte[8];
        Array.Fill(bufferB, (byte)9);
        b.Close();
        Assert.AreEqual(0, a.Read(new byte[8]).Value);
        Assert.AreEqual(8, c.Read(bufferB).Value);
        CollectionAssert.AreEqual(new byte[8], bufferB);
        Assert.IsFalse(b.Read(bufferB).IsOK());
    }

    private static byte[] Enumerable16(byte value)
    {
        var data = new byte[16];
        Array.Fill(data, value);
        return data;
    }
}
