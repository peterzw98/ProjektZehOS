using System;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <summary>
/// Neustart und Ausschalten nach erfolgreicher Vorbereitung der Datentraeger.
/// </summary>
internal sealed class PowerShellCommands
{
    private readonly Func<ResultErr> prepare;
    private readonly Action reboot;
    private readonly Action shutdown;

    public ShellCommand[] Commands { get; }

    public PowerShellCommands(Func<ResultErr> prepare, Action reboot, Action shutdown)
    {
        this.prepare = prepare;
        this.reboot = reboot;
        this.shutdown = shutdown;
        Commands =
        [
            new("reboot", "reboot", "Startet ZehOS neu.", false,
                (_, output) => Execute(this.reboot, "ZehOS wird neu gestartet.", output)),
            new("shutdown", "shutdown", "Schaltet den Rechner aus.", false,
                (_, output) => Execute(this.shutdown, "ZehOS wird ausgeschaltet.", output))
        ];
    }

    private ResultErr Execute(Action powerAction, string message, ITextOutput output)
    {
        try
        {
            var result = prepare();
            if (!result.IsOK())
            {
                return result;
            }

            output.WriteLine(message);
            powerAction();
            return ResultErr.CreateFailure(ResultCode.Failure,
                "cause='PowerActionReturned' message='Cosmos konnte die Energieaktion nicht abschliessen.'");
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFromException(exception);
        }
    }
}
