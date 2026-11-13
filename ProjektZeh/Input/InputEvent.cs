using System;

namespace ProjektZeh.Input;

internal enum InputEventType { KeyDown, KeyUp, Text, PointerMove, PointerDown, PointerUp, Reset }

[Flags]
internal enum PointerButtons { None = 0, Left = 1, Right = 2, Middle = 4 }

internal enum PhysicalKey : byte
{
    Escape = 1, Backspace = 14, Tab = 15, Enter = 28, Control = 29, LeftShift = 42,
    RightShift = 54, Alt = 56, Space = 57, CapsLock = 58, F1 = 59, F4 = 62, NumLock = 69,
    Home = 71, Up = 72, PageUp = 73, Left = 75, Right = 77, End = 79, Down = 80, PageDown = 81, Delete = 83,
    AltGr = 96
}

internal readonly record struct InputEvent(InputEventType Type, byte KeyCode = 0,
    char Character = '\0', int X = 0, int Y = 0, PointerButtons Button = PointerButtons.None,
    bool IsRepeat = false, bool IsTextKey = false);
