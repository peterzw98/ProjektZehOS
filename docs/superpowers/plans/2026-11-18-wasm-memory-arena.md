# Native Wasm-Laufarena und Budget

**Ziel:** Hosttestbarer nativer Baustein fuer I2, ohne Aktivierung im Worker.
**Grundlage:** [Isolation 12.4](../specs/2026-11-18-isolation-12-4.md), Abschnitt 6.5.
**Aufteilung:** Arena/Budget hier; Worker-Kill und Laufzeitintegration separat. Koordinator-Zuweisung vom 18. November 2026.

- [x] Bestehenden Allokator und Aufrufer lesen; 12 vorhandene libc-Hosttests als Ausgangsstand ausfuehren.
- [x] Fehlendes Budget-API mit einem zuerst ausgefuehrten Test belegen.
- [x] Blockliste, Besitzerbindung, Limit, Belegung und Spitzenwert im vorhandenen `libc.c` ergaenzen.
- [x] Tests fuer Fremdthreads, Ablehnung, Freigabe, Wiederholung und die Kopierspitze pruefen.
- [x] Native Python-Suite und .NET-Hostsuite ausfuehren; Release bauen und normalen QEMU-Boot pruefen.
- [x] Unabhaengiges Review und Versionsabgleich.

Abschluss: geprueften Stand pushen und als eigenen PR vorlegen; keine I2-Gastabnahme behaupten.

Pruefstand: 23 libc-Tests; gesamte Python-Suite 218 Tests mit 6 erwarteten Ueberspringungen; .NET-Hostsuite 1394 bestanden,
7 plattformbedingt uebersprungen, keine Fehler. Nativer Release-Build und normaler WHPX-Desktopablauf bis shutdown bestanden.
VMware ohne Datenplatte erreichte ebenfalls den Desktop; keine vollstaendige VMware-Bedienpruefung.

Besonders pruefen: Fremde Bloecke bleiben erhalten; ein zweiter `begin` zerstoert keinen Lauf; fehlgeschlagenes `realloc`
erhaelt den alten Inhalt; ein Kill darf keine Listenmutation unterbrechen; `end` setzt einen sicher ruhenden Worker voraus.
