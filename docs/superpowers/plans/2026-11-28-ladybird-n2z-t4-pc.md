# N2-Z T4 · Windows-PC-Vergleich unter QEMU

Prüfbericht für #755 vom 28. November 2026. Verglichen wurden `master`
[`c042ab90`](https://github.com/peterzw98/ProjektZehOS/commit/c042ab90a96b234d8e14eab645d5e57b2ab0979e)
und `story/ladybird`
[`161f03ae`](https://github.com/peterzw98/ProjektZehOS/commit/161f03aef7a07a4fe5366b08d1a1f4859b566d7c).
Pro Stand wurden ein Normal- und ein Recovery-ISO lokal aus den jeweiligen
Cosmos-Quellen gebaut. Windows, .NET SDK 10.0.404, QEMU 10.2.2/TCG mit einer
CPU und 512 MiB. `ZehOSLinuxRun` war bei allen vier Bauten deaktiviert.
[N2-Z-Torplan](2026-11-28-ladybird-n2z-tor.md)

## Bauten

| Stand | Cosmos-Pin | Normal-ISO SHA-256 | Recovery-ISO SHA-256 |
| --- | --- | --- | --- |
| `c042ab90` | `9b1f5778`, `3.0.97-zehos.6-smp.17` | `2b463486a218c525ec991967704b85f5d7d66a2f17e3a8200fd91928afc997b4` | `b1bde007c9dbdf3d90b1f6abc84ff7fbc9286814699018cea49d31f14549233c` |
| `161f03ae` | `f2b64df6`, `3.0.97-zehos.0.gf2b64df` | `1424d37b211db403aec355a8e647fe1cd1ea4087cdee7a5acae2d64d192d71d5` | `545be3354f65a6fdb1eb81346c7fff2a1a97d92ae6660539c16ff03943940e40` |

Die Cosmos-Werkzeuge wurden je Arbeitskopie installiert, und `cosmos check`
war auf beiden Ständen erfolgreich. Alle vier Kernelbauten meldeten 0 Warnungen
und 0 Fehler. Normalbau mit `cosmos build --arch x64 --config Release`,
Recovery-Bau mit `dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSRecovery=true`.

## Gastprüfungen

| Ablauf | `master` | `story/ladybird` |
| --- | --- | --- |
| Normalstart, Konto auf Wegwerfplatte, Terminal, Dateien, Einstellungen, Textshell und F4-Rückkehr, Shutdown | Bestanden, 107 s | Bestanden, 88 s |
| Recovery-Start bis Textshell ohne Desktop, Shutdown | Bestanden, 8 s | Bestanden, 7 s |
| Recovery `test-power-commands.ps1`, Reboot bis Textshell, Shutdown mit QEMU-Exit 0 | Bestanden, 12 s | Bestanden, 13 s |
| Signierte Pakete aus dem öffentlichen HTTPS-Katalog, Rechner 1.0.0 und Doom 1.1.0 | Bestanden, 299 s | Bestanden, 273 s |

Beide Rechner zeigten `2+3=5` und ließen sich schließen. Doom renderte
auf beiden Ständen im Desktopfenster und reagierte bis zur Auswahl des
Schwierigkeitsgrads auf Tastatureingaben. Die Bilder wurden visuell geprüft.

Die Desktop- und Paketproben arbeiteten mit je einem eigenen temporären
Datenträger. Der Recovery-Reboot-Test lief ohne Datenplatte. Der erste
Recovery-Versuch war wegen eines falsch kopierten ISO-Ausgabepfads ungültig:
`dotnet build` schreibt sein ISO nach `bin/Release/net10.0/linux-x64/cosmos/`,
während das CLI den Normalbau nach `output-x64/` legt. Die Tabelle verwendet
für Recovery die aus dem MSBuild-Protokoll abgeleiteten und per SHA-256
unterschiedenen ISOs. Der erste Master-App-Versuch zeigte `2+3=5`; mit
weiter geöffnetem Rechner meldete das Startmenü, dass bereits eine Paket-App
läuft. Die Vergleichsprobe schließt deshalb Rechner und wartet auf das
Wasm-Ende, bevor sie Doom startet.

Die Zeiten bezeichnen vollständige Host-Aufrufe. KVM, VMware und echte Hardware
wurden für T4 auf diesem PC nicht geprüft.
