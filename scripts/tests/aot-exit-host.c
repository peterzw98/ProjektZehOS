// Hosttest des AOT-Ausstiegs (#423, R8): echte Assemblerroutinen aus zehos_host_aot_exit.c. Ein Signal spielt den
// Scheduler-Tick und setzt RIP wie der Cosmos-Hook; "AOT-Code" ist eine ausfuehrbare Seite mit einer Endlosschleife,
// die vorher callee-saved Register, DF, MXCSR und x87 verstellt und den Stack tief benutzt.
#define _GNU_SOURCE
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/time.h>
#include <ucontext.h>
#include <xmmintrin.h>
#include "zehos_host_aot_exit.h"

volatile int32_t zehos_host_abort;
zehos_aot_exit_state* zehos_aot_exit_state_address(void);
void* zehos_aot_exit_address(void);
volatile int32_t* zehos_host_abort_address(void);

static volatile int ticks, inHelper, redirects;
static uint8_t* page;
static int mode;    // 0 Schleife, 1 ok, 2 Fehlschlag, 3 Helfer ausserhalb des Codes

// push rax x2; xor ebx..r15d; std; ldmxcsr; fld1 x2; jmp $
static const uint8_t loop[] = { 0x31,0xdb, 0x31,0xed, 0x45,0x31,0xe4, 0x45,0x31,0xed, 0x45,0x31,0xf6, 0x45,0x31,0xff,
    0x68,0x80,0x7f,0x00,0x00, 0x0f,0xae,0x14,0x24, 0xd9,0xe8, 0xd9,0xe8, 0xfd, 0xeb,0xfe };

static void tick(int number, siginfo_t* info, void* context)
{
    (void)number; (void)info;
    __asm__ volatile("cld");
    ucontext_t* frame = context;
    zehos_aot_exit_state* state = zehos_aot_exit_state_address();
    uint64_t ip = (uint64_t)frame->uc_mcontext.gregs[REG_RIP], sp = (uint64_t)frame->uc_mcontext.gregs[REG_RSP];
    ticks++;
    if (!*zehos_host_abort_address() || state->code == 0 || state->stack == 0 || ip < state->code || ip >= state->codeEnd || sp >= state->stack) return;
    redirects++;
    frame->uc_mcontext.gregs[REG_RIP] = (greg_t)(uintptr_t)zehos_aot_exit_address();
}

bool wasm_runtime_call_wasm(void* env, void* function, uint32_t argc, uint32_t* argv)
{
    if (env != (void*)1 || function != (void*)2 || argc != 3) return false;
    argv[0] = 42;
    if (mode == 1) return true;
    if (mode == 2) return false;
    if (mode == 3) { int start = ticks; while (ticks < start + 5) inHelper = 1; return true; }
    volatile uint8_t deep[65536];
    deep[0] = 1;
    ((void (*)(void))page)();
    return deep[0] == 2;
}

// Ruft zehos_aot_call mit bekannten callee-saved Werten; Rueckgabe -1, wenn einer danach anders ist oder DF steht.
static int32_t call(uint32_t* argv)
{
    uint64_t bad;
    int32_t result;
    register uint32_t* argument __asm__("rcx") = argv;
    __asm__ volatile(
        "pushq %%rbp\n subq $136, %%rsp\n movq $0x11, %%rbx\n movq $0x22, %%rbp\n movq $0x33, %%r12\n movq $0x44, %%r13\n"
        "movq $0x55, %%r14\n movq $0x66, %%r15\n movl $1, %%edi\n movl $2, %%esi\n movl $3, %%edx\n"
        "movq %%rsp, (%%rsp)\n andq $-16, %%rsp\n call zehos_aot_call\n"
        "xorq $0x11, %%rbx\n xorq $0x22, %%rbp\n xorq $0x33, %%r12\n xorq $0x44, %%r13\n xorq $0x55, %%r14\n xorq $0x66, %%r15\n"
        "orq %%rbp, %%rbx\n orq %%r12, %%rbx\n orq %%r13, %%rbx\n orq %%r14, %%rbx\n orq %%r15, %%rbx\n"
        "pushfq\n popq %%rdx\n andq $0x400, %%rdx\n orq %%rdx, %%rbx\n addq $136, %%rsp\n popq %%rbp\n"
        : "=a"(result), "=b"(bad), "+r"(argument) :
        : "rdi", "rsi", "rdx", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15", "memory", "cc");
    return bad ? -1 : result;
}

#define CHECK(condition) do { if (!(condition)) { printf("FEHLER Zeile %d: %s\n", __LINE__, #condition); return 1; } } while (0)

int main(void)
{
    uint32_t argv[2];
    page = mmap(NULL, 4096, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    CHECK(page != MAP_FAILED);
    memcpy(page, loop, sizeof loop);
    struct sigaction action = { .sa_sigaction = tick, .sa_flags = SA_SIGINFO | SA_RESTART };
    sigaction(SIGALRM, &action, NULL);
    struct itimerval timer = { { 0, 2000 }, { 0, 2000 } };
    setitimer(ITIMER_REAL, &timer, NULL);
    zehos_aot_exit_state* state = zehos_aot_exit_state_address();
    state->code = (uint64_t)(uintptr_t)page;
    state->codeEnd = state->code + 4096;

    uint32_t mxcsr = _mm_getcsr();
    mode = 1; CHECK(call(argv) == ZEHOS_AOT_CALL_OK && argv[0] == 42 && state->stack == 0);
    mode = 2; CHECK(call(argv) == ZEHOS_AOT_CALL_FAILED && state->stack == 0);
    zehos_host_abort = 1;
    // Gegenprobe: ausserhalb des Codebereichs wird trotz Merker nie umgelenkt.
    mode = 3; CHECK(call(argv) == ZEHOS_AOT_CALL_OK && redirects == 0 && inHelper == 1);
    mode = 0;
    for (int i = 1; i <= 10; i++)
    {
        CHECK(call(argv) == ZEHOS_AOT_CALL_LEFT);
        CHECK(redirects == i && state->stack == 0 && _mm_getcsr() == mxcsr);
        // x87 leer: acht Ladevorgaenge ohne Ueberlauf.
        uint16_t status;
        __asm__ volatile("fld1\nfld1\nfld1\nfld1\nfld1\nfld1\nfld1\nfld1\nfnstsw %0\nfninit" : "=m"(status));
        CHECK((status & 0x40) == 0);
    }
    // Danach laeuft ein gewoehnlicher Aufruf wieder.
    zehos_host_abort = 0;
    mode = 1; CHECK(call(argv) == ZEHOS_AOT_CALL_OK);
    puts("OK");
    return 0;
}
