# NetSurf als Offline-Paket

Stand: 15. November 2026. NetSurf 3.11 läuft als separates `wasm32-wasip1`-Paket im bestehenden wasm3-Loader.
HTML, CSS, Links, Zurück, Scrollen und Formulareingabe sind im ZehOS-Gast unter QEMU geprüft.
GPL-Code bleibt im Paket. Der Kernel ergänzt allgemeine Text-/Zeigereingabe und lesende Dateistatusabfragen.
NetSurf ist noch nicht im öffentlichen Paketkatalog veröffentlicht.

## Bauen und installieren

Die Linux-Bauanleitung steht in [experiments/NetSurf.Wasi/README.md](../experiments/NetSurf.Wasi/README.md).
`python3 experiments/NetSurf.Wasi/build.py` lädt feste Quellen/Toolchain-Versionen mit SHA-256-Prüfung,
wendet die Portpatches auf einen zum Patchsatz gehörenden Quellbaum an und liefert
`experiments/NetSurf.Wasi/bin/package/`. Der CI-Job „NetSurf offline package“ baut denselben Stand und
liefert diese Dateien als **unsigniertes** Artefakt `NetSurf-offline-files`.

Diese Dateien werden mit [build-package.py](../scripts/build-package.py) zu einem signierten Paket
`netsurf` mit Einstieg `netsurf.wasm` zusammengefasst. Alle Ressourcen, Testseiten und Lizenztexte müssen
in das Manifest. Für lokale Tests ausschließlich einen eigenen Testschlüssel verwenden und dessen öffentlichen
Teil beim Kernelbuild mit `-p:ZehOSPackageKeyPath=<public.b64>` einbetten. Normale Builds behalten den
bestehenden Herausgeberschlüssel. Schlüssel nie committen. Veröffentlichung bleibt ein gesonderter Auftrag.

Nach dem Kopieren des signierten Paketordners auf eine isolierte Testplatte als Administrator:

```text
pkg install-local /data/pkgs/netsurf/1.0.0
```

Danach im Programmmenü „NetSurf“ starten. Die Startseite `file:///pages/index.html` ist im Paket enthalten.
Der Zurückpfeil oben links führt zurück; Mausrad, Pfeile und Bild auf/ab scrollen. Die Adressleiste nimmt
lokale `file:///pages/...`-Adressen an. Das X des ZehOS-Fensters beendet den Lauf. F4/Esc erhalten den Wechsel
zwischen Desktop und Textshell; Fokusverlust setzt Eingaben zurück und bricht einen begonnenen Klick ab.

## Wiederholbare Gastprüfung

```powershell
python scripts/test-netsurf-desktop.py --iso <Test-ISO> --package <unsignierte-Paketdateien> --key <Testschlüssel-private.pem>
```

Das ISO muss zum öffentlichen Gegenstück des Testschlüssels passen. Der Test erstellt selbst ein signiertes
lokales Paket und eine frische beschreibbare QEMU-Platte; bestehende Datenimages werden nicht verwendet.
Er prüft Installation, Start, Markerfarben für HTML/CSS und Linknavigation, Zurück, Fokusverlust ohne Klick,
Mausrad/Bild ab, Weiterbedienung nach einem kaputten Link und drei Starts mit Schließen.
Bildschirmfotos dokumentieren zusätzlich „Grüße @“ im Formular und die sichtbare Fehlerseite „Not found“;
diese beiden Inhalte werden visuell geprüft. Drei Speicherwerte nach dem Aufräumen dürfen um höchstens
8 MiB sinken. Nachweise bleiben unter `.tools/netsurf-guest/` und werden nicht eingecheckt.

## Bisheriger Nachweis

- 445 Hosttests mit Warnungen als Fehler bestanden; native Release- und Recovery-Builds ohne Warnungen.
- Acht native Tests verwenden die echte C-Brücke mit wasm3: Ereignislayout, unaligned Pointer,
  Speicherende/Überlauf, lesende Dateiabfragen und Paketpfadgrenzen. Zwei Tests sichern wiederholte Builds
  mit neuen bzw. zuvor fehlgeschlagenen Patches ab. Der Browserbuild führt einen nativen Eingabetest aus.
- Linux-CI-Lauf [43743357576](https://github.com/peterzw98/ProjektZehOS/actions/runs/43743357576):
  Hosttests, Skripttests, Kernelbuild und frischer Browserbuild bestanden.
- QEMU TCG, vollständiger Lauf mit korrigiertem Frontend: bestanden. Freier Speicher nach drei Läufen
  444912, 445232, 445488 KiB. Formulartext und Fehlerseite anhand der Bilder geprüft.
- Eine zusätzliche kleine Paketprobe hat Tastendruck/-freigabe, Text, alle drei Maustasten, Rad in beide
  Richtungen, Fokusreset, Koordinaten, Loslassen außerhalb sowie Neustart/Schließen im Gast geprüft.

Die letzten Lizenz-/Quellenbeigaben und der überlaufsichere Radbetrag sind nach diesem Gastnachweis ergänzt;
der endgültige Paketbuild und die erneute Gastabnahme werden vor dem PR nachgetragen.

## Grenzen

- Kein HTTP/HTTPS und kein JavaScript. GIF/BMP sind aktiviert; PNG/JPEG/SVG sind ausgeschaltet.
- Festes Browserbild 632×392, kein Resize-Import. Das ZehOS-Fenster zentriert bzw. skaliert es.
- Formulare unterstützen Unicode; die Adressleiste nur ASCII. Strg-Kürzel orientieren sich an der
  physischen US-Tastenposition. Ziffernblock und Navigation haben teilweise dieselben normalisierten Codes.
- Dateien nur lesend im eigenen Paket; keine persistenten Profile, Downloads oder Verzeichnislisten.
- Eine Paket-App zur Zeit; NetSurf muss den gemeinsamen Worker nach dem Schließen freigeben.
- Der finale Recovery-Kernel hat in QEMU ohne Datenplatte bis zur Textshell gebootet.
- VMware-Browserbedienung und echte Hardware sind noch nicht geprüft. Ein QEMU-Ergebnis belegt sie nicht.
- Unter Windows scheitern fünf bestehende Skript-Testfälle an direktem Shellskriptstart bzw. fehlendem
  Symlinkrecht. Dieselben unveränderten Tests bestehen in Linux-CI.

Lizenztexte und `licenses/SOURCES.txt` werden mit dem Paket ausgeliefert; letzteres nennt Archive,
Prüfsummen, Rezept-Commit und die Quellen der statisch gelinkten WASI-Laufzeit.
