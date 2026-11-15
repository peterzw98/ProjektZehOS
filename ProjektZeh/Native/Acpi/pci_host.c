#include <stdint.h>

/* Cosmos 3.0.94 stellt nur unbenutzte generische PCI-Stubs bereit; LAI ruft diese sechs Symbole auf. */
extern void laihost_outb(uint16_t port, uint8_t value);
extern void laihost_outw(uint16_t port, uint16_t value);
extern void laihost_outd(uint16_t port, uint32_t value);
extern uint8_t laihost_inb(uint16_t port);
extern uint16_t laihost_inw(uint16_t port);
extern uint32_t laihost_ind(uint16_t port);

#ifndef ZEHOS_PCI_HOST_TEST
static uint64_t pci_lock(void) {
    uint64_t flags;
    /* Adresse und Daten gehoeren zusammen; auf der Ein-CPU-Referenz darf kein IRQ dazwischenfunken. */
    __asm__ volatile ("pushfq; popq %0; cli" : "=r"(flags) : : "memory");
    return flags;
}

static void pci_unlock(uint64_t flags) {
    if (flags & 0x200) __asm__ volatile ("sti" : : : "memory");
}
#endif

static int pci_valid(uint16_t segment, uint8_t slot, uint8_t function, uint16_t offset, unsigned int width) {
    /* Der vorhandene x64-PCI-Host nutzt Mechanismus 1: Segment 0, 256 Byte Konfigurationsraum. */
    return segment == 0 && slot < 32 && function < 8 && offset < 256 && (offset & (width - 1)) == 0;
}

static uint16_t pci_select(uint8_t bus, uint8_t slot, uint8_t function, uint16_t offset) {
    laihost_outd(0xcf8, 0x80000000u | ((uint32_t)bus << 16) | ((uint32_t)slot << 11) |
        ((uint32_t)function << 8) | (offset & 0xfc));
    return (uint16_t)(0xcfc + (offset & 3));
}

uint8_t laihost_pci_readb(uint16_t segment, uint8_t bus, uint8_t slot, uint8_t function, uint16_t offset) {
    if (!pci_valid(segment, slot, function, offset, 1)) return UINT8_MAX;
    uint64_t flags = pci_lock();
    uint8_t value = laihost_inb(pci_select(bus, slot, function, offset));
    pci_unlock(flags);
    return value;
}

uint16_t laihost_pci_readw(uint16_t segment, uint8_t bus, uint8_t slot, uint8_t function, uint16_t offset) {
    if (!pci_valid(segment, slot, function, offset, 2)) return UINT16_MAX;
    uint64_t flags = pci_lock();
    uint16_t value = laihost_inw(pci_select(bus, slot, function, offset));
    pci_unlock(flags);
    return value;
}

uint32_t laihost_pci_readd(uint16_t segment, uint8_t bus, uint8_t slot, uint8_t function, uint16_t offset) {
    if (!pci_valid(segment, slot, function, offset, 4)) return UINT32_MAX;
    uint64_t flags = pci_lock();
    uint32_t value = laihost_ind(pci_select(bus, slot, function, offset));
    pci_unlock(flags);
    return value;
}

void laihost_pci_writeb(uint16_t segment, uint8_t bus, uint8_t slot, uint8_t function, uint16_t offset, uint8_t value) {
    if (!pci_valid(segment, slot, function, offset, 1)) return;
    uint64_t flags = pci_lock();
    laihost_outb(pci_select(bus, slot, function, offset), value);
    pci_unlock(flags);
}

void laihost_pci_writew(uint16_t segment, uint8_t bus, uint8_t slot, uint8_t function, uint16_t offset, uint16_t value) {
    if (!pci_valid(segment, slot, function, offset, 2)) return;
    uint64_t flags = pci_lock();
    laihost_outw(pci_select(bus, slot, function, offset), value);
    pci_unlock(flags);
}

void laihost_pci_writed(uint16_t segment, uint8_t bus, uint8_t slot, uint8_t function, uint16_t offset, uint32_t value) {
    if (!pci_valid(segment, slot, function, offset, 4)) return;
    uint64_t flags = pci_lock();
    laihost_outd(pci_select(bus, slot, function, offset), value);
    pci_unlock(flags);
}
