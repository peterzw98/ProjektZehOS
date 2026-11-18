# Herkunft von DefaultFont.psf

Standardschrift (PSF2, 16 x 32 Pixel, 256 Glyphen) für die Referenzbilder der Hosttests (`ScreenshotSupport.LoadCosmosFontData`, `Golden.cs`).

| Angabe | Wert |
| --- | --- |
| Quelle | Cosmos-Quellbaum, Branch `cosmos` dieses Repositorys, Commit `bd5889b46a6b1175ff8b66620437e072b78636de` |
| Pfad dort | `src/Cosmos.Kernel.System/Graphics/Fonts/DefaultFont.psf` |
| Größe | 17403 Byte |
| SHA-256 | `f588cdf02336ee3f561ab5cfc6a5615422030fbd7bb493a30add4a74377e1207` |
| Gleichheit | Byteweise gleich der Ressource, die in `Cosmos.Kernel.System` 3.0.97 (nuget.org) und 3.0.97-zehos.1 (Eigenbau) eingebettet ist; die bisherigen Referenzbilder bleiben deshalb pixelgleich |
| Lizenz | BSD-3-Clause des Cosmos-Repositorys, Text in `COSMOS-LICENSE.txt` |

**Herkunft der Schrift in Cosmos nicht dokumentiert.** Im Cosmos-Baum steht zu `DefaultFont.psf` keine eigene Urheber- oder Lizenzangabe, nur die Lizenz des Repositorys. Die Datei dient ausschließlich den Hosttests und gelangt nicht in den Kernel oder in ein Release.
Ändert sich die Schrift, ändern sich alle Referenzbilder (`ProjektZeh.Tests/Golden/*.sha256`); dann Quelle und Prüfsumme hier nachtragen.
