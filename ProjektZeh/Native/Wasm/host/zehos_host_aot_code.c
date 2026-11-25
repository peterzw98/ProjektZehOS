// ZehOS: liest WAMRs AOT-Modulstruktur fuer den Schleifenausstieg (zehos_host_aot_exit.h). Eigene Datei, weil sie als
// einzige der Hostschicht WAMRs interne Kopfdatei braucht.

#ifdef ZEHOS_WASM_AOT

#include <string.h>
#include "aot_runtime.h"
#include "zehos_host_aot_exit.h"

void zehos_aot_code_range(void* module, uint64_t* code, uint64_t* codeEnd)
{
    AOTModule* aot = (AOTModule*)module;
    *code = (uint64_t)(uintptr_t)aot->code;
    *codeEnd = *code + aot->code_size;
}

// Wie execute_post_instantiate_functions (aot_runtime.c): start-Funktion und die von WAMR gerufenen Exporte. _initialize
// ruft WAMR nur mit seiner WASI-libc, die nicht im Kernel ist.
int zehos_aot_runs_on_instantiate(void* module)
{
    AOTModule* aot = (AOTModule*)module;
    if (aot->start_function != NULL) return 1;
    for (uint32_t index = 0; index < aot->export_count; index++)
    {
        if (aot->exports[index].kind != EXPORT_KIND_FUNC) continue;
        const char* name = aot->exports[index].name;
        if (strcmp(name, "__post_instantiate") == 0 || strcmp(name, "__wasm_call_ctors") == 0) return 1;
    }
    return 0;
}

#endif
