using System;
using System.IO;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Update;

/// <summary>Bootpfade sind relativ zur EFI-Partition (Legacy: /data, Root-Profil: /boot/efi).</summary>
/// <param name="persist">Root-Profil: Sync der ESP nach dem Austausch (Spec 7b §0.3); Legacy braucht keinen.</param>
internal sealed class FileUpdateTarget(string root = "/data", Func<string, int?>? directoryEntryLimit = null,
    Func<IDisposable?>? tryBeginWrite = null, Func<ResultErr>? persist = null) : IUpdateTarget
{
    public ResultErr Persist() => persist?.Invoke() ?? ResultErr.CreateSuccess();
    private readonly FileService files = new(root, directoryEntryLimit: directoryEntryLimit, tryBeginWrite: tryBeginWrite);
    public ResultErr ReplaceKernel(byte[] kernel, bool rotateBackup) => files.WriteBytes(Path.Combine(root, "boot/ProjektZeh.elf"), kernel, rotateBackup);
    public ResultErr WriteVersion(string version) => files.WriteText(VersionPath, version + "\n");
    public ResultObject<string> ReadVersion() => files.ReadText(VersionPath, allowMissing: true);
    private string VersionPath => Path.Combine(root, "boot/zehos-version.txt");
}
