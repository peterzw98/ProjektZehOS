using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class Ext2InodeReleaseTests
{
    private const uint BlockSize = 1024;
    private const uint Bitmap = 3;
    private const uint Table = 5;

    private readonly Dictionary<uint, byte[]> disk = [];
    private int writes;
    private uint inodeSize = 128;

    private byte[] Read(uint block) => (byte[])(disk.TryGetValue(block, out byte[]? data) ? data : new byte[BlockSize]).Clone();

    private void Write(uint block, byte[] data)
    {
        writes++;
        disk[block] = (byte[])data.Clone();
    }

    private bool Release(uint index, uint now = 1_800_000_000) =>
        Ext2InodeRelease.Release(Read, Write, Table, Bitmap, index, BlockSize, inodeSize, now);

    // Inode wie nach Cosmos Rmdir: Bit gesetzt, auf der Platte noch Verzeichnis mit zwei Links und ohne dtime.
    private void LiveInode(uint index)
    {
        byte[] bitmap = Read(Bitmap);
        bitmap[index / 8] |= (byte)(1 << (int)(index % 8));
        disk[Bitmap] = bitmap;
        uint block = Table + index / (BlockSize / inodeSize);
        byte[] table = Read(block);
        int offset = (int)(index % (BlockSize / inodeSize) * inodeSize);
        BitConverter.TryWriteBytes(table.AsSpan(offset, 2), (ushort)0x41ED);
        BitConverter.TryWriteBytes(table.AsSpan(offset + 26, 2), (ushort)2);
        disk[block] = table;
    }

    private (ushort Links, uint Dtime, ushort Mode) Inode(uint index)
    {
        byte[] table = Read(Table + index / (BlockSize / inodeSize));
        int offset = (int)(index % (BlockSize / inodeSize) * inodeSize);
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

    [DataTestMethod]
    [DataRow(128u, 7u)]
    [DataRow(128u, 15u)]
    [DataRow(256u, 3u)]
    [DataRow(256u, 7u)]
    public void Release_LastInodeOfTableBlock_MarksOnlyThatSlot(uint size, uint index)
    {
        inodeSize = size;
        LiveInode(index);
        LiveInode(index - 1);

        Assert.IsTrue(Release(index));

        Assert.AreEqual(((ushort)0, 1_800_000_000u, (ushort)0x41ED), Inode(index));
        Assert.AreEqual(((ushort)2, 0u, (ushort)0x41ED), Inode(index - 1), "vorheriges Inode im Block bleibt");
        Assert.IsFalse(disk.ContainsKey(Table + index / (BlockSize / size) + 1), "kein Schreiben hinter dem Block");
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
