using System;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Installer;

internal interface IInstallTarget
{
    ResultErr Partition();
    ResultErr Format();
    ResultErr Complete();
    ResultErr Abort();
    ResultErr WriteFileChunk(string path, byte[] data, int offset, int count, bool first, bool last);
}

internal sealed class InstallJob : IInstallJob
{
    private const int ChunkSize = 64 * 1024;
    private readonly IInstallTarget target;
    // Die .bak-Kopie macht den Rueckfalleintrag schon vor dem ersten Update startfaehig.
    private readonly string[] paths = ["/EFI/BOOT/BOOTX64.EFI", "/boot/ProjektZeh.elf", "/boot/ProjektZeh.elf.bak", "/boot/zehos-version.txt"];
    private readonly byte[][] files;
    private int fileIndex;
    private int fileOffset;
    private ResultErr? failure;
    private static readonly byte[] BootConfig = Encoding.UTF8.GetBytes(
        // Wie das ISO: Full HD als Standard, 1024x768 als sicherer Rueckfall fuer Anzeigen ohne 1920x1080.
        "timeout: 3\n\n/ZehOS\n    protocol: limine\n    path: boot():/boot/ProjektZeh.elf\n    resolution: 1920x1080x32\n\n" +
        "/ZehOS (1024x768)\n    protocol: limine\n    path: boot():/boot/ProjektZeh.elf\n    resolution: 1024x768x32\n\n" +
        "/ZehOS (vorherige Version)\n    protocol: limine\n    path: boot():/boot/ProjektZeh.elf.bak\n    resolution: 1024x768x32\n");

    public InstallJob(IInstallTarget target, byte[] efi, byte[] kernel, string version)
    {
        this.target = target;
        files = [efi, kernel, kernel, Encoding.UTF8.GetBytes(version + "\n")];
    }

    public InstallStage Stage { get; private set; } = InstallStage.Partitioning;
    public int Percent => Stage switch
    {
        InstallStage.Partitioning => 0,
        InstallStage.Formatting => 10,
        InstallStage.CopyingFiles => 20 + (int)(70L * BytesCopied() / TotalBytes()),
        InstallStage.WritingBootConfig => 90,
        _ => 100
    };
    public bool IsCompleted => Stage == InstallStage.Completed;

    public ResultErr Step()
    {
        if (failure != null) return failure;
        ResultErr result;
        switch (Stage)
        {
            case InstallStage.Partitioning:
                result = target.Partition();
                if (result.IsOK()) Stage = InstallStage.Formatting;
                break;
            case InstallStage.Formatting:
                result = target.Format();
                if (result.IsOK()) Stage = InstallStage.CopyingFiles;
                break;
            case InstallStage.CopyingFiles:
                byte[] file = files[fileIndex];
                int count = Math.Min(ChunkSize, file.Length - fileOffset);
                result = target.WriteFileChunk(paths[fileIndex], file, fileOffset, count, fileOffset == 0, fileOffset + count == file.Length);
                if (result.IsOK())
                {
                    fileOffset += count;
                    if (fileOffset == file.Length)
                    {
                        fileIndex++;
                        fileOffset = 0;
                        if (fileIndex == files.Length) Stage = InstallStage.WritingBootConfig;
                    }
                }
                break;
            case InstallStage.WritingBootConfig:
                result = target.WriteFileChunk("/boot/limine/limine.conf", BootConfig, 0, BootConfig.Length, true, true);
                if (result.IsOK()) result = target.Complete();
                if (result.IsOK()) Stage = InstallStage.Completed;
                break;
            default:
                return ResultErr.CreateSuccess();
        }
        if (!result.IsOK())
        {
            var cleanup = target.Abort();
            failure = cleanup.IsOK() ? result : ResultErr.CreateFailure(ResultCode.Failure,
                result + " Bereinigung fehlgeschlagen: " + cleanup);
            return failure;
        }
        return result;
    }

    private long TotalBytes() => (long)files[0].Length + files[1].Length + files[2].Length;
    private long BytesCopied()
    {
        long total = fileOffset;
        for (int index = 0; index < fileIndex; index++) total += files[index].Length;
        return total;
    }
}
