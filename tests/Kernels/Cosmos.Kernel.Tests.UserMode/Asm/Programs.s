.intel_syntax noprefix

// ZehOS patch user-mode-probe, see PATCHES.md
// The ring 3 programs of the suite: position-independent, copied to a user page. Calls: 1 echoes
// rdi + 1, 2 leaves with rdi, 3 runs the kernel side's frame change. rdi, rsi and rdx arrive as
// zero, so a program takes its argument from the data page at rsp.

.equ SYS_ECHO,   1
.equ SYS_LEAVE,  2
.equ SYS_CHANGE, 3
.equ WORK_ROUNDS, 64    // Kernel.cs WorkRounds

.section .rodata
.balign 16
.global cosmos_test_user_programs
cosmos_test_user_programs:

.macro PROGRAM n
    .balign 512, 0xCC
.Lprogram\n:
.endm

PROGRAM 0               // a system call and its result
    mov edi, 0x1234
    mov eax, SYS_ECHO
    syscall
    mov rdi, rax
    mov eax, SYS_LEAVE
    syscall

PROGRAM 1               // #UD; goes on behind it if the handler says so
    ud2
    mov edi, 0x77
    mov eax, SYS_LEAVE
    syscall

PROGRAM 2               // #PF, write to the address in the data page
    mov rax, [rsp]
    mov qword ptr [rax], 1
    mov edi, 0x78
    mov eax, SYS_LEAVE
    syscall

PROGRAM 3               // #PF, execute the address in the data page
    jmp qword ptr [rsp]

PROGRAM 4               // #GP
    cli
    ud2

PROGRAM 5               // no system call, ever; the counter shows that it runs
1:  inc qword ptr [rsp]
    pause
    jmp 1b

PROGRAM 6               // the kernel side changes the frame
    mov eax, SYS_CHANGE
    syscall
    mov edi, 0x79
    mov eax, SYS_LEAVE
    syscall

PROGRAM 7               // NT, which ring 3 may set
    pushfq
    or qword ptr [rsp], 0x4000
    popfq
    mov eax, SYS_ECHO
    syscall
    mov edi, 0x7A
    mov eax, SYS_LEAVE
    syscall

PROGRAM 8               // registers across interrupts and system calls; 0 if all are unchanged
    mov rbx, 0x1111
    mov rbp, 0x2222
    mov r12, 0x3333
    mov r13, 0x4444
    mov r14, 0x5555
    mov r15, 0x6666
    mov r8, 0x7777
    mov r9, 0x8888
    mov r10, 0x9999
    mov rax, 0x123456789ABC
    movq xmm0, rax
    movq xmm15, rax
    mov dword ptr [rsp], 0x7F80
    ldmxcsr dword ptr [rsp]
    mov word ptr [rsp], 0x0F7F
    fldcw word ptr [rsp]
    std
    mov rsi, 200000
1:  mov edi, 5
    mov eax, SYS_ECHO
    syscall
    cmp rax, 6
    jne 9f
    mov edx, 200
2:  pause
    dec edx
    jnz 2b
    cmp rbx, 0x1111
    jne 9f
    cmp rbp, 0x2222
    jne 9f
    cmp r12, 0x3333
    jne 9f
    cmp r13, 0x4444
    jne 9f
    cmp r14, 0x5555
    jne 9f
    cmp r15, 0x6666
    jne 9f
    cmp r8, 0x7777
    jne 9f
    cmp r9, 0x8888
    jne 9f
    cmp r10, 0x9999
    jne 9f
    mov rcx, 0x123456789ABC
    movq rax, xmm0
    cmp rax, rcx
    jne 9f
    movq rax, xmm15
    cmp rax, rcx
    jne 9f
    stmxcsr dword ptr [rsp]
    cmp dword ptr [rsp], 0x7F80
    jne 9f
    fnstcw word ptr [rsp]
    cmp word ptr [rsp], 0x0F7F
    jne 9f
    pushfq
    pop rax
    test eax, 0x400
    jz 9f
    dec rsi
    jnz 1b
    xor edi, edi
    mov eax, SYS_LEAVE
    syscall
