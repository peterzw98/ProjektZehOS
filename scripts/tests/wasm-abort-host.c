// Hostersatz fuer den nativen Abbruchtest (Roadmap 12.4-I2-A): alle Kernelrueckrufe wie in wasm-input-host.c, nur
// should_exit zaehlt seine Aufrufe und setzt beim eingestellten Aufruf den Abbruchmerker, wie es die Hauptschleife
// waehrend eines Hostaufrufs taete. Die Umbenennung haelt die uebrigen Rueckrufe an einer Stelle.
#define zehos_wasm_host_should_exit zehos_input_host_should_exit
#include "wasm-input-host.c"
#undef zehos_wasm_host_should_exit

extern void zehos_host_set_abort(int32_t on);

static int abort_calls, abort_at;
void abort_test_reset(int at) { abort_calls = 0; abort_at = at; }
int abort_test_calls(void) { return abort_calls; }
int32_t zehos_wasm_host_should_exit(void)
{
    abort_calls++;
    if (abort_at > 0 && abort_calls == abort_at) zehos_host_set_abort(1);
    return 0;
}
