namespace ProjektZeh.Runtime.Wasm;

// Eigene Datei: auch ohne ZEHOS_WASM verfuegbar, damit WasmJobQueue im Hostbuild getestet werden kann.
// CpuFault liefert nur der Kernel (Roadmap 12.4-I1), nicht die C-Schicht. Aborted (ZEHOS_WASM_ABORTED) liefert die
// C-Schicht, wenn WasmRuntime.PollAbort den Lauf nach dem Schliessen beendet hat (Roadmap 12.4-I2-A).
internal enum WasmStatus { Ok = 0, NoMemory = 1, InvalidModule = 2, LoadFailed = 3, EntryMissing = 4, Trap = 5, OutOfGas = 6, LimitFailed = 7, CpuFault = 8, Aborted = 9 }
