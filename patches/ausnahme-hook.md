# ausnahme-hook

- **Commit:** Squash von #349
- **Dateien:** `src/Cosmos.Kernel.Core/CPU/CpuExceptionHook.cs` (neu), `src/Cosmos.Kernel.Core/Scheduler/SchedulerManager.cs` (`TryAbandonCurrentFromInterrupt`), `src/Cosmos.Kernel/Panic.cs` (`CpuException`), `src/Cosmos.Kernel.Core/PublicAPI.Unshipped.txt`, `Scheduler/Experimentals.cs`, `docs/articles/dev/public-api.md`
- **Grund:** Öffentlicher, experimenteller Hook (`COSMOS0001`) für CPU-Ausnahmen: der Kernel kann den fehlerhaften Thread aufgeben statt anzuhalten; Schachtelungszähler, ohne Hook unverändert Halt. Der Hook bekommt `CpuExceptionInfo` (Vektor, IP, SP, Fehleradresse) statt `IRQContext`, weil dessen Layout je Architektur verschieden ist und eine öffentliche Fassung die gemeinsame `PublicAPI`-Datei des Mehrarchitekturpakets bricht
- **Entfernter ZehOS-Umweg:** `CosmosPanicHaltPlug`, `CosmosIrqContextStub`, Feldversätze, `UnsafeAccessor` `ScheduleFromInterrupt`/`Halt`, `DllImport` `_native_get_context_switch_sp` (entfallen mit E3-B)
- **Prüfung:** ZehOS E3-B: `test-isolation-probe.py` positiv (KVM 10, TCG 3), negativ, NMI; Desktop, Doom quick
- **Auf SMP-Stand (`feat/smp-phase5b@4495213`):** neu angebracht: `PublicAPI.Unshipped.txt`, `Experimentals.cs` und `public-api.md` von Hand zusammengeführt (Upstream ersetzt `InterruptMaskScope` durch `CpuLockScope` und `CpuSet`; die Hook-Einträge stehen daneben). `TryAbandonCurrentFromInterrupt` unverändert; es ruft `ScheduleFromInterrupt`, das jetzt die Queue-Sperre der CPU nimmt. Ein Fehler unter dieser Sperre hängt statt anzuhalten (E5 Review-Schwerpunkt 6, offen)
