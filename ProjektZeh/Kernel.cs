using System;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using Sys = Cosmos.Kernel.System;

namespace ProjektZeh;

public sealed class Kernel : Sys.Kernel
{
    private readonly CosmosStorageService storage = new();
    private readonly ShellService shell;
    private readonly ConsoleTextOutput output = new();

    public Kernel()
    {
        var files = new FileService("/data");
        shell = new ShellService(
        [
            .. new FileShellCommands(files).Commands,
            .. new StorageShellCommands(storage).Commands
        ]);
    }

    protected override void BeforeRun()
    {
        output.WriteLine("ZehOS Shell - 'help' zeigt die verfuegbaren Befehle.");
        var mounted = storage.Mount("sata0");
        if (mounted.IsOK())
        {
            output.WriteLine("Datenlaufwerk unter /data eingebunden.");
        }
        else
        {
            output.WriteLine(mounted.Message);
            output.WriteLine("Laufwerke: disk list. Leere Testplatte: disk init sata0, dann disk mount sata0.");
        }
    }

    protected override void Run()
    {
        Console.Write("zehos> ");
        var result = shell.ExecuteLine(Console.ReadLine(), output);
        if (!result.IsOK())
        {
            output.WriteLine(result.Message);
        }
    }
}
