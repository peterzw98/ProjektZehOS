// Nativer Test der WAMR-Bindung (Roadmap 12.4-I2-A): ruft einen echten Wrapper bei gesetztem Abbruchmerker, einmal mit
// den Nutzerdaten eines Laufs und einmal ohne, wie WAMR es waehrend des Instanziierens tut (Startfunktion). Die wenigen
// WAMR-Funktionen des Wrappers sind Stubs; alles Uebrige der Datei entfernt der Linker (--gc-sections).
#include <stdio.h>
#include "wasm-input-host.c"
#include "zehos_host_wamr.c"

static void* user_data;
static const char* exception;

wasm_module_inst_t wasm_runtime_get_module_inst(wasm_exec_env_t env) { (void)env; return NULL; }
void* wasm_runtime_get_user_data(wasm_exec_env_t env) { (void)env; return user_data; }
void* wasm_runtime_addr_app_to_native(wasm_module_inst_t instance, uint64_t offset) { (void)instance; (void)offset; return NULL; }
bool wasm_runtime_get_native_addr_range(wasm_module_inst_t instance, uint8_t* pointer, uint8_t** start, uint8_t** end)
{
    (void)instance; (void)pointer; (void)start; (void)end;
    return false;
}
void wasm_runtime_set_exception(wasm_module_inst_t instance, const char* text) { (void)instance; exception = text; }

static int check(int with_run)
{
    static zehos_wamr_run run;
    memset(&run, 0, sizeof(run));
    user_data = with_run ? &run : NULL;
    exception = NULL;
    zehos_host_set_abort(1);
    zehos_wamr_zehos_v1_ticks_ms(NULL);
    zehos_host_set_abort(0);
    return exception == abort_exception && zehos_host_depth == 0 && run.aborted == with_run;
}

int main(void)
{
    int with_run = check(1);
    int without_run = check(0);
    printf("with_run=%d without_run=%d\n", with_run, without_run);
    return with_run && without_run ? 0 : 1;
}
