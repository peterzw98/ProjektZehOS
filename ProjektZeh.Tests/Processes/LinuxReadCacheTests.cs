using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Tests.Processes;

/// <summary>V40: Lese-Cache für Dateiseiten (Blöcke, Generation, Halt am Knoten).</summary>
[TestClass]
public sealed class LinuxReadCacheTests
{
    private const int ORdWr = 2, OCreat = 0x40, OTrunc = 0x200;
    private const int Block = LinuxReadCache.BlockBytes;

    [TestMethod]
    public void ReadsABlockOnceAndServesRepeatsFromCache()
    {
        var file = new CountingFile(Pattern(3 * Block + 100));
        var cache = new LinuxReadCache(1 << 20);
        var node = new LinuxFileNode();
        byte[] page = new byte[4096];

        Assert.AreEqual(4096, cache.Read(file, node, 4096, page));
        CollectionAssert.AreEqual(Pattern(3 * Block + 100).AsSpan(4096, 4096).ToArray(), page);
        int readsAfterFirst = file.Reads;
        Array.Clear(page);
        Assert.AreEqual(4096, cache.Read(file, node, 4096, page));
        CollectionAssert.AreEqual(Pattern(3 * Block + 100).AsSpan(4096, 4096).ToArray(), page);
        Assert.AreEqual(readsAfterFirst, file.Reads, "Wiederholung aus dem Cache");
        Assert.AreEqual(1, cache.Misses);
        Assert.AreEqual(1, cache.Hits);
    }

    [TestMethod]
    public void CrossesBlocksAndStopsAtEndOfFile()
    {
        byte[] content = Pattern(Block + 100);
        var cache = new LinuxReadCache(1 << 20);
        var file = new CountingFile(content);
        var node = new LinuxFileNode();
        byte[] dst = new byte[100];

        Assert.AreEqual(100, cache.Read(file, node, Block - 50, dst));
        CollectionAssert.AreEqual(content.AsSpan(Block - 50, 100).ToArray(), dst);
        Assert.AreEqual(10, cache.Read(file, node, Block + 90, dst));
        Assert.AreEqual(0, cache.Read(file, node, Block + 100, dst));
        Assert.AreEqual(0, cache.Read(file, node, 5 * Block, dst));
    }

    [TestMethod]
    public void ReadErrorIsReportedAndNotCached()
    {
        var file = new CountingFile(Pattern(Block)) { Fail = true };
        var cache = new LinuxReadCache(1 << 20);
        var node = new LinuxFileNode();
        byte[] dst = new byte[4096];

        Assert.AreEqual(-LinuxErrno.EIO, cache.Read(file, node, 0, dst));
        file.Fail = false;
        Assert.AreEqual(4096, cache.Read(file, node, 0, dst));
        Assert.AreEqual(0, cache.Hits);
    }

    [TestMethod]
    public void NewGenerationMissesOldBlocks()
    {
        byte[] content = Pattern(Block);
        var file = new CountingFile(content);
        var cache = new LinuxReadCache(1 << 20);
        var node = new LinuxFileNode();
        byte[] dst = new byte[4];

        _ = cache.Read(file, node, 0, dst);
        content[0] = 0xEE;
        node.Generation++;
        _ = cache.Read(file, node, 0, dst);
        Assert.AreEqual(0xEE, dst[0]);
    }

    [TestMethod]
    public void OldestBlocksLeaveAtTheLimit()
    {
        var file = new CountingFile(Pattern(4 * Block));
        var cache = new LinuxReadCache(2 * Block);
        var node = new LinuxFileNode();
        byte[] dst = new byte[1];

        for (int b = 0; b < 4; b++)
        {
            _ = cache.Read(file, node, (long)b * Block, dst);
        }

        Assert.AreEqual(2L * Block, cache.Bytes);
        _ = cache.Read(file, node, 3L * Block, dst); // jüngster Block bleibt
        Assert.AreEqual(1, cache.Hits);
        _ = cache.Read(file, node, 0, dst); // ältester ist weg
        Assert.AreEqual(5, cache.Misses);
    }

    [TestMethod]
    public void PageSourcesOfTwoOpensShareTheCache()
    {
        LinuxFileSystem fs = NewFs(Pattern(2 * Block));
        ILinuxPageSource a = Source(fs, "/f", 0, out ILinuxFile fa);
        ILinuxPageSource b = Source(fs, "/f", 0, out ILinuxFile fb);
        byte[] dst = new byte[4096];

        Assert.AreEqual(4096, a.ReadAt(4096, dst));
        Assert.AreEqual(4096, b.ReadAt(4096, dst));
        Assert.AreEqual(1, fs.ReadCache!.Misses);
        Assert.AreEqual(1, fs.ReadCache.Hits);
        Close(a, fa);
        Close(b, fb);
    }

