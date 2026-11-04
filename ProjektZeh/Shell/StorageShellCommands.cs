using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Shell;

/// <summary>
/// Laufwerksverwaltung als ausdruecklich ausgeloeste Shelloperationen.
/// </summary>
internal sealed class StorageShellCommands
{
    private readonly IStorageService storage;

    public ShellCommand[] Commands { get; }

    public StorageShellCommands(IStorageService storage)
    {
        this.storage = storage;
        Commands =
        [
            new("disk", "disk list|init <Name>|mount <Name>|unmount",
                "Listet Laufwerke oder richtet eine leere 64-MiB-Testplatte ein.", true, Execute)
        ];
    }

    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        if (arguments.Length == 0)
        {
            return InvalidArguments();
        }

        switch (arguments[0])
        {
            case "list" when arguments.Length == 1:
                return ListDevices(output);
            case "init" when arguments.Length == 2:
                return storage.InitializeTestDisk(arguments[1]);
            case "mount" when arguments.Length == 2:
                return storage.Mount(arguments[1]);
            case "unmount" when arguments.Length == 1:
                return storage.Unmount();
            default:
                return InvalidArguments();
        }
    }

    private ResultErr ListDevices(ITextOutput output)
    {
        var result = storage.GetDevices();
        if (!result.IsOK())
        {
            return result.Status;
        }

        if (result.Value!.Length == 0)
        {
            output.WriteLine("Keine Laufwerke erkannt.");
        }

        foreach (StorageDeviceInfo device in result.Value)
        {
            output.WriteLine($"{device.Name}: {device.SizeBytes} Bytes, {device.PartitionCount} Partition(en)");
        }

        return ResultErr.CreateSuccess();
    }

    private static ResultErr InvalidArguments()
    {
        return ResultErr.CreateFailure(ResultCode.InvalidArgument,
            "cause='InvalidArguments' message='Verwendung: disk list|init <Name>|mount <Name>|unmount'");
    }
}
