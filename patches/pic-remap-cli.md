# pic-remap-cli

- **Commit:** Squash des PRs gegen `cosmos`
- **Dateien:** `src/Cosmos.Kernel.Core.X64/Cpu/LegacyPic.cs`
- **Grund:** `_native_x64_load_idt` endet mit `sti`, `LegacyPic.RemapAndDisable` läuft danach also mit IF=1. ICW1 löscht die Maske beider 8259; bis zum letzten Maskenschreiben liefert der Master einen anstehenden PIT-Tick (IRQ0) noch auf der Firmware-Vektorbasis 0x08 aus, also über den Stub von #DF (Vektor 8, `has_err=1`). Der Stub nimmt ein Fehlerwort vom Stack, das die CPU bei einem externen Interrupt nie legt; der um 8 Byte verschobene Rahmen löst im verwalteten Handler beim ersten `movaps` #GP(0) aus (`[INT] FATAL: Exception 13`, Fehlercode 0, RFLAGS 0x10046: IF=0 im Handler, RF=1 bei Fault). Unter Hostlast wird das Fenster zwischen ICW1 und Maske (je `out` ein VM-Exit) lang genug, dass ein Tick hineinfällt. Die Sequenz läuft jetzt mit gesperrten Interrupts; danach sind alle Leitungen maskiert. ZehOS E5-N (#521).
- **Entfernter ZehOS-Umweg:** keiner (Fehler liegt vor ZehOS-Code)
- **Prüfung:** Cosmos-Bau (`scripts/build-cosmos.sh`); ZehOS-Bootschleife KVM SMP 4 unter Hostlast bis `[ApicManager] APIC system initialized`, vorher/nachher
