// Linux-Seite des Kernel-Rückrufvertrags zehos_wasm_host_* (ProjektZeh/Native/Wasm/host/zehos_wasm.c).
// Zeiger sind native Adressen, die der Aufrufer bereits gegen den Gastspeicher geprüft hat.

#ifndef ZEHOS_HOST_CORE_H
#define ZEHOS_HOST_CORE_H

#include <stdint.h>

void zehos_wasm_host_present(const uint32_t* pixels, int32_t width, int32_t height, int32_t tic, int32_t test);
uint32_t zehos_wasm_host_ticks_ms(void);
void zehos_wasm_host_sleep_ms(uint32_t milliseconds);
int32_t zehos_wasm_host_poll_event(int32_t* event);
int32_t zehos_wasm_host_should_exit(void);
int32_t zehos_wasm_host_net_open(const uint8_t* host, uint32_t length, int32_t port, int32_t flags, int32_t* handle);
int32_t zehos_wasm_host_net_status(int32_t handle);
int32_t zehos_wasm_host_net_send(int32_t handle, const uint8_t* data, uint32_t length, int32_t* count);
int32_t zehos_wasm_host_net_recv(int32_t handle, uint8_t* data, uint32_t length, int32_t* count);
int32_t zehos_wasm_host_net_close(int32_t handle);

#endif
