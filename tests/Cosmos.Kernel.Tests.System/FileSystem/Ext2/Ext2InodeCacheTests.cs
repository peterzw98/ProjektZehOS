// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-inode-cache-evict, see PATCHES.md

using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// The inode cache keeps a bounded number of inodes alive, and evicting one
/// never creates a second object for a number somebody still references.
/// </summary>
[TestFixture]
public class Ext2InodeCacheTests
{
    [Test]
    public void InodeCache_ReadDirOf30000Entries_DoesNotGrowDictionary()
    {
        // 256 MiB with 2 KiB blocks: 8 groups of 4096 inodes, directory fits into direct + single indirect blocks.
        MemoryBlockDevice device = new("ram0", 512, 524288);
        Assert.That(new Ext2FileSystemType(device).TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = 2048 }), Is.True);

        const int Files = 30_000;
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? writer), Is.True);
        Assert.That(((Ext2Superblock)writer!).InodesCount, Is.GreaterThan((uint)Files + 16), "precondition: enough inodes");
        Ext2Inode writeRoot = (Ext2Inode)writer.Root;
        for (int i = 0; i < Files; i++)
        {
            Assert.That(writeRoot.InodeOperations.Create(writeRoot, "f" + i, VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out _), Is.True, $"create {i}");
        }

        // Unmount the writer: only one writable mount per device.
        writer.SuperOperations.Drop(writer);

        // Fresh mount: ReadDir alone fills the cache with all 30,000 inodes; a dictionary starting small would resize many times.
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        Ext2Superblock sb = (Ext2Superblock)vsb!;
        int capacity = sb.InodeCache.Capacity;

        Ext2Inode root = (Ext2Inode)sb.Root;
        Assert.That(root.InodeOperations.ReadDir(root, out IReadOnlyList<IVfsInode> entries), Is.True);
        Assert.That(entries.Count, Is.GreaterThanOrEqualTo(Files));
        Assert.That(sb.InodeCache.LiveCount, Is.LessThanOrEqualTo(Ext2InodeCache.Limit));
        Assert.That(capacity, Is.LessThan(25_000), "presize per mount");

        // The listing references every inode, so all of them keep their identity.
        Dictionary<uint, IVfsInode> byNumber = [];
        foreach (Ext2Inode entry in entries.Cast<Ext2Inode>())
        {
            byNumber.Add(entry.InodeNumber, entry);
        }

        foreach ((uint number, IVfsInode inode) in byNumber)
        {
            if (!ReferenceEquals(sb.ReadInode(number, "x"), inode))
            {
                Assert.Fail($"inode {number} has two live objects");
            }
        }

        GC.KeepAlive(entries);
    }

    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;
    private const int Fillers = Ext2InodeCache.Limit * 3;

    private MemoryBlockDevice _device = null!;
    private Ext2Superblock _sb = null!;
    private Ext2Inode _root = null!;

    [SetUp]
    public void SetUp()
    {
        _device = new("ram0", 512, 131072); // 64 MiB, 8192 inodes
        Assert.That(new Ext2FileSystemType(_device).TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = 2048 }), Is.True);
        _sb = Mount(MountFlags.None);
        _root = (Ext2Inode)_sb.Root;
        Assert.That(_sb.InodesCount, Is.GreaterThan((uint)Fillers + 32), "precondition: enough inodes");
    }

    private Ext2Superblock Mount(MountFlags flags)
    {
        Assert.That(new Ext2FileSystemType(_device).TryMount(ReadOnlySpan<char>.Empty, flags, out IVfsSuperblock? vsb), Is.True);
        return (Ext2Superblock)vsb!;
    }

    private Ext2Inode Create(string name)
    {
        Assert.That(_root.InodeOperations.Create(_root, name, FileMode, out IVfsInode? inode), Is.True, name);
        return (Ext2Inode)inode!;
    }

    private Ext2Inode Lookup(string name)
    {
        Assert.That(_root.InodeOperations.Lookup(_root, name, out IVfsInode? inode), Is.True, name);
        return (Ext2Inode)inode!;
    }

    private void Fill(string prefix = "fill")
    {
        for (int i = 0; i < Fillers; i++)
        {
            Create(prefix + i);
        }
    }

    // A handle on the inode, opened the way VfsManager does.
    private sealed class OpenFile(Ext2Inode inode) : IVfsOpenFile
    {
        public string Name => "open";
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.FileOperations!;
        public long Position { get; set; }
    }

    private static byte[] Pattern(int length, byte seed)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i);
        }

        return data;
    }

    private static byte[] ReadAll(Ext2Inode inode)
    {
        byte[] data = new byte[(int)inode.FullSize];
        Assert.That(inode.FileOperations!.Read(new OpenFile(inode), data), Is.EqualTo(data.Length));
        return data;
    }

    [Test]
    public void CreateManyFiles_KeepsAtMostLimitInodes()
    {
        Fill();

        Assert.That(_sb.InodeCache.LiveCount, Is.EqualTo(Ext2InodeCache.Limit));
    }

    [Test]
    public void ManyEvictions_SweepsCollectedInodes()
    {
        Ext2InodeCache cache = new(100_000);
        int capacity = cache.Capacity;
        for (uint number = 1; number <= 60_000; number++)
        {
            cache.GetOrAdd(new Ext2Inode(_sb, number, "n") { Dirty = false });
            if (number % 3000 == 0)
            {
                Collect();
            }
        }

        Assert.That(cache.LiveCount, Is.EqualTo(Ext2InodeCache.Limit));
        Assert.That(cache.EvictedCount, Is.LessThan(12_000));
        Assert.That(cache.Capacity, Is.EqualTo(capacity), "tables never grow");
        cache.Clear();
    }

    [Test]
    public void OpenInode_StaysTheSameObjectAfterEviction()
    {
        OpenFile open = new(Create("open"));
        Ext2Inode held = (Ext2Inode)open.Inode;

        Fill();
        Collect();

        Assert.That(Lookup("open"), Is.SameAs(held));
        Assert.That(_sb.InodeCache.LiveCount, Is.LessThanOrEqualTo(Ext2InodeCache.Limit));
        GC.KeepAlive(open);
    }

    [Test]
    public void WriteThroughHandleAfterEviction_IsSeenByNewLookup()
    {
        OpenFile open = new(Create("open"));
        byte[] first = Pattern(3000, 1);
        Assert.That(open.Operations.Write(open, first), Is.EqualTo(first.Length));
        open.Position = first.Length;

        Fill();
        Collect();

        // A lookup in between must not leave a second, shorter object behind.
        Ext2Inode seen = Lookup("open");
        byte[] second = Pattern(5000, 77);
        Assert.That(open.Operations.Write(open, second), Is.EqualTo(second.Length));
        Assert.That(seen.FullSize, Is.EqualTo(8000));

        Fill("again");
        OpenFile other = new(seen);
        Assert.That(seen.InodeOperations.SetAttr(seen, SetAttrFlags.ModificationTime, new VfsStat { ModificationTime = new VfsTimespec(5, 0) }), Is.True);
        Assert.That(ReadAll(seen), Is.EqualTo(first.Concat(second).ToArray()));
        open.Operations.Release(open);

        Ext2Inode disk = Mount(MountFlags.ReadOnly).ReadInode(seen.InodeNumber, "probe");
        Assert.That(disk.FullSize, Is.EqualTo(8000));
        Assert.That(disk.Mtime, Is.EqualTo(5));
        Assert.That(ReadAll(disk), Is.EqualTo(first.Concat(second).ToArray()));
        GC.KeepAlive(other);
    }

    [Test]
    public void CollectedInode_IsReloadedWithItsContent()
    {
        byte[] data = Pattern(20_000, 9);
        WriteFile("data", data);

        Fill();
        Collect();

        Assert.That(ReadAll(Lookup("data")), Is.EqualTo(data));
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void WriteFile(string name, byte[] data)
    {
        OpenFile file = new(Create(name));
        Assert.That(file.Operations.Write(file, data), Is.EqualTo(data.Length));
    }

    [Test]
    public void UnlinkWhileOpenAfterEviction_HandleCannotTouchTheReusedInode()
    {
        OpenFile open = new(Create("open"));
        Ext2Inode held = (Ext2Inode)open.Inode;
        Assert.That(open.Operations.Write(open, Pattern(4000, 3)), Is.EqualTo(4000));
        uint number = held.InodeNumber;

        Fill();
        Assert.That(_root.InodeOperations.Unlink(_root, "open"), Is.True);
        Assert.That(held.LinksCount, Is.Zero, "the release reached the object the handle holds");

        // The lowest free number is handed out again.
        byte[] data = Pattern(6000, 50);
        WriteFile("new", data);
        Ext2Inode reused = Lookup("new");
        Assert.That(reused.InodeNumber, Is.EqualTo(number), "precondition: number reused");

        uint freeBlocks = _sb.FreeBlocksCount;
        open.Position = 0;
        Assert.That(open.Operations.Write(open, Pattern(2000, 200)), Is.Zero);
        open.Operations.Release(open);

        Assert.That(_sb.FreeBlocksCount, Is.EqualTo(freeBlocks), "no leaked blocks");
        Assert.That(Lookup("new"), Is.SameAs(reused));
        Assert.That(ReadAll(Lookup("new")), Is.EqualTo(data));
        Ext2Inode disk = Mount(MountFlags.ReadOnly).ReadInode(number, "probe");
        Assert.That(disk.LinksCount, Is.EqualTo(1));
        Assert.That(disk.Dtime, Is.Zero);
        Assert.That(ReadAll(disk), Is.EqualTo(data));
    }

    [Test]
    public void DirtyInode_IsNotEvicted()
    {
        uint number = MakeDirty("dirty");

        Fill();
        Collect();

        Assert.That(_sb.ReadInode(number, "dirty").Mtime, Is.EqualTo(4711), "the only copy of the unwritten field");
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private uint MakeDirty(string name)
    {
        Ext2Inode inode = Create(name);
        inode.Mtime = 4711; // not written back
        return inode.InodeNumber;
    }

    [Test]
    public void AllDirty_CacheGrowsInsteadOfEvicting()
    {
        const int Count = Ext2InodeCache.Limit + 100;
        uint[] numbers = new uint[Count];
        for (int i = 0; i < Count; i++)
        {
            numbers[i] = MakeDirty("d" + i);
        }

        Collect();
        foreach (uint number in numbers)
        {
            Assert.That(_sb.ReadInode(number, "d").Mtime, Is.EqualTo(4711));
        }
    }

    [Test]
    public void GrownByDirty_SearchIsBoundedAndShrinksAgain()
    {
        const int Count = Ext2InodeCache.Limit * 6;
        Ext2InodeCache cache = new(100_000);
        Ext2Inode[] made = new Ext2Inode[Count];
        for (int i = 0; i < Count; i++)
        {
            made[i] = new Ext2Inode(_sb, (uint)i + 100, "p") { Dirty = true };
            cache.Set(made[i]);
        }

        Assert.That(cache.LiveCount, Is.EqualTo(Count), "dirty inodes stay");
        Assert.That(cache.SearchSteps, Is.LessThanOrEqualTo(Count * 3L), "all dirty: constant cost per insertion");

        foreach (Ext2Inode inode in made)
        {
            inode.Dirty = false;
        }

        for (int i = 0; i < Count * 2; i++)
        {
            cache.Set(new Ext2Inode(_sb, (uint)(100_000 + i), "q") { Dirty = false });
        }

        Assert.That(cache.LiveCount, Is.LessThanOrEqualTo(Ext2InodeCache.Limit), "back below the limit");
        cache.Clear();
    }

    [Test]
    public void MountRefused_LeavesNothingInTheCache_AndDropFreesWeakEntries()
    {
        Fill();
        Assert.That(_sb.InodeCache.EvictedCount, Is.GreaterThan(0), "precondition");

        Assert.That(new Ext2FileSystemType(_device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out _), Is.False, "second writable mount");
        Assert.That(Mount(MountFlags.ReadOnly).InodeCache.EvictedCount, Is.Zero, "mounting reads at most the root, so a failed mount has no weak entry");

        _sb.SuperOperations.Drop(_sb);
        Assert.That(_sb.InodeCache.EvictedCount, Is.Zero);
    }

    [Test]
    public void SetOfNewObject_DetachesTheOldOne()
    {
        Ext2Inode old = Create("a");
        Ext2Inode fresh = new(_sb, old.InodeNumber, "b") { Mode = old.Mode, LinksCount = 1 };

        _sb.WriteInode(fresh);
        old.Mtime = 123;
        _sb.WriteInode(old);

        Assert.That(_sb.ReadInode(old.InodeNumber, "b"), Is.SameAs(fresh));
        Assert.That(Mount(MountFlags.ReadOnly).ReadInode(old.InodeNumber, "p").Mtime, Is.Not.EqualTo(123));
    }

    [Test]
    public void ParallelReads_YieldOneObjectPerNumber()
    {
        Ext2Inode[] made = new Ext2Inode[Fillers];
        for (int i = 0; i < Fillers; i++)
        {
            made[i] = new Ext2Inode(_sb, (uint)i + 100, "p") { Dirty = false };
        }

        Ext2InodeCache cache = new(100_000);
        int wrong = 0;
        Parallel.For(0, 8, worker =>
        {
            for (int round = 0; round < 20; round++)
            {
                for (int i = 0; i < Fillers; i++)
                {
                    Ext2Inode candidate = (worker & 1) == 0 ? made[i] : new Ext2Inode(_sb, (uint)i + 100, "q") { Dirty = false };
                    Ext2Inode got = cache.GetOrAdd(candidate);
                    if (!cache.TryGet((uint)i + 100, out Ext2Inode? again) || !ReferenceEquals(got, again))
                    {
                        Interlocked.Increment(ref wrong);
                    }
                }
            }
        });

        Assert.That(wrong, Is.Zero);
        Assert.That(cache.LiveCount, Is.LessThanOrEqualTo(Ext2InodeCache.Limit));
        cache.Clear();
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
