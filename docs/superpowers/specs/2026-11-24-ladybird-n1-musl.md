# N1-D2: Vollbau und Headless-Lauf von Ladybird mit musl

Stand 27. November 2026. Dieser Bericht dokumentiert den Nachweis für
[#636](https://github.com/peterzw98/ProjektZehOS/issues/636) und das Urteil D für
das [Tor #624](https://github.com/peterzw98/ProjektZehOS/issues/624).

## Vollbau

- Ladybird-Quellen `6fba5b78a1043fe93b3451204e04dcc0e3bd983a`, ZehOS-Zweig
  `task/ladybird-n1d2-alles` bei `99fb75b7`, Alpine 3.24.4 x86-64 in WSL 2.
- Das Ziel `all` baut die Browser-Anwendung und ihre Dienste mit dem
  Distribution-Preset ohne JavaScript-JIT. Der Ninja-Vollbau wurde erfolgreich
  abgeschlossen. Die ursprüngliche anschließende Probe über die Qt-Anwendung
  meldete `no_screenshot`. [Baumeldung #636](https://github.com/peterzw98/ProjektZehOS/issues/636#issuecomment-7200048117)
- Der Vollbau verwendet Qt-Entwicklungspakete und dynamisches Qt aus Alpine.
  Ladybirds interne Bibliotheken sind statisch gelinkt. [Bauanleitung auf `99fb75b7`](https://github.com/peterzw98/ProjektZehOS/blob/99fb75b795b68c53eaa0914250773b67c6a1dbe7/experiments/Ladybird.Probe/musl/README.md)

```bash
experiments/Ladybird.Probe/musl/build.sh --target all --check
timeout --kill-after=30s 8h experiments/Ladybird.Probe/musl/build.sh --target all --jobs 3
```

| Anpassung im Bauzweig | Hinzugefügte und entfernte Quellzeilen |
| --- | ---: |
| `0001-initialize-msghdr-portably.patch` | 80 |
| `0002-include-linux-ioctl-uapi.patch` | 1 |
| `0003-express-libgfx-static-link-cycle.patch` | 3 |
| `0004-express-libunicode-static-link-cycle.patch` | 3 |
| **Ladybird gesamt** | **87** |
| **vcpkg** | **0** |

Gezählt sind geänderte Quellzeilen ohne Diff-Köpfe. Die vier Patches
liegen in [`patches/all` am Bau-Commit](https://github.com/peterzw98/ProjektZehOS/tree/99fb75b795b68c53eaa0914250773b67c6a1dbe7/experiments/Ladybird.Probe/musl/patches/all).

## Headless-Lauf unter Linux

Der bereits gebaute `Hub` startete direkt unter Alpine. Die zehn ELF-Programme
im lokalen Bündel verwendeten den musl-Interpreter. `ldd` zeigte weder
Qt-Abhängigkeiten noch fehlende Bibliotheken. Mit vollständiger Fontconfig
(`fonts.conf`, `conf.d`, 27 gebündelte Schrifteinträge) starteten `WebContent`,
`Compositor` und `RequestServer`. Hub endete mit Exit 0. Das visuell geprüfte
Ganzseiten-PNG misst **798 × 4087 Pixel** und hat SHA-256
`f038bd48c1100c817e20e8b68884f9c9e56adc74c54d37b57c2d006fd96910fd`.
[Linux-Lauf #636](https://github.com/peterzw98/ProjektZehOS/issues/636#issuecomment-7211849801)

Für die Laufzeitprobe zeigt `bundle` auf die absolute Wurzel des
Linux-Prüfbündels. `output` ist ein absoluter, beschreibbarer Pfad für das PNG.

```bash
runtime=$(mktemp -d)
HOME="$runtime" TMPDIR="$runtime" XDG_RUNTIME_DIR="$runtime" \
  XDG_CACHE_HOME="$runtime/cache" LD_LIBRARY_PATH="$bundle/lib" FONTCONFIG_FILE="$bundle/fonts.conf" \
  FONTCONFIG_PATH="$bundle" "$bundle/bin/Hub" --headless=screenshot \
  --force-cpu-painting --window-width 798 --window-height 519 --screenshot-delay 5 \
  --screenshot-path "$output" "file://$bundle/share/zehos-probe/static.html"
```

## Gastnachweis und Urteil D

Auf `cbcdb6bb` renderte das musl-Bündel `99fb75b7` im ZehOS-Gast mit
KVM, 1 CPU und 30 s Screenshot-Verzögerung `static.html` vollständig
(798 × 4087 Pixel, 50 Absätze und eine Tabelle).
[Erster Gastlauf #636](https://github.com/peterzw98/ProjektZehOS/issues/636#issuecomment-7215865651)

**D trägt.** Der Vollbau, der Headless-Lauf von `static.html` und
87 Ladybird-Patchzeilen bei einer Grenze von 1000 erfüllen die D2-Regel.
Der [D1-Bericht](https://github.com/peterzw98/ProjektZehOS/blob/8235f651ffa37e12419ae39211cee09e2e0d94ab/experiments/Ladybird.Probe/musl/bericht-n1-d1.md)
belegt `js` separat mit 0 Patchzeilen in Ladybird und vcpkg.
[Abnahmeregel D2](../plans/2026-11-24-ladybird-n1-nachweise.md#n1-d2-ganzes-ladybird-mit-musl-und-urteil)

Der Qt-freie Lauf verwendet vorhandene Artefakte. Eine frische
CMake-Konfiguration ohne Qt-Entwicklungspakete, Browserfenster, VMware und
echte Hardware stehen aus. Weitere Gastläufe und ihre Bedingungen stehen im
[Machbarkeitsbericht](../../LADYBIRD_FEASIBILITY.md).
