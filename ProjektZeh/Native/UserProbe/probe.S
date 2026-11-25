/*
 * Ring-3-Probeprogramm fuer die Gastfaelle von N1-A6 (docs/superpowers/plans/2026-11-24-ladybird-n1-nachweise.md).
 *
 * Flaches, positionsunabhaengiges Binaerabbild ohne Relokation: Der Kernel bildet es an eine beliebige Seitengrenze der
 * unteren Haelfte ab (lesbar und ausfuehrbar, nicht schreibbar) und springt mit UserMode.Enter(basis + versatz, rsp) in
 * einen Einstieg. Jeder Fall hat einen festen Einstieg; die Versaetze stehen in der Kopftabelle und aendern sich nicht,
 * wenn ein Fall waechst (Platzhalter mit .org, Ueberlauf laesst den Bau scheitern). Aufrufer lesen den Versatz immer ueber den
 * NAMEN aus der Tabelle (ZUP1), nie als Zahl im Code. Platz: Tabelle bis 64 Eintraege (ab 0x10), Slots 0x1000 + 0x10 * Index,
 * Ruempfe ab 0x1400; neue Faelle (N1-B4) werden am Ende der Tabelle und der Slots angehaengt.
 *
 * Annahmen ueber den CPU-Zustand (sonst endet ein Fall nur durch das Zeitlimit statt durch die erwartete Ausnahme):
 * die TSS hat keine E/A-Bitmap (iomap_base >= Limit, sonst ist `in` kein #GP), CR4.PVI = 0 (sonst ist `cli` kein #GP),
 * CR4.TSD = 0 (sonst ist `rdtsc` in loop ein #GP). Grenzen der Messung: loop liefert die Summe der TSC-Ticks ueber
 * 1 000 000 Aufrufe, also nur einen Mittelwert je Lauf (kein Median je Aufruf); die TSC-Frequenz muss der Kernel loggen.
 * KERNEL_ADDR ist der HHDM-Anfang: ob dort gemappt ist (Schutzverletzung) oder nicht, haengt vom Lader ab; beides ist #PF,
 * der Fehlercode (P-Bit) gehoert ins Log. Namen in der Tabelle sind hoechstens 23 Zeichen, damit ein NUL folgt.
 *
 * Systemaufrufe (syscall; rax = Nummer, Rueckgabe in rax, rcx und r11 zerstoert wie bei Linux):
 *   0x7A01  zaehlen: der Kernel erhoeht einen Zaehler; keine Argumente.
 *   0x7A02  beenden: rdi = Ergebnis (0 = Fall erreicht sein Ziel, sonst Fehlerkennung), rsi = Hilfswert (loop: TSC-Ticks
 *           des gesamten Laufs). Der Handler ruft LeaveCurrent; der Aufruf kehrt nie zurueck.
 * Faelle ohne Ende (spin) und fault_* beenden nicht selbst: spin endet durch RequestLeave, fault_* durch den FaultHandler.
 * Stack: nur fpu braucht 32 Byte unterhalb von rsp, kein anderer Fall fasst den Stack an.
 */

        .intel_syntax noprefix
        .text
        .global probe_base
probe_base:

.equ SYS_COUNT, 0x7A01
.equ SYS_EXIT,  0x7A02
.equ LOOP_CALLS, 1000000
.equ FPU_ROUNDS, 20000000               /* Runden ohne Systemaufruf; A6 darf anpassen, bis die Laufzeit passt */
.equ KERNEL_ADDR, 0xFFFF800000000000     /* kanonisch, obere Haelfte: #PF statt #GP */

.equ TABLE_OFF, 0x000
.equ SLOT_OFF,  0x1000
.equ SLOT_SIZE, 0x10
.equ BODY_OFF,  0x1400
.equ CAPACITY,  64

/* Kopf: "ZUP1", Anzahl, Eintragsgroesse (32), Kapazitaet; je Eintrag Name (24 Byte, mit 0 aufgefuellt), Versatz (u32), 0 (u32). */
        .org TABLE_OFF
        .ascii "ZUP1"
        .long 13
        .long 32
        .long CAPACITY

/* Ein zu langer Name (> 24 Byte) ergibt eine as-Warnung (.fill mit negativer Anzahl); build.sh macht daraus einen Abbruch. */
.macro NAMED name, index
1:      .ascii "\name"
        .fill 24 - (. - 1b), 1, 0       /* Namen bis 23 Zeichen: ein NUL folgt */
        .long SLOT_OFF + SLOT_SIZE * \index
        .long 0
.endm

        NAMED loop, 0
        NAMED spin, 1
        NAMED fpu, 2
        NAMED fault_de, 3
        NAMED fault_ud, 4
        NAMED fault_gp_cli, 5
        NAMED fault_gp_hlt, 6
        NAMED fault_gp_in, 7
        NAMED fault_pf_read, 8
        NAMED fault_pf_write, 9
        NAMED fault_pf_exec, 10
        NAMED badret, 11
        NAMED leave, 12

.macro SLOT index, target
        .org SLOT_OFF + SLOT_SIZE * \index, 0xCC   /* Luecken als int3 */
        jmp \target
