// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-inode-cache-evict, see PATCHES.md

using System;
using System.Numerics;
using System.Threading;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using Cosmos.Kernel.System.Threading;
using Cosmos.TestRunner.Framework;
using SysThread = System.Threading.Thread;

namespace Cosmos.Kernel.Tests.Smp;

/// <summary>
/// The ext2 inode cache under threads on several CPUs while collections run:
/// its monitor, its weak GC handles and the objects they lead to.
/// </summary>
internal static class Ext2InodeCacheCells
{
    private const int Threads = 4;
    private const int Numbers = Ext2InodeCache.Limit * 3;
    private const int FilesPerThread = 600;

    /// <summary>A cell starts no new work after this; the engine takes ten silent seconds for a hang.</summary>
    private const int WorkMs = 2500;

    private const int JoinTimeoutMs = 120_000;
    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;

    private static readonly object s_volume = new();
    private static Ext2Superblock? s_sb;
    private static Ext2InodeCache? s_cache;
    private static int s_wrong;
    private static int s_failed;
    private static int s_done;
    private static int s_cpus;
    private static int s_evictedSeen;
    private static int s_next;
    private static int s_created;
    private static long s_until;

    /// <summary>Four threads look up, add, drop and replace the same numbers without any outer lock.</summary>
    internal static void CacheAlone()
    {
        Mount();
        s_cache = new Ext2InodeCache(100_000);
        RunThreads(CacheWorker);
        Assert.True(s_cache.LiveCount <= Ext2InodeCache.Limit, "the limit holds");
        s_cache.Clear();
        Assert.Equal(0, s_cache.EvictedCount, "no weak entry left");
        Unmount();
    }

    /// <summary>Four threads create, write, read, delete files; the volume is locked per operation, as ZehOS does.</summary>
    internal static void Files()
    {
        Mount();
        RunThreads(FileWorker);
        Assert.True(s_evictedSeen > 0, "inodes were evicted");
        Unmount();
    }

    private static void RunThreads(ThreadStart body)
    {
        s_wrong = s_failed = s_done = s_cpus = s_evictedSeen = s_next = s_created = 0;
        s_until = Environment.TickCount64 + WorkMs;
        int collections = GC.CollectionCount(0);
        SysThread[] threads = new SysThread[Threads];
        for (int i = 0; i < Threads; i++)
        {
            threads[i] = new SysThread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref s_failed);
                }