9:  mov edi, 1
    mov eax, SYS_LEAVE
    syscall

PROGRAM 9               // FS base: fs:[0] is the value in the data page, for a while; 0 if it stays
    mov rsi, 100000000
1:  mov rax, fs:[0]
    cmp rax, [rsp]
    jne 9f
    dec rsi
    jnz 1b
    xor edi, edi
    mov eax, SYS_LEAVE
    syscall
9:  mov edi, 1
    mov eax, SYS_LEAVE
    syscall

PROGRAM 10              // what a program starts with: the or of all registers, 0
    or rax, rbx
    or rax, rcx
    or rax, rdx
    or rax, rsi
    or rax, rdi
    or rax, rbp
    or rax, r8
    or rax, r9
    or rax, r10
    or rax, r11
    or rax, r12
    or rax, r13
    or rax, r14
    or rax, r15
    mov rdi, rax
    movq rax, xmm0
    or rdi, rax
    movq rax, xmm7
    or rdi, rax
    mov eax, SYS_LEAVE
    syscall

PROGRAM 11              // an invalid stack pointer, met by the next interrupt
    mov rax, 0x8000000000000000
    mov rsp, rax
1:  pause
    jmp 1b

PROGRAM 12              // #UD: ring 3 must not set the GS base; a base with bit 63 looks like the kernel's
    mov rax, 0xFFFF800000000000
    wrgsbase rax
    mov edi, 0x7B
    mov eax, SYS_LEAVE
    syscall

PROGRAM 13              // returns from interrupts with work (UserMode.RequestWork): every register survives
    // ZehOS patch user-return-work, see PATCHES.md
    // [rsp] counts, [rsp + 8] the handler's runs (written by the kernel), [rsp + 24] scratch. 0 if
    // all GPRs, XMM0-15 (both halves), MXCSR, FCW and DF are as set after the runs; else the number.
    mov dword ptr [rsp + 16], 0x7F80
    ldmxcsr dword ptr [rsp + 16]
    mov word ptr [rsp + 16], 0x0F7F
    fldcw word ptr [rsp + 16]
    mov rax, 0x01010101A5A50101
    movq xmm0, rax
    pshufd xmm0, xmm0, 0x44
    mov rax, 0x02020202A5A50202
    movq xmm1, rax
    pshufd xmm1, xmm1, 0x44
    mov rax, 0x03030303A5A50303
    movq xmm2, rax
    pshufd xmm2, xmm2, 0x44
    mov rax, 0x04040404A5A50404
    movq xmm3, rax
    pshufd xmm3, xmm3, 0x44
    mov rax, 0x05050505A5A50505
    movq xmm4, rax
    pshufd xmm4, xmm4, 0x44
    mov rax, 0x06060606A5A50606
    movq xmm5, rax
    pshufd xmm5, xmm5, 0x44
    mov rax, 0x07070707A5A50707
    movq xmm6, rax
    pshufd xmm6, xmm6, 0x44
    mov rax, 0x08080808A5A50808
    movq xmm7, rax
    pshufd xmm7, xmm7, 0x44
    mov rax, 0x09090909A5A50909
    movq xmm8, rax
    pshufd xmm8, xmm8, 0x44
    mov rax, 0x0A0A0A0AA5A50A0A
    movq xmm9, rax
    pshufd xmm9, xmm9, 0x44
    mov rax, 0x0B0B0B0BA5A50B0B
    movq xmm10, rax
    pshufd xmm10, xmm10, 0x44
    mov rax, 0x0C0C0C0CA5A50C0C
    movq xmm11, rax
    pshufd xmm11, xmm11, 0x44
    mov rax, 0x0D0D0D0DA5A50D0D
    movq xmm12, rax
    pshufd xmm12, xmm12, 0x44
    mov rax, 0x0E0E0E0EA5A50E0E
    movq xmm13, rax
    pshufd xmm13, xmm13, 0x44
    mov rax, 0x0F0F0F0FA5A50F0F
    movq xmm14, rax
    pshufd xmm14, xmm14, 0x44
    mov rax, 0x10101010A5A51010
    movq xmm15, rax
    pshufd xmm15, xmm15, 0x44
    mov rax, 0xA100A100A100A100
    mov rbx, 0xA200A200A200A200
    mov rcx, 0xA300A300A300A300
    mov rdx, 0xA400A400A400A400
    mov rsi, 0xA500A500A500A500
    mov rdi, 0xA600A600A600A600
    mov rbp, 0xA700A700A700A700
    mov r8, 0xA800A800A800A800
    mov r9, 0xA900A900A900A900
    mov r10, 0xAA00AA00AA00AA00
    mov r11, 0xAB00AB00AB00AB00
    mov r12, 0xAC00AC00AC00AC00
    mov r13, 0xAD00AD00AD00AD00
    mov r14, 0xAE00AE00AE00AE00
    mov r15, 0xAF00AF00AF00AF00
    std
