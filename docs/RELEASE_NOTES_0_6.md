# ZehOS 0.6 – Release-Notes (Entwurf)

**Status: Entwurf, nicht veröffentlicht.** Peter gibt den Text frei und legt Tag und Release an. Das erste 0.6-Release ist **0.6.1**: Der Merge von #309 (Storage-Schalter im Release-Build) leitet 0.6.1 ab. Der frühere Merge von #307 (0.6.0) enthält diese Schalter noch nicht und wird deshalb nicht getaggt.

## Neu: Speicher mit Linux-Dateibaum

- **Installer mit GPT-Layout:** Neben dem bisherigen Layout bietet das Installer-ISO „GPT mit ext2-Root (32 bis 128 GiB)“ an. Angelegt werden eine EFI-Partition (512 MiB, FAT32) und ein ext2-Root.
  - `--erase` ist Pflicht und löscht die ganze Platte, auch ein dort installiertes ZehOS samt Konten und `/home`.
  - Abgewiesen werden das Installationsmedium, `ZEHOSDATA`-Datenplatten, eingebundene Ziele und Rechner ohne Hardware-Zufallsquelle.
- **Root-Profil:** Ein so installiertes System startet mit ext2 als `/`, `/boot/efi`, `/home`, `/etc/zehos` und `/var/lib/zehos`. Bestehende Installationen mit FAT-Datenplatte starten unverändert mit `/data` (Legacy-Profil).
- **Migration:** `system migrate` überträgt einen Legacy-Bestand (Konten, Rechte, Dateien) auf eine neu installierte GPT-Platte. Die Quelle bleibt unverändert. Ein Abbruch lässt sich nach `e2fsck -fy` wiederholen.
- **Virtuelle Dateien:** `/proc`, `/sys` und `/dev` lassen sich lesen, zum Beispiel `cat /proc/version` und `/proc/mounts`.
- **Updates und Energieaktionen im Root-Profil:**
  - Kernel-Updates landen auf `/boot/efi`; erst nach einem Sync der EFI-Partition gilt ein Update als erfolgreich.
  - `shutdown` und `reboot` verweigern, solange eine Paket-App läuft. Danach sperren sie alle Schreibzugriffe, hängen sauber aus und markieren den Root als sauber.
  - Bei jedem Fehler nehmen sie alles zurück; die Shell bleibt bedienbar.
- **Cosmos-ext2-Korrekturen per Plug:** keine Verzeichniseinträge über Blockgrenzen (#225), vollständige Freigabe bei `rmdir` (#226) und beim Kürzen großer Dateien (#259). Dazu kommen eine gemeinsame Speicher-Sperre gegen Nebenläufigkeitsfehler (#247) und abgesicherte Thread-Starts unter Speicherdruck (#241, #246).

## Geprüft

QEMU/KVM, Einzelheiten in [Storage-Abnahme 0.6](STORAGE_0_6_ABNAHME.md):
- Installer G1–G9,
- Migration G4–G6,
- Energieaktionen P1–P6 einschließlich Fehlerprobe,
- Gesamtmatrix 32/64/128 GiB,
- Legacy-Regression,
- Release-Build.

## Bekannte Grenzen

- **Speicher und Dateien:**
  - Das Root-Profil braucht mindestens 2 GiB RAM.
  - Höchstens etwa 17.500 verschiedene Dateien je Mount und Sitzung (Cosmos-Inode-Cache) und 8189 Einträge je Verzeichnis.
  - `du` und `find` sind auf 1024 Einträge begrenzt.
  - Dateien aus `/proc`, `/sys` und `/dev` lassen sich lesen, aber nicht kopieren.
- **Stromausfall:** Es gibt keine Stromausfallsicherheit (ext2 ohne Journal). Nach einem harten Abbruch ist offline `e2fsck -fy` nötig.
- **Fremd formatierte FAT-Platten:**
  - Kurze kleingeschriebene 8.3-Namen fremder Systeme erscheinen groß.
  - Extern formatierte FAT32 mit großen Clustern werden nicht unterstützt.
- **Installer:** Unter starker Hostlast kann die Installation mit einem SATA-Timeout abbrechen. Die Platte gilt dann als unklar; eine Neuinstallation mit `--erase` gelingt.
- **Nicht geprüft:** VMware, echte Hardware und ein voller Datenträger auf großen Platten.
