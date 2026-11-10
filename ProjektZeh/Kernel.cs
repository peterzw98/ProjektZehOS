using ProjektZeh.Runtime;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using Sys = Cosmos.Kernel.System;

namespace ProjektZeh;

public sealed class Kernel : Sys.Kernel
{
    private readonly CosmosStorageService storage = new();
    private readonly ShellService shell;
    private readonly ConsoleTextOutput output = new();
    private readonly InteractiveSession interactive;

    public Kernel()
    {
        var files = new FileService("/data");
        shell = new ShellService(
        [
            .. new FileShellCommands(files).Commands,
            .. new StorageShellCommands(storage).Commands
        ]);
        interactive = new InteractiveSession(shell, output);
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
        interactive.Run();
    }
}
