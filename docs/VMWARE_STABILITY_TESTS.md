# VMware: Zeitkalibrierung, Tastatur und Ausschalten

Pruefstand vom 15. November 2026, Implementierung `892a988`, Cosmos Gen3 3.0.94. Es wurden keine SDKs, Pakete oder Werkzeuge aktualisiert.

## Fehler und Korrektur

Die [urspruenglichen Installer-Pruefungen](INSTALLER_UPDATES_TESTS.md#vmware-workstation-15-november-2026) zeigten falsche TSC-/LAPIC-Kalibrierung, einmal einen fehlgeschlagenen PS/2-Selbsttest und einen LAI-Abbruch beim Ausschalten. Ein erneuter Linux-Baseline-Lauf mit vier kalten Starts und je zwei VM-Resets (zwoelf Starts) lieferte neun falsche TSC-Werte zwischen etwa 13 MHz und 92 GHz; einmal wurde keine Tastatur gebunden.

`CosmosVmwareClockPlug.cs` ersetzt die beiden Kalibrierungsroutinen vor Initialisierung von Uhr, Scheduler und Treibern. Nur bei VMware-Kennung, vorhandenem CPUID-Leaf `0x40000010` und nicht leeren Frequenzen werden dessen Angaben genutzt: EAX enthaelt TSC-kHz, EBX Bus-kHz. Cosmos verwendet LAPIC-Teiler 16, also gilt `TSC-Hz = EAX * 1000` und `LAPIC-Ticks/ms = EBX / 16`. Die Schnittstellenbelegung wird auch im [VMware-Code des Linux-Kernels](https://github.com/torvalds/linux/blob/master/arch/x86/kernel/cpu/vmware.c) verwendet. Andere Umgebungen behalten die Cosmos-3.0.94-PIT-/LAPIC-Messung. [Herkunft und BSD-Lizenz der uebernommenen Routinen](../ProjektZeh/ThirdParty/CosmosTiming/README.md).

`Native/Acpi/pci_host.c` ergaenzt genau die sechs von LAI erwarteten `laihost_pci_read{b,w,d}`-/`write{b,w,d}`-Symbole. Die im Cosmos-Paket vorhandenen generischen PCI-Stubs werden von LAI nicht verwendet. Die Ergaenzung nutzt den bestehenden x64-Konfigurationsmechanismus ueber `0xCF8/0xCFC`, beachtet Zugriffbreite und Byteposition und sperrt Interrupts fuer Adresse plus Daten. Der vorherige Interruptzustand bleibt erhalten. Unterstuetzt sind Segment 0 und die ersten 256 Konfigurationsbytes; ungueltige Zugriffe bleiben ohne Portzugriff. Das entspricht der bestehenden x64-PCI-Anbindung. Die Sicherung ist fuer die Ein-CPU-Referenz ausgelegt, keine SMP-Sperre.

Die erste Gegenpruefung von `dc4794c` erkannte den noch unwirksamen TSC-Plug: `Plug.TargetName` verlangt den Typnamen ohne Assemblyzusatz, waehrend `UnsafeAccessorType` den Assemblyzusatz benoetigt. `892a988` korrigiert die Zuordnung. Das unabhaengige Review hat anschliessend den CPUID-Pfad in der tatsaechlich gepatchten `X64CpuOps.CalibrateTsc` geprueft; ein erfolgreicher Build allein haette das Ueberspringen nicht erkannt.

## Host und QEMU unter Windows

- SDK 10.0.403. Vollstaendige Hosttests mit `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`: 433 von 434 bestanden. Einziger Fehler: `TlsChecksChainNameAndDates`, bereits vor diesen Aenderungen reproduziert (Baseline 431 von 432). Die zwei neuen Frequenztests bestehen.
- Nativer Release- und Recovery-Build von `892a988`: jeweils null Warnungen und Fehler. Ein zwischenzeitlicher Recovery-Versuch scheiterte an einer Dateisperre waehrend paralleler Assembly-Untersuchung; der sequenzielle Wiederholungslauf bestand.
- QEMU ohne Datenplatte: Normalboot bis Shell und Desktop, F4-Wechsel zur Shell, `reboot` bis zum erneuten Boot und `shutdown` mit Exitcode 0. Der normale Energiehelfer benoetigt fuer diesen Desktopstand F4 vor den Befehlen; fuer diesen Lauf wurde eine lokale Kopie mit genau diesen Fokuswechseln verwendet.
- Recovery: Textshellstart, `reboot` und `shutdown` mit dem unveraenderten `scripts/test-power-commands.ps1` bestanden. Kein GDB-Zusatzlauf.
- C-Regression fuer LAI-Portadresse, Zugriffbreite, Daten, Interruptschutz und abgewiesene Zugriffe:

```powershell
gcc -std=c11 -Wall -Wextra -Werror scripts/tests/pci_host_test.c -o .tools/pci_host_test.exe
& .tools/pci_host_test.exe
```

Der Lauf meldete `LAI PCI host tests passed`. Der Test ersetzt nur Port-I/O und Interruptmaskierung; die echte Wirkung im Gast pruefen die VMware-Laeufe.

## VMware Player unter Windows

VMware Player 17.5.2 (Build 23775571), neue plattenlose Test-VM unter `.tools/vmware-stability`, UEFI, 1 vCPU, 1024 MiB, ohne Netzwerkkarte. Der vorhandene VMware-Ordner und vorhandene Datenimages wurden nicht verwendet. Der Test laeuft ohne sichtbares Fenster ueber `vmrun`.

Der kalte Normalstart und zwei anschliessende harte VM-Resets liefern jeweils beide CPUID-Marker, TSC `3072000000 Hz`, LAPIC `4125 ticks/ms`, gebundene PS/2-Tastatur und einen bereiten Desktop. Ein VM-Reset ist hier keine durch die ZehOS-Shell ausgeloeste Energieaktion. Windows-Installer und Shell-Energieaktionen in VMware Player sind damit nicht geprueft.

## Grenzen

Keine Aussage zu echter Hardware, mehreren CPUs, anderen VMware-Versionen oder weiteren PCI-Segmenten. VMware-Mausbedienung, Aufloesungswechsel, Netzwerk, Kernelupdates und Rueckfallstart werden durch diese Korrektur nicht als neu geprueft bezeichnet. Die beobachteten Tastaturfehler werden durch korrekte Kalibrierung vermieden; es wurde kein PS/2-Treiber auf Verdacht veraendert.

Lokale Protokolle und ISO-Kopien liegen unter dem ignorierten `.tools/vmware-stability` und `.tools/vmware-source`; sie werden nicht als Quellcode eingecheckt.
