using System;
using Cosmos.Kernel.HAL.Devices.Storage;

namespace ProjektZeh.Storage.Ext2;

internal sealed class CosmosReadOnlyBlockDevice(IBlockDevice device) : IBlockDevice
{
    public ulong BlockSize => device.BlockSize;
    public ulong BlockCount => device.BlockCount;
    public string Name => device.Name;

    public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data)
    {
        // Die Cosmos-VFS ruft das unter der Sperre ihres Aufrufers; reentrant, damit auch ein direkter Aufruf gesperrt ist.
        using (StorageLock.Enter()) device.ReadBlock(blockNo, blockCount, data);
    }

    public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data) =>
        throw new UnauthorizedAccessException("ext2-Lesegerät: Schreiben gesperrt.");
    public void Flush() => throw new UnauthorizedAccessException("ext2-Lesegerät: Flush gesperrt.");
}
