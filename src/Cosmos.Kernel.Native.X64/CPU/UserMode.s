// ZehOS patch user-mode-probe, see PATCHES.md
.intel_syntax noprefix

// Ring 3 entry, the syscall entry and both exits that are not an interrupt's. Every exit runs
// masked, after the managed check of the frame (UserMode.Prepare); nothing touches gs: between
// an exit's swapgs and its return. NMI, #DF and #MC in those windows have IST stacks and read
// IA32_GS_BASE (patches user-gdt, user-stub).

// PerCpuBlock.cs is the layout's home.
.equ PERCPU_USER_KERNEL_RSP,  0x80
.equ PERCPU_USER_SCRATCH_RSP, 0x88

// Gdt.cs is the selectors' home.
.equ USER_DATA, 0x43
.equ USER_CODE, 0x4B

// UserFrame: 15 GPRs, 4 info words, then the interrupt frame.
.equ FRAME_INFO_SIZE, 32
.equ FRAME_RIP,    152
.equ FRAME_CS,     160
.equ FRAME_RFLAGS, 168
.equ FRAME_RSP,    176
.equ FRAME_SS,     184
.equ FPU_SIZE,     512

.section .rodata
.balign 16
// A fxsave64 image: FCW 0x37F, MXCSR 0x1F80, everything else zero, so no kernel value is left.
.Luser_fpu_initial:
    .word 0x037F
    .zero 22
    .long 0x1F80
    .zero 484
.balign 4
.Luser_kernel_mxcsr:
    .long 0x1F80

.text

// void _native_x64_user_enter(UserFrame* frame, uint64_t* kernelRspSlot)
// Goes to ring 3 with the frame's RIP, CS, RFLAGS, RSP and SS and every register zero. Returns
// when the thread leaves ring 3 for good, through _native_x64_user_leave. Interrupts are
// enabled on the call and on the return.
.global _native_x64_user_enter
_native_x64_user_enter:
    push    rbx
    push    rbp
    push    r12
    push    r13
    push    r14
    push    r15
    sub     rsp, 8
    stmxcsr dword ptr [rsp]
    fnstcw  word ptr [rsp + 4]

    cli
    mov     qword ptr [rsi], rsp    // 16-byte aligned: the RSP0 of this thread
    mov     rbx, rdi
    call    __managed__user_prepare
    test    rax, rax
    jnz     .Luser_leave_rax

    push    qword ptr [rbx + FRAME_SS]
    push    qword ptr [rbx + FRAME_RSP]
    push    qword ptr [rbx + FRAME_RFLAGS]
    push    qword ptr [rbx + FRAME_CS]
    push    qword ptr [rbx + FRAME_RIP]
    fxrstor64 [rip + .Luser_fpu_initial]
    xor     eax, eax
    xor     ebx, ebx
    xor     ecx, ecx
    xor     edx, edx
    xor     esi, esi
    xor     edi, edi
    xor     ebp, ebp
    xor     r8d, r8d
    xor     r9d, r9d
    xor     r10d, r10d
    xor     r11d, r11d
    xor     r12d, r12d
    xor     r13d, r13d
    xor     r14d, r14d
    xor     r15d, r15d
    swapgs
    iretq

.Luser_leave_rax:
    mov     rsp, rax
// Reached masked, with the kernel's GS, RSP at the thread's slot value: from here or from the
// iretq of an interrupt whose frame the managed check redirected.
.global _native_x64_user_leave
_native_x64_user_leave:
    cld
    fninit                          // not fldcw first: it would raise a pending exception
    fldcw   word ptr [rsp + 4]
    ldmxcsr dword ptr [rsp]
    sti
    add     rsp, 8
    pop     r15
    pop     r14
    pop     r13
    pop     r12
    pop     rbp
    pop     rbx
    ret

// The LSTAR target: masked (SFMASK), program's stack and GS.
.balign 16
_native_x64_user_syscall:
    swapgs
    mov     qword ptr gs:[PERCPU_USER_SCRATCH_RSP], rsp
    mov     rsp, qword ptr gs:[PERCPU_USER_KERNEL_RSP]
    push    USER_DATA
    push    qword ptr gs:[PERCPU_USER_SCRATCH_RSP]
    push    r11                     // RFLAGS
    push    USER_CODE
    push    rcx                     // RIP
    push    0                       // Temp, Cr2, CpuFlags, Interrupt
    push    0
    push    0
    push    0
    push    rax
    push    rcx
    push    rdx
    push    rbx
    push    rbp
    push    rsi
    push    rdi
    push    r8
    push    r9
    push    r10
    push    r11
    push    r12
    push    r13
    push    r14
    push    r15
    mov     rdi, rsp
    sub     rsp, FPU_SIZE
    fxsave64 [rsp]
    fninit
    ldmxcsr dword ptr [rip + .Luser_kernel_mxcsr]
    xor     ebp, ebp                // a frame-pointer walk ends here

    call    __managed__user_syscall // enables interrupts for the handler, returns masked
    test    rax, rax
    jnz     .Luser_leave_rax

    fxrstor64 [rsp]
    add     rsp, FPU_SIZE
    pop     r15
    pop     r14
    pop     r13
    pop     r12
    pop     r11
    pop     r10
    pop     r9
    pop     r8
    pop     rdi
    pop     rsi
    pop     rbp
    pop     rbx
    pop     rdx
    pop     rcx
    pop     rax
    add     rsp, FRAME_INFO_SIZE
    pop     rcx                     // RIP, checked: a non-canonical one would fault in ring 0
    add     rsp, 8
    pop     r11                     // RFLAGS, checked
    pop     rsp
    swapgs
    sysretq

// uint64_t _native_x64_user_syscall_address()
.global _native_x64_user_syscall_address
_native_x64_user_syscall_address:
    lea     rax, [rip + _native_x64_user_syscall]
    ret

// uint64_t _native_x64_user_leave_address()
.global _native_x64_user_leave_address
_native_x64_user_leave_address:
    lea     rax, [rip + _native_x64_user_leave]
    ret

// void _native_x64_user_wrmsr(uint32_t index, uint64_t value)
.global _native_x64_user_wrmsr
_native_x64_user_wrmsr:
    mov     ecx, edi
    mov     eax, esi
    mov     rdx, rsi
    shr     rdx, 32
    wrmsr
    ret

// uint64_t _native_x64_user_change_cr4(uint64_t set, uint64_t clear)
// Returns the new value.
.global _native_x64_user_change_cr4
_native_x64_user_change_cr4:
    mov     rax, cr4
    or      rax, rdi
    not     rsi
    and     rax, rsi
    mov     cr4, rax
    ret

// void _native_x64_user_clear_fs()
// A null FS selector: iretq to ring 3 would clear the bootloader's, and may clear the base with it.
.global _native_x64_user_clear_fs
_native_x64_user_clear_fs:
    xor     eax, eax
    mov     fs, eax
    ret
