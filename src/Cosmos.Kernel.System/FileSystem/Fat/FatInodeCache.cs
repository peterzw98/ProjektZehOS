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

    /// <summary>Entries, dead ones included until swept (test hook).</summary>
    internal int Count => _entries.Count;

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
        }
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
