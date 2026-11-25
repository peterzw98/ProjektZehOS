.intel_syntax noprefix

// ZehOS patch user-mode-probe, see PATCHES.md
// The ring 3 programs of the suite: position-independent, copied to a user page. Calls: 1 echoes
// rdi + 1, 2 leaves with rdi, 3 runs the kernel side's frame change. rdi, rsi and rdx arrive as
// zero, so a program takes its argument from the data page at rsp.

.equ SYS_ECHO,   1
.equ SYS_LEAVE,  2
.equ SYS_CHANGE, 3

.section .rodata
.balign 16
.global cosmos_test_user_programs
cosmos_test_user_programs:

.macro PROGRAM n
    .balign 256, 0xCC
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

    .balign 256, 0xCC
.Lprograms_end:

.text
.global cosmos_test_user_programs_address
cosmos_test_user_programs_address:
    lea rax, [rip + cosmos_test_user_programs]
    ret

.global cosmos_test_user_programs_size
cosmos_test_user_programs_size:
    lea rax, [rip + .Lprograms_end]
    lea rcx, [rip + cosmos_test_user_programs]
    sub rax, rcx
    ret
