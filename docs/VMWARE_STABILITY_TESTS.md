# VMware: Zeitkalibrierung, Tastatur und Ausschalten

Pruefstand vom 15. November 2026, Implementierung `892a988`, Standardversion ZehOS 0.1.1, Cosmos Gen3 3.0.94. Es wurden keine SDKs, Pakete oder Werkzeuge aktualisiert.

## Fehler und Korrektur

Die [urspruenglichen Installer-Pruefungen](INSTALLER_UPDATES_TESTS.md#vmware-workstation-15-november-2026) zeigten falsche TSC-/LAPIC-Kalibrierung, einmal einen fehlgeschlagenen PS/2-Selbsttest und einen LAI-Abbruch beim Ausschalten. Ein erneuter Linux-Baseline-Lauf mit vier kalten Starts und je zwei VM-Resets (zwoelf Starts) lieferte neun falsche TSC-Werte zwischen etwa 13 MHz und 92 GHz; einmal wurde keine Tastatur gebunden.

`CosmosVmwareClockPlug.cs` ersetzt die beiden Kalibrierungsroutinen vor Initialisierung von Uhr, Scheduler und Treibern. Nur bei VMware-Kennung, vorhandenem CPUID-Leaf `0x40000010` und nicht leeren Frequenzen werden dessen Angaben genutzt: EAX enthaelt TSC-kHz, EBX Bus-kHz. Cosmos verwendet LAPIC-Teiler 16, also gilt `TSC-Hz = EAX * 1000` und `LAPIC-Ticks/ms = EBX / 16`. Die Schnittstellenbelegung wird auch im [VMware-Code des Linux-Kernels](https://github.com/torvalds/linux/blob/master/arch/x86/kernel/cpu/vmware.c) verwendet. Andere Umgebungen behalten die Cosmos-3.0.94-PIT-/LAPIC-Messung. [Herkunft und BSD-Lizenz der uebernommenen Routinen](../ProjektZeh/ThirdParty/CosmosTiming/README.md).

`Native/Acpi/pci_host.c` ergaenzt genau die sechs von LAI erwarteten `laihost_pci_read{b,w,d}`-/`write{b,w,d}`-Symbole. Die im Cosmos-Paket vorhandenen generischen PCI-Stubs werden von LAI nicht verwendet. Die Ergaenzung nutzt den bestehenden x64-Konfigurationsmechanismus ueber `0xCF8/0xCFC`, beachtet Zugriffbreite und Byteposition und sperrt Interrupts fuer Adresse plus Daten. Der vorherige Interruptzustand bleibt erhalten. Unterstuetzt sind Segment 0 und die ersten 256 Konfigurationsbytes; ungueltige Zugriffe bleiben ohne Portzugriff. Das entspricht der bestehenden x64-PCI-Anbindung. Die Sicherung ist fuer die Ein-CPU-Referenz ausgelegt, keine SMP-Sperre. Seit ZehOS 0.5.4 (Cosmos 3.0.97, uACPI statt LAI) entfallen `pci_host.c` und sein C-Test; uACPI bringt den PCI-Zugriff selbst mit, und `shutdown` schaltet VMware aus (siehe COSMOS_SUPPORT.md).

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

## Unabhaengige Gegenpruefung unter Linux / Workstation 26.0.1

Der Agent der urspruenglichen VMware-Installer-Pruefung hat `892a988` mit Freigabe seines Benutzers separat gebaut und getestet. Umgebung und VMX entsprechen dem [urspruenglichen Pruefaufbau](INSTALLER_UPDATES_TESTS.md#vmware-workstation-15-november-2026). Fuer `scripts/build-release.sh 0.1.0` wurde ausdruecklich die Testversion 0.1.0 gesetzt; danach wurde im Arbeitsbranch nur die Standardversion auf 0.1.1 erhoeht. Die Kernelkorrektur ist identisch. Die Ergebnisse wurden im Projektkanal am 15. November 2026 uebergeben:

| Pruefung | Ergebnis |
| --- | --- |
| Release-Artefakte einschliesslich Setup-ISO | Build ohne Warnungen/Fehler |
| Vier kalte Live-Starts mit jeweils zwei VM-Resets | 12/12 mit beiden CPUID-Markern, TSC 3072000000 Hz, LAPIC 4125 Ticks/ms und gebundener Tastatur |
| Uhr gegen Hostzeit | `uptime` +60,97 s bei 61,0 s Hostzeit |
| Setup-Assistent und Installation auf neue Testplatte | erfolgreich |
| Warmstart ueber „Neu starten“, Start ohne ISO, Adminanlage, `/data` | erfolgreich |
| Weiterer Kaltstart und Anmeldung | erfolgreich |
| `reboot` vom installierten System | startet erneut; Tastaturgrenze siehe unten |
| `shutdown` vom installierten System | `_PTS(5)` ohne LAI-Panik, VM schaltet sich aus |
| Testplatte danach auf dem Host | FAT-Pruefung sauber; Kernel und `.bak` stimmen mit gebautem ELF ueberein |
| Kalibrierung bei Setup- und Plattenstarts | durchgehend beide CPUID-Marker, TSC 3072000000 Hz und LAPIC 4125 Ticks/ms |

### Verbleibender PS/2-Fehler bei korrektem Takt

Bei einem von insgesamt 26 Starts meldete `Ps2KeyboardDriver` nach `reboot` des installierten Systems `enable scanning was not acknowledged`. Die Tastatur blieb bis zu einem Kaltstart unbenutzbar. Sechs weitere Shell-Neustarts und sechs VM-Resets mit Tastendruecken reproduzierten den Fehler nicht. Der Fehler ist von der bisherigen fehlgeschlagenen Selbstpruefung bei falscher Zeitkalibrierung zu unterscheiden: Beide CPUID-Frequenzen waren hier korrekt.

Die Quellpruefung von Cosmos 3.0.94 (Commit `5d0b953` laut Paketmetadaten) zeigt, dass `Ps2Access.TryCommand` einen belegten Austausch, einen fehlgeschlagenen Controller-Schreibzugriff, viermal Resend (`0xFE`) und einen ACK-Timeout mit demselben `false` meldet. Der Controller wartet bis zu 10 ms auf seinen Eingabepuffer, die Aktivierung bis zu 100 ms auf das ACK. Im Fehllauf liegen zwischen `published keyboard` und der Fehlermeldung nur zwei LAPIC-Ticks des 10-ms-Timers, also etwa 10 bis hoechstens 30 ms, sofern keine Timerinterrupts verloren gingen; TSC- und LAPIC-Frequenz stammen beide aus VMware-CPUID. Die Untergrenze passt zur 10-ms-Wartezeit von `TrySend`. Der 100-ms-ACK-Timeout ist damit sehr unwahrscheinlich. Ein belegter Austausch ist ebenfalls unwahrscheinlich: LED-Befehle von `KeyboardManager` entstehen erst bei Sperrtasten und laufen auf demselben Treiberthread wie die Bindung. Es bleiben ein voller i8042-Eingabepuffer (10 ms in `TrySend`) oder wiederholtes Resend der Tastatur. Die vorhandene Meldung unterscheidet diese Faelle nicht. Ein oeffentlicher Weg, eine fehlgeschlagene Bindung erneut anzubieten, besteht in Cosmos 3.0.94 nicht. Ohne reproduzierbaren Fehler oder entsprechende Laufzeitdaten waere eine Treiberaenderung spekulativ; dieser Befund ist deshalb ausdruecklich **nicht behoben**.

Der Gegenpruefer ordnet den Fehllauf seinem Linux-Protokoll `.tools/vmware-e2e3/serial.log` zu (Reset bei Zeile 131008, Fehler bei 131315): Admin in der Textshell nach etwa 140 s Laufzeit, `/data` eingebunden, Desktop zuvor geoeffnet. `reboot` und Return wurden per VNC mit jeweils 50 ms Tastendruck und 80 ms Abstand gesendet; keine Taste blieb gehalten, kein Logout. Nach Return-Up folgten keine Eingaben bis zum Kaltstart. Ein gehaltener Neustart-Tastendruck ist damit fuer diesen beobachteten Lauf nicht belegt.

### Reset-Schleifen vom 15. November 2026

`scripts/test-vmware-ps2-reset.py` startet ZehOS in VMware Workstation unter Linux wiederholt neu und zaehlt nach jeder seriellen Zeile `ps2:kbd offer Ps2KeyboardDriver -> ...` das Ergebnis. Es kennt zwei Arten des Neustarts:

- `--iso`: Start nur vom ISO, ohne Datenlaufwerk und Netz; neu gestartet wird mit `vmrun reset hard`.
- `--vmx`: eine ausgeschaltete VM mit installiertem ZehOS und VNC auf `127.0.0.1`. Nach jeder Bindung meldet sich das Skript ueber `scripts/vnc_type.py` an (`--user`, Kennwort aus `ZEHOS_PS2_PASSWORD`), wechselt mit F4 in die Textshell und tippt `reboot`. Das ist der Warmstart ueber ACPI (`lai_acpi_reset`), nach dem der Fehler beobachtet wurde. Scheitert eine Bindung, startet `vmrun reset hard` die VM neu. VMware-VNC tippt mit US-Belegung, der Gast nutzt die deutsche; Konto und Kennwort deshalb ohne `y`, `z` und Sonderzeichen waehlen.

```sh
python3 scripts/test-vmware-ps2-reset.py --iso ProjektZeh/output-x64/ProjektZeh.iso --runs 100
ZEHOS_PS2_PASSWORD=<Testkennwort> python3 scripts/test-vmware-ps2-reset.py --vmx <VM>.vmx --vnc-port 5974 --runs 50
```

Ergebnisse mit VMware Workstation 26.0.1 unter Linux, 1 vCPU, UEFI:

| Ablauf | Stand | Starts | Ergebnis |
| --- | --- | --- | --- |
| Hardware-Reset, ISO ohne Datenlaufwerk | master `1671481` (0.2.1) | 100 | 100 `bound` |
| Gast-`reboot` nach Anmeldung, installierte Platte | `b817c24` (0.2.4, Installer-ISO, neue 256-MiB-Platte) | 50 (49 Warmstarts) | 50 `bound` |

Jeder Start meldete TSC 3072000000 Hz und LAPIC 4125 Ticks/ms. Jeder der 49 Warmstarts folgte auf Anmeldung, F4 und Tastatureingaben ueber VNC (50 ms Druck, 50 ms Abstand). Der Fehler liess sich damit nicht reproduzieren; bei einer beobachteten Rate von 1 zu 26 waere in 49 Warmstarts mit etwa 85 % Wahrscheinlichkeit mindestens ein Fehllauf zu erwarten gewesen. Abweichend vom Fehllauf dauerte ein vollstaendiger Zyklus mit Start, Anmeldung und `reboot` nur etwa 16 s, der Desktop blieb ohne geoeffnete Fenster und `/data` war die frisch installierte Platte. Der Befund bleibt offen.

## Grenzen

Keine Aussage zu echter Hardware, mehreren CPUs, anderen VMware-Versionen oder weiteren PCI-Segmenten. VMware-Mausbedienung, Aufloesungswechsel, Netzwerk, Kernelupdates und Rueckfallstart werden durch diese Korrektur nicht als neu geprueft bezeichnet. Es wurde kein PS/2-Treiber auf Verdacht veraendert; der beschriebene seltene ACK-Fehler bleibt offen.

Lokale Protokolle und ISO-Kopien liegen unter dem ignorierten `.tools/vmware-stability` und `.tools/vmware-source`; sie werden nicht als Quellcode eingecheckt.
