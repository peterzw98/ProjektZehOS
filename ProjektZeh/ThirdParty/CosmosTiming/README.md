# Cosmos-Kalibrierungsroutinen

Der Nicht-VMware-Rueckfall in `Runtime/CosmosVmwareClockPlug.cs` uebernimmt die bisherigen Kalibrierungsablaeufe aus Cosmos 3.0.94. Die Aufrufe sind an die zugaenglichen Schnittstellen angepasst. Die VMware-Frequenzabfrage ist eine ZehOS-Ergaenzung.

Quelle: CosmosOS/Cosmos, Commit `5d0b9535251472db7ecfe012f0435f0a04ffca16`:

- [LocalApic.CalibrateTimer](https://github.com/CosmosOS/Cosmos/blob/5d0b9535251472db7ecfe012f0435f0a04ffca16/src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs)
- [X64CpuOps.CalibrateTsc](https://github.com/CosmosOS/Cosmos/blob/5d0b9535251472db7ecfe012f0435f0a04ffca16/src/Cosmos.Kernel.Core.X64/Cpu/X64CpuOps.cs)

Copyright (c) 2007-2026, CosmosOS, COSMOS Project. Lizenz: [BSD-3-Clause](LICENSE).
