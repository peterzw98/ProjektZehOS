using System;
using System.Collections.Generic;
using ProjektZeh.Input;

namespace ProjektZeh.Graphics;

internal sealed class WindowManager
{
    private readonly List<Window> windows = [];
    private readonly int width;
    private readonly int height;
    private Window? dragging;
    private Window? pointerCapture;
    private int dragX;
    private int dragY;

    public Window? ActiveWindow { get; private set; }
    public int Count => windows.Count;

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
        windows.Add(window);
        Activate(window);
    }

    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { Reset(); return; }
        if (value.Type is InputEventType.KeyDown or InputEventType.KeyUp or InputEventType.Text)
        {
            ActiveWindow?.Application.Handle(value);
            return;
        }
        if (dragging != null)
        {
            if (value.Type is InputEventType.PointerMove or InputEventType.PointerUp)
            {
                dragging.X = (int)Math.Clamp((long)value.X - dragX, 0, width - dragging.Width);
                dragging.Y = (int)Math.Clamp((long)value.Y - dragY, 0, height - dragging.Height);
                Deliver(dragging, value);
            }
            if (value.Type == InputEventType.PointerUp && value.Button == PointerButtons.Left) dragging = null;
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
            if (value.Button == PointerButtons.Left && hit.ContainsClose(value.X, value.Y))
            {
                hit.Application.Handle(new(InputEventType.Reset));
                hit.Application.Close();
                windows.Remove(hit);
                ActiveWindow = null;
                Activate(windows.Count == 0 ? null : windows[^1]);
                return;
            }
            if (value.Button == PointerButtons.Left && value.Y < hit.ContentY)
            {
                dragging = hit;
                dragX = value.X - hit.X;
                dragY = value.Y - hit.Y;
                return;
            }
            if (value.Button == PointerButtons.Left && hit.ContainsContent(value.X, value.Y)) pointerCapture = hit;
        }
        if (hit?.ContainsContent(value.X, value.Y) == true) Deliver(hit, value);
    }

    public void Update(long milliseconds)
    {
        foreach (Window window in windows) window.Application.Update(milliseconds);
    }

    public void Reset()
    {
        dragging = null;
        pointerCapture = null;
        foreach (Window window in windows) window.Application.Handle(new(InputEventType.Reset));
    }

    public void Render(PixelSurface surface, byte[] font, int glyphWidth, int glyphHeight)
    {
        foreach (Window window in windows) window.Render(surface, font, glyphWidth, glyphHeight, window == ActiveWindow);
    }

    private void Activate(Window? window)
    {
        if (ActiveWindow != window) ActiveWindow?.Application.Handle(new(InputEventType.Reset));
        ActiveWindow = window;
        if (window == null) return;
        windows.Remove(window);
        windows.Add(window);
    }

    private Window? HitTest(int x, int y)
    {
        for (int index = windows.Count - 1; index >= 0; index--)
            if (windows[index].Contains(x, y)) return windows[index];
        return null;
    }

    private static void Deliver(Window window, InputEvent value) =>
        window.Application.Handle(value with { X = value.X - window.ContentX, Y = value.Y - window.ContentY });
}