.endm

        SLOT 0, body_loop
        SLOT 1, body_spin
        SLOT 2, body_fpu
        SLOT 3, body_fault_de
        SLOT 4, body_fault_ud
        SLOT 5, body_fault_gp_cli
        SLOT 6, body_fault_gp_hlt
        SLOT 7, body_fault_gp_in
        SLOT 8, body_fault_pf_read
        SLOT 9, body_fault_pf_write
        SLOT 10, body_fault_pf_exec
        SLOT 11, body_badret
        SLOT 12, body_leave

        .org BODY_OFF, 0xCC

/* loop: 1 000 000 Zaehlaufrufe, danach Ende mit rsi = verstrichene TSC-Ticks (Messung fuer N1-A7). */
body_loop:
        rdtsc
        shl rdx, 32
        or rax, rdx
        mov r8, rax
        mov r9d, LOOP_CALLS
1:      mov eax, SYS_COUNT
        syscall
        dec r9d
        jnz 1b
        rdtsc
        shl rdx, 32
        or rax, rdx
        sub rax, r8
        mov rsi, rax
        xor edi, edi
        mov eax, SYS_EXIT
        syscall
        jmp .                           /* nie erreicht */

/* spin: kein Systemaufruf; nur Unterbrechungen holen den Thread zurueck. */
body_spin:
1:      pause
        jmp 1b

/* fpu: x87-Stapel, FCW, MXCSR und XMM0-15 mit festen Werten belegen, FPU_ROUNDS Runden ohne Systemaufruf laufen, vergleichen.
 * Ende: rdi = 0 unveraendert, 1 x87-Stapel, 2 FCW, 3 MXCSR, 4 XMM. */
body_fpu:
        fninit
        fldcw [rip + fpu_fcw]
        ldmxcsr [rip + fpu_mxcsr]
        fld1
        fldpi
        fldl2e
.irp i, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
        movdqa xmm\i, [rip + fpu_xmm + 16 * \i]
.endr
        mov ecx, FPU_ROUNDS
1:      pause
        dec ecx
        jnz 1b
        mov edi, 1
        fldl2e
        fucomip st, st(1)
        jne fpu_fail
        jp fpu_fail
        fstp st(0)
        fldpi
        fucomip st, st(1)
        jne fpu_fail
        jp fpu_fail
        fstp st(0)
        fld1
        fucomip st, st(1)
        jne fpu_fail
        jp fpu_fail
        fstp st(0)
        mov edi, 2
        fnstcw [rsp - 8]
        cmp word ptr [rsp - 8], 0x027F
        jne fpu_fail
        mov edi, 3
        stmxcsr [rsp - 8]
        cmp dword ptr [rsp - 8], 0x5F80
        jne fpu_fail
        mov edi, 4
        movdqu [rsp - 24], xmm0         /* xmm0 dient als Hilfsregister; Original merken */
.irp i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
        movdqa xmm0, [rip + fpu_xmm + 16 * \i]
        pcmpeqb xmm0, xmm\i
        pmovmskb eax, xmm0
        cmp eax, 0xFFFF
        jne fpu_fail
.endr
        movdqu xmm0, [rsp - 24]
        movdqa xmm1, [rip + fpu_xmm]   /* xmm0 zuletzt: xmm1 ist geprueft und dient als Hilfsregister */
        pcmpeqb xmm1, xmm0
        pmovmskb eax, xmm1
        cmp eax, 0xFFFF
        jne fpu_fail
        xor edi, edi
fpu_fail:
        xor esi, esi
        mov eax, SYS_EXIT
        syscall
        jmp .

body_fault_de:
        xor edx, edx
        xor ecx, ecx
        mov eax, 1
        div ecx
        jmp .
body_fault_ud:
        ud2
        jmp .
body_fault_gp_cli:
        cli
        jmp .
body_fault_gp_hlt:
        hlt
        jmp .
body_fault_gp_in:
        in al, 0x80                     /* IOPL 0, kein E/A-Bitfeld: #GP */
        jmp .
body_fault_pf_read:
        mov rax, KERNEL_ADDR
        mov rax, [rax]
        jmp .
body_fault_pf_write:
        mov rax, KERNEL_ADDR
        mov qword ptr [rax], 0
        jmp .
body_fault_pf_exec:
        mov rax, KERNEL_ADDR
        jmp rax

/* badret: ein Aufruf, dann Ende; der Kernelhandler verfaelscht die Ruecksprungadresse, Enter muss mit Fehler zurueckkehren. */
body_badret:
        mov eax, SYS_COUNT
        syscall
        xor edi, edi
        xor esi, esi
        mov eax, SYS_EXIT
        syscall
        jmp .

/* leave: sofort beenden (1000-mal Enter und LeaveCurrent). */
body_leave:
        xor edi, edi
        xor esi, esi
        mov eax, SYS_EXIT
        syscall
        jmp .

        .balign 16
        .global probe_data
probe_data:
fpu_xmm:
.irp i, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
        .quad 0x1122334455667700 + \i
        .quad 0xFFEEDDCCBBAA9900 - \i
.endr
fpu_fcw:
        .word 0x027F                    /* doppelte Genauigkeit, alle Ausnahmen maskiert (Standard waere 0x037F) */
fpu_mxcsr:
        .long 0x5F80                    /* Rundung nach oben, alle Ausnahmen maskiert (Standard waere 0x1F80) */
probe_end:
