namespace ProjektZeh.Runtime.Wasm;

// Eigene Datei: auch ohne ZEHOS_WASM verfuegbar, damit WasmJobQueue im Hostbuild getestet werden kann.
// CpuFault liefert nur der Kernel (Roadmap 12.4-I1), nicht die C-Schicht.
internal enum WasmStatus { Ok = 0, NoMemory = 1, InvalidModule = 2, LoadFailed = 3, EntryMissing = 4, Trap = 5, OutOfGas = 6, LimitFailed = 7, CpuFault = 8 }