1:  inc qword ptr [rsp]
    cmp qword ptr [rsp + 8], WORK_ROUNDS
    jb 1b
    mov qword ptr [rsp + 24], 1
    push rax
    mov rax, 0xA100A100A100A100
    xchg rax, [rsp]
    cmp rax, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 2
    push rax
    mov rax, 0xA200A200A200A200
    xchg rax, [rsp]
    cmp rbx, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 3
    push rax
    mov rax, 0xA300A300A300A300
    xchg rax, [rsp]
    cmp rcx, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 4
    push rax
    mov rax, 0xA400A400A400A400
    xchg rax, [rsp]
    cmp rdx, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 5
    push rax
    mov rax, 0xA500A500A500A500
    xchg rax, [rsp]
    cmp rsi, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 6
    push rax
    mov rax, 0xA600A600A600A600
    xchg rax, [rsp]
    cmp rdi, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 7
    push rax
    mov rax, 0xA700A700A700A700
    xchg rax, [rsp]
    cmp rbp, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 8
    push rax
    mov rax, 0xA800A800A800A800
    xchg rax, [rsp]
    cmp r8, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 9
    push rax
    mov rax, 0xA900A900A900A900
    xchg rax, [rsp]
    cmp r9, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 10
    push rax
    mov rax, 0xAA00AA00AA00AA00
    xchg rax, [rsp]
    cmp r10, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 11
    push rax
    mov rax, 0xAB00AB00AB00AB00
    xchg rax, [rsp]
    cmp r11, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 12
    push rax
    mov rax, 0xAC00AC00AC00AC00
    xchg rax, [rsp]
    cmp r12, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 13
    push rax
    mov rax, 0xAD00AD00AD00AD00
    xchg rax, [rsp]
    cmp r13, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 14
    push rax
    mov rax, 0xAE00AE00AE00AE00
    xchg rax, [rsp]
    cmp r14, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    mov qword ptr [rsp + 24], 15
    push rax
    mov rax, 0xAF00AF00AF00AF00
    xchg rax, [rsp]
    cmp r15, [rsp]
    lea rsp, [rsp + 8]
    jne 8f
    cld
    mov rcx, 0x01010101A5A50101
    mov edi, 32
    movq rax, xmm0
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm0, xmm0
    movq rax, xmm0
    cmp rax, rcx
    jne 9f
    mov rcx, 0x02020202A5A50202
    mov edi, 33
    movq rax, xmm1
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm1, xmm1
    movq rax, xmm1
    cmp rax, rcx
    jne 9f
    mov rcx, 0x03030303A5A50303
    mov edi, 34
    movq rax, xmm2
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm2, xmm2
    movq rax, xmm2
    cmp rax, rcx
    jne 9f
    mov rcx, 0x04040404A5A50404
    mov edi, 35
    movq rax, xmm3
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm3, xmm3
    movq rax, xmm3
    cmp rax, rcx
    jne 9f
    mov rcx, 0x05050505A5A50505
    mov edi, 36
    movq rax, xmm4
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm4, xmm4
    movq rax, xmm4
    cmp rax, rcx
    jne 9f
    mov rcx, 0x06060606A5A50606
    mov edi, 37
    movq rax, xmm5
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm5, xmm5
    movq rax, xmm5
    cmp rax, rcx
    jne 9f
    mov rcx, 0x07070707A5A50707
    mov edi, 38
    movq rax, xmm6
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm6, xmm6
    movq rax, xmm6
    cmp rax, rcx
    jne 9f
    mov rcx, 0x08080808A5A50808
    mov edi, 39
    movq rax, xmm7
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm7, xmm7
    movq rax, xmm7
    cmp rax, rcx
    jne 9f
    mov rcx, 0x09090909A5A50909
    mov edi, 40
    movq rax, xmm8
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm8, xmm8
    movq rax, xmm8
    cmp rax, rcx
    jne 9f
    mov rcx, 0x0A0A0A0AA5A50A0A
    mov edi, 41
    movq rax, xmm9
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm9, xmm9
    movq rax, xmm9
    cmp rax, rcx
    jne 9f
    mov rcx, 0x0B0B0B0BA5A50B0B
    mov edi, 42
    movq rax, xmm10
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm10, xmm10
    movq rax, xmm10
    cmp rax, rcx
    jne 9f
    mov rcx, 0x0C0C0C0CA5A50C0C
    mov edi, 43
    movq rax, xmm11
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm11, xmm11
    movq rax, xmm11
    cmp rax, rcx
    jne 9f
    mov rcx, 0x0D0D0D0DA5A50D0D
    mov edi, 44
    movq rax, xmm12
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm12, xmm12
    movq rax, xmm12
    cmp rax, rcx
    jne 9f
    mov rcx, 0x0E0E0E0EA5A50E0E
    mov edi, 45
    movq rax, xmm13
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm13, xmm13
    movq rax, xmm13
    cmp rax, rcx
    jne 9f
    mov rcx, 0x0F0F0F0FA5A50F0F
    mov edi, 46
    movq rax, xmm14
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm14, xmm14
    movq rax, xmm14
    cmp rax, rcx
    jne 9f
    mov rcx, 0x10101010A5A51010
    mov edi, 47
    movq rax, xmm15
    cmp rax, rcx
    jne 9f
    punpckhqdq xmm15, xmm15
    movq rax, xmm15
    cmp rax, rcx
    jne 9f
    mov edi, 0x40
    stmxcsr dword ptr [rsp + 16]
    cmp dword ptr [rsp + 16], 0x7F80
    jne 9f
    mov edi, 0x41
    fnstcw word ptr [rsp + 16]
    cmp word ptr [rsp + 16], 0x0F7F
    jne 9f
    xor edi, edi
    mov eax, SYS_LEAVE
    syscall
