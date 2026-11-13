using System.IO;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Update;

/// <summary>Bootpfade sind relativ zur unter /data eingebundenen EFI-Partition.</summary>
internal sealed class FileUpdateTarget(string root = "/data") : IUpdateTarget
{
    private readonly FileService files = new(root);
    public ResultErr ReplaceKernel(byte[] kernel) => files.WriteBytes(Path.Combine(root, "boot/ProjektZeh.elf"), kernel, true);
    public ResultErr WriteVersion(string version) => files.WriteText(Path.Combine(root, "boot/zehos-version.txt"), version + "\n");
}
