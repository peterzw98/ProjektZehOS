// Prüfhost für Linux: stellt zehos_v1 als WAMR-Native-Bibliothek bereit (iwasm --native-lib=...).
// Ersetzt nicht den ZehOS-Loader; er macht Bild und Eingabe ohne VM prüfbar.
//
// Umgebung:
//   ZEHOS_PPM=<datei>      jedes present als PPM (P6) dorthin schreiben
//   ZEHOS_EXIT_MS=<ms>     ab dieser App-Zeit meldet should_exit 1 (Vorgabe 10000)
//   ZEHOS_EVENTS=<datei>   Zeilen "<ms> <typ> <code> <x> <y>"; jedes Ereignis wird ab seiner Zeit geliefert

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

#include "wasm_export.h"

#define MAX_EVENTS 1024

struct scripted_event { uint32_t at; int32_t fields[4]; };

static struct scripted_event events[MAX_EVENTS];
static int event_count, next_event, loaded;
static uint64_t start_ms;
static uint32_t exit_ms = 10000, presents;

static uint64_t now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000 + (uint64_t)ts.tv_nsec / 1000000;
}

static void load(void)
{
    if (loaded) return;
    loaded = 1;
    start_ms = now_ms();
    const char *limit = getenv("ZEHOS_EXIT_MS");
    if (limit != NULL) exit_ms = (uint32_t)strtoul(limit, NULL, 10);
    const char *path = getenv("ZEHOS_EVENTS");
    FILE *file = path != NULL ? fopen(path, "r") : NULL;
    if (path != NULL && file == NULL) { fprintf(stderr, "zehos-host: %s nicht lesbar\n", path); exit(2); }
    while (file != NULL && event_count < MAX_EVENTS) {
        struct scripted_event *e = &events[event_count];
        if (fscanf(file, "%u %d %d %d %d", &e->at, &e->fields[0], &e->fields[1], &e->fields[2], &e->fields[3]) != 5) break;
        event_count++;
    }
    if (file != NULL) fclose(file);
}

static uint32_t ticks(void) { load(); return (uint32_t)(now_ms() - start_ms); }

static void present_wrapper(wasm_exec_env_t env, uint32_t pixels, int32_t width, int32_t height, int32_t tic, int32_t test)
{
    (void)tic; (void)test;
    wasm_module_inst_t inst = wasm_runtime_get_module_inst(env);
    // Gleiche Grenzen wie im ZehOS-Loader; Größe in 64 Bit gegen Überlauf rechnen.
    if (width <= 0 || height <= 0 || width > 1920 || height > 1200 ||
        !wasm_runtime_validate_app_addr(inst, pixels, (uint64_t)width * (uint64_t)height * 4)) {
        wasm_runtime_set_exception(inst, "present: ungültiger Bildpuffer");
        return;
    }
    presents++;
    const char *path = getenv("ZEHOS_PPM");
    if (path == NULL) return;
    FILE *file = fopen(path, "wb");
    if (file == NULL) return;
    const uint32_t *data = wasm_runtime_addr_app_to_native(inst, pixels);
    fprintf(file, "P6\n%d %d\n255\n", width, height);
    for (int64_t i = 0; i < (int64_t)width * height; i++) {
        uint8_t rgb[3] = { (uint8_t)(data[i] >> 16), (uint8_t)(data[i] >> 8), (uint8_t)data[i] };
        fwrite(rgb, 1, 3, file);
    }
    fclose(file);
}

static int32_t poll_event_wrapper(wasm_exec_env_t env, uint32_t address)
{
    wasm_module_inst_t inst = wasm_runtime_get_module_inst(env);
    if (!wasm_runtime_validate_app_addr(inst, address, 16)) {
        wasm_runtime_set_exception(inst, "poll_event: ungültiger Zeiger");
        return -1;
    }
    if (ticks() < (next_event < event_count ? events[next_event].at : UINT32_MAX)) return 0;
    int32_t *target = wasm_runtime_addr_app_to_native(inst, address);
    for (int i = 0; i < 4; i++) target[i] = events[next_event].fields[i];
    next_event++;
    return 1;
}

static uint32_t ticks_wrapper(wasm_exec_env_t env) { (void)env; return ticks(); }

static void sleep_wrapper(wasm_exec_env_t env, uint32_t ms)
{
    (void)env;
    if (ms > 1000) ms = 1000;
    struct timespec ts = { ms / 1000, (long)(ms % 1000) * 1000000 };
    nanosleep(&ts, NULL);
}

static int32_t should_exit_wrapper(wasm_exec_env_t env)
{
    (void)env;
    if (ticks() < exit_ms) return 0;
    fprintf(stderr, "zehos-host: should_exit nach %u ms, %u Bilder\n", ticks(), presents);
    return 1;
}

static NativeSymbol symbols[] = {
    { "present", present_wrapper, "(iiiii)", NULL },
    { "poll_event", poll_event_wrapper, "(i)i", NULL },
    { "ticks_ms", ticks_wrapper, "()i", NULL },
    { "sleep_ms", sleep_wrapper, "(i)", NULL },
    { "should_exit", should_exit_wrapper, "()i", NULL },
};

uint32_t get_native_lib(char **module_name, NativeSymbol **native_symbols)
{
    *module_name = "zehos_v1";
    *native_symbols = symbols;
    return sizeof(symbols) / sizeof(symbols[0]);
}
