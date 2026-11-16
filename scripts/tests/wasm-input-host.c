// Hostersatz fuer native ABI-Tests: der Entnahmezaehler bleibt auch nach einem Gast-Trap lesbar.
#include <stdint.h>
#include <string.h>
static int available, calls, dequeues;
void input_test_reset(int mode) { available = mode; calls = dequeues = 0; }
int input_test_calls(void) { return calls; }
int input_test_dequeues(void) { return dequeues; }
int32_t zehos_wasm_host_poll_event(int32_t* event) {
    calls++;
    if (available != 1) return available;
    const int32_t value[4] = {3, 65, -17, 29};
    memcpy(event, value, sizeof(value));
    dequeues++;
    available = 0;
    return 1;
}
void zehos_wasm_host_write(int32_t fd, const uint8_t* data, uint32_t length) { }
uint64_t zehos_wasm_host_time_ns(int32_t clock) { return 0; }
static int random_error, random_calls;
void random_test_reset(int error) { random_error = error; random_calls = 0; }
int random_test_calls(void) { return random_calls; }
int32_t zehos_wasm_host_random(uint8_t* data, uint32_t length) {
    random_calls++;
    if (random_error) return random_error;
    memset(data, 0xa5, length);
    return 0;
}
int32_t zehos_wasm_host_open(const uint8_t* path, uint32_t length) { return -1; }
int64_t zehos_wasm_host_file_size(int32_t handle) { return -1; }
int32_t zehos_wasm_host_file_read(int32_t handle, uint64_t offset, uint8_t* data, uint32_t length) { return -1; }
void zehos_wasm_host_close(int32_t handle) { }
void zehos_wasm_host_present(const uint32_t* p, int32_t w, int32_t h, int32_t tic, int32_t test) { }
void zehos_wasm_host_present_indexed(const uint8_t* i, const uint32_t* p, int32_t w, int32_t h, int32_t tic) { }
uint32_t zehos_wasm_host_ticks_ms(void) { return 0; }
void zehos_wasm_host_sleep_ms(uint32_t ms) { }
int32_t zehos_wasm_host_poll_key(void) { return -1; }
int32_t zehos_wasm_host_pointer_capture(int32_t enabled) { (void)enabled; return -1; }
int32_t zehos_wasm_host_should_exit(void) { return 0; }

static int stat_calls;
void stat_test_reset(void) { stat_calls = 0; }
int stat_test_calls(void) { return stat_calls; }
int32_t zehos_wasm_host_stat(const uint8_t* path, uint32_t length, uint64_t* size, int32_t* type) {
    stat_calls++;
    if (length == 7 && memcmp(path, "missing", 7) == 0) return 44;
    if (length == 6 && memcmp(path, "denied", 6) == 0) return 2;
    if (length == 2 && memcmp(path, "io", 2) == 0) return 29;
    *type = length == 9 && memcmp(path, "asset.txt", 9) == 0 ? 4 : 3;
    *size = *type == 4 ? 7 : 0;
    return 0;
}

static int net_calls;
void net_test_reset(void) { net_calls = 0; }
int net_test_calls(void) { return net_calls; }
int32_t zehos_wasm_host_net_open(const uint8_t* host, uint32_t length, int32_t port, int32_t flags, int32_t* handle)
{ (void)host; (void)length; (void)port; (void)flags; (void)handle; net_calls++; return 2; }
int32_t zehos_wasm_host_net_status(int32_t handle) { (void)handle; return 8; }
int32_t zehos_wasm_host_net_send(int32_t handle, const uint8_t* data, uint32_t length, int32_t* count)
{ (void)handle; (void)data; (void)length; (void)count; net_calls++; return 8; }
int32_t zehos_wasm_host_net_recv(int32_t handle, uint8_t* data, uint32_t length, int32_t* count)
{ (void)handle; (void)data; (void)length; (void)count; net_calls++; return 8; }
int32_t zehos_wasm_host_net_close(int32_t handle) { (void)handle; return 8; }
