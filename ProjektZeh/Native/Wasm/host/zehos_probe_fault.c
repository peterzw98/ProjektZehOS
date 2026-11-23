// ZehOS: Nur fuer die Gastprobe 12.4-I1 (-p:ZehOSIsolationProbe*): erzeugt aus C heraus ein echtes #UD (Vektor 6).
// Verwaltetes C# kann keinen ungueltigen Opcode ausloesen; ohne den Probe-Schalter bleibt die Datei leer.

#ifdef ZEHOS_ISOLATION_PROBE
void zehos_probe_ud(void)
{
    // __builtin_trap() uebersetzt clang auf x86-64 zu ud2.
    __builtin_trap();
}
#endif
