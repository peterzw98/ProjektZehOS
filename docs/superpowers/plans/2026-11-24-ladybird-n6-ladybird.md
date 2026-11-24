# Ladybird N6: Ladybird (Umriss)

> Umriss. Geschnitten nach den Toren N3-Z und N4-Z. Grundlage: [L2](../../LADYBIRD_FEASIBILITY.md#ergebnis-l2), N1-C, N1-D.

**Ziel:** Ladybird als Paket mit eigenem Fenster, ohne JIT, GPU, WebGL und Medien.

| Schritt | Inhalt | Nachweis |
| --- | --- | --- |
| 1 | Bauvorschriften für die Abhängigkeiten (ICU, OpenSSL, SQLite, harfbuzz, Skia, …) und Ladybird; Lizenztexte und Quellen | baut gegen den sysroot |
| 2 | `js` | `1+1`, Schleife, Array-Sort; gleiche Ausgabe wie Linux |
| 3 | Headless-Bild der drei L1-Seiten, Satz S4 | Bild wie auf Linux (höchstens 1 Abweichung je Farbkanal) |
| 4 | Frontend `UI/ZehOS` auf `Hub::ApplicationHost` mit `libzehos` | Seite im Fenster, Tastatur, Maus, Größe |
| 5 | Netz, TLS, Schriften, Absturz eines Dienstes | Hub startet den Compositor neu, Desktop bleibt bedienbar |
| 6 | Speicher, Startzeit, Dauerlauf, Paketgröße | Tor N6-Z |

Nach dem ersten Bild wird ein Rhythmus für neue Ladybird-Stände festgelegt. NetSurf bleibt Standardbrowser, bis Peter oder Lars anders entscheiden. **Abnahme:** Tor N6-Z.
