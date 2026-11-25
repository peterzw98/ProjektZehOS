// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-inode-cache-evict, see PATCHES.md

using System.Runtime.InteropServices;

namespace Cosmos.Kernel.System.FileSystem.Ext2;

/// <summary>
/// Bounded cache of a mount's inodes, keyed by inode number. Keeps at most
/// <see cref="Limit"/> clean inodes alive; the rest are evicted by second
/// chance (a hit sets <see cref="Ext2Inode.CacheReferenced"/>).
///
/// <para>One number never has two live objects. An evicted inode that a
/// handle, a directory listing or a running operation still references stays
/// reachable through a weak handle, and a lookup returns that same object.
/// An open inode is therefore never replaced by a second one read from disk,
/// and releasing it (unlink) still reaches the object the handle holds.
/// Reference counts would not do: directory handles and listings have no
/// close, and lookup handles are not always disposed.</para>
///
/// <para>An inode whose fields are ahead of the disk
/// (<see cref="Ext2Inode.Dirty"/>) is never evicted. When every entry is
/// dirty the cache grows instead.</para>
///
/// <para>The lock covers the bookkeeping only and is a leaf: no disk I/O and
/// no other lock inside. It is a monitor, not a spinlock, because entries and
/// weak handles allocate. It does not serialize operations on the volume;
/// callers do that, as before.</para>
/// </summary>
internal sealed class Ext2InodeCache
{
    /// <summary>
    /// Most clean inodes held alive per mount. Small objects allocated over
    /// time each can hold a 64 KiB heap segment, so the bound limits heap
    /// fragmentation (64 MiB at worst), not the bytes of the inodes.
    /// </summary>
    internal const int Limit = 1024;

    /// <summary>Weak entries from which dead ones are swept out.</summary>
    private const int WeakSweepMin = 4096;

    /// <summary>
    /// Presize of the weak table: growing copies it into one larger array,
    /// which a fragmented heap may not have room for. It only grows when this
    /// many evicted inodes are not collected yet.
    /// </summary>
    private const int WeakPresize = 16_384;

    private readonly object _gate = new();
    private readonly Dictionary<uint, Ext2Inode> _live;
    private readonly Dictionary<uint, GCHandle> _evicted;
    private readonly Stack<int> _freeSlots;
    private readonly List<uint> _sweep = [];
    private Ext2Inode?[] _slots;
    private int _hand;
    private int _sweepAt = WeakSweepMin;

    /// <summary>Creates the cache for a volume.</summary>
    /// <param name="inodesCount">Total inodes of the volume; a small one needs no full presize.</param>
    public Ext2InodeCache(uint inodesCount)
    {
        int capacity = (int)Math.Clamp(inodesCount, 1u, (uint)Limit);
        _live = new Dictionary<uint, Ext2Inode>(capacity);
        _evicted = new Dictionary<uint, GCHandle>((int)Math.Min(inodesCount, (uint)WeakPresize));
        _slots = new Ext2Inode?[capacity];
        _freeSlots = new Stack<int>(capacity);
        AddFreeSlots(0, capacity);
    }

    /// <summary>Inodes the cache keeps alive (test hook).</summary>
    internal int LiveCount => _live.Count;

    /// <summary>Weak entries, dead ones included until swept (test hook).</summary>
    internal int EvictedCount => _evicted.Count;

    /// <summary>Current capacity of both tables (test hook).</summary>
    internal int Capacity => _live.EnsureCapacity(0) + _evicted.EnsureCapacity(0);

