using System;
using System.IO;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Update;

/// <summary>Bootpfade sind relativ zur EFI-Partition (Legacy: /data, Root-Profil: /boot/efi).</summary>
/// <param name="persist">Root-Profil: Sync der ESP nach dem Austausch (Spec 7b §0.3); Legacy braucht keinen.</param>
/// <param name="pathGuard">Root-Profil: Waechter fuer /boot/efi (Spec 7b §1), sperrt Symlinks und fremde Mounts.</param>
/// <param name="isMounted">Root-Profil: ohne eingehaengte ESP landeten Marker und Kernel im Verzeichnis /boot/efi des Root.</param>
internal sealed class FileUpdateTarget(string root = "/data", Func<string, int?>? directoryEntryLimit = null,
    Func<IDisposable?>? tryBeginWrite = null, Func<ResultErr>? persist = null, Func<string, bool, ResultErr>? pathGuard = null,
    Func<bool>? isMounted = null) : IUpdateTarget
{
    private readonly FileService files = new(root, pathGuard: pathGuard, directoryEntryLimit: directoryEntryLimit, tryBeginWrite: tryBeginWrite);
    public ResultErr Persist() => persist?.Invoke() ?? ResultErr.CreateSuccess();
    public ResultErr ReplaceKernel(byte[] kernel, bool rotateBackup) =>
        Missing() ?? files.WriteBytes(Path.Combine(root, "boot/ProjektZeh.elf"), kernel, rotateBackup);
    public ResultErr WriteVersion(string version) => Missing() ?? files.WriteText(VersionPath, version + "\n");
    public ResultObject<string> ReadVersion() => files.ReadText(VersionPath, allowMissing: true);
    private string VersionPath => Path.Combine(root, "boot/zehos-version.txt");

    private ResultErr? Missing() => isMounted == null || isMounted() ? null
        : ResultErr.CreateFailure(ResultCode.Failure, $"cause='UpdateRootMissing' message='{root} ist nicht eingehaengt; Updates gesperrt.'");
}
