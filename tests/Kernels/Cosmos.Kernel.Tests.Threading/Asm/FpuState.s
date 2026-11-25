.intel_syntax noprefix

// ZehOS patch fxsave, see PATCHES.md
// Holds a distinct x87/SSE state in a thread, for the FPU_* cells.
// FpuHold (Kernel.cs): +0 mxcsr, +4 fcw, +8 x87 base, +16 XMM pattern, +24 stop,
// +32 loops, +40 mismatch bits (1 MXCSR, 2 FCW). The stack is moved first: the
// kernel takes interrupts on it, so there is no red zone.

.text

// void cosmos_test_fpu_hold(FpuHold* hold, void* before, void* after)
.global cosmos_test_fpu_hold
cosmos_test_fpu_hold:
    sub rsp, 24
    fninit
    fldcw word ptr [rdi + 4]
    ldmxcsr dword ptr [rdi]

    mov ecx, 8
1:
    inc qword ptr [rdi + 8]
    fild qword ptr [rdi + 8]
    dec ecx
    jnz 1b

    movq xmm0, qword ptr [rdi + 16]
    punpcklqdq xmm0, xmm0
    movdqa xmm1, xmm0
    paddq xmm1, xmm0
    movdqa xmm2, xmm1
    paddq xmm2, xmm0
    movdqa xmm3, xmm2
    paddq xmm3, xmm0
    movdqa xmm4, xmm3
    paddq xmm4, xmm0
    movdqa xmm5, xmm4
    paddq xmm5, xmm0
    movdqa xmm6, xmm5
    paddq xmm6, xmm0
    movdqa xmm7, xmm6
    paddq xmm7, xmm0
    movdqa xmm8, xmm7
    paddq xmm8, xmm0
    movdqa xmm9, xmm8
    paddq xmm9, xmm0
    movdqa xmm10, xmm9
    paddq xmm10, xmm0
    movdqa xmm11, xmm10
    paddq xmm11, xmm0
    movdqa xmm12, xmm11
    paddq xmm12, xmm0
    movdqa xmm13, xmm12
    paddq xmm13, xmm0
    movdqa xmm14, xmm13
    paddq xmm14, xmm0
    movdqa xmm15, xmm14
    paddq xmm15, xmm0
    fxsave64 [rsi]

2:
    inc qword ptr [rdi + 32]
    stmxcsr dword ptr [rsp]
    mov eax, dword ptr [rsp]
    cmp eax, dword ptr [rdi]
    je 3f
    or qword ptr [rdi + 40], 1
3:
    fnstcw word ptr [rsp]
    mov ax, word ptr [rsp]
    cmp ax, word ptr [rdi + 4]
    je 4f
    or qword ptr [rdi + 40], 2
4:
    pause
    cmp dword ptr [rdi + 24], 0
    je 2b

    fxsave64 [rdx]
    fninit
    mov dword ptr [rsp], 0x1F80
    ldmxcsr dword ptr [rsp]
    add rsp, 24
    ret

// void cosmos_test_fpu_capture(void* image)
.global cosmos_test_fpu_capture
cosmos_test_fpu_capture:
    fxsave64 [rdi]
    ret
