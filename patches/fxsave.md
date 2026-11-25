# fxsave

- **Commit:** Squash des PRs (Hash beim Merge); Basis `cosmos` 6b28fc44. Probebranch N1, nicht gemergt
- **Dateien:** `src/Cosmos.Kernel.Native.X64/CPU/Interrupts.s` (FPU-Teil des Stubs), `src/Cosmos.Kernel.Core/Scheduler/ThreadContext.X64.cs`, `src/Cosmos.Kernel.Core/Scheduler/SchedulerThread.cs` (nur Kommentare), `src/Cosmos.Kernel.Core.X64/Cpu/X64InterruptController.cs`, `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs`, `tests/Kernels/Cosmos.Kernel.Tests.Threading`
- **Grund:** Ring 3 (ZehOS N1-A2, #625) braucht je Thread den ganzen x87- und SSE-Zustand. Der Stub sicherte nur XMM0–15; FCW, MXCSR und ST0–7 blieben nach einem Threadwechsel die des vorigen Threads. Jetzt `fxsave64`/`fxrstor64` (512 Byte, 16-Byte-ausgerichtet). Neue Threads beginnen mit FCW `0x37F` und MXCSR `0x1F80`. Die Größe steht je Sprache einmal: `FPU_AREA_SIZE` (Interrupts.s) und `ThreadContext.FpuAreaSize`
- **Grenzen:** Handler laufen wie bisher mit den Steuerwörtern des unterbrochenen Threads; für Ring 3 muss der Eintritt (N1-A3) sie auf die Vorgabe setzen, sonst wirken unmaskierte Ausnahmen eines Programms im Kernel. Kein AVX (`CR4.OSXSAVE` bleibt aus). FIP, FDP und FOP stellt AMD nur bei anstehender Ausnahme wieder her. Je Rahmen 256 Byte Stack mehr. Nur x64
- **Entfernter ZehOS-Umweg:** keiner (neu)
- **Prüfung:** siehe PR
