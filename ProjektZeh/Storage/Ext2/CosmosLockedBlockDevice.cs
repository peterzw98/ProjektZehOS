using System;
using Cosmos.Kernel.HAL.Devices.Storage;

namespace ProjektZeh.Storage.Ext2;

/// <summary>
/// Führt jede Plattenoperation unter der <see cref="StorageLock"/> aus. Der Cosmos-Formatter kennt die Sperre nicht;
/// gesperrt wird bewusst je einzelner Operation und nicht um das ganze Formatieren (Regel in <see cref="StorageLock"/>),
/// damit die Hauptschleife bei großen Geräten nicht wartet.
/// </summary>
internal sealed class CosmosLockedBlockDevice(IBlockDevice device) : IBlockDevice
{
    public string Name => device.Name;

    public ulong BlockSize => device.BlockSize;

    public ulong BlockCount => device.BlockCount;

    public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data)
    {
        using (StorageLock.Enter()) device.ReadBlock(blockNo, blockCount, data);
    }

    public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
    {
        using (StorageLock.Enter()) device.WriteBlock(blockNo, blockCount, data);
    }

    public void Flush()
    {
        using (StorageLock.Enter()) device.Flush();
    }
}
