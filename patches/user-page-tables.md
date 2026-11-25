# user-page-tables

- **Commit:** offen (Entwurf, wird in N1 nicht gemergt); Basis `cosmos` 6b28fc44
- **Dateien:** neu `src/Cosmos.Kernel.Core/Memory/UserPageTable.cs`, `src/Cosmos.Kernel.Core.X64/Cpu/PageTables.cs`, `tests/Kernels/Cosmos.Kernel.Tests.UserPaging/`; geändert `src/Cosmos.Kernel.Core.X64/Cpu/DeviceMapper.cs`, `src/Cosmos.Kernel.HAL.X64/X64PlatformInitializer.cs` (Registrierung), `src/Cosmos.Kernel.Core/PublicAPI.Unshipped.txt`, `InternalsVisibleTo` in den Projektdateien von Core und Core.X64, `nativeaot-patcher.tests.slnx`
- **Grund:** ZehOS N1-B2 (#631): Seitentabellen für Programme, ohne sie zu laden (`UserPageTable`). `ShareKernelHalf` legt alle 256 oberen PML4-Einträge an; danach legt `DeviceMapper` keine mehr an und lehnt Adressen ab, die in die untere Hälfte umlaufen. Vor dem Aufruf bleibt `DeviceMapper` unverändert. Kopien der oberen Einträge tragen nie das User-Bit; Seite 0 und die oberste Seite der unteren Hälfte sind nicht abbildbar.
- **Entfernter ZehOS-Umweg:** keiner
- **Grenzen:** kein TLB-Abgleich (keine CPU lädt die Tabellen; N1-B3 muss ihn ergänzen); leere Tabellen nach `Unmap` bleiben bis `Dispose`; ARM64 meldet `false`/`null`.
- **Prüfung:** offen, weder gebaut noch im Gast gelaufen.