    /// <summary>Finds the object for an inode number, evicted or not.</summary>
    public bool TryGet(uint number, [global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Ext2Inode? inode)
    {
        lock (_gate)
        {
            inode = Find(number);
            return inode is not null;
        }
    }

    /// <summary>
    /// Adds an inode just read from disk. When another caller was faster the
    /// object already known wins.
    /// </summary>
    public Ext2Inode GetOrAdd(Ext2Inode inode)
    {
        lock (_gate)
        {
            Ext2Inode? known = Find(inode.InodeNumber);
            if (known is not null)
            {
                return known;
            }

            Insert(inode);
            return inode;
        }
    }

    /// <summary>
    /// Makes <paramref name="inode"/> the object for its number (a new inode,
    /// or one written back). A different object known for the number describes
    /// a released inode and is cut off from the disk.
    /// </summary>
    public void Set(Ext2Inode inode)
    {
        lock (_gate)
        {
            Ext2Inode? known = Find(inode.InodeNumber);
            if (ReferenceEquals(known, inode))
            {
                return;
            }

            if (known is not null)
            {
                known.Detached = true;
                RemoveLive(known);
            }

            Insert(inode);
        }
    }

    /// <summary>
    /// Forgets a released inode number and cuts the object, which a handle may
    /// still hold, off from the disk: its number may be reused.
    /// </summary>
    public void Remove(uint number)
    {
        lock (_gate)
        {
            Ext2Inode? known = Find(number);
            if (known is not null)
            {
                known.Detached = true;
                RemoveLive(known);
            }
        }
    }

    /// <summary>Drops every entry (unmount).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (GCHandle handle in _evicted.Values)
            {
                handle.Free();
            }

            _evicted.Clear();
            _live.Clear();
            Array.Clear(_slots);
            _freeSlots.Clear();
            AddFreeSlots(0, _slots.Length);
            _hand = 0;
            _sweepAt = WeakSweepMin;
        }
    }

    private Ext2Inode? Find(uint number)
    {
        if (_live.TryGetValue(number, out Ext2Inode? inode))
        {
            inode.CacheReferenced = true;
            return inode;
        }

        if (!_evicted.TryGetValue(number, out GCHandle handle))
        {
            return null;
        }

        // The target is a strong reference from here on. Insert first: when it
        // fails for memory the weak entry still leads to the object.
        inode = (Ext2Inode?)handle.Target;
        if (inode is not null)
        {
            Insert(inode);
        }

        _evicted.Remove(number);
        handle.Free();
        return inode;
    }

    private void Insert(Ext2Inode inode)
    {
        if (_freeSlots.Count == 0 && !EvictOne())
        {
            int old = _slots.Length;
            Array.Resize(ref _slots, old * 2);
            AddFreeSlots(old, _slots.Length);
        }

        // Enter the dictionary first: it is the step that can allocate.
        _live.Add(inode.InodeNumber, inode);
        int slot = _freeSlots.Pop();
        _slots[slot] = inode;
        inode.CacheSlot = slot;
        inode.CacheReferenced = false;
    }

    /// <summary>Evicts one clean inode; false when all are dirty.</summary>
    private bool EvictOne()
    {
        // Two rounds: the first may only clear the referenced marks.
        for (int steps = _slots.Length * 2; steps > 0; steps--)
        {
            Ext2Inode? victim = _slots[_hand];
            _hand = (_hand + 1) % _slots.Length;
            if (victim is null || victim.Dirty)
            {
                continue;
            }

            if (victim.CacheReferenced)
            {
                victim.CacheReferenced = false;
                continue;
            }

            // Weak entry first: an allocation failure leaves the inode cached.
            SweepDead();
            GCHandle handle = GCHandle.Alloc(victim, GCHandleType.Weak);
            try
            {
                _evicted.Add(victim.InodeNumber, handle);
            }
            catch
            {
                handle.Free();
                throw;
            }

            RemoveLive(victim);
            return true;
        }

        return false;
    }

    /// <summary>Frees the weak entries of collected inodes once there are enough of them.</summary>
    private void SweepDead()
    {
        if (_evicted.Count < _sweepAt)
        {
            return;
        }

        _sweep.Clear();
        foreach (KeyValuePair<uint, GCHandle> entry in _evicted)
        {
            if (entry.Value.Target is null)
            {
                _sweep.Add(entry.Key);
            }
        }

        foreach (uint number in _sweep)
        {
            _evicted.Remove(number, out GCHandle handle);
            handle.Free();
        }

        _sweep.Clear();

        // Doubling keeps the scan amortized while few inodes are collected.
        _sweepAt = Math.Max(WeakSweepMin, _evicted.Count * 2);
    }

    private void RemoveLive(Ext2Inode inode)
    {
        _live.Remove(inode.InodeNumber);
        _slots[inode.CacheSlot] = null;
        _freeSlots.Push(inode.CacheSlot);
    }

    private void AddFreeSlots(int from, int to)
    {
        for (int slot = to - 1; slot >= from; slot--)
        {
            _freeSlots.Push(slot);
        }
    }
}
