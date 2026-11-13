# Installation und Updates – Entwurf

Stand: 13. November 2026. Baut auf `feature/user-management` (`2bca0db`) auf. Cosmos 3.0.94, SDK 10.0 und BouncyCastle 2.7.0 bleiben unverändert.

## Entscheidungen

| Frage | Entscheidung |
| --- | --- |
| Updatequelle | GitHub Releases sind die Quelle. ZehOS hat kein TLS; der Gast lädt deshalb über einfaches HTTP von einem konfigurierbaren Spiegel. `scripts/update-mirror.py` spiegelt das neueste GitHub-Release per HTTPS auf dem Host. Echtheit sichert eine Ed25519-Signatur, nicht der Transport. Offline-Update aus einer Datei unter `/data` ist ebenfalls möglich. |
| Installationsziel | Jede erkannte Platte außer dem Installationsmedium. Platten mit Partitionen nur nach ausdrücklicher Bestätigung „alles löschen“. |
| Startmodus | Nur UEFI. Kein BIOS-Stage auf der Zielplatte. |
| Installer | Eigener Buildvariant `-p:ZehOSInstaller=true`, startet direkt in einen Einrichtungsassistenten im Stil von Windows Setup. |

## Plattenlayout nach der Installation

MBR mit genau einer Partition ab LBA 2048, Typ `0xEF` (EFI-Systempartition), FAT32. Sie ist zugleich `/data`, damit `CosmosStorageService.Mount` (genau eine Partition) unverändert funktioniert.

```
/EFI/BOOT/BOOTX64.EFI        Limine-UEFI aus cosmos.build.common/3.0.94/tools/Limine
/boot/limine/limine.conf     Eintrag „ZehOS“ und „ZehOS (vorherige Version)“
/boot/ProjektZeh.elf         installierter Kernel
/boot/ProjektZeh.elf.bak     vorheriger Kernel nach einem Update (Rückfall im Bootmenü)
/boot/zehos-version.txt      installierte Version
```

## Updatepaket (Release-Assets)

| Datei | Inhalt |
| --- | --- |
| `ZehOS-<v>.iso` | Live-ISO wie bisher |
| `ZehOS-Setup-<v>.iso` | Installer-ISO |
| `ProjektZeh-<v>.elf` | Kernel für das Update |
| `update-manifest.txt` | Textmanifest, siehe unten |
| `update-manifest.sig` | 64 Byte rohe Ed25519-Signatur über die Manifestbytes |

```
zehos-update 1
version=0.2.0
kernel=ProjektZeh-0.2.0.elf
size=7795440
sha256=<64 hex>
```

Textformat statt JSON: kein reflexionsbasiertes Parsen im NativeAOT-Kernel. Der öffentliche Schlüssel liegt im Kernel; der private als GitHub-Secret `ZEHOS_UPDATE_SIGNING_KEY` (PEM). Versionen sind `major.minor.patch`; Update nur bei echt höherer Version.

## Ablauf `update`

`update source <host[:port]>` speichert den Spiegel in `/data/.zehos-update.conf` (Standard `10.0.2.2:8080`, QEMU-User-Netz). `update check` lädt Manifest und Signatur, prüft Signatur und Version. `update install [Datei]` lädt beziehungsweise liest den Kernel, prüft Größe und SHA-256, ersetzt `/boot/ProjektZeh.elf` über die bestehende Sicherungs-/Ersetzungslogik und behält den alten Kernel als `.bak`. Neustart durch den Benutzer. Nur Administratoren.

## Schnittstelle Installer ↔ Oberfläche

`ProjektZeh/Installer/IInstallService.cs` (plattformunabhängig, hostgetestet). Die Installation läuft schrittweise über `IInstallJob.Step()`, damit die kooperative Schleife Fortschritt zeichnet und Eingaben annimmt.

## Arbeitsteilung

| Teil | Dateien |
| --- | --- |
| Backend (Codex) | `Installer/` außer UI, `Update/`, Shellbefehle, Cosmos-Adapter, `Kernel.cs`, Projektdateien, Hosttests |
| CI/Werkzeuge (Codex) | `.github/workflows/release.yml`, `scripts/build-release.sh`, `scripts/sign-update.py`, `scripts/update-mirror.py` samt Tests |
| Oberfläche (Claude) | `Installer/SetupWizard.cs`, `Runtime/InstallerSession.cs`, Hosttests der Oberfläche |

## Grenzen

Kein HTTPS im Gast; ohne Spiegel kein Online-Update. Kein BIOS-Start installierter Systeme. Ein Stromausfall während des Ersetzens ist nicht abgesichert; der Bootmenü-Rückfall hilft nur, solange `.bak` vollständig ist. Datenmigration zwischen Versionen ist nicht vorgesehen.
