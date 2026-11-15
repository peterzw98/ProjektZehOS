#if ZEHOS_WASM
using System;
using System.Collections.Generic;
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
internal sealed class WasmGameApp(PackageStore store, string packageId, UiPainter painter) : IApplication, IWasmSession
{
    private const int MaximumModuleBytes = 64 * 1024 * 1024;
    private const int KeyQueueCapacity = 64;
    // Ohne Zeichnen in dieser Zeit ist das Fenster nicht sichtbar (Textshell, minimiert): die App pausiert.
    private const long VisibleTimeoutMilliseconds = 250;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long lastDrawMilliseconds;
    private long pausedMilliseconds;
    private readonly object gate = new();
    private readonly Queue<int> keys = new();
    private readonly HashSet<byte> held = [];
    private uint[] frame = [];
    private int frameWidth;
    private int frameHeight;
    private int[] scaled = [];
    private int scaledFor = -1;
    private int frameRevision;
    private int drawnRevision = -1;
    private bool running;
    private bool exitRequested;
    private bool active;
    private string? status = "Pruefe Paket ...";
    private string? root;
    private readonly System.Text.StringBuilder errors = new();

    public bool NeedsAnimation => running && active;
    public bool IsCloseRequested { get; private set; }
    string? IWasmSession.Root => root;
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
        lock (gate) { root = package.FilesDirectory; status = "Lade ..."; }
        return (module, null);
    }

    // Laeuft auf dem Worker, wenn das Modul endet.
    private void Finished(WasmStatus result, int exitCode, string message)
    {
        store.Release(packageId);
        lock (gate)
        {
            running = false;
            if (result == WasmStatus.Ok && exitCode == 0) IsCloseRequested = !exitRequested;
            else if (result == WasmStatus.Ok) status = $"App beendet (Exitcode {exitCode}): {LastErrorLine()}";
            else if (result == WasmStatus.LoadFailed && frameWidth == 0) status = message;
            else status = $"App abgebrochen: {result} {message}";
        }
    }

    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { ReleaseKeys(); return; }
        if (value.Type is not (InputEventType.KeyDown or InputEventType.KeyUp) || value.IsRepeat) return;
        bool pressed = value.Type == InputEventType.KeyDown;
        lock (gate)
        {
            if (pressed ? !held.Add(value.KeyCode) : !held.Remove(value.KeyCode)) return;
            if (keys.Count < KeyQueueCapacity) keys.Enqueue((pressed ? 0x100 : 0) | value.KeyCode);
        }
    }

    // Gehaltene Tasten muessen bei Fokusverlust losgelassen werden, sonst laeuft die Spielfigur weiter.
    private void ReleaseKeys()
    {
        lock (gate)
        {
            foreach (byte code in held)
                if (keys.Count < KeyQueueCapacity) keys.Enqueue(code);
            held.Clear();
        }
    }

    public void Update(long milliseconds) { }

    public void Draw(PixelSurface surface, int x, int y, int width, int height, bool isActive)
    {
        if (active && !isActive) ReleaseKeys();
        lock (gate)
        {
            active = isActive;
            lastDrawMilliseconds = clock.ElapsedMilliseconds;
            Monitor.PulseAll(gate);
        }
        surface.FillRectangle(x, y, width, height, unchecked((int)0xff000000));
        lock (gate)
        {
            if (frameWidth > 0)
            {
                int scale = Math.Max(1, Math.Min(width / frameWidth, height / frameHeight));
                int targetWidth = frameWidth * scale, targetHeight = frameHeight * scale;
                if (scaledFor != scale || scaled.Length != targetWidth * targetHeight)
                {
                    scaled = new int[targetWidth * targetHeight];
                    scaledFor = scale;
                    drawnRevision = -1;
                }
                if (drawnRevision != frameRevision) Scale(scale, targetWidth);
                drawnRevision = frameRevision;
                surface.DrawPixels(scaled, x + (width - targetWidth) / 2, y + (height - targetHeight) / 2, targetWidth, targetHeight);
            }
            if (status != null && (!running || frameWidth == 0)) painter.Text(surface, status, x + 12, y + 12, width - 24, UiPainter.Foreground);
        }
    }

    private void Scale(int scale, int targetWidth)
    {
        for (int row = 0; row < frameHeight; row++)
        {
            int target = row * scale * targetWidth;
            for (int column = 0; column < frameWidth; column++)
            {
                int color = unchecked((int)(0xff000000 | frame[row * frameWidth + column]));
                for (int repeat = 0; repeat < scale; repeat++) scaled[target + column * scale + repeat] = color;
            }
            for (int repeat = 1; repeat < scale; repeat++) Array.Copy(scaled, target, scaled, target + repeat * targetWidth, targetWidth);
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
        ReleaseKeys();
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
        lock (gate)
        {
            // Eine nicht sichtbare oder nicht aktive App haelt hier an; ihre Uhr steht in dieser Zeit still,
            // damit sie danach keine verpassten Spieltakte nachholt.
            while (!exitRequested && (!active || clock.ElapsedMilliseconds - lastDrawMilliseconds > VisibleTimeoutMilliseconds))
            {
                long start = clock.ElapsedMilliseconds;
                Monitor.Wait(gate, 100);
                pausedMilliseconds += clock.ElapsedMilliseconds - start;
            }
            if (frame.Length != pixels.Length) frame = new uint[pixels.Length];
            pixels.CopyTo(frame);
            frameWidth = width;
            frameHeight = height;
            frameRevision++;
        }
    }

    int IWasmSession.PollKey()
    {
        lock (gate) return keys.Count > 0 ? keys.Dequeue() : -1;
    }
}
#endif
