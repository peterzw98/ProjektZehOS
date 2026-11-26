// ZehOS patch user-gdt, see PATCHES.md
.intel_syntax noprefix

// The kernel's selectors, the bootloader's values; Gdt.cs is the table's home.
.equ KERNEL_CODE_SELECTOR, 0x28
.equ KERNEL_DATA_SELECTOR, 0x30

.text

// void _native_x64_load_gdt(GdtPointer* ptr, uint32_t tssSelector)
// Loads the calling CPU's GDT, reloads CS, SS, DS and ES from it and loads TR. The caller masks
// interrupts. FS and GS are left alone: loading a selector into GS would clear IA32_GS_BASE, the
// per-CPU block's address. ltr faults on a busy descriptor;
// the caller clears the busy bit first.
.global _native_x64_load_gdt
_native_x64_load_gdt:
    lgdt    [rdi]
    push    KERNEL_CODE_SELECTOR
    lea     rax, [rip + .Lgdt_reload]
    push    rax
    retfq
.Lgdt_reload:
    mov     eax, KERNEL_DATA_SELECTOR
    mov     ss, ax
    mov     ds, ax
    mov     es, ax
    ltr     si
    ret

// void _native_x64_store_gdt(GdtPointer* ptr)
// Writes the calling CPU's GDTR (10 bytes) to ptr.
.global _native_x64_store_gdt
_native_x64_store_gdt:
    sgdt    [rdi]
    ret

// uint64_t _native_x64_get_selectors()
// CS in bits 15:0, SS in 31:16, TR in 47:32 of the calling CPU.
.global _native_x64_get_selectors
_native_x64_get_selectors:
    // Into 32-bit registers, which clears the upper half: the assembler encodes a move from a
    // segment register to ax without the operand-size prefix, as one to eax.
    str     eax
    shl     rax, 16
    mov     ecx, ss
    or      rax, rcx
    shl     rax, 16
    mov     ecx, cs
    or      rax, rcx
    ret
