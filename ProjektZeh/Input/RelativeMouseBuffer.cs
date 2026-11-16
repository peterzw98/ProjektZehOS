using System;
using System.Runtime.CompilerServices;

namespace ProjektZeh.Input;

internal readonly record struct RelativeMousePacket(int X, int Y, PointerButtons Buttons, int Wheel);

// Der Interrupt schreibt unter der Mausmaske; Lesen und Leeren erfolgen ebenfalls unter dieser Maske.
internal struct RelativeMouseBuffer
{
    private const int Capacity = 128;
    [InlineArray(Capacity)]
    private struct PacketStorage { private RelativeMousePacket first; }
    private PacketStorage packets;
    private int head;
    private int count;
    private long deltaX;
    private long deltaY;
    private long lost;

    public void Add(RelativeMousePacket packet)
    {
        deltaX = Math.Clamp(deltaX + packet.X, int.MinValue, int.MaxValue);
        deltaY = Math.Clamp(deltaY + packet.Y, int.MinValue, int.MaxValue);
        if (count == Capacity)
        {
            lost += count;
            head = count = 0;
            deltaX = packet.X;
            deltaY = packet.Y;
        }
        packets[(head + count) % Capacity] = packet;
        count++;
    }

    public bool TryRead(out RelativeMousePacket packet)
    {
        if (count == 0) { packet = default; return false; }
        packet = packets[head];
        head = (head + 1) % Capacity;
        count--;
        return true;
    }

    public void TakeMotion(out int x, out int y, out long dropped)
    {
        x = (int)deltaX;
        y = (int)deltaY;
        dropped = lost;
        deltaX = deltaY = lost = 0;
    }
}
