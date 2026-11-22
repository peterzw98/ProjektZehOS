// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics;
using System.Runtime.InteropServices;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.HAL.DriverKit;
using Cosmos.Kernel.HAL.DriverKit.Resources;
using Cosmos.Kernel.HAL.DriverKit.Threading;

namespace Cosmos.Kernel.Drivers.Pci.Storage.Ahci;

/// <summary>
/// One SATA port of an AHCI HBA, the block device <see cref="AhciDriver"/>
/// publishes to the ring as <c>sata{n}</c>, with the geometry and the
/// strings IDENTIFY DEVICE reported. Every transfer moves through the
/// port's one physically contiguous bounce buffer, at most
/// <see cref="MaxSectorsPerCommand"/> sectors per command (one page when
/// only a page could be allocated), and one command is in flight per port: a caller
/// claims the port's busy flag under the controller's lock, held only
/// around the flag, and releases it when its command and its copy are
/// done, so chunks of two callers may interleave but never overlap.
/// Commands are polled to completion. Every method is thread context,
/// entered by the ring from any thread; a task file error surfaces as an
/// <see cref="IOException"/>, a stuck port or a timeout as an
/// <see cref="InvalidOperationException"/>. Before the busy flag is
/// released the port is recovered: after a task file error its engine is
/// stopped and started again, after a timeout or a stuck task file the
/// device is reset as well (ZehOS patch ahci-error-recovery). A port that
/// cannot be stopped, or whose device does not come back, stays disabled
/// and fails every later call with an <see cref="IOException"/>: while the
/// engine stands no transfer of the abandoned command reaches a bounce
/// buffer the next caller fills.
/// </summary>
internal sealed class AhciPort : IBlockDevice
{
    // --- Constants ---

    /// <summary>Bytes of one page: the bounce buffer is page aligned, and one page is the fallback size. The kit exposes no page size.</summary>
    private const int PageBytes = 4096;

    // ZehOS patch ahci-large-commands, see PATCHES.md
    /// <summary>
    /// Sectors one command moves at most: 64 KiB, one contiguous bounce
    /// buffer, one PRDT entry (well under its 4 MiB byte count). One page
    /// per command cost a full command round trip, under KVM several VM
    /// exits, per 4 KiB.
    /// </summary>
    private const uint MaxSectorsPerCommand = 128;

    /// <summary>Sectors per command when only one bounce page could be allocated.</summary>
    private const uint PageSectorsPerCommand = PageBytes / (int)AhciProtocol.SectorBytes;

    /// <summary>
    /// Completion polls that pause on the timestamp counter instead of
    /// <see cref="DeviceBinding.Delay"/>: on x64 the platform delay reads
    /// port 0x80 once per microsecond, a VM exit each under KVM, so a
    /// command that completes within tens of microseconds would wait
    /// mostly on the delay. Past these polls the loop falls back to the
    /// platform delay.
    /// </summary>
    private const int FastPolls = 2000;

    /// <summary>Pause between two fast completion polls, on the timestamp counter.</summary>
    private const long FastPollMicroseconds = 2;

    /// <summary>Microseconds in one second, for the timestamp pause.</summary>
    private const long MicrosecondsPerSecond = 1_000_000;

    /// <summary>How long the task file gets to leave BSY and DRQ before a command is issued.</summary>
    private const uint TaskFileMilliseconds = 1000;

    /// <summary>Bits in the low dword of a 64-bit address.</summary>
    private const int DwordBits = 32;

    /// <summary>Bits in one byte, for the LBA and count lanes of the FIS.</summary>
    private const int ByteBits = 8;

    /// <summary>Bits in one IDENTIFY word, for the multi-word counts.</summary>
    private const int WordBits = 16;

    /// <summary>Mask of one byte, for the low byte of an IDENTIFY word.</summary>
    private const int ByteMask = 0xFF;

