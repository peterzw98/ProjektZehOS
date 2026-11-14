using System.IO;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Update;

/// <summary>Bootpfade sind relativ zur unter /data eingebundenen EFI-Partition.</summary>
internal sealed class FileUpdateTarget(string root = "/data") : IUpdateTarget
{
    private readonly FileService files = new(root);
    public ResultErr ReplaceKernel(byte[] kernel, bool rotateBackup) => files.WriteBytes(Path.Combine(root, "boot/ProjektZeh.elf"), kernel, rotateBackup);
    public ResultErr WriteVersion(string version) => files.WriteText(VersionPath, version + "\n");
    public ResultObject<string> ReadVersion() => files.ReadText(VersionPath, allowMissing: true);
    private string VersionPath => Path.Combine(root, "boot/zehos-version.txt");
}
