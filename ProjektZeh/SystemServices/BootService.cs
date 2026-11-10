using System;
using ProjektZeh.Shared;

namespace ProjektZeh.SystemServices;

/// <summary>Protokolliert die ZehOS-Initialisierung und haelt die Textdiagnose erreichbar.</summary>
internal sealed class BootService
{
    private readonly SystemLog log;

    public bool IsRecoveryMode { get; private set; }

    public BootService(SystemLog log)
    {
        this.log = log;
    }

    /// <summary>Fuehrt einen benannten Schritt aus; optionale Fehler beenden den Boot nicht.</summary>
    public bool RunStep(string component, Func<ResultErr> initialize)
    {
        log.Information(component, "Initialisierung beginnt.");
        try
        {
            ResultErr result = initialize();
            if (result.IsOK())
            {
                log.Information(component, "Initialisierung erfolgreich.");
                return true;
            }

            log.Error(component, result.ToString());
        }
        catch (Exception exception)
        {
            // Eine optionale Initialisierung darf den Diagnosezugang nicht beenden.
            log.Error(component, exception.ToString());
        }

        return false;
    }

    /// <summary>Ueberspringt die normale Anzeige im Recovery-Modus oder stellt bei Fehlern die Konsole wieder her.</summary>
    public bool InitializeDisplay(bool forceRecovery, Func<ResultErr> initializeDisplay,
        Func<ResultErr> restoreTextConsole)
    {
        if (!forceRecovery && RunStep("Display", initializeDisplay))
        {
            return true;
        }

        IsRecoveryMode = true;
        log.Warning("Boot", forceRecovery
            ? "Textshell-Wiederherstellungsmodus angefordert."
            : "Anzeige fehlgeschlagen; Textshell-Wiederherstellung beginnt.");
        return RunStep("TextConsole", restoreTextConsole);
    }
}
