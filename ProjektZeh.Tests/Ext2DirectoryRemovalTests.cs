using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Tests;

/// <summary>
/// Ersatz fuer Cosmos 3.0.97 Ext2DirectoryHelper.RemoveEntry (Ext2Directory.cs:302-365): ein geloeschter Eintrag
/// verschmilzt nur mit dem Vorgaenger im selben Block; der erste Record eines Blocks wird nur freigegeben (inode 0).
/// Das Original zog den letzten Record des Vorgaengerblocks ueber die Blockgrenze (e2fsck: directory corrupted).
/// </summary>
[TestClass]
public sealed class Ext2DirectoryRemovalTests
{
    private const int BlockSize = 4096;

    /// <summary>Verzeichnisdaten wie Cosmos AddEntry sie anlegt: je Block dicht gepackt, letzter Record bis Blockende.</summary>
    private static byte[] Directory(int count, out List<(string Name, int Offset)> layout)
    {
        var names = new List<string> { ".", ".." };
        for (int index = 0; index < count; index++) names.Add($"n{index:D4}");
        layout = [];
        var blocks = new List<byte[]>();
        byte[] block = new byte[BlockSize];
        int pos = 0, previous = -1;
        uint inode = 2;
        foreach (string name in names)
        {
            int length = (8 + name.Length + 3) & ~3;
            if (pos + length > BlockSize)
            {
                Set16(block, previous + 4, (ushort)(BlockSize - previous));
                blocks.Add(block);
                block = new byte[BlockSize];
                pos = 0;
            }

            Set32(block, pos, inode++);
            Set16(block, pos + 4, (ushort)length);
            block[pos + 6] = (byte)name.Length;
            block[pos + 7] = 1;
            Encoding.ASCII.GetBytes(name).CopyTo(block, pos + 8);
            layout.Add((name, blocks.Count * BlockSize + pos));
            previous = pos;
            pos += length;
        }

        Set16(block, previous + 4, (ushort)(BlockSize - previous));
        blocks.Add(block);
        byte[] data = new byte[blocks.Count * BlockSize];
        for (int index = 0; index < blocks.Count; index++) blocks[index].CopyTo(data, index * BlockSize);
        return data;
    }

