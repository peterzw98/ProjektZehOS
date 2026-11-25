.intel_syntax noprefix

// ZehOS patch user-stub, see PATCHES.md
// Raises vector 2 in ring 0 with a program's GS base, MXCSR, FCW and DF in place: the state an
// NMI finds between a ring 3 entry and its swapgs. Interrupts stay masked while GS is foreign.

.equ MSR_GS_BASE,        0xC0000101
.equ MSR_KERNEL_GS_BASE, 0xC0000102
.equ FOREIGN_GS,         0x7000

.section .rodata
.balign 4
.Luser_mxcsr:
    .long 0x1F00
.Luser_fcw:
    .word 0x037E

.text

// void cosmos_test_user_gs_int2(UserGsResult* result, uint32_t vector)
// Vector 2 and 18 by int. 8 by a frame built here and a jump to its stub: int pushes no error code.
// +0 GS base after the stub, +8 MXCSR after, +12 FCW after, +16 DF after (0x400 if set)
.global cosmos_test_user_gs_int2
cosmos_test_user_gs_int2:
    push rbx
    push r12
    push r13
    push r14
    sub rsp, 24
    mov rbx, rdi
    pushfq
    pop r12
    cli
    stmxcsr dword ptr [rsp]
    fnstcw word ptr [rsp + 4]
    mov dword ptr [rsp + 8], esi
    mov edi, 8
    call _native_x64_get_irq_stub
    mov [rsp + 16], rax

    mov ecx, MSR_KERNEL_GS_BASE
    rdmsr
    mov r13d, eax
    mov r14d, edx
    mov eax, FOREIGN_GS
    xor edx, edx
    wrmsr

    ldmxcsr dword ptr [rip + .Luser_mxcsr]
    fldcw word ptr [rip + .Luser_fcw]
    std
    swapgs
    cmp dword ptr [rsp + 8], 18
    je .Lint18
    cmp dword ptr [rsp + 8], 8
    je .Lint8
    int 2
    jmp .Lafter_int
.Lint18:
    int 18
    jmp .Lafter_int
.Lint8:
    mov rax, [rsp + 16]
    mov rcx, rsp
    push 0x30
    push rcx
    pushfq
    push 0x28
    lea rdx, [rip + .Lafter_int]
    push rdx
    push 0
    jmp rax
.Lafter_int:
    pushfq
    pop rax
    cld
    and eax, 0x400
    mov [rbx + 16], rax
    stmxcsr dword ptr [rbx + 8]
    fnstcw word ptr [rbx + 12]
    mov ecx, MSR_GS_BASE
    rdmsr
    mov [rbx], eax
    mov [rbx + 4], edx
    swapgs

    mov ecx, MSR_KERNEL_GS_BASE
    mov eax, r13d
    mov edx, r14d
    wrmsr
    fldcw word ptr [rsp + 4]
    ldmxcsr dword ptr [rsp]
    push r12
    popfq
    add rsp, 24
    pop r14
    pop r13
    pop r12
    pop rbx
    ret

// void cosmos_test_cpu_state(CpuState* state): +0 GS base, +8 MXCSR, +12 FCW, +16 RFLAGS
.global cosmos_test_cpu_state
cosmos_test_cpu_state:
    pushfq
    pop rax
    mov [rdi + 16], rax
    stmxcsr dword ptr [rdi + 8]
    mov dword ptr [rdi + 12], 0
    fnstcw word ptr [rdi + 12]
    mov ecx, MSR_GS_BASE
    rdmsr
    mov [rdi], eax
    mov [rdi + 4], edx
    ret

// void cosmos_test_int2(void)
.global cosmos_test_int2
cosmos_test_int2:
    int 2
    ret