    // ZehOS patch ahci-error-recovery, see PATCHES.md
    /// <summary>
    /// Sectors a 48-bit LBA addresses: IDENTIFY words 100 to 103 hold 64
    /// bits, so a capacity above this is clamped; the FIS carries six LBA
    /// bytes and would wrap a larger sector number back to the start.
    /// </summary>
    private const ulong MaxLba48Sectors = 1UL << 48;

    // --- Private fields ---

    private readonly AhciState _controller;
    private readonly DeviceBinding _binding;
    private readonly uint _portNumber;
    private readonly string _name;
    private readonly DmaBuffer _bounce;
    private readonly uint _maxSectors;
    private readonly ulong _blockCount;
    private readonly string _model;
    private readonly string _serial;
    private readonly string _firmware;
    private bool _busy;

    // ZehOS patch ahci-error-recovery, see PATCHES.md
    /// <summary>Set once a recovery failed (engine left running, device not back); written only under the busy flag, read in <see cref="Claim"/> under the lock.</summary>
    private bool _disabled;

    // --- Constructor ---

    /// <summary>
    /// Brings the port up: refuses an ATAPI device, allocates the bounce
    /// buffer (below 4 GiB on a 32-bit HBA; 64 KiB, one page when that
    /// fails), issues IDENTIFY DEVICE and reads
    /// the strings and the capacity out of it. Thread context, from the
    /// probe, after the port was rebased and its engine started.
    /// </summary>
    /// <param name="controller">The HBA the port belongs to.</param>
    /// <param name="binding">The function's binding, for the bounce page and the delays.</param>
    /// <param name="portNumber">The port index on the HBA.</param>
    /// <param name="index">The port's <c>sata{index}</c> number, global across controllers.</param>
    /// <exception cref="InvalidOperationException">The port holds an ATAPI device, no bounce page could be allocated, or IDENTIFY did not complete.</exception>
    /// <exception cref="IOException">IDENTIFY ended in a task file error.</exception>
    internal AhciPort(AhciState controller, DeviceBinding binding, uint portNumber, int index)
    {
        _controller = controller;
        _binding = binding;
        _portNumber = portNumber;
        _name = "sata" + index;

        if ((controller.ReadPort(portNumber, AhciProtocol.PxCmd) & AhciProtocol.CmdAtapi) != 0)
        {
            throw new InvalidOperationException("the port is not a SATA device");
        }

        DmaConstraints constraints = controller.Supports64Bit ? DmaConstraints.None : DmaConstraints.Addressable32Bit;
        int largeBytes = (int)(MaxSectorsPerCommand * AhciProtocol.SectorBytes);
        if (binding.TryAllocateDma(largeBytes, PageBytes, constraints, out DmaBuffer? bounce))
        {
            _bounce = bounce;
            _maxSectors = MaxSectorsPerCommand;
        }
        else if (binding.TryAllocateDma(PageBytes, PageBytes, constraints, out bounce))
        {
            // No contiguous 64 KiB (or none below 4 GiB): one page per
            // command stays correct, only slower.
            _bounce = bounce;
            _maxSectors = PageSectorsPerCommand;
        }
        else
        {
            throw new InvalidOperationException(controller.Supports64Bit
                ? "No pages left for DMA memory."
                : "no DMA memory below 4 GiB for a 32-bit controller");
        }

        ushort[] identify = new ushort[AhciProtocol.IdentifyWords];
        Claim();
        try
        {
            IssueCommand(AhciProtocol.AtaIdentify, 0, 1, isWrite: false, useLba48: false, hasData: true);
            MemoryMarshal.Cast<byte, ushort>(_bounce.Span).Slice(0, AhciProtocol.IdentifyWords).CopyTo(identify);
        }
        finally
        {
            Release();
        }

        _serial = IdentifyString(identify, AhciProtocol.IdentifySerialWord, AhciProtocol.IdentifySerialWords);
        _firmware = IdentifyString(identify, AhciProtocol.IdentifyFirmwareWord, AhciProtocol.IdentifyFirmwareWords);
        _model = IdentifyString(identify, AhciProtocol.IdentifyModelWord, AhciProtocol.IdentifyModelWords);
        if ((identify[AhciProtocol.IdentifyCommandSetsWord] & AhciProtocol.IdentifyLba48Supported) != 0)
        {
            _blockCount = identify[AhciProtocol.IdentifyLba48CountWord]
                | ((ulong)identify[AhciProtocol.IdentifyLba48CountWord + 1] << WordBits)
                | ((ulong)identify[AhciProtocol.IdentifyLba48CountWord + 2] << (2 * WordBits))
                | ((ulong)identify[AhciProtocol.IdentifyLba48CountWord + 3] << (3 * WordBits));
            if (_blockCount > MaxLba48Sectors)
            {
                _blockCount = MaxLba48Sectors;
            }
        }
        else
        {
            _blockCount = identify[AhciProtocol.IdentifyLba28CountWord]
                | ((ulong)identify[AhciProtocol.IdentifyLba28CountWord + 1] << WordBits);
        }
    }

