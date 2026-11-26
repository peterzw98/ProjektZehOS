# Ladybird js im ZehOS-Gast und unter Linux: Laufzeiten (26. November 2026)

Zahlen für die Vorstellung am 4. Dezember. Es läuft dieselbe Binärdatei (LibJS `bin/js`, statisch gegen musl gebaut) mit denselben Skripten
einmal im ZehOS-Gast und einmal direkt unter Linux auf demselben Rechner.

## Umgebung

| | |
| --- | --- |
| Rechner | MSI, Intel Core i5-10500H (12 logische CPUs), Debian 13, Linux 6.12.117 |
| Gast | QEMU 10.0.13, KVM, `ZEHOS_QEMU_SMP=1`, Testbau `-p:ZehOSLinuxRun=true` (nativ, `-warnaserror`, 0 Warnungen, 0 Fehler) |
| ZehOS-Stand | story/ladybird `b91bbe5c`; bis `d1715c86` sind nur Folien-Dokumente dazugekommen, der Code ist gleich |
| js | `story/ladybird-bin` `6a391904`, Blob `ladybird/bin/js` `58800d91` (gegen die verwendete Datei per `git hash-object` geprüft) |
| Linux-Seite | dieselbe Datei, `taskset -c 2` (eine CPU), sonst keine Messung oder CI auf der Maschine (Last vor der Messung 0,4 bis 1,1) |

## Skripte

Jede Messung steckt in einer Funktion `main()`. Grund: Diese js-Binärdatei wirft unter Linux bei einer `for`-Schleife auf oberster Ebene
`TypeError: Maximum call stack size exceeded` (siehe unten). Gemessen wird im Skript mit `Date.now()` um die eigentliche Arbeit herum.
Jeder Lauf ist ein eigener Prozess.

```js
// sort.js: 10000 Zahlen, feste Pseudozufallsfolge, numerischer Vergleich; danach Prüfung der Reihenfolge
function main() {
  let x = 12345; const a = [];
  for (let i = 0; i < 10000; i++) { x = (x * 1103515245 + 12345) % 2147483648; a.push(x); }
  const t0 = Date.now(); a.sort((p, q) => p - q); const t1 = Date.now();
  let ok = true; for (let i = 1; i < a.length; i++) if (a[i - 1] > a[i]) ok = false;
  console.log("BENCH sort ms=" + (t1 - t0) + " ok=" + ok);
}
main();

// loop.js: let s = 0; for (let i = 0; i < 1e6; i++) s += i;  (loop7.js 1e7, loop8.js 1e8, gleicher Aufbau)
// fib.js:  function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }  fib(25)
```

Befehle:

- Gast: Die Skripte liegen neben `bin/js` als `/data/t` auf der Datenplatte (Aufbau wie `scripts/test-linux-js.py`). Im Desktop-Terminal:
  `cd /data/t`, dann `run bin/js sort.js` und so weiter. Ein Aufwärmlauf `run bin/js -l -c 1+1` wird nicht gewertet. Die Ausgabe kommt über
  den seriellen Spiegel (`LINUXRUN OUT`), das Ende über `LINUXRUN END exit 0`.
- Linux: `taskset -c 2 bin/js sort.js` und so weiter, Prozesszeit mit `date +%s%N` davor und danach.

## Ergebnis (je 3 Läufe, Millisekunden nach `Date.now()`)

| Messung | ZehOS-Gast (KVM, 1 CPU) | Linux (MSI, 1 CPU) | Ergebnis geprüft |
| --- | --- | --- | --- |
| Array-Sort 10000 | 11 / 10 / 10 | 9 / 10 / 10 | sortiert (`ok=true`) |
| Schleife 1e6 | 5 / 4 / 4 | 4 / 4 / 4 | `s=499999500000` |
| fib(25) | 6 / 6 / 10 | 6 / 7 / 6 | `r=75025` |
| Schleife 1e7 | 41 / 43 / 41 | 41 / 41 / 41 | `s=49999995000000` |
| Schleife 1e8 (Gegenprobe) | 407 / 409 / 407 | 403 / 402 / 401 | `s=4999999950000000` |

Rechnen geht im Gast so schnell wie unter Linux: Bei 1e8 liegt der Unterschied bei etwa 1 %, bei den kleinen Messungen unter der
Millisekundenauflösung. Das passt dazu, dass reine Rechenarbeit unter KVM ohne Systemaufrufe direkt auf der CPU läuft. Einzelne Ausreißer
(fib 10 ms im Gast) sind bei Läufen dieser Länge Rauschen. Ein Benchmark ist das nicht: Es sind drei Läufe ohne Statistik.

**Prozess insgesamt** (Start bis Ende, vom Host gemessen): Linux 9 bis 17 ms (bei 1e7 46 ms). Im Gast misst der Host vom seriellen
`LINUXRUN BEGIN` bis `LINUXRUN END` 340 bis 500 ms (bei 1e7 380 bis 460 ms). Darin stecken das Laden des Programms von der Datenplatte, die
ICU-Daten, der serielle Spiegel und das Abfragen des Protokolls alle 20 ms. Wie sich die etwa 0,4 s aufteilen, ist nicht untersucht.
Für die Vorstellung heißt das: Die Rechenleistung ist gleich, der Programmstart im Gast ist deutlich langsamer.

**Gegenprobe der Gastuhr:** Im Gast steigt die vom Host gemessene Prozesszeit von 1e6 (343 bis 403 ms) zu 1e8 (766 bis 847 ms) im Mittel um
436 ms. Nach `Date.now()` im Gast sind es 403 ms. Die Gastuhr geht also im Rahmen der Messgenauigkeit richtig, und die Zahlen in der
Tabelle sind keine Täuschung durch eine langsame Uhr.

## Nebenbefund: `for` auf oberster Ebene unter Linux

`bin/js -c 'let s=0; for (let i=0;i<10;i++) s+=i; console.log(s)'` endet unter Linux mit `TypeError: Maximum call stack size exceeded`
(Exit 1, auch mit `ulimit -s` 1024, 65536 oder unlimited). Mit `var` ist es genauso, eine `while`-Schleife läuft, dieselbe `for`-Schleife in
einer Funktion auch. Im ZehOS-Gast gibt dieselbe Datei (`top.js`) richtig `TOP s=45` aus. Vermutlich ermittelt LibJS die Stackgrenze unter
musl/Linux anders als im Gast. Die Ursache ist nicht untersucht; die Vorstellung betrifft das nur, wenn dort Linux-Vergleiche live laufen.

## Nicht geprüft

- smp4, TCG, VMware, echte Hardware.
- Andere Engines (etwa Node) als Vergleich, größere Benchmarks, Statistik über mehr als drei Läufe.
- Woraus die etwa 0,4 s Startzeit im Gast bestehen.

Rohdaten (nicht im Repository, MSI): `agent-peter-senior2/jsb/g1.txt`, `g2.txt` (Gast) und `linux.txt`.
