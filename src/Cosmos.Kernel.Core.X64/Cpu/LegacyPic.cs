// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using Cosmos.Kernel.Core;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.IO;

namespace Cosmos.Kernel.Core.X64.Cpu;

/// <summary>
/// Legacy 8259 PIC (Programmable Interrupt Controller).
/// Used to disable/mask all IRQs when using APIC.
/// </summary>
public static class LegacyPic
{
    // PIC ports
    private const ushort PIC1_COMMAND = 0x20;
    private const ushort PIC1_DATA = 0x21;
    private const ushort PIC2_COMMAND = 0xA0;
    private const ushort PIC2_DATA = 0xA1;

    /// <summary>
    /// Disables the legacy 8259 PIC by masking all IRQs.
    /// This should be called when using the APIC.
    /// </summary>
    public static void Disable()
    {
        Serial.WriteString("[LegacyPIC] Disabling 8259 PIC...\n");

        // Mask all IRQs on both PICs
        Native.IO.Write8(PIC1_DATA, 0xFF);
        Native.IO.Write8(PIC2_DATA, 0xFF);

        Serial.WriteString("[LegacyPIC] 8259 PIC disabled\n");
    }

    /// <summary>
    /// Remaps and disables the legacy PIC.
    /// Remaps IRQs to vectors 0x20-0x2F to avoid conflicts with CPU exceptions,
    /// then masks all IRQs.
    /// </summary>
    public static void RemapAndDisable()
    {
        Serial.WriteString("[LegacyPIC] Remapping and disabling 8259 PIC...\n");

        // ZehOS patch pic-remap-cli, see PATCHES.md
        // ICW1 clears the interrupt mask register, and the IDT load before this
        // (_native_x64_load_idt) leaves interrupts enabled. Until the final mask
        // write below, a pending PIT tick (IRQ0) is delivered at the firmware's
        // vector base 0x08, i.e. through the #DF stub, which pops an error code
        // the CPU never pushed; the misaligned frame then faults with #GP(0) at
        // the first movaps. Keep interrupts off for the whole sequence: once
        // they are on again, all lines are masked and nothing reaches the CPU.
        using var noInterrupts = InternalCpu.DisableInterruptsScope();

        // Save masks
        byte mask1 = Native.IO.Read8(PIC1_DATA);
        byte mask2 = Native.IO.Read8(PIC2_DATA);

        // Start initialization sequence (ICW1)
        Native.IO.Write8(PIC1_COMMAND, 0x11);
        Native.IO.Write8(PIC2_COMMAND, 0x11);

        // ICW2: Set vector offsets
        Native.IO.Write8(PIC1_DATA, 0x20);  // PIC1 vectors start at 0x20
        Native.IO.Write8(PIC2_DATA, 0x28);  // PIC2 vectors start at 0x28

        // ICW3: Tell Master PIC there's a slave at IRQ2
        Native.IO.Write8(PIC1_DATA, 0x04);
        Native.IO.Write8(PIC2_DATA, 0x02);

        // ICW4: 8086 mode
        Native.IO.Write8(PIC1_DATA, 0x01);
        Native.IO.Write8(PIC2_DATA, 0x01);

        // Mask all IRQs
        Native.IO.Write8(PIC1_DATA, 0xFF);
        Native.IO.Write8(PIC2_DATA, 0xFF);

        Serial.WriteString("[LegacyPIC] 8259 PIC remapped and disabled\n");
    }
}
