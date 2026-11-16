# ClassiCube: Lizenz, Texturen und Nachladen beim Start

Recherche für Etappe 2 des [ClassiCube-Plans](../plans/2026-11-16-classicube.md), Stand 16. November 2026. Nur gelesen, nichts gebaut oder ausgeführt.

Quellstand: `ClassiCube/ClassiCube` Commit `2b01bee867fe81331d0a898e7ad40a71882131de` (15. November 2026). Dateiverweise beziehen sich auf diesen Commit. Vor dem Port den dann verwendeten Commit erneut gegen diese Notiz prüfen.

## Ergebnis in Kürze

- Der ClassiCube-Code steht unter BSD-3-Clause und darf als Wasm-Paket weitergegeben werden. Bei Weitergabe als Binärdatei gehören `license.txt` und die Lizenztexte der tatsächlich mitgebauten Drittquellen ins Paket.
- Die Standardtexturen sind **nicht** verwendbar. ClassiCube erzeugt `texpacks/default.zip` beim ersten Start aus Mojang-Material (Minecraft-Client-JARs) oder lädt es von `static.classicube.net`. Auch `misc/cc_textures.zip` im Repository hat keine belegte freie Lizenz.
- Ohne `CC_BUILD_NETWORKING` sind alle HTTP-Abrufe Stubs. Mit dem Startargument `--singleplayer` läuft der Launcher nicht. Damit entfallen Texturdownload, Skins, Update-Prüfung und classicube.net-Anmeldung.
- Ein freies Texturpaket muss für ZehOS zusammengestellt werden. Kandidat sind die Texturen des Minetest-Game-Mods `default` (CC BY-SA 3.0), ergänzt um eigene Grafiken.

## Lizenz des Codes

`license.txt`: „Copyright (c) 2014 - 2024, UnknownShadow200“, BSD 3-Clause (Quelltext- und Binärweitergabe mit Hinweis, kein Werben mit dem Namen). Danach folgen in derselben Datei:

| Bestandteil | Lizenz laut `license.txt` | Für ZehOS relevant |
| --- | --- | --- |
| Ray-Box-Schnitt (Williams u. a. 2005) | Quelle ohne angegebene Lizenz, nur Algorithmus nachempfunden | im Spielcode; unverändert hinnehmen, im Paket mit `license.txt` nennen |
| Voxel-Traversierung (xnawiki) | laut archivierter Seite gemeinfrei | im Spielcode |
| Frustum-Culling (Mark Morley) | „use … in any way you want“ | im Spielcode |
| OpenTK, Mono-Anteile | MIT | nur Windows/X11-Fensterschicht, nicht für WASI |
| Emscripten | MIT bzw. University of Illinois/NCSA | nur `src/webclient`, nicht für WASI |
| BearSSL | MIT | nur mit Netz/TLS (`third_party/bearssl`); ohne `CC_BUILD_NETWORKING` nicht mitbauen |
| FreeType | FreeType License (FTL), Nennungspflicht in der Dokumentation | nur mit `CC_BUILD_FREETYPE` (`src/freetype`, `src/_ft*.c`) |

Weitere Drittquellen unter `third_party/`, nicht in `license.txt` aufgeführt:

| Datei | Lizenz | Für ZehOS relevant |
| --- | --- | --- |
| `third_party/tinyalloc/` | Apache-2.0 (`LICENSE`) | nur, wenn eine Plattform ihn einbindet; für WASI nicht nötig (wasi-libc `malloc`) |
| `third_party/fix16_sqrt.c` | MIT, Christophe Meessen 2019 | nur Konsolenplattformen ohne FPU |
| `third_party/citro3d.c` | zlib-artig, fincs 2014–2018 | nur 3DS |

Für das Paket gilt deshalb: ClassiCube-`license.txt` vollständig, dazu die Lizenzen von wasi-libc und compiler-rt wie bei NetSurf und Rechner sowie `SOURCES.txt` mit Commit und Rezept (GPL-Pflicht entfällt, die Quelle wird trotzdem wie bei den anderen Paketen genannt). FTL kommt nur hinzu, falls FreeType doch mitgebaut wird.

## Standardtexturen: Mojang-Material

