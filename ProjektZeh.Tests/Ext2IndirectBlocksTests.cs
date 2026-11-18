using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Tests;

/// <summary>
/// Ergaenzung zu Cosmos 3.0.97 Truncate fuer den Rmdir-Plug: die Zeigerbloecke eines Verzeichnisses mit mehr als zwoelf
/// Bloecken blieben nach Rmdir belegt (E4.0 Lauf 9, e2fsck "Block bitmap differences").
/// </summary>
[TestClass]
public sealed class Ext2IndirectBlocksTests
{
    private const uint BlockSize = 1024;
    private const uint BlocksCount = 100_000;

    private readonly Dictionary<uint, byte[]> disk = [];
    private readonly List<uint> reads = [];

    private byte[] Read(uint block)
    {
        reads.Add(block);
        return disk.TryGetValue(block, out byte[]? data) ? data : new byte[BlockSize];
    }

    private void PointerBlock(uint block, params uint[] pointers)
    {
        byte[] data = new byte[BlockSize];
        for (int index = 0; index < pointers.Length; index++) BitConverter.TryWriteBytes(data.AsSpan(index * 4, 4), pointers[index]);
        disk[block] = data;
    }

    private static uint[] Inode(uint single = 0, uint dbl = 0, uint triple = 0)
    {
        var block = new uint[15];
        for (uint index = 0; index < 12; index++) block[index] = 500 + index;
        block[12] = single;
        block[13] = dbl;
        block[14] = triple;
        return block;
    }

    [TestMethod]
    public void DirectOnly_NothingToFree()
    {
        CollectionAssert.AreEqual(new List<uint>(), Ext2IndirectBlocks.Collect(Inode(), BlockSize, BlocksCount, Read));
        Assert.AreEqual(0, reads.Count);
    }

    [TestMethod]
    public void SingleIndirect_OnlyPointerBlockWithoutReading()
    {
        // Lauf 9: ~70 Verzeichnisbloecke; die Datenbloecke hat Truncate schon freigegeben, der Zeigerblock fehlte.
        PointerBlock(900, 0, 0, 0);
        CollectionAssert.AreEqual(new List<uint> { 900 }, Ext2IndirectBlocks.Collect(Inode(single: 900), BlockSize, BlocksCount, Read));
        Assert.AreEqual(0, reads.Count, "Datenzeiger der ersten Ebene werden nicht gelesen");
    }

    [TestMethod]
    public void DoubleIndirect_FreesSecondLevelPointerBlocks()
    {
        // Truncate nullt nur die Datenzeiger in den Bloecken der zweiten Ebene; deren Zeiger in 910 bleiben stehen.
        PointerBlock(910, 911, 0, 912);
        var result = Ext2IndirectBlocks.Collect(Inode(single: 900, dbl: 910), BlockSize, BlocksCount, Read);
        CollectionAssert.AreEquivalent(new List<uint> { 900, 910, 911, 912 }, result);
        CollectionAssert.AreEqual(new List<uint> { 910 }, reads);
    }

    [TestMethod]
    public void TripleIndirect_WalksAllLevels()
    {
        PointerBlock(920, 921);
        PointerBlock(921, 922, 923);
        var result = Ext2IndirectBlocks.Collect(Inode(triple: 920), BlockSize, BlocksCount, Read);
        CollectionAssert.AreEquivalent(new List<uint> { 920, 921, 922, 923 }, result);
    }

    [TestMethod]
    public void PointersOutsideVolume_SkippedWithoutRead()
    {
        // Beschaedigtes Inode: weder lesen noch einen Block ausserhalb des Volumes freigeben.
        PointerBlock(910, BlocksCount, 911, uint.MaxValue);
        var result = Ext2IndirectBlocks.Collect(Inode(single: BlocksCount + 5, dbl: 910), BlockSize, BlocksCount, Read);
        CollectionAssert.AreEquivalent(new List<uint> { 910, 911 }, result);
        CollectionAssert.AreEqual(new List<uint> { 910 }, reads);
    }

    [TestMethod]
    public void DuplicatePointers_FreedOnce()
    {
        // Doppelte Zeiger wuerden in Cosmos FreeBlock den Freizaehler zweimal erhoehen.
        PointerBlock(910, 911, 911, 910);
        var result = Ext2IndirectBlocks.Collect(Inode(single: 911, dbl: 910), BlockSize, BlocksCount, Read);
        CollectionAssert.AreEquivalent(new List<uint> { 910, 911 }, result);
    }

    [TestMethod]
    public void ShortInodeArray_NoOutOfRange()
    {
        Assert.AreEqual(0, Ext2IndirectBlocks.Collect(new uint[12], BlockSize, BlocksCount, Read).Count);
        Assert.AreEqual(0, Ext2IndirectBlocks.Collect(new uint[13], BlockSize, BlocksCount, Read).Count);
    }
}