    [TestMethod]
    public void WriteThroughAnotherDescriptorReachesMappedSource()
    {
        LinuxFileSystem fs = NewFs(Pattern(Block));
        ILinuxPageSource source = Source(fs, "/f", 0, out ILinuxFile reader);
        byte[] dst = new byte[4];
        _ = source.ReadAt(0, dst);

        // Wie mmap und close: Die Quelle lebt ohne Deskriptor weiter, der Knoten muss im Register bleiben.
        Assert.AreEqual(0, reader.Close());
        Assert.AreEqual(0, fs.Open("/f", ORdWr, 0, out ILinuxFile? writer));
        Assert.AreEqual(4, writer!.Write(new byte[] { 9, 8, 7, 6 }));

        Assert.AreEqual(4, source.ReadAt(0, dst));
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, dst);
        Assert.AreEqual(0, writer.Close());
        source.Release();
    }

    [TestMethod]
    public void TruncateOnOpenAndFtruncateReachMappedSource()
    {
        LinuxFileSystem fs = NewFs(Pattern(Block));
        ILinuxPageSource source = Source(fs, "/f", 0, out ILinuxFile reader);
        byte[] dst = new byte[4];
        Assert.AreEqual(4, source.ReadAt(100, dst));

        Assert.AreEqual(0, fs.Open("/f", ORdWr | OTrunc, 0, out ILinuxFile? trunc));
        Assert.AreEqual(0, source.ReadAt(100, dst), "O_TRUNC: Datei leer");
        Assert.AreEqual(0, trunc!.Truncate(200));
        Assert.AreEqual(4, source.ReadAt(100, dst));
        CollectionAssert.AreEqual(new byte[4], dst, "ftruncate: Nullen");
        Close(source, reader);
        Assert.AreEqual(0, trunc.Close());
    }

    [TestMethod]
    public void RecreatedFileStartsANewGeneration()
    {
        LinuxFileSystem fs = NewFs(Pattern(Block));
        ILinuxPageSource source = Source(fs, "/f", 0, out ILinuxFile reader);
        byte[] dst = new byte[4];
        _ = source.ReadAt(0, dst);
        Assert.AreEqual(0, fs.Unlink("/f"));
        Assert.AreEqual(0, fs.Open("/f", ORdWr | OCreat, 0x1A4, out ILinuxFile? created));
        Assert.AreEqual(4, created!.Write(new byte[] { 1, 1, 1, 1 }));

        ILinuxPageSource fresh = Source(fs, "/f", 0, out ILinuxFile reader2);
        Assert.AreEqual(4, fresh.ReadAt(0, dst));
        CollectionAssert.AreEqual(new byte[] { 1, 1, 1, 1 }, dst);
        Assert.AreEqual(4, source.ReadAt(0, dst), "alte Abbildung liest weiter die gelöschte Datei");
        CollectionAssert.AreEqual(Pattern(4), dst);
        Close(source, reader);
        Close(fresh, reader2);
        Assert.AreEqual(0, created.Close());
    }

    [TestMethod]
    public void DescriptorReadsBypassTheCache()
    {
        LinuxFileSystem fs = NewFs(Pattern(Block));
        Assert.AreEqual(0, fs.Open("/f", 0, 0, out ILinuxFile? f));
        byte[] dst = new byte[4096];
        Assert.AreEqual(4096, f!.Read(dst));
        Assert.AreEqual(0, fs.ReadCache!.Misses + fs.ReadCache.Hits);
        Assert.AreEqual(0, f.Close());
    }

    private static LinuxFileSystem NewFs(byte[] content)
    {
        var fs = new LinuxFileSystem(new MemoryFileStore(), new Term(), readCacheBytes: 1 << 20);
        Assert.AreEqual(0, fs.Open("/f", ORdWr | OCreat, 0x1A4, out ILinuxFile? f));
        Assert.AreEqual(content.Length, f!.Write(content));
        Assert.AreEqual(0, f.Close());
        return fs;
    }

    private static ILinuxPageSource Source(LinuxFileSystem fs, string path, int flags, out ILinuxFile file)
    {
        Assert.AreEqual(0, fs.Open(path, flags, 0, out ILinuxFile? opened));
        file = opened!;
        ILinuxPageSource? source = ((ILinuxPageSourceFile)opened!).OpenPageSource();
        Assert.IsNotNull(source);
        return source;
    }

    private static void Close(ILinuxPageSource source, ILinuxFile file)
    {
        source.Release();
        Assert.AreEqual(0, file.Close());
    }

    private static byte[] Pattern(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 7) + (i >> 12));
        }

        return data;
    }

    private sealed class Term : ILinuxDescriptor
    {
        public long Write(ReadOnlySpan<byte> data) => data.Length;
    }

    private sealed class CountingFile(byte[] content) : ILinuxStoreFile
    {
        public int Reads;
        public bool Fail;

        public long Length() => content.Length;

        public long ReadAt(long offset, Span<byte> dst)
        {
            Reads++;
            if (Fail)
            {
                return -LinuxErrno.EIO;
            }

            if (offset >= content.Length)
            {
                return 0;
            }

            int n = (int)Math.Min(dst.Length, content.Length - offset);
            content.AsSpan((int)offset, n).CopyTo(dst);
            return n;
        }

        public long WriteAt(long offset, ReadOnlySpan<byte> src) => -LinuxErrno.EBADF;

        public long SetLength(long length) => -LinuxErrno.EBADF;

        public long Close() => 0;
    }
}
