using System;
using ProjektZeh.Shared;

namespace ProjektZeh.Graphics;

internal readonly record struct DisplayMode(int Width, int Height)
{
    public override string ToString() => $"{Width} x {Height}";
    // Untergrenze: Arbeitsflaeche ueber der Taskleiste muss Standardfenster von 800 x 560 aufnehmen.
    public static bool IsSupportedSize(int width, int height) => width is >= 1024 and <= 1920 && height is >= 720 and <= 1080;
}

internal sealed class DisplaySettings(DisplayMode current, DisplayMode[] modes, Func<DisplayMode, ResultErr> setMode)
{
    private DisplayMode previous;
    public DisplayMode Current { get; private set; } = current;
    public DisplayMode[] Modes { get; } = modes;
    public bool Pending { get; private set; }
    public long Deadline { get; private set; }
    public string Status { get; private set; } = "";
    public Action? Confirmed { get; set; }
    /// <summary>Nur Treiber mit mehreren gemeldeten Modi koennen zur Laufzeit wechseln.</summary>
    public bool CanSwitch => Modes.Length > 1;
    public long SecondsLeft(long milliseconds) => Pending ? Math.Max(0, (Deadline - milliseconds + 999) / 1000) : 0;

    public ResultErr Apply(DisplayMode mode, long milliseconds)
    {
        if (Pending) return ResultErr.CreateFailure(ResultCode.Failure, "Zuerst den aktuellen Wechsel bestaetigen oder zuruecknehmen.");
        if (Array.IndexOf(Modes, mode) < 0) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Aufloesung wird vom Treiber nicht angeboten.");
        if (mode == Current) return ResultErr.CreateSuccess();
        var result = setMode(mode);
        Status = result.IsOK() ? "Anzeige innerhalb von 15 Sekunden bestaetigen" : result.Message;
        if (!result.IsOK()) return result;
        previous = Current;
        Current = mode;
        Deadline = milliseconds + 15000;
        Pending = true;
        return result;
    }

    public void Confirm()
    {
        if (!Pending) return;
        Pending = false;
        Status = "Anzeige bestaetigt";
        Confirmed?.Invoke();
    }

    public ResultErr Revert()
    {
        if (!Pending) return ResultErr.CreateSuccess();
        var result = setMode(previous);
        Status = result.IsOK() ? "Vorherige Aufloesung wiederhergestellt" : "Rueckkehr fehlgeschlagen: " + result.Message;
        if (result.IsOK()) { Current = previous; Pending = false; }
        return result;
    }

    public void Update(long milliseconds)
    {
        if (Pending && milliseconds >= Deadline) Revert();
    }
}
