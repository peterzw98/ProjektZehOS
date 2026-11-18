# Patches gegenüber Upstream

## Grundstand

| Repository | Commit | Entspricht | Eingespielt am |
| --- | --- | --- | --- |
| `https://github.com/CosmosOS/Cosmos` | `efcf20cc62884b042a0adadffe9fc4540491fd89` | NuGet-Pakete 3.0.97 | 18. November 2026 |

## Übernommene Upstream-Stände

Noch keine.

## Eigene Patches

| Kurzname | Commit | Dateien | Grund | Entfernter ZehOS-Umweg | Prüfung |
| --- | --- | --- | --- | --- | --- |
| lint1-nmi | dieser Commit (Hash nach dem Merge nicht in der Zeile selbst möglich) | `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs` | NMI erreicht den Kernel: `LocalApic.Initialize` richtet LINT1 unmaskiert mit Zustellart NMI ein statt es zu maskieren; LINT0 bleibt maskiert. LINT1 ist fest verdrahtet (PC-Konvention wie Linux), MADT-Einträge Typ 4 (Local APIC NMI) wertet Cosmos nicht aus. Folge: Ein externer NMI hält das System jetzt an, statt still verloren zu gehen. Mit SMP (E5) nur auf der BSP setzen, APs maskiert lassen (wie Linux `setup_local_APIC`) | Entmaskierung von LINT1 in `CosmosIsolationProbe` (entfällt mit ZehOS E3-C) | ZehOS E3-C: `test-isolation-probe.py --nmi` (KVM); Desktop TCG und KVM ohne unerwarteten NMI |
