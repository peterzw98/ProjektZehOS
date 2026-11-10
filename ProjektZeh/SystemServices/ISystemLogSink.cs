namespace ProjektZeh.SystemServices;

/// <summary>
/// Nimmt formatierte Systemdiagnosen entgegen.
/// </summary>
internal interface ISystemLogSink
{
    void Write(SystemLogEntry entry);
}
