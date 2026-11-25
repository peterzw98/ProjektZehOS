# apic-hhdm

- **Commit:** Squash des PRs (Hash beim Merge); Basis `cosmos-patch/fxsave` 02ea6ec9. Probebranch N1, nicht gemergt
- **Dateien:** `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs`, `IoApic.cs`, `ApicManager.cs`, `src/Cosmos.Kernel.Core/Firmware/EfiRtc.cs` (nur Kommentar), `tests/Kernels/Cosmos.Kernel.Tests.Interrupts`
- **Grund:** Local APIC und I/O APIC wurden über die physische Adresse angesprochen, also über Limines Identitätsabbildung in der unteren Hälfte. Unter einem CR3 mit leerer unterer Hälfte (ZehOS N1-B3, #632) ist der erste EOI ein #PF. Jetzt Zugriff über die HHDM, vorher `DeviceMapper.EnsureMapped`. `LocalApic.BaseAddress` und die MSI-Adresse bleiben physisch
- **Grenzen:** `EfiRtc` ruft die Firmware weiter über physische Adressen auf: Sie folgt ihren eigenen Zeigern, die HHDM reicht nicht. Einziger Aufrufer ist der Start, vor jedem eigenen CR3. Scheitert die Abbildung (nur über 4 GiB möglich), bleibt die APIC aus
- **Entfernter ZehOS-Umweg:** keiner (neu)
- **Prüfung:** siehe PR
