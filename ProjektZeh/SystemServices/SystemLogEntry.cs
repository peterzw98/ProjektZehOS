using System;

namespace ProjektZeh.SystemServices;

/// <summary>
/// Unveraenderlicher Diagnoseeintrag seit dem Start des Kernels.
/// </summary>
internal sealed class SystemLogEntry
{
    public long ElapsedMilliseconds { get; }
    public LogLevel Level { get; }
    public string Component { get; }
    public string Message { get; }

    public SystemLogEntry(long elapsedMilliseconds, LogLevel level, string component, string message)
    {
        ElapsedMilliseconds = elapsedMilliseconds;
        Level = level;
        Component = component;
        Message = message;
    }

    /// <summary>
    /// Formatiert den Eintrag fuer die serielle Diagnoseausgabe.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return $"[{ElapsedMilliseconds} ms] {Level} {Component}: {Message}";
    }
}
