using System;
using System.Threading;

namespace ProjektZeh.Processes;

/// <summary>
/// N3-B2: Belegung des einen Linux-Laufs (<c>busy</c> in <c>CosmosLinuxRun</c>) zusammen mit der Übergabe der Paketbindung.
/// Wer <see cref="TryEnter"/> eine Bindung mitgibt, hat sie abgegeben, auch wenn die Belegung scheitert; sonst bliebe der
/// Paketstand gebunden, bis der Rechner neu startet (Review B2, P2). Plattformunabhängig, damit die Hosttests das prüfen.
/// </summary>
internal static class LinuxRunGate
{
    /// <summary>Belegt <paramref name="busy"/> (0 auf 1); ist schon belegt, gibt es <paramref name="handedOver"/> frei und liefert false.</summary>
    public static bool TryEnter(ref int busy, IDisposable? handedOver)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) == 0)
        {
            return true;
        }

        handedOver?.Dispose();
        return false;
    }
}
