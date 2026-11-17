# Etappe 7b: Updates mit `/boot/efi` + Root, Herunterfahren und Neustart – Entwurf

Stand: 17. November 2026, Basis `origin/master` @ `06ac97a`. Plan [Etappe 7b](../plans/2026-11-17-storage-linux.md#etappe-7-installer-updates-und-energieaktionen) nach Etappen 4–6. Baut auf [Etappe 4 §1/§2](2026-11-17-storage-etappe4-design.md) (Profile, `/boot/efi`, `s_state`, doppelter Sync) und [7a-S4](2026-11-17-storage-7a-s4-installer.md) auf. **Nur Entwurf**, kein Code, nichts im Gast. Cosmos-Aussagen aus der Quelle `efcf20c` (= 3.0.97). Umsetzung erst nach 4.0-Go; 4.3 liefert den gemeinsamen Baustein (§3).

## 0. Entscheidungen (Vorschlag)

1. **`Legacy` bleibt unverändert** (heute: `/data` aushängen, `Kernel.cs:313-337`, `UpdateController`/`FileUpdateTarget` unter `/data`). Alles Neue gilt nur im `Root`-Profil.
2. **Ein Update schreibt nur auf die ESP.** Kernel, `.bak` und Versionsmarker liegen alle unter `/boot/efi/boot/…` (`SystemPaths.UpdateRoot`). Die bestehende Reihenfolge „Marker zuerst, dann Kernel mit `.bak`-Rotation, bei Fehler Marker zurück“ (`UpdateService.cs:83-93`) bleibt die einzige Konsistenzregel; es gibt keine Transaktion über zwei Dateisysteme und keine wird behauptet. Root enthält nur die Updatekonfiguration (`/etc/zehos/update.conf`), die bei `update source …` geschrieben wird, nie während `update install`.
3. **Dauerhaftigkeit vor Erfolgsmeldung:** nach `ReplaceKernel` ESP synchronisieren und `Device.Flush()`; erst dann meldet `update install` Erfolg (Cosmos-FAT puffert, vgl. 7a-S4 P2-1).
4. **Herunterfahren/Neustart sind fail-closed:** jeder Fehler vor der eigentlichen Energieaktion bricht ab, stellt den vorherigen Zustand her (Mounts, Netzwerk, `s_state` = 0) und lässt die Shell bedienbar.
5. **Ein gemeinsamer Baustein `RootVolumeState`** (aus 4.3) für `s_state` = 0 nach dem Mount und Sync, Sync, `s_state` = 1, `Device.Flush()` beim Herunterfahren; Installer (S4), Energieaktion (7b) und Probe (4.0) nutzen ihn, keine Kopie.

## 1. Updates im `Root`-Profil

| Teil | Heute (`Legacy`) | `Root` |
|---|---|---|
| Ziel `FileUpdateTarget` | `new FileService("/data")`, Pfade `boot/…` (`FileUpdateTarget.cs:8-14`) | Wurzel `SystemPaths.UpdateRoot` = `/boot/efi`, **eigene `FileService`-Instanz mit `CosmosPathGuard.For("/boot/efi")`** (Etappe 4 §2); der Nutzer-`FileService` sieht `/boot/efi` nicht |
| Konfiguration `UpdateController` | `/data/.zehos-update.conf` (`UpdateController.cs:16`) | `SystemPaths.UpdateConfigPath` = `/etc/zehos/update.conf` über eigene Instanz mit `For("/")` (für Nutzer gesperrt, Paket 4.2) |
| Offline-Kernel `update install <datei>` | muss unter `/data` liegen (`UpdateController.cs:67-69`) | muss ein für den Administrator lesbarer verwalteter Pfad unter `/` sein, **nicht** unter `/boot/efi`, `/etc/zehos`, `/var/lib/zehos`; gelesen über den Nutzer-`FileService` des Admins (Rechte, Pfadwächter) |
| Nach `ReplaceKernel` | – | ESP-Sync + `Device.Flush()` vor der Erfolgsmeldung (§0.3); Fehler dabei ⇒ wie heute Marker zurücksetzen, Meldung „Update unvollständig, Rückfalleintrag bleibt startfähig“ |

`UpdateController` bekommt dazu Konfigurationspfad und Offline-Wurzel getrennt (heute ein gemeinsames `root`); im `Legacy`-Profil bleiben beide `/data`.

## 2. Energieaktion im `Root`-Profil

`PreparePowerAction` (`Kernel.cs:313`) bekommt eine zweite Reihenfolge; die ersten drei Schritte sind die heutigen.

1. Kennwortoperationen laufen ⇒ Abbruch (unverändert).
2. `Sys.Power.IsSupported` ⇒ sonst Abbruch (unverändert).
3. Netzwerk stoppen, Hintergrundaufgaben `StopAll(2000)` ⇒ sonst Abbruch (unverändert).
4. **Laufende Aufträge** ⇒ Abbruch mit Meldung: Paket-Apps (halten Dateien im Paketbestand offen, „Apps zuerst schließen“), `update install`, Migration, Kopier-/Verschiebeaufträge (analog `HasRunningPasswordOperations`). Danach **Schreibsperre für Root** auf Ebene aller `FileService`-Instanzen (Nutzer-, Konten-, Paket-, Update-Instanz) und `RootVolumeState`; sie gilt auch für den Shell-/UI-Thread (Einstellungen, Verlauf, Protokolldatei, Editor) und wird **erst durch `Reopen` aufgehoben** (peter-review P2-2). Grund: Cosmos verweigert das Aushängen bei offenen Dateien **nicht**, es nimmt den Mount einfach heraus und offene Dateien scheitern beim nächsten Zugriff (`VfsManager.cs:252-292`); einen „busy“-Zustand muss ZehOS selbst sicherstellen.
5. **Weitere persistente Mounts aushängen**, feste Reihenfolge: erst nachträglich eingehängte Datenträger (`disk mount`, `/mnt/*`, ggf. `/data`) in umgekehrter Einhängereihenfolge, **danach immer `/boot/efi`**: je Mount zuerst `SuperOperations.Sync` (Rückgabewert geprüft) und `Device.Flush()`, dann `TryUnmount`. Grund: `TryUnmount` meldet einen Fehler in `Drop` nicht und eine Flush-Ausnahme erst, wenn der Mount schon entfernt ist (`VfsManager.cs:250-256`); der vorgezogene Sync macht Fehler sichtbar, solange noch zurückgerollt werden kann. Für die Rücknahme wird der Zustand je Mount über `VfsManager.TryGetMount` neu bestimmt, nicht gezählt: ein Mount, dessen Flush beim Aushängen warf, ist schon entfernt und muss mit zurück (peter-review P2-3).
6. Virtuelle Mounts (`/proc`, `/sys`, `/dev`) halten keine Daten und bleiben eingehängt.
7. **Root:** `RootVolumeState.Close()` = Sync, Sync, `s_state` = 1 (Lesen-Ändern-Schreiben), `Device.Flush()`; `/` wird nicht ausgehängt.
8. `Sys.Power.Reboot`/`Shutdown`.

**Abbruch und Rücknahme:**

| Fehler in | Rücknahme | Ergebnis |
|---|---|---|
| 1–4 | Netzwerk wieder starten (heute schon) | Shell bedienbar, nichts ausgehängt |
| 5 (Sync, Flush oder Aushängen eines Mounts) | alle Mounts, die laut `TryGetMount` fehlen (einschließlich des fehlgeschlagenen), in Einhängereihenfolge wieder einhängen; Schreibsperre aufheben (Root war unberührt, `s_state` noch 0); Netzwerk starten | Shell bedienbar; scheitert das Wiedereinhängen, wird es protokolliert und gemeldet („/boot/efi nicht eingehängt, Updates gesperrt“), der Rest läuft weiter |
| 7, Fehler **vor** dem Schreiben von `s_state` = 1 (Sync) | `s_state` bleibt 0; Rücknahme wie 5 | Shell bedienbar, nächster Boot nach Absturz wäre `RootUnclean` (gewollt) |
| 7, Fehler **nach** dem Schreiben von `s_state` = 1 (Flush, Neulesen) | auf der Platte kann schon 1 stehen ⇒ `Reopen` (`s_state` = 0 + Flush) vor jeder Aufhebung der Schreibsperre, dann Rücknahme wie 5 | Shell bedienbar (peter-review P2-1) |
| `Reopen` scheitert | Root bleibt **schreibgesperrt** bis ein späteres `Reopen` gelingt; Meldung „Root schreibgesperrt, neu starten“; Lesen bleibt möglich | kein Schreiben über einem sauberen Kennzeichen |
| 8 kehrt zurück | `RootVolumeState.Reopen()` setzt `s_state` = 0 + Flush, erst dann Schreibsperre aufheben, Mounts wie 5 zurück | Shell bedienbar; ohne `Reopen` stünde ein sauberes Kennzeichen über einem weiter beschriebenen Root |

`Legacy`: unverändert (nur `/data` aushängen), zusätzlich §0.3 nicht nötig, weil `Unmount` dort ohnehin Drop/Flush auslöst.

## 3. Gemeinsamer Baustein `RootVolumeState` (aus 4.3)

- `Open(device)`: `s_state` = 0 per Lesen-Ändern-Schreiben, `Device.Flush()`; vor dem ersten Datenschreibzugriff nach dem RW-Mount.
- `Close()`: Sync, Sync (Cosmos schreibt den Superblock nach dem Flush, Etappe 4 §2), `s_state` = 1, `Device.Flush()`; danach Superblock neu lesen und `s_state` = 1 prüfen.
- `Reopen()`: wie `Open` nach einem zurückgekehrten oder abgebrochenen `Close`.
- Der Superblock-Zugriff am Treiber vorbei geschieht **nur** in diesen drei Methoden und nie nebenläufig zu Dateischreibern (Etappe 4 §2). Reine Logik (Offset, Lesen-Ändern-Schreiben über ein `IBlockDevice`-Abbild) ist hosttestbar.

Reihenfolge der Umsetzung: 4.3 liefert den Baustein; S4 und 7b nutzen ihn; die 4.0-Probe darf ihn vorab als Kopie in der Probe tragen, bis 4.3 gemergt ist (Probe ist kein Produktcode).

## 4. Tests

**Hosttests:**
1. `UpdateController`/`FileUpdateTarget` im `Root`-Profil: Ziel `/boot/efi/boot/ProjektZeh.elf`, Konfiguration `/etc/zehos/update.conf`; Offline-Kernel unter `/home/admin/x.elf` erlaubt, unter `/boot/efi/…`, `/etc/zehos/…`, `/var/lib/zehos/…` abgelehnt; `Legacy` byteidentisch zu heute (bestehende Tests).
2. `update install`: Sync/Flush-Fehler nach `ReplaceKernel` ⇒ Marker zurückgesetzt, Fehlermeldung, `.bak` unverändert.
3. `PreparePowerAction` `Root` mit Fakes für Mounts, Sync, Flush, Unmount, `RootVolumeState`: Reihenfolge 1–8; Fehler in jedem Schritt ⇒ Rücknahme laut Tabelle, Netzwerk wieder an, Mounts wieder eingehängt in richtiger Reihenfolge; laufende Paket-App, `update install`, Migration, Kopierauftrag ⇒ Abbruch vor jedem Aushängen; Schreibversuch (alle `FileService`-Instanzen) nach Schritt 4 abgelehnt; Fake „Flush wirft nach Entfernen des Mounts“ ⇒ dieser Mount wird mit zurückgehängt; Fehler nach dem `s_state`-Schreiben ⇒ `Reopen` vor Aufhebung der Sperre.
4. Rückkehr aus Schritt 8 ⇒ Schreiben erst nach `Reopen` wieder erlaubt (Fake zählt); `Reopen` scheitert ⇒ Root bleibt gesperrt, Lesen geht.
5. `RootVolumeState`: Offset 58, nur 2 Byte geändert, Flush nach jedem Schreiben, Neulesen nach `Close`.

**Gastprüfung `scripts/test-power-commands.ps1` erweitert** (heute: Boot, `reboot`, `shutdown`, optional GDB, **ohne** Datenlaufwerk). Alle schreibenden Fälle P1–P6 laufen nur auf **isolierten Image-Kopien** in einem neuen Arbeitsordner (AGENTS.md); das Skript bekommt dafür neue Platten-Parameter:
- P1 `Legacy` mit Datenplatte: `shutdown` hängt `/data` aus, `fsck.fat -n` des Images danach Exit 0 (Regression).
- P2 `Root` (Platte aus 7a-G1 oder Hostprobe `scripts/test-installer-layout.py`): `shutdown` ⇒ `dumpe2fs -h` „clean“, `e2fsck -fn` 0, `fsck.fat -n` der ESP 0.
- P3a `Root` `reboot` mit QEMU `-no-reboot` ⇒ QEMU endet, der Host liest die Superblockbytes am Rohimage (Offset 1024 + 58) = 1. Kein `dumpe2fs` auf einem laufenden Gast.
- P3b getrennter Neustart-Rundlauf: `reboot` ohne `-no-reboot` ⇒ Gast startet erneut im `Root`-Profil; danach `shutdown`, Host prüft „clean“.
- P4 Fehlerprobe (Testbuild `-p:ZehOSPowerFaultProbe=true` mit **Zählnummer** „n-ter Aufruf schlägt fehl“, damit jede Stufe einzeln trifft): Sync `/boot/efi`; Unmount mit bereits entferntem Mount; Root-Sync vor dem `s_state`-Schreiben; Root-Flush **nach** dem `s_state`-Schreiben; `Reopen`; Wiedereinhängen. Je Fall: `shutdown` bricht ab, Shell bedienbar, Zustand laut Tabelle §2 (insbesondere `s_state` = 0 am Image nach `shutdown` mit `-no-reboot` erzwungen hart beendet); zweites `shutdown` gelingt, wo die Tabelle es vorsieht.
- P5 offline `update install`, `reboot` ⇒ neue Version läuft, `.bak` = alte Version, Rückfalleintrag startet. **Voraussetzung:** ein gültig signiertes Manifest. Der öffentliche Schlüssel ist fest im Kernel (`ManifestSignature.cs:10`), der private liegt nicht im Repository. P5 läuft daher nur mit einem je Lauf erzeugten Testschlüssel über den Test-Key-Buildweg (nur Testbuild, nie Normalbuild, nie committet, §5.5); ohne ihn ist P5 offen, nicht bestanden.
- P6 laufende Paket-App ⇒ `shutdown` verweigert mit Meldung, nach Schließen gelingt es.
QEMU zuerst, VMware danach; Ansage, ein Gast.

## 5. Offene Punkte

1. **Leitung:** §0.4 Schritt 4 (laufende Paket-Apps verweigern die Energieaktion statt sie zu beenden) bestätigen.
2. `test-power-commands.ps1` braucht PowerShell; auf dem Linux-Testrechner fehlt `pwsh` (Etappe 1). Entweder `pwsh` dort installieren (Nachfrage beim Benutzer) oder P1–P6 als Python-Skript nach dem Muster der Storage-Skripte; Vorschlag: Python, die PS1 bleibt für Windows.
3. Ob ein offenes Handle nach `TryUnmount` beim nächsten Zugriff sauber scheitert oder hängt, ist ungeprüft; P6 und der Abbruch in Schritt 4 vermeiden den Fall, belegen ihn aber nicht.
4. *Geklärt (Quelle, Voraussetzung für §2 Schritt 5):* FAT-`Sync` ruft nur `Device.Flush()` (`FatSuperblockOperations.cs:13-23`, `FatSuperblock.cs:47-50`), Clusterschreibzugriffe und FAT-Einträge gehen direkt ans Gerät (FAT-Einträge sofort in alle Kopien, `FatTable.cs:436-440`, der Sektor-Cache gilt nur fürs Lesen); `Drop` flusht ebenfalls. Der Sync vor dem Aushängen genügt also, liefert aber immer `true` – Fehler kommen nur als Ausnahme aus `Device.Flush()` und werden in Schritt 5 gefangen und als Abbruch behandelt.
5. *Entschieden 15:14:* P5 nur mit eigenem Testschlüssel je Lauf (im Arbeitsordner erzeugt, über den vorhandenen Test-Key-Buildweg, nie im Normalbuild, nie committet); ohne diesen Build ist P5 **offen, nicht bestanden**. Hinweis zur Umsetzung: der vorhandene Weg (`-p:ZehOSPackageKeyPath`) gilt für Pakete; `ManifestSignature` hat den öffentlichen Schlüssel fest eingebaut (`ManifestSignature.cs:10`). Der Update-Pfad braucht deshalb dieselbe Mechanik für Update-Manifeste (nur Testbuild), eigener kleiner Arbeitsschritt mit Review.
