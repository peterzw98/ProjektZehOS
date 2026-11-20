# ahci-large-commands

- **Commit:** Squash des PRs gegen `cosmos`
- **Dateien:** `src/Cosmos.Kernel.Drivers/Pci/Storage/Ahci/AhciPort.cs` (`MaxSectorsPerCommand`, Bounce-Puffer im Konstruktor, `ReadBlock`, `WriteBlock`, Warteschleife in `IssueCommand`); Test `tests/Kernels/Cosmos.Kernel.Tests.Storage/Kernel.cs` (`Device_CommandSizes_OddLba`), `tests/profiles.json` (Kommentar), `docs/articles/dev/testing.md` (Testzahlen)
- **Grund:** Jeder SATA-Befehl bewegte höchstens eine Seite (8 Sektoren, 4 KiB) über einen Bounce-Puffer von einer Seite, und das Warten auf PxCI pausierte je Abfrage mit `DeviceBinding.Delay(10)`, auf x64 zehn Lesezugriffe auf Port 0x80, unter KVM je ein VM-Exit. Je 4 KiB kostete das einen ganzen Befehlsumlauf (ZehOS #542, Plan Paket A)
- **Änderung:**
  - Bounce-Puffer 64 KiB, physisch zusammenhängend (`TryAllocateDma`, bei 32-Bit-HBA unter 4 GiB), ein PRDT-Eintrag; bis zu 128 Sektoren je Befehl. Gibt es keine 64 KiB, fällt der Port auf eine Seite und 8 Sektoren je Befehl zurück.
  - Warten auf PxCI: die ersten 2000 Abfragen pausieren je 2 µs auf dem Zeitstempelzähler (`Stopwatch`, kein Portzugriff), danach wie bisher `Delay(10)`. Task-File-Fehler und Zeitlimit werden in jeder Runde geprüft wie vorher.
- **Grenzen:** Weiter ein Befehl je Port zur Zeit, abgefragt statt per Interrupt, mit Kopie durch den Bounce-Puffer. Die schnelle Abfrage hält die CPU bis zu etwa 4 ms busy, wie vorher die Portzugriffe. ATAPI (`AhciAtapiPort`) unverändert.
- **Entfernter ZehOS-Umweg:** keiner
- **Prüfung:** siehe PR
