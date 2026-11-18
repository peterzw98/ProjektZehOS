#if ZEHOS_POWER_FAULT_PROBE
using System;

namespace ProjektZeh.Storage;

/// <summary>
/// Nur Testbuild <c>-p:ZehOSPowerFaultProbe=true</c> (Spec 7b §4, P4): genau ein Fehlerpunkt der Energieaktion im
/// Root-Profil schlaegt beim naechsten Durchlauf einmal fehl. Der Shellbefehl <c>powerfault</c> schaltet ihn scharf.
/// </summary>
internal static class PowerFaultProbe
{
    internal static readonly string[] Points = ["efi-sync", "efi-unmount", "root-sync", "root-flush", "reopen", "remount"];
    private static string? armed;

    public static bool Arm(string point)
    {
        if (Array.IndexOf(Points, point) < 0) return false;
        armed = point;
        return true;
    }

    /// <summary>true, wenn <paramref name="point"/> scharf war; er ist danach entschaerft und protokolliert.</summary>
    public static bool Fires(string point)
    {
        if (armed != point) return false;
        armed = null;
        Cosmos.Kernel.System.Diagnostics.Log.WriteString("POWER_FAULT " + point + "\n");
        return true;
    }
}
#endif
