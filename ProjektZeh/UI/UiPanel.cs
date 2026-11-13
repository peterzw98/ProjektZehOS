using System;
using System.Collections.Generic;
using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.UI;

internal sealed class UiPanel
{
    private readonly List<UiControl> controls = [];
    private UiControl? captured;
    private bool leftShift;
    private bool rightShift;
    private bool control;
    public UiControl? Focused { get; private set; }

    public void Add(UiControl item)
    {
        controls.Add(item);
        if (Focused == null && item.Enabled) Focus(item);
    }

    public void Focus(UiControl? item)
    {
        if (Focused == item) return;
        if (Focused != null) { Focused.Handle(new(InputEventType.Reset)); Focused.Focused = false; }
        Focused = item;
        if (item != null) item.Focused = true;
    }

    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset)
        {
            captured = null;
            leftShift = rightShift = control = false;
            foreach (var item in controls) item.Handle(value);
            return;
        }
        if (value.Type is InputEventType.KeyDown or InputEventType.KeyUp)
        {
            bool down = value.Type == InputEventType.KeyDown;
            if (value.KeyCode == (byte)PhysicalKey.LeftShift) leftShift = down;
            if (value.KeyCode == (byte)PhysicalKey.RightShift) rightShift = down;
            if (value.KeyCode == (byte)PhysicalKey.Control) control = down;
            if (down && value.KeyCode == (byte)PhysicalKey.Tab)
            {
                int index = Focused == null ? -1 : controls.IndexOf(Focused);
                int direction = leftShift || rightShift ? -1 : 1;
                for (int i = 0; i < controls.Count; i++)
                {
                    index = (index + direction + controls.Count) % controls.Count;
                    if (controls[index].Enabled) { Focus(controls[index]); break; }
                }
                return;
            }
            if (control && down && value.KeyCode == 30 && Focused is UiTextBox box)
            {
                box.SelectAll();
                return;
            }
        }
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            captured = null;
            foreach (var item in controls)
            {
                if (!item.Enabled || !item.Bounds.Contains(value.X, value.Y)) continue;
                Focus(item);
                captured = item;
                item.Handle(value);
                return;
            }
            Focus(null);
            return;
        }
        if (value.Type is InputEventType.PointerMove or InputEventType.PointerUp)
        {
            if (captured?.Enabled == true) captured.Handle(value);
            if (value.Type == InputEventType.PointerUp && value.Button == PointerButtons.Left) captured = null;
            return;
        }
        if (Focused?.Enabled == true) Focused.Handle(value);
    }

    public void Draw(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        foreach (var item in controls) item.Draw(surface, painter, x, y, active);
    }
}
