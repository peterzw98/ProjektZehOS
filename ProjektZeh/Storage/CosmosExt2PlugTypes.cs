// Attrappentypen fuer CosmosExt2RemoveEntryPlug (eigene Datei, weil der Namespace den Cosmos-Typen entsprechen muss).
namespace Cosmos.Kernel.System.FileSystem.Ext2;

// Nur fuer die Plug-Signatur: gleichnamig zu den internen Cosmos-Typen, damit der Patcher RemoveEntry findet. Nach dem
// Patchen traegt die Cosmos-DLL wieder ihre eigenen Typen; ZehOS-Code verwendet diese Attrappen nirgends.
public sealed class Ext2Superblock
{
    private Ext2Superblock()
    {
    }
}

public sealed class Ext2Inode
{
    private Ext2Inode()
    {
    }
}
