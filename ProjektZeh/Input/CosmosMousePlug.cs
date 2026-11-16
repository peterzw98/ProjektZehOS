using System.Runtime.CompilerServices;
using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.System.Input;
using KitPointerButtons = Cosmos.Kernel.HAL.Devices.Input.PointerButtons;

namespace ProjektZeh.Input;

#pragma warning disable COSMOS0003 // Der Plug verwendet die Pointer-Signatur aus Cosmos 3.0.94.
[Plug(typeof(MouseManager))]
public static class CosmosMousePlug
{
    private const string MouseType = "Cosmos.Kernel.System.Input.MouseManager, Cosmos.Kernel.System";
    private static RelativeMouseBuffer packets;

    // Kein Rueckruf in MouseManager: der NativeAOT-Initialisierer haette sonst einen Zyklus.
    [PlugMember]
    public static void HandleRelative(int deltaX, int deltaY, KitPointerButtons buttons, int wheel) =>
        packets.Add(new(deltaX, deltaY, (PointerButtons)buttons, wheel));

    // Vor einem absoluten Tabletbericht und beim Poll anwenden; die Aufrufer schuetzen gegen Interrupts.
    internal static void ApplyPending()
    {
        while (packets.TryRead(out var packet))
        {
            ref int x = ref X(null);
            ref int y = ref Y(null);
            x += (int)(packet.X * MouseManager.Sensitivity);
            y += (int)(packet.Y * MouseManager.Sensitivity);
            Scroll(null) += packet.Wheel;
            if (x < 0) x = 0;
            if (x >= MouseManager.ScreenWidth) x = MouseManager.ScreenWidth - 1;
            if (y < 0) y = 0;
            if (y >= MouseManager.ScreenHeight) y = MouseManager.ScreenHeight - 1;
            Left(null) = (packet.Buttons & PointerButtons.Left) != 0;
            Right(null) = (packet.Buttons & PointerButtons.Right) != 0;
            Middle(null) = (packet.Buttons & PointerButtons.Middle) != 0;
        }
    }

    internal static void RecordTabletMotion(int x, int y) => packets.AddTabletMotion(x, y);

    internal static void TakeMotion(out int x, out int y, out bool hasRaw, out int tabletX, out int tabletY, out long dropped) =>
        packets.TakeMotion(out x, out y, out hasRaw, out tabletX, out tabletY, out dropped);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<X>k__BackingField")]
    private static extern ref int X([UnsafeAccessorType(MouseType)] object? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<Y>k__BackingField")]
    private static extern ref int Y([UnsafeAccessorType(MouseType)] object? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<ScrollDelta>k__BackingField")]
    private static extern ref int Scroll([UnsafeAccessorType(MouseType)] object? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<LeftButton>k__BackingField")]
    private static extern ref bool Left([UnsafeAccessorType(MouseType)] object? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<RightButton>k__BackingField")]
    private static extern ref bool Right([UnsafeAccessorType(MouseType)] object? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<MiddleButton>k__BackingField")]
    private static extern ref bool Middle([UnsafeAccessorType(MouseType)] object? owner);
}
#pragma warning restore COSMOS0003
