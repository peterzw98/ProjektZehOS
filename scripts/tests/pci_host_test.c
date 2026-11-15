#include <assert.h>
#include <stdint.h>
#include <stdio.h>

#define ZEHOS_PCI_HOST_TEST
static unsigned int selected;
static unsigned int data_port;
static unsigned int data_width;
static unsigned int written;
static unsigned int addresses;
static unsigned int locked;
static uint64_t pci_lock(void) { assert(!locked); locked = 1; return 0x200; }
static void pci_unlock(uint64_t flags) { assert(locked && flags == 0x200); locked = 0; }
void laihost_outd(uint16_t port, uint32_t value) {
    assert(locked);
    if (port == 0xcf8) { selected = value; addresses++; }
    else { data_port = port; written = value; data_width = 4; }
}
void laihost_outw(uint16_t port, uint16_t value) { assert(locked); data_port = port; written = value; data_width = 2; }
void laihost_outb(uint16_t port, uint8_t value) { assert(locked); data_port = port; written = value; data_width = 1; }
uint8_t laihost_inb(uint16_t port) { assert(locked); data_port = port; return 0x12; }
uint16_t laihost_inw(uint16_t port) { assert(locked); data_port = port; return 0x3456; }
uint32_t laihost_ind(uint16_t port) { assert(locked); data_port = port; return 0x789abcde; }

#include "../../ProjektZeh/Native/Acpi/pci_host.c"

int main(void) {
    assert(laihost_pci_readb(0, 2, 3, 4, 0x53) == 0x12);
    assert(selected == 0x80021c50 && data_port == 0xcff && !locked);
    assert(laihost_pci_readw(0, 0, 7, 3, 0x82) == 0x3456);
    assert(selected == 0x80003b80 && data_port == 0xcfe && !locked);
    assert(laihost_pci_readd(0, 255, 31, 7, 0xfc) == 0x789abcde);
    assert(selected == 0x80fffffc && data_port == 0xcfc && !locked);
    laihost_pci_writeb(0, 0, 7, 3, 0x83, 0xaa);
    assert(data_port == 0xcff && data_width == 1 && written == 0xaa && !locked);
    laihost_pci_writew(0, 0, 7, 3, 0x82, 0xbbcc);
    assert(data_port == 0xcfe && data_width == 2 && written == 0xbbcc && !locked);
    laihost_pci_writed(0, 0, 7, 3, 0x80, 0xdeadbeef);
    assert(data_port == 0xcfc && data_width == 4 && written == 0xdeadbeef && !locked);
    unsigned int before = addresses;
    assert(laihost_pci_readb(1, 0, 0, 0, 0) == UINT8_MAX);
    assert(laihost_pci_readw(0, 0, 32, 0, 0) == UINT16_MAX);
    assert(laihost_pci_readd(0, 0, 0, 8, 0) == UINT32_MAX);
    assert(laihost_pci_readb(0, 0, 0, 0, 256) == UINT8_MAX);
    assert(laihost_pci_readw(0, 0, 0, 0, 1) == UINT16_MAX);
    assert(laihost_pci_readd(0, 0, 0, 0, 2) == UINT32_MAX);
    laihost_pci_writeb(1, 0, 0, 0, 0, 1);
    laihost_pci_writew(0, 0, 0, 0, 255, 1);
    laihost_pci_writed(0, 0, 0, 0, 254, 1);
    assert(addresses == before && !locked);
    puts("LAI PCI host tests passed");
    return 0;
}
