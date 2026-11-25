// ZehOS: siehe zehos_host_aot_exit.h. Nur x86-64, wie der AOT-Bau selbst.

#ifdef ZEHOS_WASM_AOT

#include "zehos_host_core.h"
#include "zehos_host_aot_exit.h"

zehos_aot_exit_state zehos_aot_exit_current;

// Die Sicherung liegt auf dem Worker-Stack, statisch nur der Zeiger darauf: Die callee-saved Register koennen Referenzen
// des C#-Aufrufers halten, und der GC durchsucht nur Stack und gesicherte Register. Ohne CFI, damit sein genauer Lauf
// aus einem Hostrueckruf hier in den konservativen uebergeht.
// Am Ausstieg sind Register, Flags und FPU die des AOT-Codes: DF, MXCSR und x87 muessen wieder zum Kernelstand passen.
__asm__(
    ".text\n"
    ".globl zehos_aot_call\n"
    ".type zehos_aot_call, @function\n"
    "zehos_aot_call:\n"
    "    pushq %rbp\n"
    "    pushq %rbx\n"
    "    pushq %r12\n"
    "    pushq %r13\n"
    "    pushq %r14\n"
    "    pushq %r15\n"
    "    subq $8, %rsp\n"
    "    stmxcsr (%rsp)\n"
    "    fnstcw 4(%rsp)\n"
    "    movq %rsp, 16+zehos_aot_exit_current(%rip)\n"
    "    call wasm_runtime_call_wasm\n"
    "    movzbl %al, %eax\n"
    "    movq $0, 16+zehos_aot_exit_current(%rip)\n"
    "    addq $8, %rsp\n"
    "1:  popq %r15\n"
    "    popq %r14\n"
    "    popq %r13\n"
    "    popq %r12\n"
    "    popq %rbx\n"
    "    popq %rbp\n"
    "    ret\n"
    ".size zehos_aot_call, . - zehos_aot_call\n"
    ".globl zehos_aot_exit\n"
    ".type zehos_aot_exit, @function\n"
    "zehos_aot_exit:\n"
    "    movq 16+zehos_aot_exit_current(%rip), %rsp\n"
    "    movq $0, 16+zehos_aot_exit_current(%rip)\n"
    "    cld\n"
    "    fninit\n"
    "    fldcw 4(%rsp)\n"
    "    ldmxcsr (%rsp)\n"
    "    movl $2, %eax\n"
    "    addq $8, %rsp\n"
    "    jmp 1b\n"
    ".size zehos_aot_exit, . - zehos_aot_exit\n");

extern void zehos_aot_exit(void);

// Adressen fuer den Tick (CosmosAotLoopExit.cs); einmal beim Start abgefragt, im Tick selbst kein P/Invoke.
zehos_aot_exit_state* zehos_aot_exit_state_address(void) { return &zehos_aot_exit_current; }
void* zehos_aot_exit_address(void) { return (void*)zehos_aot_exit; }
volatile int32_t* zehos_host_abort_address(void) { return &zehos_host_abort; }

#endif
