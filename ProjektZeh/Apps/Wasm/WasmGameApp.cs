#if ZEHOS_WASM
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Packages;
using ProjektZeh.Runtime.Wasm;
using ProjektZeh.UI;

namespace ProjektZeh.Apps.Wasm;

/// <summary>
/// Fenster fuer eine installierte Paket-App mit eigener Bildausgabe (zehos_v1). Das Modul laeuft auf dem gemeinsamen
/// Wasm-Worker; Bilder und Tasten werden ueber gesperrte, begrenzte Puffer zwischen Worker und Desktop getauscht.
/// Die Paketversion bleibt bis zum Ende des Laufs gesichert, damit Entfernen oder Aktualisieren sie nicht loescht.
/// </summary>
/// <remarks><paramref name="log"/> erhaelt erstes Bild und Ende des Laufs, damit Gasttests darauf warten koennen.</remarks>
internal sealed class WasmGameApp(PackageStore store, string packageId, UiPainter painter, Action<string>? log = null)
    : IApplication, IWasmSession
{
    private const int MaximumModuleBytes = 64 * 1024 * 1024;
    // Ohne Zeichnen in dieser Zeit ist das Fenster nicht sichtbar (Textshell, minimiert): die App pausiert.
    private const long VisibleTimeoutMilliseconds = 250;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long lastDrawMilliseconds;
    private long pausedMilliseconds;
    private readonly object gate = new();
    private readonly WasmInputBuffer input = new();
    // Nur fuer Doom: der Langlauf (ROADMAP 9.6) misst dessen Bildzeit; andere Apps bleiben ohne Messung und Logzeilen.
    private readonly WasmFrameTimes? frameTimes = string.Equals(packageId, "doom", StringComparison.Ordinal) ? new() : null;
    private uint[] frame = [];
    private int frameWidth;
    private int frameHeight;
    private int[] scaled = [];
    // Quellspalte bzw. -zeile je Zielpixel; neu berechnet nur, wenn sich Ziel- oder Bildgroesse aendert.
    private int[] sourceColumns = [];
    private int[] sourceRows = [];
    private int scaledFrameWidth;
    private int scaledFrameHeight;
    private int frameRevision;
    private int drawnRevision = -1;
    private bool running;
    // Nur vom Wasm-Worker gelesen und geschrieben.
    private bool presented;
    private bool exitRequested;
    private bool active;
    private string? status = "Pruefe Paket ...";
    private string? root;
    private bool allowsNetwork;
    private readonly System.Text.StringBuilder errors = new();

    public bool NeedsAnimation => running && active;
    public bool IsCloseRequested { get; private set; }
    string? IWasmSession.Root => root;
    bool IWasmSession.AllowsNetwork => allowsNetwork;
    bool IWasmSession.ShouldExit { get { lock (gate) return exitRequested; } }
    uint IWasmSession.TicksMilliseconds { get { lock (gate) return (uint)(clock.ElapsedMilliseconds - pausedMilliseconds); } }

    public void Start()
    {
        var acquired = store.Acquire(packageId);
        if (!acquired.IsOK())
        {
            status = acquired.Status.Message;
            return;
        }
        running = true;
        WasmRuntime.Enqueue(() => Load(acquired.Value!), "_start", 0, this, Finished);
    }

    // Laeuft auf dem Worker: die Dateien koennen nach der Installation veraendert worden sein und werden erneut geprueft.
    private (byte[]? Module, string? Error) Load(InstalledPackage package)
    {
        lock (gate) if (exitRequested) return (null, "Start abgebrochen.");
        var verified = store.VerifyOnce(package, CancellationToken.None);
        if (!verified.IsOK()) return (null, verified.Message);
        string entry = Path.Combine(package.FilesDirectory, package.Manifest.Entry);
        if (new FileInfo(entry).Length > MaximumModuleBytes) return (null, "App zu gross.");
        byte[] module = File.ReadAllBytes(entry);
        lock (gate) { root = package.FilesDirectory; allowsNetwork = package.Manifest.AllowsNetwork; status = "Lade ..."; }
        return (module, null);
    }

    // Laeuft auf dem Worker, wenn das Modul endet.
    private void Finished(WasmStatus result, int exitCode, string message)
    {
        store.Release(packageId);
        string ending;
        lock (gate)
        {
            running = false;
            if (result == WasmStatus.Ok && exitCode == 0) IsCloseRequested = !exitRequested;
            else if (result == WasmStatus.Ok) status = $"App beendet (Exitcode {exitCode}): {LastErrorLine()}";
            else if (result == WasmStatus.LoadFailed && frameWidth == 0) status = message;
            else status = $"App abgebrochen: {result} {message}";
            ending = result == WasmStatus.Ok && exitCode == 0 ? "beendet" : "Ende: " + status;
        }
        log?.Invoke(ending);
    }

    public void Handle(InputEvent value)
    {
        lock (gate) input.Handle(value);
    }

    private void ResetInput()
    {
        lock (gate) input.Reset();
    }

    public void Update(long milliseconds) { }

    public void Draw(PixelSurface surface, int x, int y, int width, int height, bool isActive)
    {
        if (active && !isActive) ResetInput();
        lock (gate)
        {
            active = isActive;
            lastDrawMilliseconds = clock.ElapsedMilliseconds;
            Monitor.PulseAll(gate);
        }
        surface.FillRectangle(x, y, width, height, unchecked((int)0xff000000));
        lock (gate)
        {
            input.SetViewport(width, height, frameWidth, frameHeight);
            var (targetWidth, targetHeight) = WasmImageFit.Fit(width, height, frameWidth, frameHeight);
            if (targetWidth > 0)
            {
                if (sourceColumns.Length != targetWidth || sourceRows.Length != targetHeight
                    || scaledFrameWidth != frameWidth || scaledFrameHeight != frameHeight)
                {
                    // Beim Ziehen am Fensterrand aendert sich die Zielgroesse fast jedes Bild; der Bildpuffer waechst
                    // deshalb nur (DrawPixels nimmt einen groesseren Puffer), statt jedes Mal mehrere MiB neu anzulegen.
                    if (scaled.Length < targetWidth * targetHeight) scaled = new int[targetWidth * targetHeight];
                    sourceColumns = new int[targetWidth];
                    sourceRows = new int[targetHeight];
                    WasmImageFit.FillSourceIndices(sourceColumns, frameWidth);
                    WasmImageFit.FillSourceIndices(sourceRows, frameHeight);
                    scaledFrameWidth = frameWidth;
                    scaledFrameHeight = frameHeight;
                    drawnRevision = -1;
                }
                if (drawnRevision != frameRevision) Scale(targetWidth);
                drawnRevision = frameRevision;
                surface.DrawPixels(scaled, x + (width - targetWidth) / 2, y + (height - targetHeight) / 2, targetWidth, targetHeight);
            }
            if (status != null && (!running || frameWidth == 0)) painter.Text(surface, status, x + 12, y + 12, width - 24, UiPainter.Foreground);
        }
    }

    // Naechster Nachbar ueber die Tabellen; wiederholte Quellzeilen werden als ganze Zeile kopiert.
    private void Scale(int targetWidth)
    {
        for (int row = 0; row < sourceRows.Length; row++)
        {
            int target = row * targetWidth;
            if (row > 0 && sourceRows[row] == sourceRows[row - 1])
            {
                Array.Copy(scaled, target - targetWidth, scaled, target, targetWidth);
                continue;
            }
            int source = sourceRows[row] * frameWidth;
            for (int column = 0; column < targetWidth; column++)
                scaled[target + column] = unchecked((int)(0xff000000 | frame[source + sourceColumns[column]]));
        }
    }

    public bool RequestClose()
    {
        lock (gate) { exitRequested = true; Monitor.PulseAll(gate); }
        return true;
    }

    public void Close()
    {
        lock (gate) { exitRequested = true; Monitor.PulseAll(gate); }
        ResetInput();
    }

    // stderr wird begrenzt gesammelt, damit eine Fehlermeldung der App im Fenster erscheint.
    void IWasmSession.Write(int fd, ReadOnlySpan<byte> data)
    {
        if (fd != 2) return;
        lock (gate)
        {
            if (errors.Length > 2048) errors.Remove(0, errors.Length - 1024);
            errors.Append(System.Text.Encoding.UTF8.GetString(data));
        }
    }

    private string LastErrorLine()
    {
        string[] lines = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : "keine Meldung";
    }

    void IWasmSession.Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test)
    {
        if (test) return;
        // Vor der Sichtbarkeitspause melden: die Marke belegt, dass das Modul laeuft, unabhaengig vom Fensterzustand.
        if (!presented) { presented = true; log?.Invoke("erstes Bild"); }
        string? frameReport = null;
        lock (gate)
        {
            // Eine nicht sichtbare oder nicht aktive App haelt hier an; ihre Uhr steht in dieser Zeit still,
            // damit sie danach keine verpassten Spieltakte nachholt.
            bool paused = false;
            while (!exitRequested && (!active || clock.ElapsedMilliseconds - lastDrawMilliseconds > VisibleTimeoutMilliseconds))
            {
                paused = true;
                long start = clock.ElapsedMilliseconds;
                Monitor.Wait(gate, 100);
                pausedMilliseconds += clock.ElapsedMilliseconds - start;
            }
            if (frameTimes != null)
            {
                long now = clock.ElapsedMilliseconds - pausedMilliseconds;
                if (paused) frameTimes.Restart(now);
                else frameReport = frameTimes.Record(now);
            }
            if (frame.Length != pixels.Length) frame = new uint[pixels.Length];
            pixels.CopyTo(frame);
            frameWidth = width;
            frameHeight = height;
            frameRevision++;
        }
        // Ausserhalb der Sperre: die serielle Ausgabe soll das Zeichnen des Desktops nicht aufhalten.
        if (frameReport != null) log?.Invoke($"{frameReport} heap={GC.GetGCMemoryInfo().HeapSizeBytes / 1024} KiB");
    }

    bool IWasmSession.TryReadEvent(out WasmInputEvent value)
    {
        lock (gate) return input.TryRead(out value);
    }

    // Nur das aktive Fenster darf einfangen; Fokusverlust setzt die Eingabe und damit das Einfangen zurueck.
    bool IWasmSession.SetPointerCapture(bool enabled)
    {
        lock (gate)
        {
            if (enabled && !active) return false;
            input.SetCapture(enabled);
            return true;
        }
    }

    int IWasmSession.PollKey()
    {
        lock (gate) return input.PollKey();
    }
}
#endif
