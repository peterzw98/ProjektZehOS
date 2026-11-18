using System;
using System.Runtime.CompilerServices;

namespace ProjektZeh.Input;

internal readonly record struct RelativeMousePacket(int X, int Y, PointerButtons Buttons, int Wheel);

// Der Interrupt schreibt unter CosmosMousePlug.PointerLock; Lesen und Leeren erfolgen unter derselben Sperre.
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
    private bool hasRawMotion;
    private long tabletX;
    private long tabletY;

    public void Add(RelativeMousePacket packet)
    {
        hasRawMotion |= packet.X != 0 || packet.Y != 0;
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

    public void AddTabletMotion(int x, int y)
    {
        tabletX = Math.Clamp(tabletX + x, int.MinValue, int.MaxValue);
        tabletY = Math.Clamp(tabletY + y, int.MinValue, int.MaxValue);
    }

    public void TakeMotion(out int x, out int y, out bool hasRaw, out int absoluteX, out int absoluteY, out long dropped)
    {
        x = (int)deltaX;
        y = (int)deltaY;
        hasRaw = hasRawMotion;
        absoluteX = (int)tabletX;
        absoluteY = (int)tabletY;
        dropped = lost;
        deltaX = deltaY = tabletX = tabletY = lost = 0;
        hasRawMotion = false;
    }
}
