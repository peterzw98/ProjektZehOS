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

Texte, Seitencode und die beiden PNG-Illustrationen wurden für ZehOS erstellt
und stehen unter der [Projektlizenz GNU GPL v3](../../../LICENSE). Für die
mitgelieferten DejaVu-Schriften gilt die [DejaVu-Lizenz](fonts/LICENSE-DejaVu.txt).