8:  cld
    mov rdi, [rsp + 24]
9:  mov eax, SYS_LEAVE
    syscall

    .balign 512, 0xCC
.Lprograms_end:

.text
.global cosmos_test_user_programs_address
cosmos_test_user_programs_address:
    lea rax, [rip + cosmos_test_user_programs]
    ret

// ZehOS patch user-return-work, see PATCHES.md
// void cosmos_test_user_clobber_fpu(): what kernel code may leave in the FPU and SSE registers.
.global cosmos_test_user_clobber_fpu
cosmos_test_user_clobber_fpu:
    mov rax, 0xDEADBEEFDEADBEEF
    movq xmm0, rax
    pshufd xmm0, xmm0, 0x44
    .irp i, 1,2,3,4,5,6,7,8,9,10,11,12,13,14,15
    movdqa xmm\i, xmm0
    .endr
    fninit
    sub rsp, 8
    mov dword ptr [rsp], 0x1F80
    ldmxcsr dword ptr [rsp]
    add rsp, 8
    xor eax, eax
    xor ecx, ecx
    xor edx, edx
    xor esi, esi
    xor edi, edi
    xor r8d, r8d
    xor r9d, r9d
    xor r10d, r10d
    xor r11d, r11d
    ret

.global cosmos_test_user_programs_size
cosmos_test_user_programs_size:
    lea rax, [rip + .Lprograms_end]
    lea rcx, [rip + cosmos_test_user_programs]
    sub rax, rcx
    ret
