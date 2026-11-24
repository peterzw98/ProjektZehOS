namespace ProjektZeh.Runtime.Wasm;

/// <summary>
/// Merkt, welcher Lauf geschlossen werden soll, und sagt nach der Frist genau einmal "abbrechen" (Roadmap 12.4-I2-A).
/// Laeufe sind beliebige Objekte und werden nur ueber ihre Identitaet verglichen; so trifft ein Abbruch nie einen Lauf,
/// der nach dem geschlossenen gestartet ist. Nicht threadsicher: WasmRuntime ruft alles unter seiner Sperre gate.
/// Gemerkt wird hoechstens ein Lauf, weil der eine Wasm-Worker hoechstens einen zur Zeit ausfuehrt.
/// </summary>
internal sealed class AbortWatchdog
{
    /// <summary>Frist zwischen Schliessen des Fensters und Abbruch; so lange darf die App ueber should_exit enden.</summary>
    public const long GraceMilliseconds = 5000;

    private object? requested;
    private long requestedAt;
    private object? aborted;

    /// <summary>Merkt den Lauf ab nowMilliseconds; ein zweiter Aufruf fuer denselben Lauf aendert nichts.</summary>
    public void CloseRequested(object run, long nowMilliseconds)
    {
        if (ReferenceEquals(run, requested) || ReferenceEquals(run, aborted)) return;
        requested = run;
        requestedAt = nowMilliseconds;
    }

    /// <summary>Vergisst den Lauf, wenn er endet; gilt fuer einen gemerkten wie fuer einen schon abgebrochenen.</summary>
    public void RunEnded(object run)
    {
        if (ReferenceEquals(run, requested)) requested = null;
        if (ReferenceEquals(run, aborted)) aborted = null;
    }

    /// <summary>
    /// true genau einmal: wenn currentRun der gemerkte Lauf ist und die Frist abgelaufen ist. Danach gilt der Lauf als
    /// abgebrochen (<see cref="IsAborting"/>), bis <see cref="RunEnded"/> ihn vergisst.
    /// </summary>
    public bool ShouldAbort(object? currentRun, long nowMilliseconds)
    {
        if (currentRun == null || requested == null || !ReferenceEquals(currentRun, requested)) return false;
        if (nowMilliseconds - requestedAt < GraceMilliseconds) return false;
        aborted = requested;
        requested = null;
        return true;
    }

    /// <summary>
    /// true, solange currentRun der abgebrochene Lauf ist. WasmRuntime setzt den Merker der C-Schicht damit erneut, falls
    /// der Lauf ihn nach dem Setzen noch geloescht hat (Laufstart in zehos_wasm_run, Rueckfall von AOT auf wasm3).
    /// </summary>
    public bool IsAborting(object? currentRun) => currentRun != null && ReferenceEquals(currentRun, aborted);
}
