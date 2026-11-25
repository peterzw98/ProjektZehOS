.intel_syntax noprefix

// ZehOS patch apic-hhdm, see PATCHES.md
.global cosmos_test_load_cr3
.global cosmos_test_read_cr3

.text

cosmos_test_load_cr3:
    mov     cr3, rdi
    ret

cosmos_test_read_cr3:
    mov     rax, cr3
    ret
