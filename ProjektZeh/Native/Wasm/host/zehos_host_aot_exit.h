// ZehOS: Ausstieg aus AOT-Code in einer Schleife ohne Hostaufruf (#423, R8). Der Scheduler-Tick (Cosmos-Patch
// aot-loop-exit) setzt RIP des Wasm-Workers auf zehos_aot_exit; der Worker stellt dort den Stack von zehos_aot_call
// wieder her und kehrt regulaer zurueck, der Abbau in zehos_wasm_run_aot laeuft danach wie immer.

#ifndef ZEHOS_HOST_AOT_EXIT_H
#define ZEHOS_HOST_AOT_EXIT_H

#include <stdint.h>

// Nur der Worker schreibt, der Tick liest auf derselben CPU (er unterbricht genau diesen Thread). Alles 0 ausserhalb
// des Aufrufs von zehos_aot_call, also auch beim Laden, Instanziieren und Abbauen: dann wird nie umgelenkt.
// Feldfolge wie AotExitState in CosmosAotLoopExit.cs.
typedef struct
{
    volatile uint64_t code;      // Anfang des AOT-Codes samt PLT
    volatile uint64_t codeEnd;   // erstes Byte dahinter
    volatile uint64_t stack;     // RSP der Sicherung auf dem Worker-Stack (zehos_aot_call)
} zehos_aot_exit_state;

extern zehos_aot_exit_state zehos_aot_exit_current;

enum { ZEHOS_AOT_CALL_FAILED = 0, ZEHOS_AOT_CALL_OK = 1, ZEHOS_AOT_CALL_LEFT = 2 };

// Ruft wasm_runtime_call_wasm; LEFT, wenn der Tick den Lauf beendet hat. Fuer den Compiler ein gewoehnlicher Aufruf,
// deshalb kein setjmp mit seinen Regeln fuer lokale Variablen.
int32_t zehos_aot_call(void* env, void* function, uint32_t argc, uint32_t* argv);

// Codebereich eines geladenen Moduls und ob WAMR beim Instanziieren Gastcode ausfuehren wuerde.
void zehos_aot_code_range(void* module, uint64_t* code, uint64_t* codeEnd);
int zehos_aot_runs_on_instantiate(void* module);

#endif