    private static void Set16(byte[] data, int offset, ushort value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 2), value);
    private static void Set32(byte[] data, int offset, uint value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 4), value);

    private static int Remove(byte[] data, string name) => Ext2DirectoryRemoval.Remove(data, BlockSize, Encoding.UTF8.GetBytes(name));

    /// <summary>Lebende Namen in Verzeichnisreihenfolge; wirft, wenn ein Record die Blockgrenze ueberschreitet.</summary>
    private static List<string> Live(byte[] data)
    {
        var result = new List<string>();
        for (int block = 0; block < data.Length; block += BlockSize)
        {
            int pos = block;
            while (pos < block + BlockSize)
            {
                ushort recLen = BitConverter.ToUInt16(data, pos + 4);
                Assert.IsTrue(recLen >= 8 && recLen % 4 == 0, $"rec_len {recLen} bei {pos}");
                Assert.IsTrue(pos + recLen <= block + BlockSize, $"Record bei {pos} ueberschreitet die Blockgrenze ({recLen})");
                if (BitConverter.ToUInt32(data, pos) != 0) result.Add(Encoding.ASCII.GetString(data, pos + 8, data[pos + 6]));
                pos += recLen;
            }

            Assert.AreEqual(block + BlockSize, pos, "Records fuellen den Block nicht genau");
        }

        return result;
    }

    [TestMethod]
    public void Layout_SpansSeveralBlocks()
    {
        byte[] data = Directory(600, out var layout);
        Assert.IsTrue(data.Length >= 3 * BlockSize);
        Assert.IsTrue(layout.Exists(entry => entry.Offset == BlockSize), "ein Eintrag beginnt Block 1");
        Assert.AreEqual(602, Live(data).Count);
    }

    [TestMethod]
    public void FirstEntryOfLaterBlock_OnlyFreedNotMerged()
    {
        // Genau der Fall aus Lauf 10 / codex-upper-test: erster Eintrag von Block 1.
        byte[] data = Directory(600, out var layout);
        string first = layout.Find(entry => entry.Offset == BlockSize).Name;
        ushort before = BitConverter.ToUInt16(data, BlockSize + 4);
        Assert.AreEqual(1, Remove(data, first));
        Assert.AreEqual(0u, BitConverter.ToUInt32(data, BlockSize), "Inode des ersten Records auf 0");
        Assert.AreEqual(before, BitConverter.ToUInt16(data, BlockSize + 4), "rec_len bleibt");
        var live = Live(data);
        Assert.IsFalse(live.Contains(first));
        Assert.AreEqual(601, live.Count);
    }

    [TestMethod]
    public void EntryInsideBlock_MergesWithPredecessorInSameBlock()
    {
        byte[] data = Directory(600, out var layout);
        var (name, offset) = layout[10];
        var predecessor = layout[9];
        ushort merged = (ushort)(BitConverter.ToUInt16(data, predecessor.Offset + 4) + BitConverter.ToUInt16(data, offset + 4));
        Assert.AreEqual(0, Remove(data, name));
        Assert.AreEqual(merged, BitConverter.ToUInt16(data, predecessor.Offset + 4));
        Assert.IsFalse(Live(data).Contains(name));
    }

    [TestMethod]
    public void LastEntryOfBlock_MergesUpToBlockEnd()
    {
        byte[] data = Directory(600, out var layout);
        var (name, _) = layout.FindLast(entry => entry.Offset < BlockSize);
        Assert.AreEqual(0, Remove(data, name));
        Live(data);
    }

    [TestMethod]
    [DataRow(300)]
    [DataRow(5000)]
    public void ManyConsecutiveDeletes_NoRecordCrossesBlockOrOverflows(int deletes)
    {
        // Lauf 9/10: das Original lief hier ueber die Blockgrenze und ab ~4095 in den 16-Bit-rec_len.
        byte[] data = Directory(Math.Max(600, deletes + 100), out _);
        for (int index = 0; index < deletes; index++) Assert.IsTrue(Remove(data, $"n{index:D4}") >= 0, $"n{index:D4}");
        var live = Live(data);
        Assert.AreEqual(Math.Max(600, deletes + 100) - deletes + 2, live.Count);
        Assert.IsFalse(live.Contains("n0000"));
    }

    [TestMethod]
    public void FreedFirstSlotThenNextEntry_MergesIntoFreedSlot()
    {
        byte[] data = Directory(600, out var layout);
        int firstIndex = layout.FindIndex(entry => entry.Offset == BlockSize);
        Assert.AreEqual(1, Remove(data, layout[firstIndex].Name));
        Assert.AreEqual(1, Remove(data, layout[firstIndex + 1].Name));
        Assert.AreEqual(0u, BitConverter.ToUInt32(data, BlockSize));
        Live(data);
    }

    [TestMethod]
    public void OnlyEntryOfBlock_FreedInPlace()
    {
        // Block 1 nach dem Loeschen aller anderen Eintraege: der verbliebene Record steht am Blockanfang und fuellt ihn.
        byte[] data = Directory(600, out var layout);
        var inBlock1 = layout.FindAll(entry => entry.Offset >= BlockSize && entry.Offset < 2 * BlockSize);
        for (int index = 1; index < inBlock1.Count; index++) Assert.AreEqual(1, Remove(data, inBlock1[index].Name));
        Assert.AreEqual((ushort)BlockSize, BitConverter.ToUInt16(data, BlockSize + 4), "einziger Record fuellt den Block");
        Assert.AreEqual(1, Remove(data, inBlock1[0].Name));
        Assert.AreEqual(0u, BitConverter.ToUInt32(data, BlockSize));
        Live(data);
    }

    [TestMethod]
    public void Missing_ReturnsNotFoundAndChangesNothing()
    {
        byte[] data = Directory(600, out _);
        byte[] copy = (byte[])data.Clone();
        Assert.AreEqual(Ext2DirectoryRemoval.NotFound, Remove(data, "fehlt"));
        CollectionAssert.AreEqual(copy, data);
    }

    [TestMethod]
    public void RecordCrossingBlock_RefusedWithoutWrite()
    {
        // Ein schon beschaedigtes Verzeichnis (Altbestand des Originals) wird nicht weiter veraendert.
        byte[] data = Directory(600, out var layout);
        var last = layout.FindLast(entry => entry.Offset < BlockSize);
        Set16(data, last.Offset + 4, (ushort)(BlockSize - last.Offset + 16));
        byte[] copy = (byte[])data.Clone();
        Assert.AreEqual(Ext2DirectoryRemoval.Malformed, Remove(data, "n0500"));
        CollectionAssert.AreEqual(copy, data);
    }

    [TestMethod]
    public void ZeroRecLen_RefusedWithoutWrite()
    {
        byte[] data = Directory(10, out _);
        Set16(data, 12 + 4, 0);
        Assert.AreEqual(Ext2DirectoryRemoval.Malformed, Remove(data, "n0005"));
    }

    [TestMethod]
    public void NamesCompareExactBytes()
    {
        byte[] data = Directory(10, out _);
        Assert.AreEqual(Ext2DirectoryRemoval.NotFound, Remove(data, "N0001"));
        Assert.AreEqual(Ext2DirectoryRemoval.NotFound, Remove(data, "n000"));
        Assert.AreEqual(0, Remove(data, "n0001"));
    }
}
