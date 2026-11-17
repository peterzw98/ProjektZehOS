using ProjektZeh.Shared;
using ProjektZeh.Storage;
using System;

namespace ProjektZeh.Shell;

/// <summary>
/// Laufwerksverwaltung als ausdruecklich ausgeloeste Shelloperationen.
/// </summary>
internal sealed class StorageShellCommands
{
    private readonly IStorageService storage;
    private readonly Func<bool>? isAdministrator;
    private readonly Action? storageChanged;
    private readonly Action<string>? commandLog;

    public ShellCommand[] Commands { get; }

    public StorageShellCommands(IStorageService storage, Func<bool>? isAdministrator = null, Action? storageChanged = null, Action<string>? commandLog = null)
    {
        this.storage = storage;
        this.isAdministrator = isAdministrator;
        this.storageChanged = storageChanged;
        this.commandLog = commandLog;
        Commands =
        [
            new("disk", "disk list|init <Name> [--yes-large] [--yes-overwrite]|use <Name> --yes|mount sata0|mount <Name> --yes|unmount",
                "Listet Laufwerke oder richtet eine leere Platte (64 MiB bis 128 GiB) als Datenplatte ZEHOSDATA ein.", true, Execute)
        ];
    }

    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        // Eingangsquittung und Fehlertext fuer Gasttests: die Shell-Ausgabe erscheint nur auf dem Bildschirm.
        commandLog?.Invoke("disk " + string.Join(' ', arguments));
        var result = Run(arguments, output);
        if (!result.IsOK()) commandLog?.Invoke("Fehler: " + result.Message);
        return result;
    }

    private ResultErr Run(string[] arguments, ITextOutput output)
    {
        if (isAdministrator != null && !isAdministrator()) return ResultErr.CreateFailure(ResultCode.Failure, "Laufwerksverwaltung erfordert Administratorrechte.");
        if (arguments.Length == 0)
        {
            return InvalidArguments();
        }

        switch (arguments[0])
        {
            case "list" when arguments.Length == 1:
                return ListDevices(output);
            case "init" when arguments.Length >= 2 && TryParseInitFlags(arguments, out bool confirmLarge, out bool confirmOverwrite):
            {
                var initialized = Changed(storage.InitializeTestDisk(arguments[1], confirmLarge, confirmOverwrite));
                if (initialized.IsOK() && confirmLarge)
                    output.WriteLine("Hinweis: Die Platte wurde nur stichprobenartig auf Leere geprueft; Daten ZWISCHEN den Stichproben (ueber 4 GiB) wurden nicht erkannt.");
                return initialized;
            }
            case "use" when arguments.Length == 3 && arguments[2] == "--yes":
                return Changed(storage.UseDataDisk(arguments[1]));
            // Nur der sata0-Bestand wird ohne Bestaetigung eingebunden; jede andere Platte wie bei use nur mit --yes.
            case "mount" when arguments.Length == 2 && string.Equals(arguments[1], DataDiskSelector.ExistingDevice, StringComparison.OrdinalIgnoreCase):
            case "mount" when arguments.Length == 3 && arguments[2] == "--yes":
                return Changed(storage.Mount(arguments[1]));
            case "unmount" when arguments.Length == 1:
                return Changed(storage.Unmount());
            default:
                return InvalidArguments();
        }
    }

    /// <summary>Hinter dem Namen sind nur --yes-large und --yes-overwrite erlaubt, jedes hoechstens einmal, in beliebiger Reihenfolge.</summary>
    private static bool TryParseInitFlags(string[] arguments, out bool confirmLarge, out bool confirmOverwrite)
    {
        confirmLarge = confirmOverwrite = false;
        for (int index = 2; index < arguments.Length; index++)
        {
            if (arguments[index] == "--yes-large" && !confirmLarge) confirmLarge = true;
            else if (arguments[index] == "--yes-overwrite" && !confirmOverwrite) confirmOverwrite = true;
            else return false;
        }

        return true;
    }

    private ResultErr Changed(ResultErr result)
    {
        if (result.IsOK()) storageChanged?.Invoke();
        return result;
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
            "cause='InvalidArguments' message='Verwendung: disk list|init <Name> [--yes-large] [--yes-overwrite]|use <Name> --yes|mount sata0|mount <Name> --yes|unmount'");
    }
}
