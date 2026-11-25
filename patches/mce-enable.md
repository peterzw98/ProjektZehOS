# mce-enable

- **Commit:** Squash des PRs (Hash beim Merge). Probebranch auf `cosmos-patch/user-gdt` (`ab2d31cc`).
- **Dateien:** `src/Cosmos.Kernel.Core/Bridge/Import/CpuNative.cs`, `src/Cosmos.Kernel.Core/CPU/InternalCpu.cs`, `src/Cosmos.Kernel.Core/CPU/Smp.cs`, `src/Cosmos.Kernel.Native.X64/CPU/CpuOps.s`, `src/Cosmos.Kernel/ExceptionHandler.cs`, `src/Cosmos.Kernel/Panic.cs`.
- **Grund:** Bei gelöschtem `CR4.MCE` kann ein Machine Check einen Prozessor-Shutdown und in QEMU einen Neustart auslösen. Cosmos prüft `CPUID.01H:EDX[7]` auf jeder x64-CPU und setzt anschließend `CR4.MCE`, sobald IDT, IST und Handler dort bereit sind. Der vorhandene Panikpfad meldet Vektor 18 und hält das System an. Beim Machine Check wird der Ausnahme-Hook übersprungen, da ein Hardwarefehler keinem einzelnen Thread sicher zugeordnet werden kann.
- **Entfernter ZehOS-Umweg:** Keiner. Der QEMU-Gasttest soll ohne manuelles Setzen von `CR4.MCE` im Debugger auskommen.
- **Prüfung:** Der Assemblerteil lässt sich mit Clang 22.1.3 für x86_64-unknown-linux-gnu übersetzen. Offen sind der vollständige native Build, die Testkerne `Interrupts` und `Smp` sowie die Injektion auf CPU 0 (1 vCPU) und CPU 2 (4 vCPUs) unter QEMU TCG und KVM. Die Injektion soll im Patch-Gast `Interrupt Vector: 18` und `System halted.` ohne Neustart ergeben. Der Basisgast dient als Gegenprobe.
- **Grenzen:** Der Patch konfiguriert keine MCA-Bänke, liest keine Fehlerregister aus und versucht keine Wiederherstellung nach #MC.
