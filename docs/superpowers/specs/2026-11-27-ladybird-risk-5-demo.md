# Prüfung Demo-Ablauf Ladybird (4.12.2026), Stand origin/story/ladybird 3baf7cb0

Rein lesend. Keine Bauten, keine Gastläufe.

Geprüft: docs/superpowers/plans/2026-12-04-ladybird-demo-ablauf.md, scripts/ladybird-demo-run.sh,
scripts/linux-root-disk.py, scripts/test-linux-root-disk-guest.py, scripts/test-wasm-doom-desktop.py (start_qemu, Guest, free_port),
scripts/fetch-data-disk-file.py, experiments/Ladybird.Probe/pages/*, LADYBIRD_FEASIBILITY.md, Pläne 2026-11-26-ladybird-vorstellung(-folien).md.

## Lokale Prüfungen

- `bash -n`: ok.
- shellcheck: 1 Warnung SC2155 (Z. 38, `local s=$(date)` ohne Wirkung), 2 Infos SC2012 (Z. 18), SC2015 (Z. 86, harmlos).
- `--help` rc 0; `--page Bad` und `--page` ohne Wert rc 2 mit Meldung; `-b /nonexist --iso /nonexist` rc 1 "Buendel fehlt (-b)";
  `-o` ohne Wert: "unbound variable" rc 1 (unschön, aber sicher).
- `python3 scripts/check-doc-links.py`: alle relativen Verweise gültig.

## Was stimmt

- Optionen im Plan (`--iso -b -o --accel kvm --smp 1 --memory 2G --page static|zehos-projekt -s 30 -u '…canvas.html?frame=90'`)
  existieren alle und werden korrekt an test-linux-root-disk-guest.py durchgereicht (`--memory`, `--accel`, `--smp` via ZEHOS_QEMU_SMP, `--fetch lb/shot.png`).
- `--iso` setzt SKIPB=1: kein Cosmos-/Kernelbau, kein `rm -rf ProjektZeh/bin|obj|output-x64` (Z. 52 nur im Baupfad).
- Screenshotpfad konsistent: Hub `--screenshot-path /shot.png` in der Prozesswurzel lb = Platte `lb/shot.png`.
- Seiten vorhanden (static, zehos-projekt, canvas, PNGs, fonts/); canvas.html liest `?frame=` aus `location.search`; zehos-projekt lädt `zehos-desktop.png` relativ.
- Datenschutz: linux-root-disk.py verweigert vorhandene Ziele (Z. 463), Gastskript nutzt eine Kopie der Platte (Z. 55 f.); kein vorhandenes Image wird überschrieben.
  `rm -rf "$OUT/confd"` (Z. 71) nur im eigenen Ausgabeordner.
- Abbruch: Strg+C -> KeyboardInterrupt in Python -> `finally` tötet QEMU (test-linux-root-disk-guest.py Z. 121-124); kein verwaister Gast.
- Exit 0 nur bei LINUXRUN BEGIN, Status "exit 0" und geholtem nicht leerem PNG (Gastskript Z. 106-135, Runner Z. 89).

## Befunde

### P1
Keine.

### P2

1. **scripts/ladybird-demo-run.sh:42 + Plan Z. 6** – `pgrep qemu` bricht bei *jedem* laufenden QEMU auf dem Rechner ab. Der Plan spricht nur von
   "freien KVM-Slot absprechen" (also parallele Gäste anderer Agenten) und nennt `QEMU_OK=1` nicht. Auf diesem Rechner lief während der Prüfung
   ein fremder qemu-system-x86_64 (Netzprobe eines anderen Agenten): genau dieser Fall. Wirkung: Livestart scheitert sofort mit
   "ein qemu laeuft". Abhilfe: im Plan alle anderen Gäste auf dem Vorführrechner vorher beenden lassen oder `QEMU_OK=1` dokumentieren (QMP-Ports sind per flock getrennt, Parallelbetrieb technisch möglich; nur RAM 2G beachten).
2. **scripts/ladybird-demo-run.sh:35 + linux-root-disk.py:463 + test-linux-root-disk-guest.py:53 + Plan Z. 29-30/48-52** – Wiederholung mit
   demselben `-o` (naheliegend nach Fehlschlag live) scheitert sicher: `lb.qcow2` existiert -> "linux-root-disk.py" (und danach wäre `guest/` per
   `mkdir()` ohne exist_ok ein Fehler). Zusätzlich leert Z. 35 vorher `run.log` und `times.txt` des ersten Laufs – genau die Diagnose, die der
   Rückfall (Plan Z. 64 f.) aufheben will. Abhilfe: im Plan "Wiederholung nur mit neuem -o (z. B. "$out/static2")" oder im Skript bei vorhandenem `$OUT/lb.qcow2`/`$OUT/guest` vor dem Leeren abbrechen bzw. Zeitstempel-Unterordner.
3. **Plan Z. 29-31, 59-62 / Skript Z. 20 (TIMEOUT=600)** – Ausfallplan bei hängendem WebContent/Hub: Der Plan sagt "nach drei Minuten auf PNGs wechseln",
   aber der Lauf wartet bis 600 s auf LINUXRUN END (+ bis 240 s Desktop, 300 s Anmeldung, 60 s Herunterfahren). Weder `--timeout` (z. B. 150) noch
   "Strg+C im Runner" steht im Plan. Läuft der alte Lauf in einem anderen Terminal weiter, blockiert er über P2-1 auch den nächsten Livestart.
   Ein Absturz von WebContent selbst ist abgedeckt (Hub endet mit Status != exit 0 -> "NICHT OK"), ein Hängen nicht.

### P3

1. Skript Z. 14/7/15 vs. Z. 20/24: Hilfetext "--memory: Gastspeicher statt 512M", Beispiel `--memory 1G`; Standard ist seit 3baf7cb0 2G. `--help`
   (`sed -n 2,14p`) lässt Z. 15 (`--accel tcg`) weg.
2. Skript Z. 35/45: `-o`, `--iso`, `-b`, `--disk` relativ angegeben werden nach `cd "$R"` falsch aufgelöst (mkdir im Aufrufordner, Rest im Repo). Plan nutzt absolute Pfade, also nur außerhalb des Plans relevant.
3. Skript Z. 31/77-78: `--page` prüft nur das Namensmuster, nicht ob `pages/<name>.html` existiert. Bei Tippfehler oder anderem Arbeitsbaum zeigt Hub
   vermutlich eine Fehlerseite und endet mit exit 0 -> "OK" mit falschem Bild (falsch positiv). Plan verlangt nur eine Sichtprüfung des Ergebnisses für static.
4. Kein inhaltlicher Bildvergleich: weißes/leeres PNG oder Fehlerseite mit exit 0 ergibt "OK" (Skript Z. 89). Der Plan fängt das nur per Sichtprüfung ab (Z. 61); ein Größencheck (798x4087 für static) oder Vergleich mit `$static_ref` wäre billig.
5. test-wasm-doom-desktop.py: Ohne Zugriff auf /dev/kvm (Gruppe kvm) oder ohne KVM endet QEMU sofort, das Gastskript wartet aber 240 s auf
   "Desktop ist bereit" (prüft `process.poll()` nicht) – vier Minuten bis zur Fehlermeldung. Skript prüft /dev/kvm nicht vorab.
6. fetch-data-disk-file.py Z. 34-40 verlangt `qemu-img`, `mcopy`, `mdir` im PATH, während die Plattenerzeugung auch den Cosmos-qemu-img unter `~/.cosmos/tools/qemu` findet:
   auf einem Rechner nur mit Cosmos-QEMU scheitert erst das Holen nach dem kompletten Gastlauf. Kein Vorabcheck auf mtools.
7. Plattenplatz: je Lauf lb.qcow2 plus Kopie guest/disk.qcow2 (Bündel + 256 MiB frei) in `$out` unter `${TMPDIR:-/tmp}` (Plan Z. 12) und das Roh-Image in TemporaryDirectory; bei drei Läufen
   und tmpfs-/tmp auf dem Vorführrechner RAM/Platz prüfen.
8. Plan Z. 9-17: Vorbereitungsblock ohne `set -e`; `test -s …` scheitert still (nur Rückgabewert). Ein `|| echo FEHLT` o. ä. macht fehlendes ISO/Bündel/Referenz sichtbar.
9. test-linux-root-disk-guest.py Z. 120: hängt das Herunterfahren (z. B. Aushängen scheitert), führt TimeoutExpired zum Abbruch vor `--fetch` – ein bereits geschriebenes PNG wird nicht geholt (falsch negativ). Bisher nicht beobachtet.
10. Anzeige: QEMU läuft mit `-display none`; das Publikum sieht während ~70 s Gastlauf nichts außer Runnerzeilen. Plan sagt nicht, was währenddessen gezeigt wird (z. B. `tail -f $out/static/guest/serial.log` oder Folie).
11. Konsistenz: Folien (2026-11-26-ladybird-vorstellung-folien.md Z. 31) "Ladezeit im Gast 5–8 s" vs. Demo-Plan Gastlauf 67 s (inkl. Boot/Anmeldung) – kein Widerspruch, aber im Vortrag auseinanderhalten. Folien Z. 32 "mit 512 MB hält der Kernel an" passt zum neuen Standard 2G. LADYBIRD_FEASIBILITY.md enthält keine widersprechenden Demo-Angaben.
12. shellcheck SC2155 Z. 38 (kosmetisch).

## Nicht prüfbar hier

- Bündel `ladybird-static-with-images.tar.gz`, ZIP, ISO und `$static_ref` liegen nur auf dem Vorführrechner (auf dem Prüfrechner fehlt das Bündel): ob das Bündel
  `./fonts.conf` mit den erwarteten relativen `<dir>`-Einträgen und `./conf.d/*.conf` enthält (Skript Z. 65-75 bricht sonst ab), `bin/Hub` und `ImageDecoder`, sowie die SHA-256 des ZIP.
- Ob Hub bei fehlender Datei exit 0 liefert (P3-3) ist Vermutung.
