namespace ProjektZeh.Runtime;

internal static class FrameTiming
{
    public static long NextDeadline(long milliseconds)
    {
        // Feste 60-Hz-Termine verhindern, dass verspaetetes Aufwachen jede weitere Frist verschiebt.
        long nextFrame = milliseconds * 60 / 1000 + 1;
        return (nextFrame * 1000 + 59) / 60;
    }
}
