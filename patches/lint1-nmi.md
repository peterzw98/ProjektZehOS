# lint1-nmi

- **Commit:** Squash von #351
- **Dateien:** `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs`
- **Grund:** NMI erreicht den Kernel: `LocalApic.Initialize` richtet LINT1 unmaskiert mit Zustellart NMI ein statt es zu maskieren; LINT0 bleibt maskiert. LINT1 ist fest verdrahtet (PC-Konvention wie Linux), MADT-Einträge Typ 4 (Local APIC NMI) wertet Cosmos nicht aus. Folge: Ein externer NMI hält das System jetzt an, statt still verloren zu gehen. Mit SMP (E5) nur auf der BSP setzen, APs maskiert lassen (wie Linux `setup_local_APIC`)
- **Entfernter ZehOS-Umweg:** Entmaskierung von LINT1 in `CosmosIsolationProbe` (entfällt mit ZehOS E3-C)
- **Prüfung:** ZehOS E3-C: `test-isolation-probe.py --nmi` (KVM); Desktop TCG und KVM ohne unerwarteten NMI
