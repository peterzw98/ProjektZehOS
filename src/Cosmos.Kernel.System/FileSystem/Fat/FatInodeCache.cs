// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch fat-inode-cache, see PATCHES.md

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Cosmos.Kernel.System.FileSystem.Fat;

/// <summary>
/// A mount's inodes by first cluster, held weakly: one live object per file
/// as long as somebody (handle, listing, child inode, running operation)
/// references it, and nothing kept alive beyond that.
///
/// <para>Holding them strongly is not needed. A lookup re-reads the directory
/// entry anyway and <see cref="FatSuperblock.GetOrCreateInode"/> overwrites
/// the fields from it; only the cached cluster chain is rebuilt after a
/// collection. An inode that is ahead of its entry has a holder.</para>
///
/// <para>The lock covers the bookkeeping only and is a leaf: no disk I/O and
/// no other lock inside. It does not serialize operations on the volume;
/// callers do that, as before.</para>
/// </summary>
internal sealed class FatInodeCache
{
    /// <summary>Entries from which dead ones are swept out.</summary>
    internal const int SweepMin = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<uint, GCHandle> _entries = new(SweepMin);
    private readonly List<uint> _sweep = [];
    private int _sweepAt = SweepMin;

    // ZehOS patch fat-rename-open, see PATCHES.md
    // Inodes without a first cluster (empty files) by directory slot, weakly:
    // renaming or removing the entry has to find the object an open handle
    // uses. Same lock as the cluster entries: both tables change together.
    internal const int EmptySweepMin = 256;
    private readonly Dictionary<ulong, GCHandle> _empty = [];
    private readonly List<ulong> _emptySweep = [];
    private int _emptySweepAt = EmptySweepMin;

    /// <summary>Entries, dead ones included until swept (test hook).</summary>
    internal int Count => _entries.Count;

    /// <summary>Slot entries of empty files, dead ones included until swept (test hook).</summary>
    internal int EmptyCount => _empty.Count;

    /// <summary>Finds the live object for a first cluster.</summary>
    public bool TryGet(uint cluster, [NotNullWhen(true)] out FatInode? inode)
    {
        inode = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(cluster, out GCHandle handle))
            {
                inode = (FatInode?)handle.Target;
            }
        }

        return inode is not null;
    }

    /// <summary>Makes <paramref name="inode"/> the object for a first cluster.</summary>
    public void Set(uint cluster, FatInode inode)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(cluster, out GCHandle old))
            {
                if (!ReferenceEquals(old.Target, inode))
                {
                    // Reuse the weak handle: no allocation that could fail.
                    old.Target = inode;
                }

                return;
            }

            SweepDead();
            GCHandle handle = GCHandle.Alloc(inode, GCHandleType.Weak);
            try
            {
                _entries.Add(cluster, handle);
            }
            catch
            {
                handle.Free();
                throw;
            }
        }
    }

    /// <summary>Forgets a first cluster whose chain was released or moved to another entry.</summary>
    public void Remove(uint cluster)
    {
        lock (_gate)
        {
            if (_entries.Remove(cluster, out GCHandle handle))
            {
                handle.Free();
            }
        }
    }

    /// <summary>Finds the live empty-file object for a directory slot.</summary>
    public bool TryGetEmpty(ulong slot, [NotNullWhen(true)] out FatInode? inode)
    {
        inode = null;
        lock (_gate)
        {
            if (_empty.TryGetValue(slot, out GCHandle handle))
            {
                inode = (FatInode?)handle.Target;
            }
        }

        return inode is not null;
    }

    /// <summary>Makes <paramref name="inode"/> the empty-file object for a directory slot.</summary>
    public void SetEmpty(ulong slot, FatInode inode)
    {
        lock (_gate)
        {
            if (_empty.TryGetValue(slot, out GCHandle old))
            {
                old.Target = inode;
                return;
            }

            SweepDeadEmpty();
            GCHandle handle = GCHandle.Alloc(inode, GCHandleType.Weak);
            try
            {
                _empty.Add(slot, handle);
            }
            catch
            {
                handle.Free();
                throw;
            }
        }
    }

    /// <summary>Forgets a directory slot (entry removed, moved or given a first cluster).</summary>
    public void RemoveEmpty(ulong slot)
    {
        lock (_gate)
        {
            if (_empty.Remove(slot, out GCHandle handle))
            {
                handle.Free();
            }
        }
    }

    /// <summary>Drops every entry (unmount).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (GCHandle handle in _entries.Values)
            {
                handle.Free();
            }

            _entries.Clear();
            _sweepAt = SweepMin;

            foreach (GCHandle handle in _empty.Values)
            {
                handle.Free();
            }

            _empty.Clear();
            _emptySweepAt = EmptySweepMin;
        }
    }

    /// <summary>Frees the slot entries of collected empty-file inodes once there are enough of them.</summary>
    private void SweepDeadEmpty()
    {
        if (_empty.Count < _emptySweepAt)
        {
            return;
        }

        _emptySweep.Clear();
        foreach (KeyValuePair<ulong, GCHandle> entry in _empty)
        {
            if (entry.Value.Target is null)
            {
                _emptySweep.Add(entry.Key);
            }
        }

        foreach (ulong slot in _emptySweep)
        {
            _empty.Remove(slot, out GCHandle handle);
            handle.Free();
        }

        _emptySweep.Clear();

        // Doubling keeps the scan amortized.
        _emptySweepAt = Math.Max(EmptySweepMin, _empty.Count * 2);
    }

    /// <summary>Frees the entries of collected inodes once there are enough of them.</summary>
    private void SweepDead()
    {
        if (_entries.Count < _sweepAt)
        {
            return;
        }

        _sweep.Clear();
        foreach (KeyValuePair<uint, GCHandle> entry in _entries)
        {
            if (entry.Value.Target is null)
            {
                _sweep.Add(entry.Key);
            }
        }

        foreach (uint cluster in _sweep)
        {
            _entries.Remove(cluster, out GCHandle handle);
            handle.Free();
        }

        _sweep.Clear();

        // Doubling keeps the scan amortized while few inodes are collected.
        _sweepAt = Math.Max(SweepMin, _entries.Count * 2);
    }
}
