# NetSurf als ZehOS-Paket

## Auftrag und Wahl

NWeh hat nach dem Vergleich mit Firefox NetSurf gewaehlt. Die Umsetzung erfolgt abgestimmt mit der Gegenseite. Zwischen-PRs liefern zusammenhaengende, benutzbare Ablaeufe; Bibliotheken, Loader und Benutzeroberflaeche werden nicht in getrennte Komponenten-PRs zerlegt.

Ein eigener kleiner HTML-Browser wuerde weniger Fremdcode portieren, aber Parser und Layout erneut entwickeln. Firefox-Wasm braucht nach aktuellem Bericht eine andere Runtime, SIMD, geteilten Speicher und viele Threads; ein erstes Bild ist nicht nachgewiesen. NetSurf 3.11 hat bereits eine Framebuffer-Oberflaeche und modulare C-Bibliotheken. Seine WASI-Portierbarkeit ist noch nachzuweisen. Die Wahl bedeutet keine Zusicherung fuer moderne Webseiten.

## Erster Lieferstand: lokaler Browser

Ein externes WASIp1-Modul mit `_start` nutzt den bestehenden signierten Paketbestand, wasm3 und das vorhandene App-Fenster. GPL-Code kommt ausschliesslich ins Paket, mit Lizenzen und oeffentlich nachvollziehbaren Portquellen. Keine appbezogenen Kernel-Buildflags und keine Paketschluessel im Repository. Quellen und Build werden auf feste Versionen/Hashes gebunden.

Nach Installation startet NetSurf aus dem Programmmenue, zeigt eine lokale HTML-/CSS-Testseite und erlaubt Scrollen, Linknavigation und Rueckkehr. Maus und Texteingabe bedienen die Browseroberflaeche. Schliessen, Fokusverlust und F4/Esc halten den Desktop bedienbar. Fehlende/beschaedigte Dateien fuehren zu sichtbaren Fehlern. Dateien bleiben auf das eigene Paket beschraenkt; Profile sind vorerst nicht persistent. Paketdateien, Modul, offene Dateien und Memory bleiben innerhalb der vorhandenen Grenzen, soweit der gemessene Bedarf keinen begruendeten eigenen Entscheid erfordert.

### Allgemeine Eingabeanbindung

Ergaenzend zu `poll_key()` erhaelt `zehos_v1` den Import `poll_event(ptr) -> i32`. `ptr` bezeichnet 16 Byte fuer vier little-endian i32: `type, code, x, y`. Rueckgabe: 1 vorhanden, 0 leer, -1 Hostfehler. Ein ungueltiger Gastbereich trappt vor Entnahme des Ereignisses. Typen: 1 Taste gedrueckt, 2 losgelassen (normalisierter ZehOS-Tastencode, kein E0-Merkmal); 3 Text (Unicode-Codepunkt); 4 Zeigerbewegung; 5/6 Maustaste gedrueckt/losgelassen (1 links, 2 rechts, 4 Mitte); 7 Rad (code = Delta); 8 Eingabereset. x/y sind Pixel im dargestellten App-Frame, kein Kernelzeiger.

Eine begrenzte Ereigniswarteschlange setzt bei Ueberlauf den Eingabezustand zurueck. Aufeinanderfolgende Bewegungen duerfen zusammengefasst werden; Text-/Tastenreihenfolge bleibt erhalten. Fokusverlust/Reset verwirft ausstehende neue Ereignisse und liefert Reset. Die bestehende Doom-Tastenschnittstelle und ihr Verhalten bleiben erhalten. Skalierung und Zentrierung des Framebildes werden fuer Mauskoordinaten rueckgerechnet; Klicks in schwarze Randbereiche werden nicht als Frameklicks gemeldet, Loslassen bleibt zur Beendigung eines Ziehens erreichbar.

Unbenutzte Felder sind null. Raddelta sind ZehOS-Schritte, positiv aufwaerts und negativ abwaerts. Wiederholungen nicht druckbarer Tasten kommen erneut als Typ 1; Texteingabe folgt als eigene Typ-3-Ereignisse. UTF-16-Surrogatpaare werden zu Unicode-Codepunkten zusammengesetzt, ungueltige Folgen zu U+FFFD; Reset verwirft ein angefangenes Paar. `poll_event` und `poll_key` konsumieren unabhaengige Puffer. NetSurf verwendet nur die neue Schnittstelle. Der Fenstermanager haelt Capture fuer alle gedrueckten Maustasten bis zum letzten Loslassen oder Reset.

PR 1 verwendet einen festen Frame von 632x392 Pixeln, der auch in das minimale Paketfenster (638x399 Inhalt) passt. Es gibt zunaechst keinen Resize-Import. Das Frontend prueft `should_exit()` in seiner Ereignisschleife, wartet ueber `sleep_ms` hoechstens 16 ms zwischen Pruefungen und beendet sich mit vollstaendigem Cleanup. `poll_oneoff` und WASI-Threads sind nicht verfuegbar. Ressourcen-/Fontpfade sind fest: `fd_readdir` und `path_filestat_get` fehlen derzeit; ihr tatsaechlicher Bedarf wird im ersten Browserlauf gemessen, bevor der Loader erweitert wird.

## Zweiter Lieferstand: Online-Browsing

Nach bestandenem lokalen Lieferstand folgt ein zweiter zusammenhaengender PR: HTTP/HTTPS-Seiten mit relativen Links, begrenzten Weiterleitungen und Datenmengen, Abbruch, Zertifikatspruefung und sichtbaren Fehlern. Vorhandenen Cosmos-/BouncyCastle-Transport wiederverwenden; Netzwerkzugriffe laufen ausserhalb des Desktops. Ein generischer Fetch-Hostdienst ist vor einem weiteren TLS-Stack in der App zu bewerten. Keine ungeprueften Zertifikate oder Kernelobjekte fuer Apps. Der genaue Vertrag entsteht aus dem ersten Lieferstand und den vorhandenen Netzwerkdiensten.

## Arbeitsteilung und Abnahme

- `agent-laser-netsurf`: `feature/netsurf-build`, ausschliesslich `experiments/NetSurf.Wasi/` fuer Build, Frontend, Testseiten und Lizenznachweise.
- Hauptlauf: `feature/netsurf`, Hosteingabe, Paket-/Gastintegration, Dokumentation und Zusammenfuehrung vor jedem gemeinsamen PR.
- Gezielte Hosttests fuer Eingabereihenfolge, Ueberlauf, Unicode, Reset und Koordinaten; native Grenzfallpruefung fuer Gastpointer.
- Tatsaechlicher Wasm-Lauf und erstes Browserbild, danach QEMU mit isolierter Datenplatte; Normal-/Recovery-Build und Doom-Regression. VMware verfuegbar nachpruefen; offene Plattformen explizit nennen.
- Vor PR unabhaengiges lesendes Review, betroffene Fehler beheben und Pruefungen wiederholen. Kein Merge oder Veroeffentlichen eines Pakets ohne ausdruecklichen Auftrag.
