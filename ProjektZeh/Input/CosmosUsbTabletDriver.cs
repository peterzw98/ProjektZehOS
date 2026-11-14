using System;
using Cosmos.Kernel.HAL.Devices.Input;
using Cosmos.Kernel.HAL.DriverKit;
using Cosmos.Kernel.HAL.DriverKit.Buses.Usb;
using Cosmos.Kernel.System;
using Cosmos.Kernel.System.Input;
using KitPointerButtons = Cosmos.Kernel.HAL.Devices.Input.PointerButtons;

namespace ProjektZeh.Input;

#pragma warning disable COSMOS0003 // Das in Cosmos 3.0.94 experimentelle Driver Kit bindet die USB-Schnittstelle und verwaltet ihre Ressourcen.
[Driver(Feature = DriverFeature.Mouse)]
public sealed class CosmosUsbTabletDriver : Driver
{
    private readonly DeviceMatch[] matches =
    [
        new UsbMatch(vendorId: 0x0627, interfaceClass: 3, interfaceSubclass: 0, interfaceProtocol: 0)
    ];

    public override string Name => "QemuUsbTablet";
    public override ReadOnlySpan<DeviceMatch> Matches => matches;

    public override ProbeResult Probe(DeviceBinding binding)
    {
        if (!KernelFeatures.Mouse) return ProbeResult.Declined("mouse support is compiled out");
        var usb = binding.Node.Access<UsbAccess>();
        var endpoint = usb.FindEndpoint(UsbEndpointType.Interrupt, true);
        if (endpoint is null) return ProbeResult.Declined("no interrupt IN endpoint");

        // Das Tablet liefert seinen absoluten Bericht nur im voreingestellten Report-Protokoll.
        var state = new TabletState();
        binding.DriverState = state;
        state.Sink = binding.PublishPointer(state);
        if (!usb.OpenInterruptPipe(binding, endpoint, state.OnReport, out _))
            return ProbeResult.Failed("could not open the report pipe");
        binding.Log("ready");
        return ProbeResult.Bound;
    }

    private sealed class TabletState : IPointer
    {
        public string Name => "qemu-usb-tablet";
        internal PointerSink? Sink { get; set; }

        // Der Handler kann im Interrupt laufen und darf daher nicht allokieren.
        internal void OnReport(ReadOnlySpan<byte> report)
        {
            if (!UsbTabletReport.TryDecode(report, MouseManager.ScreenWidth, MouseManager.ScreenHeight,
                out int x, out int y, out byte buttons)) return;

            // HandleAbsolute in Cosmos 3.0.94 übernimmt nur die Tasten, nicht die Position.
            MouseManager.SetPosition(x, y);
            Sink?.ReportAbsolute(x, y, (KitPointerButtons)buttons);
        }
    }
}
#pragma warning restore COSMOS0003
