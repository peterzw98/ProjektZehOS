using System;
using System.Collections.Generic;
using ProjektZeh.Input;

namespace ProjektZeh.Graphics;

internal sealed class WindowManager
{
    private readonly List<Window> windows = [];
    private readonly List<Window> zOrder = [];
    private readonly int width;
    private readonly int height;
    private Window? moving;
    private Window? pointerCapture;
    private int edges;
    private int startX;
    private int startY;
    private (int X, int Y, int Width, int Height) initial;

    public Window? ActiveWindow { get; private set; }
    public int Count => windows.Count;
    public bool HasPointerCapture => moving != null || pointerCapture != null;
    public Window GetWindow(int index) => windows[index];

    public WindowManager(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 96);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 64);
        this.width = width;
        this.height = height;
    }

    public void Add(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (windows.Contains(window)) throw new ArgumentException("Fenster ist bereits geoeffnet.", nameof(window));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(window.Width, width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(window.Height, height);
        window.X = Math.Clamp(window.X, 0, width - window.Width);
        window.Y = Math.Clamp(window.Y, 0, height - window.Height);
        window.Application.Start();
        window.Application.Resize(window.ContentWidth, window.ContentHeight);
        windows.Add(window);
        Activate(window);
    }

    public void ToggleTask(Window window)
    {
        if (!windows.Contains(window)) return;
        Reset();
        if (window == ActiveWindow) Minimize(window);
        else { window.IsMinimized = false; Activate(window); }
    }

    public void Cycle(bool backwards = false)
    {
        if (windows.Count == 0) return;
        int index = windows.IndexOf(ActiveWindow!);
        if (index < 0) index = backwards ? 0 : -1;
        index = (index + (backwards ? windows.Count - 1 : 1)) % windows.Count;
        Reset();
        windows[index].IsMinimized = false;
        Activate(windows[index]);
    }

    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { Reset(); return; }
        if (value.Type is InputEventType.KeyDown or InputEventType.KeyUp or InputEventType.Text)
        {
            ActiveWindow?.Application.Handle(value);
            return;
        }
        if (moving != null)
        {
            if (value.Type is InputEventType.PointerMove or InputEventType.PointerUp) Move(value.X, value.Y);
            if (value.Type == InputEventType.PointerUp && value.Button == PointerButtons.Left) moving = null;
            return;
        }
        if (pointerCapture != null)
        {
            Deliver(pointerCapture, value);
            if (value.Type == InputEventType.PointerUp && value.Button == PointerButtons.Left) pointerCapture = null;
            return;
        }
        Window? hit = HitTest(value.X, value.Y);
        if (value.Type == InputEventType.PointerDown)
        {
            Activate(hit);
            if (hit == null) return;
            if (value.Button == PointerButtons.Left)
            {
                if (hit.ContainsClose(value.X, value.Y))
                {
                    Reset();
                    hit.Application.Close();
                    windows.Remove(hit);
                    zOrder.Remove(hit);
                    Activate(TopVisible());
                    return;
                }
                if (hit.ContainsMinimize(value.X, value.Y)) { Minimize(hit); return; }
                if (hit.ContainsMaximize(value.X, value.Y)) { Maximize(hit); return; }
                edges = hit.IsMaximized ? 0 :
                    (value.X < hit.X + 5 ? 1 : 0) | (value.X >= hit.X + hit.Width - 5 ? 2 : 0) |
                    (value.Y < hit.Y + 4 ? 4 : 0) | (value.Y >= hit.Y + hit.Height - 5 ? 8 : 0);
                if (edges != 0 || (!hit.IsMaximized && value.Y < hit.ContentY))
                {
                    moving = hit;
                    startX = value.X;
                    startY = value.Y;
                    initial = (hit.X, hit.Y, hit.Width, hit.Height);
                    hit.Application.Handle(new(InputEventType.Reset));
                    return;
                }
                if (hit.ContainsContent(value.X, value.Y)) pointerCapture = hit;
            }
        }
        if (hit?.ContainsContent(value.X, value.Y) == true) Deliver(hit, value);
    }

    private void Move(int x, int y)
    {
        Window window = moving!;
        long dx = (long)x - startX;
        long dy = (long)y - startY;
        if (edges == 0)
        {
            window.X = (int)Math.Clamp(initial.X + dx, 0, width - window.Width);
            window.Y = (int)Math.Clamp(initial.Y + dy, 0, height - window.Height);
            return;
        }
        int right = initial.X + initial.Width;
        int bottom = initial.Y + initial.Height;
        int left = initial.X;
        int top = initial.Y;
        if ((edges & 1) != 0) left = (int)Math.Clamp(initial.X + dx, 0, right - window.MinimumWidth);
        if ((edges & 2) != 0) right = (int)Math.Clamp(right + dx, left + window.MinimumWidth, width);
        if ((edges & 4) != 0) top = (int)Math.Clamp(initial.Y + dy, 0, bottom - window.MinimumHeight);
        if ((edges & 8) != 0) bottom = (int)Math.Clamp(bottom + dy, top + window.MinimumHeight, height);
        window.X = left;
        window.Y = top;
        window.Width = right - left;
        window.Height = bottom - top;
        window.Application.Resize(window.ContentWidth, window.ContentHeight);
    }

    private void Minimize(Window window)
    {
        Reset();
        window.IsMinimized = true;
        Activate(TopVisible());
    }

    private void Maximize(Window window)
    {
        Reset();
        if (window.IsMaximized)
            (window.X, window.Y, window.Width, window.Height) = window.RestoreBounds;
        else
        {
            window.RestoreBounds = (window.X, window.Y, window.Width, window.Height);
            (window.X, window.Y, window.Width, window.Height) = (0, 0, width, height);
        }
        window.IsMaximized = !window.IsMaximized;
        window.Application.Resize(window.ContentWidth, window.ContentHeight);
    }

    public void Update(long milliseconds)
    {
        foreach (Window window in windows) window.Application.Update(milliseconds);
    }

    public void Reset()
    {
        moving = null;
        pointerCapture = null;
        foreach (Window window in windows) window.Application.Handle(new(InputEventType.Reset));
    }

    public void Render(PixelSurface surface, byte[] font, int glyphWidth, int glyphHeight)
    {
        foreach (Window window in zOrder)
            if (!window.IsMinimized) window.Render(surface, font, glyphWidth, glyphHeight, window == ActiveWindow);
    }

    private void Activate(Window? window)
    {
        if (ActiveWindow != window) ActiveWindow?.Application.Handle(new(InputEventType.Reset));
        ActiveWindow = window;
        if (window == null) return;
        zOrder.Remove(window);
        zOrder.Add(window);
    }

    private Window? TopVisible()
    {
        for (int index = zOrder.Count - 1; index >= 0; index--)
            if (!zOrder[index].IsMinimized) return zOrder[index];
        return null;
    }

    private Window? HitTest(int x, int y)
    {
        for (int index = zOrder.Count - 1; index >= 0; index--)
            if (!zOrder[index].IsMinimized && zOrder[index].Contains(x, y)) return zOrder[index];
        return null;
    }

    private static void Deliver(Window window, InputEvent value) =>
        window.Application.Handle(value with { X = value.X - window.ContentX, Y = value.Y - window.ContentY });
}
