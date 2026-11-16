# Doom als WASIp1-Modul (Probe für Gate B)

`platform/doomgeneric_zehos.c` (GPL-2.0-or-later, wie doomgeneric) verbindet [doomgeneric](https://github.com/ozkl/doomgeneric/tree/e257a09527062bfb986b6e5e0780839b6cbd5b71) mit den `zehos_v1`-Imports des ZehOS-Wasm-Loaders: `present`, `present_indexed`, `ticks_ms`, `sleep_ms`, `poll_key`, `poll_event`, `pointer_capture`, `should_exit`. Der erste Mausklick fängt den Zeiger ein; danach dreht die Maus die Ansicht (Typ 9 als `ev_mouse`, Faktor 4, keine Vor-/Rückbewegung), die Tasten feuern (links), strafen (rechts) und laufen (Mitte). Esc oder Fokusverlust geben ihn frei. Diese Fassung braucht ZehOS ab 0.2.22. Die WAD liest Doom über die nur lesenden WASI-Dateifunktionen aus dem freigegebenen App-Ordner. Weder doomgeneric-Quellen noch WAD-Dateien liegen im Repository.

```powershell
git clone https://github.com/ozkl/doomgeneric.git <Ordner>
git -C <Ordner> checkout --detach e257a09527062bfb986b6e5e0780839b6cbd5b71
python experiments/Wasm.DoomProbe/build.py --source <Ordner>
```

Ergebnis: `experiments/Wasm.DoomProbe/bin/doom.wasm` (rund 720 KB, 320×200, 8-Bit-Bild mit Palette über `present_indexed`, ohne Ton). Spieldaten: Freedoom 0.13.0 (`freedoom-0.13.0.zip`, SHA-256 `3f9b264f3e3ce503b4fb7f6bdcb1f419d93c7b546f4df3e874dd878db9688f59` laut veröffentlichter CHECKSUM-Datei), BSD-Lizenz.

Als installierbares Paket (Format: [docs/PACKAGE_FORMAT.md](../../docs/PACKAGE_FORMAT.md)) mit den Lizenztexten beider Teile:

```powershell
python scripts/build-package.py package --key <privater-Schlüssel.pem> --repo <repo> --id doom --version 1.0.0 --name Doom --entry doom.wasm experiments/Wasm.DoomProbe/bin/doom.wasm=doom.wasm <freedoom>/freedoom1.wad=freedoom1.wad <doomgeneric>/LICENSE=LICENSE-doomgeneric.txt <freedoom>/COPYING.txt=COPYING-freedoom.txt
python scripts/build-package.py catalog --key <privater-Schlüssel.pem> --repo <repo> --generation 1
```

Das Paket enthält ein GPL-2.0-or-later-Binary. Wer es veröffentlicht, muss die Quellen bereitstellen: doomgeneric am genannten Commit, `platform/` und `build.py`.

Tastenbelegung (PS/2-Set-1 → Doom): Pfeile bzw. W/S laufen, Pfeile links/rechts drehen, A/D seitwärts, Strg schießen, Leertaste benutzen, Shift rennen, Ziffern Waffen, Enter, Tab Karte. **Esc** gibt bei eingefangener Maus den Zeiger frei und öffnet das Doom-Menü; ein weiteres Esc führt zur Textshell. **Backspace** öffnet das Doom-Menü auch bei freiem Zeiger.

`I_Quit` und `I_Error` werden per Linker (`--wrap`) in der Plattformschicht ersetzt: Die Originale rufen eine per Cast registrierte Exitfunktion auf, die Wasm als Typfehler abbricht, und `I_Quit` beendet in doomgeneric nicht.

Messwerte und Grenzen: [docs/WASM_FEASIBILITY.md](../../docs/WASM_FEASIBILITY.md).
