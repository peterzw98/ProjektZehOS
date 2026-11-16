using System;
using System.Threading;
using Cosmos.Kernel.HAL.DriverKit;
using Cosmos.Kernel.HAL.DriverKit.Buses.Virtio;
using Cosmos.Kernel.HAL.DriverKit.Resources;

namespace ProjektZeh.Security;

#pragma warning disable COSMOS0003 // Das in Cosmos 3.0.94 experimentelle Driver Kit stellt virtio-PCI-Transport und Virtqueue bereit.
/// <summary>virtio-rng (virtio 5.4) als Ersatz fuer RDSEED/RDRAND, etwa QEMU mit -device virtio-rng-pci.</summary>
[Driver]
public sealed class CosmosVirtioEntropyDriver : Driver
{
    private const ushort RequestQueue = 0;
    private const int ChunkBytes = 256;
    private const int PageBytes = 4096;

    private static EntropyDevice? current;

    private readonly DeviceMatch[] matches = [VirtioMatch.DeviceType(VirtioDeviceType.Entropy)];

    public override string Name => "VirtioEntropy";
    public override ReadOnlySpan<DeviceMatch> Matches => matches;

    /// <summary>Fuellt bytes vollstaendig aus dem Geraet; false ohne gebundenes Geraet oder wenn es nicht antwortet.</summary>
    internal static bool TryFill(byte[] bytes) => Volatile.Read(ref current)?.TryFill(bytes) ?? false;

    public override ProbeResult Probe(DeviceBinding binding)
    {
        var access = binding.Node.Access<VirtioAccess>();
        if (!access.NegotiateFeatures(0, out _)) return ProbeResult.Failed("the device rejected the feature set");
        // Es ist immer nur eine Anfrage unterwegs; vier Eintraege genuegen.
        if (!access.TryCreateQueue(binding, RequestQueue, 4, out Virtqueue? queue)) return ProbeResult.Failed("no request queue");
        if (!queue.TryAllocateDescriptor(out ushort slot)) return ProbeResult.Failed("no descriptor");
        DmaBuffer buffer = binding.AllocateDma(ChunkBytes, PageBytes);
        access.SetDriverOk();

        var device = new EntropyDevice(binding, queue, buffer, slot);
        binding.DriverState = device;
        Volatile.Write(ref current, device);
        binding.Log("ready, polling");
        return ProbeResult.Bound;
    }

    public override void OnDetach(DeviceBinding binding, DetachReason reason)
    {
        if (binding.DriverState is not EntropyDevice device) return;
        Interlocked.CompareExchange(ref current, null, device);
        if (reason.HardwarePresent) binding.Node.Access<VirtioAccess>().Reset();
    }

    private sealed class EntropyDevice(DeviceBinding binding, Virtqueue queue, DmaBuffer buffer, ushort slot)
    {
        // 2000 x 50 us: QEMU antwortet in Mikrosekunden, ein haengendes Geraet blockiert die Ereignisschleife hoechstens 100 ms.
        private const int Polls = 2000;
        private const uint PollMicroseconds = 50;
        private const int BusyWaitMilliseconds = 250;

        private int busy;
        private volatile bool broken;

        internal bool TryFill(byte[] bytes)
        {
            // Die Virtqueue ist nicht threadsicher. Kennwortarbeit, TLS und Wasm-Apps laufen auf eigenen Threads;
            // ein zweiter Aufruf wartet deshalb begrenzt (laenger als ein ueblicher Aufruf dauert), statt sofort zu scheitern.
            for (int wait = 0; Interlocked.CompareExchange(ref busy, 1, 0) != 0; wait++)
            {
                if (wait >= BusyWaitMilliseconds) return false;
                Thread.Sleep(1);
            }
            bool filled = false;
            if (!broken)
            {
                try
                {
                    filled = Fill(bytes);
                }
                catch (Exception)
                {
                    // Etwa eine abgebaute Bindung; ohne finally (Cosmos 3.0.94), daher Freigabe unten.
                    filled = false;
                }
            }
            try
            {
                // Keine Zufallsbytes im DMA-Puffer zuruecklassen, auch nicht nach einem Teilfehler.
                buffer.Span.Clear();
            }
            catch (Exception)
            {
                // Der Puffer gehoert nach dem Abbau der Bindung schon nicht mehr dem Treiber.
            }
            Volatile.Write(ref busy, 0);
            return filled;
        }

        private bool Fill(byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                uint requested = (uint)Math.Min(ChunkBytes, bytes.Length - offset);
                if (!TryRequest(requested, out uint length)) return false;
                int count = (int)Math.Min(length, requested);
                if (count == 0) return false;
                buffer.Span[..count].CopyTo(bytes.AsSpan(offset, count));
                offset += count;
            }
            return true;
        }

        private bool TryRequest(uint requested, out uint length)
        {
            queue.SetDescriptor(slot, buffer.PhysicalAddress, requested, VirtqueueDescriptorFlags.Write);
            queue.Submit(slot);
            queue.Notify();
            for (int poll = 0; poll < Polls; poll++)
            {
                if (queue.TryTakeUsed(out _, out length)) return true;
                binding.Delay(PollMicroseconds);
            }
            // Der Deskriptor bleibt beim Geraet; spaeter eintreffende Bytes duerfen keiner neuen Anfrage zugeordnet werden.
            broken = true;
            binding.Log("no answer, disabled");
            length = 0;
            return false;
        }
    }
}
#pragma warning restore COSMOS0003
