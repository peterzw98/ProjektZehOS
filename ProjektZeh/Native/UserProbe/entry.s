// Zugang des Kernels zum Probeprogramm und zu Registern, die Cosmos nicht oeffentlich anbietet (nur -p:ZehOSUserModeProbe=true).
.intel_syntax noprefix
.text

.global zehos_user_probe_address
zehos_user_probe_address:
    lea     rax, [rip + probe_base]
    ret

.global zehos_user_probe_size
zehos_user_probe_size:
    lea     rax, [rip + probe_end]
    lea     rcx, [rip + probe_base]
    sub     rax, rcx
    ret

.global zehos_user_probe_cr3
zehos_user_probe_cr3:
    mov     rax, cr3
    ret

.global zehos_user_probe_tsc
zehos_user_probe_tsc:
    lfence
    rdtsc
    shl     rdx, 32
    or      rax, rdx
    ret

.global zehos_user_probe_cr4
zehos_user_probe_cr4:
    mov     rax, cr4
    ret

// Maskiert Interrupts und liefert die alten RFLAGS.
.global zehos_user_probe_mask
zehos_user_probe_mask:
    pushfq
    pop     rax
    cli
    ret

.global zehos_user_probe_restore
zehos_user_probe_restore:
    push    rdi
    popfq
    ret
