using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class Ext2InodeReleaseTests
{
    private const uint BlockSize = 1024;
    private const uint InodeSize = 128;
    private const uint Bitmap = 3;
    private const uint Table = 5;

    private readonly Dictionary<uint, byte[]> disk = [];
    private int writes;

    private byte[] Read(uint block) => (byte[])(disk.TryGetValue(block, out byte[]? data) ? data : new byte[BlockSize]).Clone();

    private void Write(uint block, byte[] data)
    {
        writes++;
        disk[block] = (byte[])data.Clone();
    }

    private bool Release(uint index, uint now = 1_800_000_000) =>
        Ext2InodeRelease.Release(Read, Write, Table, Bitmap, index, BlockSize, InodeSize, now);

    // Inode wie nach Cosmos Rmdir: Bit gesetzt, auf der Platte noch Verzeichnis mit zwei Links und ohne dtime.
    private void LiveInode(uint index)
    {
        byte[] bitmap = Read(Bitmap);
        bitmap[index / 8] |= (byte)(1 << (int)(index % 8));
        disk[Bitmap] = bitmap;
        uint block = Table + index / (BlockSize / InodeSize);
        byte[] table = Read(block);
        int offset = (int)(index % (BlockSize / InodeSize) * InodeSize);
        BitConverter.TryWriteBytes(table.AsSpan(offset, 2), (ushort)0x41ED);
        BitConverter.TryWriteBytes(table.AsSpan(offset + 26, 2), (ushort)2);
        disk[block] = table;
    }

    private (ushort Links, uint Dtime, ushort Mode) Inode(uint index)
    {
        byte[] table = Read(Table + index / (BlockSize / InodeSize));
        int offset = (int)(index % (BlockSize / InodeSize) * InodeSize);
        return (BitConverter.ToUInt16(table, offset + 26), BitConverter.ToUInt32(table, offset + 20), BitConverter.ToUInt16(table, offset));
    }

    private bool BitSet(uint index) => (Read(Bitmap)[index / 8] & (1 << (int)(index % 8))) != 0;

    [TestMethod]
    public void Release_LiveDirectory_WritesLinksZeroAndDtimeThenClearsBit()
    {
        LiveInode(12);
        LiveInode(13);

        Assert.IsTrue(Release(12));

        Assert.AreEqual(((ushort)0, 1_800_000_000u, (ushort)0x41ED), Inode(12));
        Assert.IsFalse(BitSet(12));
        Assert.AreEqual(((ushort)2, 0u, (ushort)0x41ED), Inode(13), "Nachbarinode im selben Block bleibt unveraendert");
        Assert.IsTrue(BitSet(13));
    }

    [TestMethod]
    public void Release_AlreadyFree_WritesNothingAndReportsFalse()
    {
        Assert.IsFalse(Release(12));
        Assert.AreEqual(0, writes);
    }

    [TestMethod]
    public void Release_WithoutClock_StoresNonZeroDtime()
    {
        LiveInode(3);
        Assert.IsTrue(Release(3, now: 0));
        Assert.AreEqual(1u, Inode(3).Dtime);
    }

    [TestMethod]
    public void Release_InodeInSecondTableBlock_UsesThatBlock()
    {
        LiveInode(9);
        Assert.IsTrue(Release(9));
        Assert.AreEqual((ushort)0, Inode(9).Links);
        Assert.IsFalse(disk.ContainsKey(Table), "erster Tabellenblock unberuehrt");
    }
}