                Interlocked.Increment(ref s_done);
            });
            threads[i].Start();
        }

        long deadline = Environment.TickCount64 + JoinTimeoutMs;
        while (Volatile.Read(ref s_done) < Threads && Environment.TickCount64 < deadline)
        {
            GC.Collect();
            SysThread.Sleep(20);
        }

        Assert.Equal(Threads, s_done, "every worker finishes");
        foreach (SysThread thread in threads)
        {
            thread.Join();
        }

        Assert.Equal(0, s_failed, "no worker threw");
        Assert.Equal(0, s_wrong, "one object per number, contents intact");
        Assert.True(GC.CollectionCount(0) - collections >= 3, "collections ran meanwhile");
        Assert.True(BitOperations.PopCount((uint)s_cpus) >= 2, "the workers ran on several CPUs");
    }

    private static void NoteCpu() => Interlocked.Or(ref s_cpus, 1 << (int)KernelCpus.Current);

    private static void CacheWorker()
    {
        Ext2InodeCache cache = s_cache!;
        uint seed = (uint)Interlocked.Increment(ref s_next) * 2654435761u;
        Ext2Inode?[] held = new Ext2Inode?[64];
        for (int step = 0; (step & 255) != 0 || Environment.TickCount64 < s_until; step++)
        {
            seed = seed * 1664525 + 1013904223;
            uint number = 100 + (seed >> 8) % Numbers;

            // Numbers divisible by 16 are removed again and again; the rest
            // must keep one object for as long as somebody holds it.
            if ((number & 15) == 0)
            {
                if ((step & 3) == 0)
                {
                    cache.Remove(number);
                }
                else if (cache.GetOrAdd(New(number)).InodeNumber != number)
                {
                    Interlocked.Increment(ref s_wrong);
                }

                continue;
            }

            Ext2Inode got = cache.GetOrAdd(New(number));
            if (got.InodeNumber != number || got.Detached
                || !cache.TryGet(number, out Ext2Inode? again) || !ReferenceEquals(again, got))
            {
                Interlocked.Increment(ref s_wrong);
            }

            held[step & 63] = got;
            Ext2Inode? old = held[(step + 1) & 63];
            if (old is not null && (!cache.TryGet(old.InodeNumber, out Ext2Inode? still) || !ReferenceEquals(still, old)))
            {
                Interlocked.Increment(ref s_wrong);
            }

            if ((step & 255) == 0)
            {
                NoteCpu();
            }
        }
    }

    private static Ext2Inode New(uint number) => new(s_sb!, number, "p") { Dirty = false };

    private static void FileWorker()
    {
        int id = Interlocked.Increment(ref s_next);
        byte[] data = new byte[700];
        byte[] back = new byte[700];
        Ext2Inode root = (Ext2Inode)s_sb!.Root;
        for (int round = 0; round == 0 || Environment.TickCount64 < s_until; round++)
        {
            Ext2Inode[] open = new Ext2Inode[8];
            for (int i = 0; i < FilesPerThread; i++)
            {
                NoteCpu();
                Array.Fill(data, (byte)(id * 31 + i + round));
                lock (s_volume)
                {
                    if (!root.InodeOperations.Create(root, Name(id, i), FileMode, out IVfsInode? inode)
                        || inode.FileOperations!.Write(new OpenFile((Ext2Inode)inode), data) != data.Length)
                    {
                        Interlocked.Increment(ref s_wrong);
                        return;
                    }

                    open[i & 7] = (Ext2Inode)inode;
                    Interlocked.Add(ref s_evictedSeen, s_sb.InodeCache.EvictedCount);
                }
            }

            // All files of the first round exist at once, more than the limit.
            if (round == 0)
            {
                Interlocked.Increment(ref s_created);
                while (Volatile.Read(ref s_created) < Threads && s_wrong + s_failed == 0)
                {
                    SysThread.Sleep(1);
                }
            }

            for (int i = 0; i < FilesPerThread; i++)
            {
                NoteCpu();
                lock (s_volume)
                {
                    if (!root.InodeOperations.Lookup(root, Name(id, i), out IVfsInode? inode)
                        || inode.FileOperations!.Read(new OpenFile((Ext2Inode)inode), back) != back.Length
                        || back.AsSpan().ContainsAnyExcept((byte)(id * 31 + i + round))
                        || (i >= FilesPerThread - 8 && !ReferenceEquals(inode, open[i & 7]))
                        || !root.InodeOperations.Unlink(root, Name(id, i)))
                    {
                        Interlocked.Increment(ref s_wrong);
                        return;
                    }
                }
            }

            // The deleted files are still held: they must not write again.
            foreach (Ext2Inode gone in open)
            {
                lock (s_volume)
                {
                    if (gone.FileOperations!.Write(new OpenFile(gone), data) != 0)
                    {
                        Interlocked.Increment(ref s_wrong);
                    }
                }
            }
        }
    }

    private static string Name(int id, int i) => "t" + id.ToString() + "_" + i.ToString();

    private static void Mount()
    {
        RamDisk disk = new(24 * 1024 * 1024);
        IVfsSuperblock? sb = null;

        // An assert of this framework does not end the cell.
        if (!new Ext2FileSystemType(disk).TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = 2048 })
            || !new Ext2FileSystemType(disk).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out sb))
        {
            throw new InvalidOperationException("no volume");
        }

        s_sb = (Ext2Superblock)sb;
    }

    private static void Unmount()
    {
        s_sb!.SuperOperations.Drop(s_sb);
        Assert.Equal(0, s_sb.InodeCache.EvictedCount, "unmounting frees the weak entries");
        s_sb = null;
        s_cache = null;
    }

    private sealed class OpenFile(Ext2Inode inode) : IVfsOpenFile
    {
        public string Name => "open";
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.FileOperations!;
        public long Position { get; set; }
    }

    private sealed class RamDisk(int bytes) : IBlockDevice
    {
        private readonly byte[] _storage = new byte[bytes];
        public ulong BlockCount => (ulong)_storage.Length / 512;
        public ulong BlockSize => 512;
        public string Name => "ram0";
        public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data) => _storage.AsSpan((int)blockNo * 512, (int)blockCount * 512).CopyTo(data);
        public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data) => data[..((int)blockCount * 512)].CopyTo(_storage.AsSpan((int)blockNo * 512));
        public void Flush() { }
    }
}
