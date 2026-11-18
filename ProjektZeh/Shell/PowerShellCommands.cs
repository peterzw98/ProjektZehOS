using System;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <summary>
/// Neustart und Ausschalten nach erfolgreicher Vorbereitung der Datentraeger.
/// </summary>
/// <param name="restore">Root-Profil (7b): nimmt die Vorbereitung zurueck, wenn nach ihr nicht ausgeschaltet wurde.</param>
internal sealed class PowerShellCommands(Func<ResultErr> prepare, Action reboot, Action shutdown, Func<ResultErr>? restore = null)
{
    public ShellCommand[] Commands { get; } =
    [
        new("reboot", "reboot", "Startet ZehOS neu.", false,
            (_, output) => Execute(prepare, restore, reboot, "ZehOS wird neu gestartet.", output)),
        new("shutdown", "shutdown", "Schaltet den Rechner aus.", false,
            (_, output) => Execute(prepare, restore, shutdown, "ZehOS wird ausgeschaltet.", output))
    ];

    private static ResultErr Execute(Func<ResultErr> prepare, Func<ResultErr>? restore, Action powerAction, string message, ITextOutput output)
    {
        ResultErr result;
        try { result = prepare(); }
        catch (Exception exception) { return ResultErr.CreateFromException(exception); }
        if (!result.IsOK()) return result;

        // Ab hier ist vorbereitet (Root sauber markiert, Schreiber gesperrt); jeder Rueckweg muss zuruecknehmen (Review 7b P2-1).
        ResultErr failure;
        try
        {
            output.WriteLine(message);
            powerAction();
            failure = ResultErr.CreateFailure(ResultCode.Failure,
                "cause='PowerActionReturned' message='Cosmos konnte die Energieaktion nicht abschliessen.'");
        }
        catch (Exception exception) { failure = ResultErr.CreateFromException(exception); }
        if (restore == null) return failure;
        ResultErr restored;
        try { restored = restore(); }
        catch (Exception exception) { restored = ResultErr.CreateFromException(exception); }
        return restored.IsOK() ? failure : ResultErr.CreateFailure(failure.Code, failure.Message + " " + restored.Message);
    }
}
