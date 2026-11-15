# Deutsche Eingabe und Scrollen

Bestätigter Umfang: deutsches QWERTZ, Trackpad in QEMU, Scrollrad in GUI-Terminal, GUI-Steuerelementen und Textshell ohne Desktop. Keine direkte Laptop-Hardwareanbindung und kein serielles Fernterminal.

## Ansatz

Die vorhandene Cosmos-DEStandardLayout-Belegung wird wiederverwendet. ZehOS korrigiert die in 3.0.94 falschen ISO-Tasten und Großschreibung und reicht AltGr als Ctrl+Alt an die vorhandene Übersetzung weiter. Keine Paket- oder Werkzeugupdates.

QEMU stellt Hostmaus und Trackpad bereits über das USB-Tablet bereit. Dessen vorzeichenbehaftetes Scrollbyte wird an MouseManager gemeldet; Poll liest und leert ScrollDelta unter derselben Interruptmaske wie Position und Tasten. Ein PointerScroll-Ereignis mit Position gelangt zum Inhalt unter dem Zeiger, ohne Tastaturfokus zu wechseln. Drei Zeilen pro Raster; positive Werte blättern zu älterer Ausgabe.

Das GUI-Terminal verwendet seinen bestehenden Ausgabepuffer. Listen und Textfelder ergänzen denselben Scrollpfad. Die Textshell verwendet ebenfalls TerminalBuffer (256 Zeilen) statt einer separaten Pufferimplementierung; Status und Eingabe bleiben beim Blättern sichtbar, Texteingabe kehrt zur neuesten Ausgabe zurück. Kennwörter bleiben maskiert, Benutzerwechsel leert den Textshell-Verlauf.

## Alternativen und Grenzen

Eine eigene deutsche Vollbelegung oder eigene Trackpadtreiber würden vorhandene Cosmos- bzw. QEMU-Funktionen duplizieren. Nur PageUp/PageDown zu simulieren würde dagegen Ereignisposition und feinere Scrollschritte verlieren. Der gewählte gemeinsame Ereignispfad erhält Tastatursteuerung und Recovery.

QEMU kann die Scrollereignisse nur liefern, wenn der Host sie an die VM weiterreicht. Automatisierte Gasttests prüfen synthetische Radereignisse; ein tatsächlicher Zwei-Finger-Test auf dem Host bleibt gesondert auszuweisen. VMware ist separat zu prüfen.
