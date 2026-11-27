# Ladybird auf ZehOS: Stichpunkte für die Vorstellung (4.12.2026)

> Stand 27. November 2026, abends. Peter gab am 27.11. das **GO** am Tor N1-Z. Teststände und Grenzen stehen im [Machbarkeitsbericht](../../LADYBIRD_FEASIBILITY.md#stand-nach-dem-go-am-27-november-2026), Befehle und Rückfall im [Demo-Ablauf](2026-12-04-ladybird-demo-ablauf.md).

## 1. Frage

Kann ZehOS (C#-Kernel auf Cosmos) einen echten Webbrowser wie Ladybird tragen, und auf welchem Weg?

## 2. Weg: echte Linux-Programme im Benutzermodus

- Ladybird ist ein Linux-Programm. Statt es umzuschreiben, bekommt ZehOS eine **Linux-Schnittstelle**: Programme laufen in **Ring 3** mit eigenem Adressraum und rufen Linux-Systemaufrufe, die der Kernel in C# beantwortet.
- Größtes Einzelrisiko vorab: verträgt sich Ring 3 mit Cosmos (GC, Interrupts, SMP)? → **Nachweis N1-A/B: trägt** (Systemaufruf hin und zurück ≈ 0,15 µs, Seitenfehler ≈ 0,8 µs, KVM).

## 3. Was diese Woche entstanden ist (alles auf `story/ladybird`, master unberührt)

| Baustein | Inhalt |
| --- | --- |
| Cosmos-Patches | GDT/TSS je CPU, FPU-Zustand je Thread, Ring-3-Einstieg, CR3 je Thread, APIC über HHDM, sofortiges Yield |
| ELF-Lader | statisch und statisch-PIE, Startstack mit auxv, W^X |
| Linux-Systemaufrufe | Speicher, Dateien, Pipes, poll/ppoll/epoll, timerfd, eventfd, Threads (clone, futex), Prozesse (fork, vfork, execve, wait), Signale inkl. Fehlersignale aus Ring 3, Unix-Sockets (Stream, Datagramm, Dateien übergeben), TCP/UDP über Loopback, memfd, Dateisperren (fcntl, flock), CPU-Zeit |
| Dynamisches Laden | `PT_INTERP`: dynamisch gelinkte musl-Programme (ld-musl, libstdc++, libgcc_s) starten, auch per `execve` aus laufenden Prozessen |
| /proc | status, stat, maps, meminfo, cpuinfo je Prozess des Laufs |
| Dateisystem | eigene Wurzel auf der Datenplatte, Rechteprüfung des angemeldeten Nutzers, kein Ausbruch über `..`/Links/8.3-Kurznamen, Wachstumskontingent |
| Terminal | Ausgabe, Eingabe, termios, Rohmodus für Zeileneditoren, Abbruch mit Strg+C und Strg+Umschalt+Esc |

## 4. Gastnachweise und Demo (live mit 1 CPU)

1. `run hello` im Desktop-Terminal → „Hallo aus dem Ring 3!“, Ende 0.
2. Linux-Testsätze (`scripts/test-linux-all.py`): **12 von 13** Sätzen wie unter Linux (KVM, 1 und 4 CPUs, engineer 27.11. 09:10); einzige Abweichung `badptr` in s1, weil FAT keine symbolischen Links kennt.
3. **Ladybirds JavaScript-Engine `js`** (statisch mit musl, 33 MB, unverändert) im ZehOS-Terminal: `1+1` → `2`, REPL mit `(7*6).toString(2)` → `"101010"`, Strg+C bricht Endlosschleife ab. Gastskript 11/11 bei 1 und 4 CPUs. Foto: `zehos-v3-out/js14-1/03-repl-antwort.png` (MSI).
4. **Erster vollständiger Screenshot im Gast:** `static.html` auf `cbcdb6bb` (798 × 4087 Pixel, 50 Absätze und eine Tabelle). Nach V38 (`4c8f73e3`) lief dieselbe Seite auf KVM je **3/3 mit 1 und 4 CPUs** und auf TCG **3/3 mit 1 CPU** ohne Absturz.
5. **Abnahme von vier Seiten:** `0760f4b8` mit V43/V45, KVM, 2 GiB, `lb-headless-99fb75b7`. HTML, CSS, JavaScript und PNG-Dekodierung je mit 1 und 4 CPUs, **8/8 im ersten Versuch**, Gastlauf 82–89 s, gleicher Anteil nicht-weißer Pixel je Seite. TCG auf demselben Stand, 1 CPU, `static.html` und `js.html` je 1/1.
6. **Generalprobe mit dem Standardbündel:** KVM auf `ca0e3238` mit dem ISO `0760f4b8-run`. `static.html`, Projektseite und Canvas dauerten je 88 s gesamt. Die WSL-Probe mit TCG prüfte `static.html` zweimal in je 104 s, davon 96 s im Gast. Beide PNGs waren bytegleich zur MSI-Referenz. [Demo-Ablauf](2026-12-04-ladybird-demo-ablauf.md)
7. **Seite über HTTP im Gast:** BusyBox-`httpd` und Ladybird in demselben LinuxRun-Prozessbaum. `http://127.0.0.1:8080/zehos-projekt.html` ergab 798 × 720 Pixel, **3/3** auf KVM mit 1 CPU und 2 GiB (`e4f5eff6`, `6301341f`, `2dd30960`, 68–91 s). [Machbarkeitsbericht](../../LADYBIRD_FEASIBILITY.md#stand-nach-dem-go-am-27-november-2026)
8. **Abendserien auf MSI/KVM, 2 GiB:** `static`, `css`, `js` und `img` jeweils mit 1 und 4 CPUs. `52a2cdc4` (V46) und `d0a5b7b7` (mit V45-P3) je **8/8**. `13d48244` (mit V48) **7/8** im ersten Durchgang. `static` mit 4 CPUs blieb einmal beim Screenshot stehen. Drei Wiederholungen dieser Zelle liefen durch. Die Ursache bleibt offen. Mit V50 (`ff57d65b`, GC-Header-Fix) **8/8** und `static` mit 4 CPUs **10/10**. [Abendstand im Demo-Ablauf](2026-12-04-ladybird-demo-ablauf.md#abendstand-der-abnahme)

## 5. Zahlen zur Machbarkeit

- **`js` im Gast so schnell wie unter Linux** (KVM, 1 CPU, je 3 Läufe, [Bericht](../specs/2026-11-26-ladybird-js-gast.md)): Sortieren 10 000 Einträge 10–11 ms vs. 9–10 ms, Schleife 1 Mio. 4–5 vs. 4 ms, `fib(25)` 6–10 vs. 6–7 ms, 10⁸ Durchläufe 407 vs. 402 ms. Nur der Programmstart ist langsamer: ~0,4 s im Gast vs. ~10 ms unter Linux.

- Ladybird-Bau (glibc, ohne JIT) auf normalem PC: ~60 min, Spitzen-RAM ~4 GiB (der angenommene 32-GiB-Rechner war unnötig).
- Laufzeitbedarf unter Linux (L1-C): 334–438 MiB PSS für drei Testseiten, meist 7 Prozesse und 69 Threads. [Messung L1](../../LADYBIRD_FEASIBILITY.md#messung-l1)
- Ganzes Ladybird mit musl: **87 Patchzeilen** im Vollbau (Grenze 1000). Unter Linux startet Hub headless ohne Qt und rendert `static.html` mit 798 × 4087 Pixeln. [Bau und Laufzeitprobe #636](https://github.com/peterzw98/ProjektZehOS/issues/636#issuecomment-7211849801)
- Systemaufrufe von Ladybird (C2): 94 verschiedene, keiner aus den Ausschlussgruppen → **trägt**.

## 6. Qualität

- Jeder Baustein mit unabhängigem Review; mehrere Sicherheitsreviews (Linux-Schnittstelle, ELF-Lader, Dateisystem) fanden und schlossen u. a.: Kernel-OOM über Terminal, Zugriff auf Sicherheitsdatenbank per 8.3-Kurzname, Datenverlust beim Ordner-Umbenennen mit offenen Dateien.
- Host- und Gasttests sind in den Berichten zu den jeweiligen Ständen dokumentiert. [Nachweise](../../LADYBIRD_FEASIBILITY.md)
- V46 ergänzt `rt_sigsuspend` für das Warten auf Kindprozesse in BusyBox `ash`. V45-P3 stellt bei `rt_sigreturn` die Flags TF und RF wie Linux wieder her. V48 begrenzt die Zahl der Speicherabbildungen auf 65.530 pro Adressraum und liefert bei Überschreitung `ENOMEM`. V50 räumt im Cosmos-GC die Header freigegebener Objekte auf. Der Patch `f2b64df6` stand zum Abgleich noch vor der Story-Übernahme. [Teststände und Quellen](2026-12-04-ladybird-demo-ablauf.md#abendstand-der-abnahme)

## 7. Grenzen (ehrlich)

- Alle Threads **aller Prozesse eines Laufs** auf einer CPU (4 CPUs bringen Ladybird noch keine Parallelität, laser Risiko-Review 27.11.); fork kopiert den Adressraum sofort (kein Copy-on-Write).
- Geprüfter Netzpfad ist HTTP über IPv4-Loopback. Die Demo rendert headless. FAT stellt keine symbolischen Links oder nativen Unix-Rechte bereit.
- Die Demo verwendet 2 GiB Gast-RAM. Seit V43 kann bei 512 MiB `WebContent` per `SIGKILL` enden. Neustarts können bis zum Zeitlimit laufen und ein weißes oder fehlendes PNG hinterlassen.
- Live läuft die Demo mit **einer CPU**. Bei einem Stillstand greifen die vorbereiteten Gastbilder und die Drei-Minuten-Grenze im Runbook.
- Nicht geprüft: VMware, echte Hardware.

## 8. Ausblick

| Phase | Inhalt | Stand |
| --- | --- | --- |
| N1 Nachweise | Risiken klären | GO erteilt, förmliches D2-Urteil offen |
| N2 Prozesse, ELF, Systemaufrufe | Linux-Programme starten | Statische und dynamische Beispiele im Gast gelaufen |
| N3 Paketdienst | native Pakete | offen |
| N4 Threads, IPC, Netz | mehrere Prozesse und Sockets | Browser-Prozesse und HTTP-Loopback im Gast belegt |
| N5/N6 | Apps umstellen, Ladybird mit Seite | Headless-Demo belegt, Browserfenster offen |

Weitere Prüfpunkte und offene Fragen stehen im [Machbarkeitsbericht](../../LADYBIRD_FEASIBILITY.md#offene-punkte).