    // --- IBlockDevice ---

    /// <inheritdoc/>
    public ulong BlockCount => _blockCount;

    /// <summary>512: the sector size every command here assumes. Any context.</summary>
    public ulong BlockSize => AhciProtocol.SectorBytes;

    /// <summary><c>sata{index}</c>, the number taken from the driver's counter before IDENTIFY. Any context.</summary>
    public string Name => _name;

    /// <summary>
    /// Reads whole sectors with READ DMA EXT, at most
    /// <see cref="MaxSectorsPerCommand"/> per command through the bounce
    /// buffer, the port claimed per chunk. Thread context; any thread.
    /// </summary>
    /// <param name="blockNo">The first sector.</param>
    /// <param name="blockCount">How many sectors.</param>
    /// <param name="data">Where they go; at least <paramref name="blockCount"/> times <see cref="BlockSize"/> bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="data"/> is shorter than the sectors asked for, or the sectors run past the device.</exception>
    /// <exception cref="IOException">A command ended in a task file error, or a failed recovery left the port disabled.</exception>
    /// <exception cref="InvalidOperationException">No free slot, the port stuck busy, or a command timed out.</exception>
    public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data)
    {
        int sector = (int)AhciProtocol.SectorBytes;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockCount, (ulong)data.Length / (uint)sector, nameof(blockCount));
        // ZehOS patch ahci-error-recovery, see PATCHES.md
        // Past the capacity the six LBA bytes of the FIS would wrap: a
        // write at 2^48 would land on sector 0. Written without overflow.
        if (blockNo > _blockCount || blockCount > _blockCount - blockNo)
        {
            throw new ArgumentOutOfRangeException(nameof(blockNo), "The sectors run past the end of the device.");
        }

        ulong done = 0;
        while (done < blockCount)
        {
            ulong remaining = blockCount - done;
            uint chunk = remaining >= _maxSectors ? _maxSectors : (uint)remaining;
            int bytes = sector * (int)chunk;
            Claim();
            try
            {
                IssueCommand(AhciProtocol.AtaReadDmaExt, blockNo + done, chunk, isWrite: false, useLba48: true, hasData: true);
                _bounce.Span.Slice(0, bytes).CopyTo(data.Slice((int)((long)done * sector), bytes));
            }
            finally
            {
                Release();
            }

            done += chunk;
        }
    }

    /// <summary>
    /// Writes whole sectors with WRITE DMA EXT, at most
    /// <see cref="MaxSectorsPerCommand"/> per command through the bounce
    /// buffer, the port claimed per chunk. Thread context; any thread.
    /// </summary>
    /// <param name="blockNo">The first sector.</param>
    /// <param name="blockCount">How many sectors.</param>
    /// <param name="data">Their bytes; at least <paramref name="blockCount"/> times <see cref="BlockSize"/> bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="data"/> is shorter than the sectors given, or the sectors run past the device.</exception>
    /// <exception cref="IOException">A command ended in a task file error, or a failed recovery left the port disabled.</exception>
    /// <exception cref="InvalidOperationException">No free slot, the port stuck busy, or a command timed out.</exception>
    public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
    {
        int sector = (int)AhciProtocol.SectorBytes;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockCount, (ulong)data.Length / (uint)sector, nameof(blockCount));
        // ZehOS patch ahci-error-recovery, see PATCHES.md
        // Past the capacity the six LBA bytes of the FIS would wrap: a
        // write at 2^48 would land on sector 0. Written without overflow.
        if (blockNo > _blockCount || blockCount > _blockCount - blockNo)
        {
            throw new ArgumentOutOfRangeException(nameof(blockNo), "The sectors run past the end of the device.");
        }

        ulong done = 0;
        while (done < blockCount)
        {
            ulong remaining = blockCount - done;
            uint chunk = remaining >= _maxSectors ? _maxSectors : (uint)remaining;
            int bytes = sector * (int)chunk;
            Claim();
            try
            {
                data.Slice((int)((long)done * sector), bytes).CopyTo(_bounce.Span);
                IssueCommand(AhciProtocol.AtaWriteDmaExt, blockNo + done, chunk, isWrite: true, useLba48: true, hasData: true);
            }
            finally
            {
                Release();
            }

            done += chunk;
        }
    }

    /// <summary>Issues FLUSH CACHE EXT so completed writes reach the media. Thread context; any thread.</summary>
    /// <exception cref="IOException">The command ended in a task file error, or a failed recovery left the port disabled.</exception>
    /// <exception cref="InvalidOperationException">No free slot, the port stuck busy, or the command timed out.</exception>
    public void Flush()
    {
        Claim();
        try
        {
            IssueCommand(AhciProtocol.AtaCacheFlushExt, 0, 0, isWrite: false, useLba48: false, hasData: false);
        }
        finally
        {
            Release();
        }
    }

    // --- Properties ---

    /// <summary>The model number IDENTIFY reported, trimmed. Any context.</summary>
    public string Model => _model;

    /// <summary>The serial number IDENTIFY reported, trimmed. Any context.</summary>
    public string Serial => _serial;

    /// <summary>The firmware revision IDENTIFY reported, trimmed. Any context.</summary>
    public string Firmware => _firmware;

    /// <summary>The port index on the HBA. Any context.</summary>
    public uint PortNumber => _portNumber;

    /// <summary>The HBA the port belongs to. Any context.</summary>
    public AhciState Controller => _controller;

    // --- Internal methods ---

    // ZehOS patch ahci-error-recovery, see PATCHES.md
    /// <summary>
    /// Issues one ATA command without data and without LBA, the port
    /// claimed around it, so the storage test kernel can drive the error
    /// path with a command the device aborts. Thread context; any thread.
    /// </summary>
    /// <param name="command">The ATA command.</param>
    /// <exception cref="IOException">The command ended in an error, or the port is disabled.</exception>
    /// <exception cref="InvalidOperationException">No free slot, the port stuck busy, or the command timed out.</exception>
    internal void IssueNonDataCommand(byte command)
    {
        Claim();
        try
        {
            IssueCommand(command, 0, 0, isWrite: false, useLba48: false, hasData: false);
        }
        finally
        {
            Release();
        }
    }

    // ZehOS patch ahci-error-recovery, see PATCHES.md
    /// <summary>
    /// Runs the recovery of a timeout or a stuck task file, device reset
    /// included, the port claimed around it, so the storage test kernel can
    /// drive the COMRESET path QEMU never times out into. Thread context;
    /// any thread.
    /// </summary>
    /// <returns>True when the port is usable afterwards, false when the recovery disabled it.</returns>
    /// <exception cref="IOException">The port was disabled already.</exception>
    internal bool RecoverWithDeviceReset()
    {
        Claim();
        try
        {
            RecoverOrDisable(resetDevice: true);
            return !_disabled;
        }
        finally
        {
            Release();
        }
    }

    // --- Private methods ---

    /// <summary>
    /// Claims the port's busy flag under the controller's lock, pausing
    /// <see cref="AhciState.PollMicroseconds"/> outside the lock while
    /// another caller holds it; no deadline, since the holder's command is
    /// bounded by <see cref="AhciState.CommandTimeoutMilliseconds"/>.
    /// Thread context; any thread.
    /// </summary>
    /// <exception cref="IOException">A failed recovery left the port disabled.</exception>
    private void Claim()
    {
        DeviceLock deviceLock = _controller.RequireLock();
        while (true)
        {
            using (deviceLock.Acquire())
            {
                if (_disabled)
                {
                    // An I/O error, not a usage error: callers that map
                    // IOException to a failed operation keep working.
                    throw new IOException("SATA: port disabled after a failed recovery.");
                }

                if (!_busy)
                {
                    _busy = true;
                    return;
                }
            }

            _binding.Delay(AhciState.PollMicroseconds);
        }
    }

    /// <summary>Releases the port's busy flag under the controller's lock. Thread context; any thread.</summary>
    private void Release()
    {
        using (_controller.RequireLock().Acquire())
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Builds and issues one command on a free slot and polls it to
    /// completion: clears PxIS and PxSERR, writes the header, the PRDT
    /// entry over the bounce buffer when the command carries data, and the
    /// H2D FIS, waits for the task file to leave BSY and DRQ, orders the
    /// stores, writes PxCI, then polls the slot's bit until it clears or an
    /// error bit shows. A command counts as done only when PxIS holds no
    /// error bit and PxTFD no ERR once the slot cleared: the HBA clears
    /// PxCI in the same step in which it sets TFES. On an error, a timeout
    /// or a task file stuck busy the port is recovered before the throw, the
    /// device reset as well after the last two.
    /// The caller holds the busy flag. Thread context; any thread.
    /// </summary>
    /// <param name="command">The ATA command.</param>
    /// <param name="lba">The first sector, used when <paramref name="useLba48"/>.</param>
    /// <param name="count">The sector count: the PRDT byte count for a data command, the FIS count when <paramref name="useLba48"/>.</param>
    /// <param name="isWrite">The data goes to the device.</param>
    /// <param name="useLba48">The FIS carries the LBA and the count.</param>
    /// <param name="hasData">The command moves data through the bounce buffer.</param>
    /// <exception cref="IOException">The command ended in a task file, host bus or interface error.</exception>
    /// <exception cref="InvalidOperationException">No slot was free, the port stuck busy, or the command timed out.</exception>
    private void IssueCommand(byte command, ulong lba, uint count, bool isWrite, bool useLba48, bool hasData)
    {
        uint port = _portNumber;
        _controller.WritePort(port, AhciProtocol.PxIs, AhciProtocol.Rw1CClearAll);
        _controller.WritePort(port, AhciProtocol.PxSerr, AhciProtocol.Rw1CClearAll);

        int slot = FindFreeSlot();
        if (slot < 0)
        {
            throw new InvalidOperationException("SATA: no free command slot.");
        }

        DmaBuffer region = _controller.CommandRegion;
        ulong tablePhysical = region.PhysicalAddress + (ulong)AhciProtocol.CommandTableOffset(port, slot);
        Span<byte> list = region.Span.Slice(AhciProtocol.CommandListOffset(port), AhciProtocol.CommandListBytes);
        Span<HbaCommandHeader> headers = MemoryMarshal.Cast<byte, HbaCommandHeader>(list);
        headers[slot] = new HbaCommandHeader
        {
            Flags0 = (byte)(AhciProtocol.HeaderFisDwords | (isWrite ? AhciProtocol.HeaderWrite : 0)),
            PrdtLength = hasData ? (ushort)1 : (ushort)0,
            CommandTableBase = (uint)tablePhysical,
            CommandTableBaseUpper = (uint)(tablePhysical >> DwordBits),
        };

        Span<byte> table = region.Span.Slice(AhciProtocol.CommandTableOffset(port, slot), AhciProtocol.CommandTableBytes);
        if (hasData)
        {
            ulong bouncePhysical = _bounce.PhysicalAddress;
            Span<HbaPrdtEntry> prdt = MemoryMarshal.Cast<byte, HbaPrdtEntry>(table.Slice(AhciProtocol.CommandTablePrdtOffset, AhciProtocol.PrdtEntryBytes));
            prdt[0] = new HbaPrdtEntry
            {
                DataBase = (uint)bouncePhysical,
                DataBaseUpper = (uint)(bouncePhysical >> DwordBits),
                ByteCountAndInterrupt = ((count * AhciProtocol.SectorBytes - 1) & AhciProtocol.PrdtByteCountMask) | AhciProtocol.PrdtInterruptOnCompletion,
            };
        }

        Span<FisRegisterH2D> fis = MemoryMarshal.Cast<byte, FisRegisterH2D>(table.Slice(AhciProtocol.CommandTableFisOffset, AhciProtocol.FisRegisterH2DBytes));
        fis[0] = new FisRegisterH2D
        {
            FisType = AhciProtocol.FisTypeRegisterH2D,
            Flags = AhciProtocol.FisCommandFlag,
            Command = command,
            Device = useLba48 ? AhciProtocol.DeviceLbaMode : (byte)0,
            Lba0 = useLba48 ? (byte)lba : (byte)0,
            Lba1 = useLba48 ? (byte)(lba >> ByteBits) : (byte)0,
            Lba2 = useLba48 ? (byte)(lba >> (2 * ByteBits)) : (byte)0,
            Lba3 = useLba48 ? (byte)(lba >> (3 * ByteBits)) : (byte)0,
            Lba4 = useLba48 ? (byte)(lba >> (4 * ByteBits)) : (byte)0,
            Lba5 = useLba48 ? (byte)(lba >> (5 * ByteBits)) : (byte)0,
            CountLow = useLba48 ? (byte)count : (byte)0,
            CountHigh = useLba48 ? (byte)(count >> ByteBits) : (byte)0,
        };

        long busyDeadline = AhciState.DeadlineAfter(TaskFileMilliseconds);
        while ((_controller.ReadPort(port, AhciProtocol.PxTfd) & (AhciProtocol.TfdBusy | AhciProtocol.TfdDataRequest)) != 0)
        {
            if (Stopwatch.GetTimestamp() >= busyDeadline)
            {
                // ZehOS patch ahci-error-recovery, see PATCHES.md
                // A device stuck busy may still run an earlier command;
                // only a reset ends that, a restart of the engine does not.
                RecoverOrDisable(resetDevice: true);
                throw new InvalidOperationException("SATA: port stuck busy (TFD BSY/DRQ) before command issue.");
            }

            _binding.Delay(AhciState.PollMicroseconds);
        }

        DmaBuffer.WriteBarrier();
        uint slotBit = 1u << slot;
        _controller.WritePort(port, AhciProtocol.PxCi, slotBit);

        long deadline = AhciState.DeadlineAfter(AhciState.CommandTimeoutMilliseconds);
        long fastPause = Stopwatch.Frequency / MicrosecondsPerSecond * FastPollMicroseconds;
        int fastPolls = 0;
        while ((_controller.ReadPort(port, AhciProtocol.PxCi) & slotBit) != 0)
        {
            // ZehOS patch ahci-error-recovery, see PATCHES.md
            if ((_controller.ReadPort(port, AhciProtocol.PxIs) & AhciProtocol.IsErrorMask) != 0)
            {
                break;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                _controller.CountTimeout();
                // The device may still run the command: after the stop the
                // override clears BSY in PxTFD, and a late D2H or DMA setup
                // FIS would land on the next command's slot. Reset it.
                RecoverOrDisable(resetDevice: true);
                throw new InvalidOperationException("SATA: command completion timeout.");
            }

            if (fastPolls < FastPolls)
            {
                // Pause on the timestamp counter: no port access, so no VM
                // exit under KVM; most commands finish within a few polls.
                fastPolls++;
                long until = Stopwatch.GetTimestamp() + fastPause;
                while (Stopwatch.GetTimestamp() < until)
                {
                }
            }
            else
            {
                _binding.Delay(AhciState.PollMicroseconds);
            }
        }

        // ZehOS patch ahci-error-recovery, see PATCHES.md
        // Checked once the slot cleared too: QEMU and real HBAs clear PxCI
        // with the D2H FIS that carries ERR, so the loop above mostly sees
        // the slot clear, not TFES. Without this an aborted read returned
        // stale bounce bytes and an aborted write reported success.
        uint status = _controller.ReadPort(port, AhciProtocol.PxIs);
        uint taskFile = _controller.ReadPort(port, AhciProtocol.PxTfd);
        if ((status & AhciProtocol.IsErrorMask) != 0 || (taskFile & AhciProtocol.TfdError) != 0)
        {
            RecoverOrDisable(resetDevice: false);
            throw new IOException($"SATA: command 0x{command:X2} failed (IS 0x{status:X8}, TFD 0x{taskFile:X4}).");
        }

        DmaBuffer.ReadBarrier();
        _controller.CountCommand();
    }

    // ZehOS patch ahci-error-recovery, see PATCHES.md
    /// <summary>
    /// Puts the port back to work after a failed command: after an error
    /// the HBA halts the command list until ST is cleared (AHCI 1.3.1
    /// section 6.2.2.1), and after a timeout the device may still run the
    /// command, which only a device reset ends (section 10.4.2). Stops the
    /// engine (ST cleared, CR and PxCI drained); resets the device through
    /// <see cref="AhciState.ResetDevice"/> when the stop fails or
    /// <paramref name="resetDevice"/> asks for it, which counts only once
    /// the link reports the device again and its task file reads ready;
    /// clears PxSERR and PxIS and starts the engine again. When the engine
    /// cannot be stopped or started, or the device does not come back, the
    /// port is disabled for good: a transfer of the abandoned command can
    /// reach the bounce buffer only while the engine runs. The caller holds
    /// the busy flag, so no other command runs on the port; only this
    /// port's registers are touched. Thread context; any thread.
    /// </summary>
    /// <param name="resetDevice">Reset the device even when the engine stopped: after a timeout or a task file stuck busy.</param>
    private void RecoverOrDisable(bool resetDevice)
    {
        uint port = _portNumber;
        // The stop runs first in both cases: COMRESET wants ST and CR clear.
        bool stopped = _controller.StopCommandEngine(port) && !resetDevice;
        if (!stopped)
        {
            stopped = _controller.ResetDevice(port);
        }

        _controller.WritePort(port, AhciProtocol.PxSerr, AhciProtocol.Rw1CClearAll);
        _controller.WritePort(port, AhciProtocol.PxIs, AhciProtocol.Rw1CClearAll);
        if (!stopped || !_controller.StartCommandEngine(port))
        {
            _disabled = true;
        }
    }

    /// <summary>The lowest slot clear in both PxSACT and PxCI, below the HBA's slot count, or -1. Any thread.</summary>
    private int FindFreeSlot()
    {
        uint used = _controller.ReadPort(_portNumber, AhciProtocol.PxSact) | _controller.ReadPort(_portNumber, AhciProtocol.PxCi);
        int slots = _controller.CommandSlots;
        for (int i = 0; i < slots; i++)
        {
            if ((used & (1u << i)) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>An IDENTIFY string: each word gives its high byte first, then its low byte; trimmed. Any context.</summary>
    private static string IdentifyString(ushort[] words, int firstWord, int wordCount)
    {
        char[] chars = new char[wordCount * 2];
        for (int i = 0; i < wordCount; i++)
        {
            ushort word = words[firstWord + i];
            chars[i * 2] = (char)(word >> ByteBits);
            chars[i * 2 + 1] = (char)(word & ByteMask);
        }

        return new string(chars).Trim();
    }
}
