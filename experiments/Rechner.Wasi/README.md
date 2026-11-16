# Rechner (ZehOS-Paket-App)

Taschenrechner als WASIp1-Modul für den ZehOS-Wasm-Loader. Er nutzt nur `zehos_v1.present`, `poll_event`, `sleep_ms` und `should_exit`, braucht also keine Netzwerkberechtigung und keinen neueren Kernel als die bisherigen Paket-Apps (`min-kernel` entfällt). Lizenz: GPL-3.0-or-later wie das Repository; `LICENSE.txt` liegt im Paket.

## Bedienung

Das Bild hat 798×519 Pixel, die Inhaltsgröße eines neuen Paketfensters, und erscheint dort 1:1.

| Eingabe | Wirkung |
| --- | --- |
| Ziffern, `.` oder `,`, `+ - * /` (auch `x` und `:`), `%`, `( )` | Tippen oder Tasten des Rechners |
| Enter oder `=` | Auswerten |
| Rücktaste / `<` | letztes Zeichen löschen |
| Entf / `CE` | zuletzt begonnene Zahl löschen |
| `c` / `C` (Esc nur bei eingefangenem Zeiger) | alles außer dem Verlauf löschen |
| `+/-` | Vorzeichen der letzten Zahl oder des letzten Klammerausdrucks wechseln |

Es gilt Punkt vor Strich; fehlende schließende Klammern ergänzt der Rechner am Ende. Nach `=` beginnt eine Ziffer eine neue Rechnung, ein Operator rechnet mit dem Ergebnis weiter. Die letzten vier Rechnungen stehen klein oben im Verlauf; Fehler landen nicht dort.

**Prozent** wie bei gängigen Rechnern (Windows, Android): `50+10%` ergibt 55, `50-10%` ergibt 45 (Zu- und Abschlag beziehen sich auf den linken Wert), `200*10%` ergibt 20 und `50%` allein 0,5.

Ergebnisse zeigt der Rechner mit höchstens 12 signifikanten Stellen, große und kleine Werte in Exponentenschreibweise (`1e+12`), mit der sich auch weiterrechnen lässt. Je Zahl sind 15 Ziffern, je Eingabe 64 Zeichen und 32 Klammerebenen möglich. Division durch 0, Werte ab 10^100 und ungültige Ausdrücke erscheinen als rote Meldung; die nächste Eingabe beginnt dann neu.

## Bauen und prüfen

```sh
python3 experiments/Rechner.Wasi/build.py
```

Das Skript übersetzt und startet zuerst die Hosttests `tests/calc_test.c` mit dem nativen `cc`, baut dann mit wasi-sdk 25 (fester SHA-256, derselbe Abruf wie `experiments/NetSurf.Wasi/build.py`) `bin/package/rechner.wasm` und bricht ab, wenn ein lokaler Pfad im Modul steht. Die Oberfläche lässt sich ohne VM mit dem Prüfhost `zehos-wasm3` aus `experiments/NetSurf.Wasi/` und einer Ereignisdatei (`ZEHOS_EVENTS`, `ZEHOS_PPM`) prüfen.

Paket: Kennung `rechner`, Name `Rechner`, Einstieg `rechner.wasm`, Dateien `rechner.wasm` und `LICENSE.txt`, ohne `--network`.
