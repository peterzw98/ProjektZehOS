# apic-hhdm

- **Commit:** Squash des PRs (Hash beim Merge); Basis `cosmos-patch/fxsave` 02ea6ec9. Probebranch N1, nicht gemergt
- **Dateien:** `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs`, `IoApic.cs`, `ApicManager.cs`, `src/Cosmos.Kernel.Core/Firmware/EfiRtc.cs` (nur Kommentar), `tests/Kernels/Cosmos.Kernel.Tests.Interrupts`
- **Grund:** Local APIC und I/O APIC wurden über die physische Adresse angesprochen, also über Limines Identitätsabbildung in der unteren Hälfte. Unter einem CR3 mit leerer unterer Hälfte (ZehOS N1-B3, #632) ist der erste EOI ein #PF. Jetzt Zugriff über die HHDM, vorher `DeviceMapper.EnsureMapped`. `LocalApic.BaseAddress` und die MSI-Adresse bleiben physisch
- **Grenzen:** `EfiRtc` ruft die Firmware weiter über physische Adressen auf: Sie folgt ihren eigenen Zeigern, die HHDM reicht nicht. Einziger Aufrufer ist der Start, vor jedem eigenen CR3. Scheitert die Abbildung (nur über 4 GiB möglich), bleibt die APIC aus
- **Entfernter ZehOS-Umweg:** keiner (neu)
- **Prüfung:** Code 0d31e93c, QEMU KVM und TCG, Zellen mit 1 und 4 CPUs: `Interrupts` 37 bestanden/0 Fehler/1 übersprungen (KVM dreimal, TCG einmal), `Smp` 67/0/21, `Threading` 213/0/5, `GarbageCollector` 212/0/4 je einmal. Neuer Fall `Apic_LowerHalfEmpty_StillWorks`; Gegenproben mit physischer Basis: #PF bei CR2 0xFEE00020 (LocalApic) und 0xFEC00000 (IoApic). ZehOS nativ 0/0; Desktop KVM/TCG mit 1 und 4 CPUs sowie Netzprobe, je normal und mit geleerter unterer Hälfte, alle rc 0. Offen: EFI-Start (`EfiRtc` nicht ausgeführt), MSI/AHCI-Einzelprobe, APIC über 4 GiB, VMware, Hardware, ARM64
