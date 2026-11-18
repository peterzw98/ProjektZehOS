using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using ProjektZeh.Storage.Ext2;

internal static class BoundaryProbe
{
    internal static int Run(string directory, string target)
    {
        Directory.CreateDirectory(directory);
        string before = Path.Combine(directory, "before.img");
        string after = Path.Combine(directory, "after.img");
        using var file = new FileStream(after, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        file.SetLength(32L * 1024 * 1024);
        var device = new FileBlockDevice(file, "boundary-probe");
        if (!CosmosExt2Formatter.FormatForKernel(device, null)) throw new InvalidOperationException("Formatieren fehlgeschlagen");
        var fs = new Ext2FileSystemType(device);
        if (!fs.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out var mounted)) throw new InvalidOperationException("Mount fehlgeschlagen");
        var root = mounted!.Root;
        if (!root.InodeOperations.Mkdir(root, "span", VfsMode.OwnerRead | VfsMode.OwnerWrite | VfsMode.OwnerExecute, out var dir))
            throw new InvalidOperationException("Mkdir fehlgeschlagen");
        for (int i = 0; i < 600; i++)
        {
            string name = $"n{i:D4}";
            if (!dir!.InodeOperations.Create(dir, name, VfsMode.OwnerRead | VfsMode.OwnerWrite, out _))
                throw new InvalidOperationException("Create fehlgeschlagen: " + name);
        }
        if (!mounted.SuperOperations.Sync(mounted)) throw new InvalidOperationException("Sync fehlgeschlagen");
        file.Flush();
        File.Copy(after, before, false);
        if (!dir!.InodeOperations.Unlink(dir, target)) throw new InvalidOperationException("Unlink fehlgeschlagen");
        if (!mounted.SuperOperations.Sync(mounted)) throw new InvalidOperationException("Sync nach Unlink fehlgeschlagen");
        mounted.SuperOperations.Drop(mounted);
        file.Flush();
        Console.WriteLine("Images: " + before + " " + after);
        return 0;
    }
}
