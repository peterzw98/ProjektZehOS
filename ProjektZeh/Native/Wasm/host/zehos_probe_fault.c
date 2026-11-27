// ZehOS: Nur fuer die Gastprobe 12.4-I1 (-p:ZehOSIsolationProbe*): erzeugt aus C heraus ein echtes #UD (Vektor 6).
// Verwaltetes C# kann keinen ungueltigen Opcode ausloesen; ohne den Probe-Schalter bleibt die Datei leer.

#ifdef ZEHOS_ISOLATION_PROBE
#include <stdint.h>

void zehos_probe_ud(void)
{
    // __builtin_trap() uebersetzt clang auf x86-64 zu ud2.
    __builtin_trap();
}

#ifdef ZEHOS_WASM_AOT
// #582: Die Probe braucht ein gueltiges .aot, dessen Code sie fuer #DE und #PF umschreibt. Dieselbe Fixture wie die
// AOT-Probe (Native/AotProbe, von fixture/build.sh erzeugt), unter eigenem Namen, damit beide Proben zusammen binden.
#define zehos_aot_fixture zehos_isolation_probe_aot
#define zehos_aot_fixture_size zehos_isolation_probe_aot_size
#include "../../AotProbe/aot_fixture_data.c"
#undef zehos_aot_fixture
#undef zehos_aot_fixture_size

const uint8_t* zehos_probe_aot_fixture(uint32_t* size)
{
    *size = zehos_isolation_probe_aot_size;
    return zehos_isolation_probe_aot;
}
#endif
#endif
