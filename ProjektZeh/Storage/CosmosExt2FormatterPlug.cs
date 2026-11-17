using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem.Ext2;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

// shortcut: Cosmos 3.0.97 formatiert ext2 nicht konsistent (Storage/Ext2/README.md); nach einem korrigierten Cosmos-Release entfernen.
// Liegt bewusst ausserhalb von Storage/Ext2/, weil die Hostprobe jene Quellen per Glob ohne Cosmos-Build-API einbindet.
[Plug(TargetName = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Formatter")]
public static class CosmosExt2FormatterPlug
{
    // Auch Ext2FileSystemType.TryFormat landet hier: im Kernel nur das 4-KiB-Profil.
    // Der Koerper darf keinen Typ aus Cosmos.Kernel.System nennen (vgl. CosmosMousePlug): Aufruf nur ueber object.
    [PlugMember]
    public static bool Format(IBlockDevice device, Ext2FormatOptions? options) =>
        CosmosExt2Formatter.FormatForKernel(device, options);
}
