// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch fat-inode-cache, see PATCHES.md

using System.Runtime.CompilerServices;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.Tests.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Fat;

/// <summary>
/// The inode cache keeps nothing alive that nobody holds, and a file that
/// somebody holds never gets a second object.
/// </summary>
[TestFixture]
public class FatInodeCacheTests
{
    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;

    private MemoryBlockDevice _device = null!;
    private FatSuperblock _sb = null!;
    private FatInode _root = null!;

    [SetUp]
    public void SetUp()
    {
        _device = new("ram0", 512, 131072); // 64 MiB
        Assert.That(new FatFileSystemType(_device).TryFormat(ReadOnlySpan<char>.Empty, new FatFormatOptions { Type = FatType.Fat32 }), Is.True);
        Mount();
    }

    private void Mount()
    {
        Assert.That(new FatFileSystemType(_device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        _sb = (FatSuperblock)vsb!;
        _root = (FatInode)_sb.Root;
    }

    private static long Write(FatInode inode, string text, long position = 0)
    {
        FatOpenFile open = new(inode, inode.FileOperations!) { Position = position };
        return inode.FileOperations!.Write(open, global::System.Text.Encoding.ASCII.GetBytes(text));
    }

    private FatInode Create(FatInode parent, string name, string text)
    {
        Assert.That(parent.InodeOperations.Create(parent, name, FileMode, out IVfsInode? inode), Is.True, name);
        if (text.Length > 0)
        {
            Assert.That(Write((FatInode)inode!, text), Is.EqualTo(text.Length));
        }

        return (FatInode)inode!;
    }

    private static FatInode Lookup(FatInode parent, string name)
    {
        Assert.That(parent.InodeOperations.Lookup(parent, name, out IVfsInode? inode), Is.True, name);
        return (FatInode)inode!;
    }

    private static string Read(FatInode inode)
    {
        byte[] buffer = new byte[256];
        long read = inode.FileOperations!.Read(new FatOpenFile(inode, inode.FileOperations!), buffer);
        return global::System.Text.Encoding.ASCII.GetString(buffer, 0, (int)read);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference CreateUnheld(string name, string text) => new(Create(_root, name, text));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FillUnheld(int from, int count)
    {
        for (int i = from; i < from + count; i++)
        {
            Create(_root, "f" + i, "x");
        }
    }

    [Test]
    public void UnheldInode_IsCollected()
    {
        WeakReference weak = CreateUnheld("a.txt", "eins");
        Collect();
        Assert.That(weak.IsAlive, Is.False);
        Assert.That(Read(Lookup(_root, "a.txt")), Is.EqualTo("eins"));
    }

    [Test]
    public void ManyFiles_EntriesStayBounded()
    {
        for (int i = 0; i < 3; i++)
        {
            FillUnheld(i * 600, 600);
            Collect();
        }

        Assert.That(_sb.InodeCache.Count, Is.LessThanOrEqualTo(FatInodeCache.SweepMin + 1));
    }

    [Test]
    public void HeldInode_SurvivesSweepAndCollection_AsTheSameObject()
    {
        FatInode held = Create(_root, "held.txt", "alt");
        FillUnheld(0, FatInodeCache.SweepMin + 100);
        Collect();
        FillUnheld(5000, 10);
        Assert.That(Lookup(_root, "held.txt"), Is.SameAs(held));
        Assert.That(Write(held, "neu"), Is.EqualTo(3));
        Assert.That(Read(Lookup(_root, "held.txt")), Is.EqualTo("neu"));
        GC.KeepAlive(held);
    }

    [Test]
    public void EmptyFile_BecomesTheSharedObject_AtItsFirstCluster()
    {
        FatInode held = Create(_root, "leer.txt", "");
        Collect();
        Assert.That(Write(held, "voll"), Is.EqualTo(4));
        Collect();
        Assert.That(Lookup(_root, "leer.txt"), Is.SameAs(held));
        GC.KeepAlive(held);
    }

    [Test]
    public void DirectoryOfAHeldFile_KeepsItsIdentity_AndItsChainWhenItGrows()
    {
        Assert.That(_root.InodeOperations.Mkdir(_root, "dir", VfsMode.Directory | VfsMode.OwnerRead, out IVfsInode? made), Is.True);
        FatInode child = Create((FatInode)made!, "kind.txt", "k");
        made = null;
        Collect();
        FatInode dir = Lookup(_root, "dir");
        Assert.That(child.Parent, Is.SameAs(dir));

        // Grow the directory past its first cluster through the looked-up object.
        for (int i = 0; i < 100; i++)
        {
            Create(dir, "datei-mit-langem-namen-" + i, "y");
        }

        Assert.That(Write(child, "kind"), Is.EqualTo(4));
        Assert.That(Read(Lookup(Lookup(_root, "dir"), "kind.txt")), Is.EqualTo("kind"));
        Assert.That(Lookup(dir, "datei-mit-langem-namen-99").Size, Is.EqualTo(1));
    }

    [Test]
    public void UnlinkOfAHeldFile_AfterCollection_StillCutsItOffFromTheDirectorySlot()
    {
        FatInode held = Create(_root, "weg.txt", "alt");
        FillUnheld(0, FatInodeCache.SweepMin + 100);
        Collect();
        Assert.That(_root.InodeOperations.Unlink(_root, "weg.txt"), Is.True);
        FatInode next = Create(_root, "neu.txt", "neuer Inhalt");
        Assert.That(held.DirEntryByteOffset, Is.EqualTo(FatInode.NoDirEntryOffset));
        _sb.UpdateInodeEntry(held);
        Assert.That(Read(Lookup(_root, "neu.txt")), Is.EqualTo("neuer Inhalt"));
        GC.KeepAlive(next);
    }

    [Test]
    public void ReusedCluster_AfterUnlink_GetsItsOwnObject()
    {
        FatInode held = Create(_root, "a.txt", "alt");
        uint cluster = held.FirstCluster;
        Assert.That(_root.InodeOperations.Unlink(_root, "a.txt"), Is.True);
        Assert.That(_sb.InodeCache.TryGet(cluster, out _), Is.False);
        _sb.Fat.Set(cluster, FatTable.FreeCluster);
        FatInode other = new(_sb, "b.txt", held.Attributes, cluster, 3, _root, 0, 1);
        _sb.InodeCache.Set(cluster, other);
        Assert.That(_sb.InodeCache.TryGet(cluster, out FatInode? found), Is.True);
        Assert.That(found, Is.SameAs(other));
        GC.KeepAlive(held);
    }

    [Test]
    public void TruncatedFile_LosesItsKey_ToTheFileThatTakesTheCluster()
    {
        FatInode a = Create(_root, "a.txt", "alt");
        uint cluster = a.FirstCluster;
        Assert.That(a.InodeOperations.SetAttr(a, SetAttrFlags.Size, new VfsStat { Size = 0 }), Is.True);
        _sb.Fat.AllocateChain(1); // whatever the hint gives; b is moved to the old cluster by hand
        FatInode b = Create(_root, "b.txt", "");
        b.FirstCluster = cluster;
        _sb.UpdateInodeEntry(b);
        Assert.That(Lookup(_root, "b.txt"), Is.SameAs(b));
        Assert.That(Lookup(_root, "a.txt").FirstCluster, Is.EqualTo(0u));
        GC.KeepAlive(a);
    }

    [Test]
    public void LookupAfterRemount_GivesTheSameObject_WhileHeld()
    {
        Create(_root, "a.txt", "eins");
        _sb.SuperOperations.Drop(_sb);
        Mount();
        FatInode held = Lookup(_root, "a.txt");
        Collect();
        Assert.That(Lookup(_root, "a.txt"), Is.SameAs(held));
        GC.KeepAlive(held);
    }

    [Test]
    public void DeadEntries_AreNotSweptBelowTheThreshold()
    {
        FillUnheld(0, 200);
        Collect();
        FillUnheld(1000, 1);
        Assert.That(_sb.InodeCache.Count, Is.EqualTo(201));
    }

    [Test]
    public void Drop_ForgetsEverything()
    {
        FatInode held = Create(_root, "a.txt", "alt");
        _sb.SuperOperations.Drop(_sb);
        Assert.That(_sb.InodeCache.Count, Is.Zero);
        Mount();
        Assert.That(Lookup(_root, "a.txt"), Is.Not.SameAs(held));
    }

    [Test]
    public void ParallelUse_OfTheTable_DoesNotLoseHeldObjects()
    {
        FatInode[] held = new FatInode[4];
        Parallel.For(0, 4, t =>
        {
            FatInode mine = new(_sb, "h" + t, FatAttr.Archive, (uint)(10 + t), 1, _root, 0, 1);
            held[t] = mine;
            _sb.InodeCache.Set((uint)(10 + t), mine);
            for (uint i = 0; i < 20_000; i++)
            {
                uint key = 100 + (i % 3000) + (uint)t * 3000;
                _sb.InodeCache.Set(key, new FatInode(_sb, "x", FatAttr.Archive, key, 1, _root, 0, 1));
                if (i % 7 == 0)
                {
                    _sb.InodeCache.Remove(key);
                }

                if (!_sb.InodeCache.TryGet((uint)(10 + t), out FatInode? found) || !ReferenceEquals(found, mine))
                {
                    throw new InvalidOperationException("held inode lost");
                }
            }
        });
        GC.KeepAlive(held);
    }

    // ZehOS patch fat-rename-open, see PATCHES.md
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FillUnheldEmpty(int from, int count)
    {
        for (int i = from; i < from + count; i++)
        {
            Create(_root, "e" + i, "");
        }
    }

    [Test]
    public void ManyEmptyFiles_SlotEntriesStayBounded()
    {
        for (int i = 0; i < 3; i++)
        {
            FillUnheldEmpty(i * 300, 300);
            Collect();
        }

        Assert.That(_sb.InodeCache.EmptyCount, Is.LessThanOrEqualTo(FatInodeCache.EmptySweepMin + 1));
    }

    [Test]
    public void Drop_ForgetsEmptyFiles_Too()
    {
        FatInode held = Create(_root, "leer.txt", "");
        Assert.That(_sb.InodeCache.EmptyCount, Is.EqualTo(1));
        _sb.SuperOperations.Drop(_sb);
        Assert.That(_sb.InodeCache.EmptyCount, Is.Zero);
        GC.KeepAlive(held);
    }

    [Test]
    public void ParallelUse_OfBothTables_DoesNotLoseHeldObjects()
    {
        FatInode[] held = new FatInode[4];
        Parallel.For(0, 4, t =>
        {
            FatInode mine = new(_sb, "h" + t, FatAttr.Archive, 0, 0, _root, 0, 1);
            held[t] = mine;
            ulong own = 1UL << 40 | (uint)t;
            _sb.InodeCache.SetEmpty(own, mine);
            for (uint i = 0; i < 20_000; i++)
            {
                ulong slot = (ulong)(100 + (i % 3000) + t * 3000);
                _sb.InodeCache.SetEmpty(slot, new FatInode(_sb, "x", FatAttr.Archive, 0, 0, _root, 0, 1));
                _sb.InodeCache.Set((uint)slot, new FatInode(_sb, "y", FatAttr.Archive, (uint)slot, 1, _root, 0, 1));
                if (i % 7 == 0)
                {
                    _sb.InodeCache.RemoveEmpty(slot);
                    _sb.InodeCache.Remove((uint)slot);
                }

                if (!_sb.InodeCache.TryGetEmpty(own, out FatInode? found) || !ReferenceEquals(found, mine))
                {
                    throw new InvalidOperationException("held empty inode lost");
                }
            }
        });
        GC.KeepAlive(held);
    }
}
