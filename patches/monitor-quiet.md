# monitor-quiet

- **Commit:** Merge-Commit von #510 (`20ad77a5`); Patch ist der Diff gegen den ersten Elternteil
- **Dateien:** `src/Cosmos.Kernel.Plugs/System/InteropSysPlug.cs`
- **Grund:** Die Plugs `LowLevelMonitor_Acquire`, `_Release`, `_Wait`, `_TimedWait` und `_Signal_Release` schrieben bei jedem Aufruf eine Zeile synchron auf die serielle Schnittstelle. Sie tragen jede umkämpfte `lock`-Sperre, `Monitor.Wait`/`PulseAll` und jedes Warten auf ein Wait-Handle der NativeAOT-Laufzeit. Unter KVM ist jedes Byte ein VM-Exit; die ZehOS-SMP-Probe (E6-D, Schritt `locks`) brauchte so ~55 ms je Übergabe zwischen zwei CPUs (SMP 8: 1000 Übergaben je Paar in 141 s, 11 MB Serial-Log, davon ~360.000 `[LowLevelMonitor]`-Zeilen). Die Zeilen sind ersatzlos entfernt; Verhalten der Plugs sonst unverändert. ZehOS E6-H (#506).
- **Entfernter ZehOS-Umweg:** `HandoffCount = 1000` statt 10.000 in `ProjektZeh/Runtime/CosmosSmpProbe.cs` (Probe-Build)
- **Prüfung:** Cosmos-Bau (`scripts/build-cosmos.sh`); ZehOS-SMP-Probe (`scripts/test-smp-probe.py`) unter KVM mit `locks` 10.000 Übergaben je Paar, Laufzeit vorher/nachher; Serial-Log ohne `[LowLevelMonitor]`
- **Nicht geändert:** `LowLevelMonitor_TimedWait` gibt weiter immer `true` zurück, auch nach Zeitablauf (eigener Befund, nicht Teil dieses Patches)