- `src/Resources.c:864-867` (`CC_BUILD_RESOURCES`): Der Launcher lädt beim ersten Start `client.jar` von Minecraft Classic 0.30 und 1.6.2 von `launcher.mojang.com` und dazu vier Texturen von `classic.minecraft.net`. Er extrahiert daraus `terrain.png`, `gui.png` und weitere Dateien und baut `texpacks/default.zip`. Zusätzlich lädt er Patches von `static.classicube.net`.
- `src/Resources.c:347`: Sounds und Musik kommen von `resources.download.minecraft.net`, ebenfalls Mojang.
- `src/Resources.c:718-722`: alternativ `RESOURCE_SERVER "/default.zip"` (`http://static.classicube.net/default.zip`).
- `readme.md:39`: „download the required assets from minecraft.net and classicube.net“.
- `misc/cc_textures.zip` (Konsolenports, `terrain.png`, `gui.png`, `char.png`, Mob-Texturen usw.) und `misc/cc_audio.zip`: Beide haben keine eigene Lizenzangabe, die Herkunft ist nicht belegt, beim Audio vermutlich Mojang. Nicht verwenden.
- Eingebaute Ersatzschrift `font_bitmap` (`src/SystemFonts.c:50-51`, Kommentar „Source: Goodly's texture pack for ClassiCube“; offenbar mit `misc/bitmap_font_gen.cs` aus dessen `default.png` erzeugt). Die Lizenz von Goodlys Texturpaket ist ungeklärt. Sie wird gezeichnet, wenn kein `default.png` geladen ist (`Drawer2D.c:552-557, 572`) oder wenn statt der Bitmapschrift die Systemschrift gewählt ist. Ohne `CC_BUILD_FREETYPE` ersetzt sie dann die Systemschrift. Im Spiel ist standardmäßig die Bitmapschrift aktiv (`Drawer2D.c:713`, `OPT_USE_CHAT_FONT` aus); die Menüoption `MenuOptions.c:931` schaltet um. Deshalb ein eigenes `default.png` mitliefern und die Systemschrift-Option im Port ausblenden oder festsetzen. Bis die Lizenz geklärt ist, eine eigene oder freie Bitmapschrift einplanen und im Port prüfen, dass `font_bitmap` nie sichtbar wird.

Folge: Paketbau und Gasttest dürfen weder den Launcher-Download noch `misc/*.zip` verwenden. Das Spiel sucht `texpacks/default.zip` und `texpacks/classicube.zip` (`TexturePack.c:505-508`). Ein eigenes freies `default.zip` im Paket ersetzt beide. Ohne Datei zeigt `Game.c:450` einen Warnhinweis.

## Nachladen beim Start und wie es abgeschaltet wird

| Weg | Code | Abschalten |
| --- | --- | --- |
| Launcher: Anmeldung, Serverliste, Flaggen (`www.classicube.net/api`, `static.classicube.net`) | `LWeb.c:241-651` | Start mit `--singleplayer` (`main_impl.h:205-208`); der Launcher läuft dann nicht |
| Launcher: Update-Prüfung `cdn.classicube.net/client/builds.json` | `LWeb.c:575-619` | wie oben |
| Launcher: Ressourcen von Mojang/classicube.net | `Resources.c` (`CC_BUILD_RESOURCES`) | wie oben; zusätzlich `CC_BUILD_RESOURCES` nicht definieren |
| Spiel: Skins `cdn.classicube.net/skin` | `Entity.c:253`, `Http_Worker.c` | `CC_BUILD_NETWORKING` nicht definieren: `Http_Worker.c:5-40` liefert dann für alle Abrufe `-1` |
| Spiel: Texturpaket-URL vom Server, WoM-Erweiterung | `TexturePack.c:712`, `Protocol.c:275-281` | nur im Mehrspieler; ebenfalls durch Stubs abgeschaltet |
| Spiel: Plugins (`dlopen`) | `Game.c:444` (`CC_BUILD_PLUGINS`) | nicht definieren; WASI kennt kein `dlopen` |
| Spiel: Menü „Texturpaket per URL“ | `Menus.c:2921` | Stub, liefert keinen Abruf |

`Core.h:182-188` definiert `CC_BUILD_NETWORKING`, `CC_BUILD_FREETYPE`, `CC_BUILD_RESOURCES` und `CC_BUILD_PLUGINS` standardmäßig. Die WASI-Plattformdefinition muss sie deshalb ausdrücklich mit `#undef` abschalten, wie andere Ports es tun (zum Beispiel `Core.h:287-289`), und `CC_BUILD_NOMUSIC`/`CC_BUILD_NOSOUNDS` setzen. Ohne FreeType hat WASI keine Systemschriften. Start immer mit `--singleplayer`. Mehrspieler (Plan-Etappe 6) braucht später ein eigenes, auf `zehos_v1.net_*` umgestelltes Netz ohne BearSSL und weiterhin ohne Skin-Server, solange keine Freigabe dafür vorliegt.

Schreibzugriffe (`options.txt`, `fontscache.txt`, Weltdateien) sind nicht Teil dieser Recherche; sie gehören zu Etappe 2 (Umleiten in den Speicher) beziehungsweise Etappe 4.

## Freies Texturpaket

Benötigt werden mindestens:

- `terrain.png`: Atlas mit 16×16 Kacheln in Classic-Anordnung.
- `default.png`: Bitmapschrift mit 16×16 Zeichen, quadratisch, Kantenlänge eine Zweierpotenz.
- `gui.png`, `icons.png`, `particles.png`, `clouds.png`.
- `char.png`: Spielerskin 64×32.

Optional sind `rain.png`, `snow.png`, `animations.png`/`.txt` und Mob-Texturen. Mobs sind im Einzelspieler-Standardmodus nicht nötig.

Kandidat für die Blocktexturen: Minetest Game, Mod `default` (`minetest/minetest_game`, `mods/default/license.txt`). Medien stehen unter CC BY-SA 3.0, Rechteinhaber celeron55, Cisoun, VanessaE, Calinou, paramat u. a. Einzelne Medien weiterer Urheber stehen unter CC BY 3.0 oder CC0. Die Zuordnung je Datei steht in derselben Datei und muss beim Zusammenstellen je verwendeter Textur übernommen werden. Folgen:

- Das zusammengestellte `default.zip` ist eine Bearbeitung und steht selbst unter CC BY-SA 3.0. Ins Paket gehören Urheberliste, Lizenztext und Änderungshinweis („zu einem ClassiCube-Atlas zusammengesetzt“).
- ShareAlike betrifft nur das Texturpaket, nicht `classicube.wasm` (getrennte Datei, nur geladen). Mit BSD-3-Clause und dem Paketformat ist das vereinbar.
- Minetests `character.png` hat das 64×32-Skin-Format und kann `char.png` liefern (gleiche Lizenzprüfung je Datei).
- `default.png`, `gui.png` und `icons.png` haben in Minetest keine Entsprechung im ClassiCube-Layout. Am einfachsten sind eigene Grafiken (im Paket als CC0 oder unter derselben Lizenz wie das Paket ausweisen) oder eine Schrift mit belegter freier Lizenz, gerastert auf 8×8 Pixel je Zeichen.

Alternative ohne ShareAlike: CC0-Kacheln, etwa „Tiny Texture Pack 3“ von Screaming Brain Studios auf OpenGameArt. Das sind Einzelkacheln mit 128 bis 512 Pixeln, kein Atlas; Auswahl und Herunterskalieren wären Eigenarbeit. Die Lizenz ist je Seite zu prüfen.

Vor der Veröffentlichung ist zu prüfen:

- Für jede Datei im Texturpaket sind Quelle, Urheber und Lizenz in einer `TEXTURES.txt` im Paket eingetragen.
- Kein Bild stammt aus `misc/`, aus einem Launcher-Download oder von minecraft.net/classicube.net.
- Der Privacy-Scan des Pakets ist wie bei den anderen Paketen sauber.

## Offen

- Lizenz der eingebauten Ersatzschrift (`font_bitmap`, laut `SystemFonts.c:50` aus Goodlys Texturpaket) sowie Herkunft von `misc/cc_textures.zip` und `misc/cc_audio.zip`: ungeklärt. Für ZehOS unkritisch, wenn ein eigenes `default.png` mitkommt und der Port die Systemschrift-Option sperrt. Das prüft der Port.
- Die Classic-Blockanordnung von `terrain.png` braucht für einige Blöcke (Schwämme, farbige Wolle, Gold-/Eisenblöcke) Entsprechungen. Minetest-Texturen decken nicht alle 1:1 ab; fehlende Kacheln selbst erstellen.
- Lizenz der in `license.txt` genannten Ray-Box-Vorlage: ohne angegebene Lizenz, der Code ist laut Text nur danach geschrieben. Das Risiko trägt der Upstream; hier nur dokumentiert.

Quellen: ClassiCube-Repository (Commit oben); [minetest_game `mods/default/license.txt`](https://raw.githubusercontent.com/minetest/minetest_game/master/mods/default/license.txt); [Debian-Copyright-Datei Luanti](https://metadata.ftp-master.debian.org/changelogs/main/l/luanti/stable_copyright) (Sekundärquelle); [OpenGameArt Tiny Texture Pack 3, Beispielseite](https://opengameart.org/node/148812).
