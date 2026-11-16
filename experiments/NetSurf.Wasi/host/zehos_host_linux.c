// Optionaler WAMR-Prüfhost: stellt zehos_v1 als WAMR-Native-Bibliothek bereit (iwasm --native-lib=...).
// Die Dienste selbst liegen in zehos_host_core.c; hier wird nur Gastspeicher geprüft und übersetzt.
// WAMR kann Exception-Handling nur im Legacy-Format; Module mit exnref (setjmp/JavaScript) laufen hier nicht,
// dafür gibt es zehos_host_wasm3.c. Umgebung siehe zehos_host_core.c. Bauen mit -lssl -lcrypto.

#include <stddef.h>
#include <stdint.h>

#include "wasm_export.h"
#include "zehos_host_core.h"

static void present_wrapper(wasm_exec_env_t env, uint32_t pixels, int32_t width, int32_t height, int32_t tic, int32_t test)
{
    wasm_module_inst_t inst = wasm_runtime_get_module_inst(env);
    // Gleiche Grenzen wie im ZehOS-Loader; Größe in 64 Bit gegen Überlauf rechnen.
    if (width <= 0 || height <= 0 || width > 1920 || height > 1200 ||
        !wasm_runtime_validate_app_addr(inst, pixels, (uint64_t)width * (uint64_t)height * 4)) {
        wasm_runtime_set_exception(inst, "present: ungültiger Bildpuffer");
        return;
    }
    zehos_wasm_host_present(wasm_runtime_addr_app_to_native(inst, pixels), width, height, tic, test);
}

static int32_t poll_event_wrapper(wasm_exec_env_t env, uint32_t address)
{
    wasm_module_inst_t inst = wasm_runtime_get_module_inst(env);
    if (!wasm_runtime_validate_app_addr(inst, address, 16)) {
        wasm_runtime_set_exception(inst, "poll_event: ungültiger Zeiger");
        return -1;
    }
    return zehos_wasm_host_poll_event(wasm_runtime_addr_app_to_native(inst, address));
}

static uint32_t ticks_wrapper(wasm_exec_env_t env) { (void)env; return zehos_wasm_host_ticks_ms(); }

static void sleep_wrapper(wasm_exec_env_t env, uint32_t ms) { (void)env; zehos_wasm_host_sleep_ms(ms); }

static int32_t should_exit_wrapper(wasm_exec_env_t env) { (void)env; return zehos_wasm_host_should_exit(); }

// Wie im Kernel: ungültiger Gastspeicher beendet den Gast, bevor eine Netzprüfung greift.
static void *checked(wasm_exec_env_t env, uint32_t address, uint64_t length, const char *error)
{
    wasm_module_inst_t inst = wasm_runtime_get_module_inst(env);
    if (!wasm_runtime_validate_app_addr(inst, address, length)) {
        wasm_runtime_set_exception(inst, error);
        return NULL;
    }
    return wasm_runtime_addr_app_to_native(inst, address);
}

enum { NET_EINVAL = 28 };

static int32_t net_open_wrapper(wasm_exec_env_t env, uint32_t host, uint32_t host_len, int32_t port, int32_t flags, uint32_t out)
{
    const uint8_t *name = checked(env, host, host_len, "net_open: ungültiger Hostname");
    if (name == NULL) return NET_EINVAL;
    int32_t *handle = checked(env, out, 4, "net: ungültiger Ausgabezeiger");
    if (handle == NULL) return NET_EINVAL;
    return zehos_wasm_host_net_open(name, host_len, port, flags, handle);
}

static int32_t net_status_wrapper(wasm_exec_env_t env, int32_t handle) { (void)env; return zehos_wasm_host_net_status(handle); }

static int32_t net_send_wrapper(wasm_exec_env_t env, int32_t handle, uint32_t data, uint32_t len, uint32_t out)
{
    const uint8_t *buffer = checked(env, data, len, "net: ungültiger Puffer");
    if (buffer == NULL) return NET_EINVAL;
    int32_t *count = checked(env, out, 4, "net: ungültiger Ausgabezeiger");
    if (count == NULL) return NET_EINVAL;
    return zehos_wasm_host_net_send(handle, buffer, len, count);
}

static int32_t net_recv_wrapper(wasm_exec_env_t env, int32_t handle, uint32_t data, uint32_t len, uint32_t out)
{
    uint8_t *buffer = checked(env, data, len, "net: ungültiger Puffer");
    if (buffer == NULL) return NET_EINVAL;
    int32_t *count = checked(env, out, 4, "net: ungültiger Ausgabezeiger");
    if (count == NULL) return NET_EINVAL;
    return zehos_wasm_host_net_recv(handle, buffer, len, count);
}

static int32_t net_close_wrapper(wasm_exec_env_t env, int32_t handle) { (void)env; return zehos_wasm_host_net_close(handle); }

static NativeSymbol symbols[] = {
    { "present", present_wrapper, "(iiiii)", NULL },
    { "poll_event", poll_event_wrapper, "(i)i", NULL },
    { "ticks_ms", ticks_wrapper, "()i", NULL },
    { "sleep_ms", sleep_wrapper, "(i)", NULL },
    { "should_exit", should_exit_wrapper, "()i", NULL },
    { "net_open", net_open_wrapper, "(iiiii)i", NULL },
    { "net_status", net_status_wrapper, "(i)i", NULL },
    { "net_send", net_send_wrapper, "(iiii)i", NULL },
    { "net_recv", net_recv_wrapper, "(iiii)i", NULL },
    { "net_close", net_close_wrapper, "(i)i", NULL },
};

uint32_t get_native_lib(char **module_name, NativeSymbol **native_symbols)
{
    *module_name = "zehos_v1";
    *native_symbols = symbols;
    return sizeof(symbols) / sizeof(symbols[0]);
}
