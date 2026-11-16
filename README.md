# ZehOS-Paketrepository

Signierte Pakete fuer `pkg` (Format: docs/PACKAGE_FORMAT.md auf master). Dieser Branch hat keine gemeinsame Historie mit master und wird vom Paketserver (deploy/package-repo) automatisch ausgeliefert.

Doom: doomgeneric (GPL-2.0-or-later) mit ZehOS-Plattformschicht, Quellen: experiments/Wasm.DoomProbe auf master und https://github.com/ozkl/doomgeneric/tree/e257a09527062bfb986b6e5e0780839b6cbd5b71. Spieldaten: Freedoom 0.13.0 (BSD), siehe COPYING-freedoom.txt. Doom 1.1.0 dreht mit der Maus (zehos_v1.pointer_capture) und braucht ZehOS 0.3.0 oder neuer.

NetSurf: Browser NetSurf 3.11 (GPL-2.0) mit HTTP/HTTPS ueber den ZehOS-Kernel, Quellen und Bauanleitung: experiments/NetSurf.Wasi auf master (Commit im Paket unter licenses/SOURCES.txt). Die Lizenztexte aller Bestandteile liegen im Paket unter licenses/. Gebaut von der CI ("Build and tests", Artefakt NetSurf-offline-files), nicht lokal. Braucht ZehOS 0.3.0 oder neuer und das Netzwerkrecht (capability=net).

Pakete mit min-kernel im Manifest werden von Kerneln vor 0.3.0 als ungueltig abgelehnt; dort bleibt nur Doom 1.0.0 nutzbar, sofern es schon installiert ist.
