# Ladybird-Demoseiten

Die Seiten lassen sich lokal aus diesem Verzeichnis öffnen. Bilder und Webfonts
werden über relative Pfade geladen.

| Seite | Prüft |
| --- | --- |
| [static.html](static.html) | HTML und Layout einer längeren Textseite |
| [css.html](css.html) | CSS-Layout, Verläufe, Sticky-Positionierung und lokale Webfonts |
| [js.html](js.html) | JavaScript-Berechnung und Ausgabe in den DOM |
| [img.html](img.html) | PNG-Dekodierung mit [probe.png](probe.png) |
| [zehos-projekt.html](zehos-projekt.html) | Navigation mit Flexbox, Grid und [Desktop-Illustration](zehos-desktop.png) |
| [canvas.html](canvas.html) | Canvas-Zeichnung und JavaScript-Animation. `?frame=90` öffnet ein reproduzierbares Standbild. |

Der [Demo-Runner](../../../scripts/ladybird-demo-run.sh) kopiert die Seiten mit
ihren Bildern und Schriften beim Plattenbau in den Gast. `--page zehos-projekt`
wählt die Projektseite aus. `-u 'file:///share/zehos-probe/canvas.html?frame=90'`
wählt das Canvas-Standbild aus. Für `img.html` und `zehos-projekt.html` muss das
Ladybird-Bündel den Dienst `ImageDecoder` enthalten.

Texte, Seitencode, Desktop-Illustration und Testverlauf wurden für ZehOS
erstellt und stehen unter der [Projektlizenz GNU GPL v3](../../../LICENSE).
Für die mitgelieferten DejaVu-Schriften gilt die [DejaVu-Lizenz](fonts/LICENSE-DejaVu.txt).

`probe.png` wurde im [Erstellungscommit 89d389776](https://github.com/peterzw98/ProjektZehOS/commit/89d38977658e453e8ca97d5b8d8d9eee42a3b645)
als selbst erzeugter Verlauf aufgenommen. Das folgende Python-Skript erzeugt
eine bytegleiche Kopie zum Vergleich.

```python
import struct
import zlib
from pathlib import Path

def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

rows = bytearray()
for y in range(64):
    rows.append(0)  # Jede Zeile wird ungefiltert gespeichert.
    for x in range(96):
        rows.extend((255 * x // 95, 255 * y // 63, 128))

header = struct.pack(">IIBBBBB", 96, 64, 8, 2, 0, 0, 0)
png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header)
png += chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")
Path("probe-vergleich.png").write_bytes(png)
```
