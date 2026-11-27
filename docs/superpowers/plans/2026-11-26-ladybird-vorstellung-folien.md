# Ladybird auf ZehOS: Stichpunkte für die Vorstellung (4.12.2026)

> Arbeitsstand 27.11. vormittags, Story `story/ladybird` @ `a549bd61` (Tor N1-Z: **GO** von Peter am 27.11.). Zahlen nur aus den genannten Belegen; was bis Donnerstag dazukommt, wird nachgetragen, was fehlt, bleibt weg.

## 1. Frage

Kann ZehOS (C#-Kernel auf Cosmos) einen echten Webbrowser wie Ladybird tragen, und auf welchem Weg?

## 2. Weg: echte Linux-Programme im Benutzermodus

- Ladybird ist ein Linux-Programm. Statt es umzuschreiben, bekommt ZehOS eine **Linux-Schnittstelle**: Programme laufen in **Ring 3** mit eigenem Adressraum und rufen Linux-Systemaufrufe, die der Kernel in C# beantwortet.
- Größtes Einzelrisiko vorab: verträgt sich Ring 3 mit Cosmos (GC, Interrupts, SMP)? → **Nachweis N1-A/B: trägt** (Systemaufruf hin und zurück ≈ 0,15 µs, Seitenfehler ≈ 0,8 µs, KVM).

## 3. Was diese Woche entstanden ist (alles auf `story/ladybird`, master unberührt)

| Baustein | Inhalt |
| --- | --- |
| Cosmos-Patches | GDT/TSS je CPU, FPU-Zustand je Thread, Ring-3-Einstieg, CR3 je Thread, APIC über HHDM, sofortiges Yield |
| ELF-Lader | statisch und statisch-PIE, Startstack mit auxv, W^X, gehärtet gegen 10 000 verfälschte Dateien |
| Linux-Systemaufrufe | Speicher, Dateien, Pipes, poll/ppoll/epoll, timerfd, eventfd, Threads (clone, futex), Prozesse (fork, vfork, execve, wait), Signale inkl. Fehlersignale aus Ring 3, Unix-Sockets (Stream, Datagramm, Dateien übergeben), TCP/UDP über Loopback, memfd, Dateisperren (fcntl, flock), CPU-Zeit |
| Dynamisches Laden | `PT_INTERP`: dynamisch gelinkte musl-Programme (ld-musl, libstdc++, libgcc_s) starten, auch per `execve` aus laufenden Prozessen |
| /proc | status, stat, maps, meminfo, cpuinfo je Prozess des Laufs |
| Dateisystem | eigene Wurzel auf der Datenplatte, Rechteprüfung des angemeldeten Nutzers, kein Ausbruch über `..`/Links/8.3-Kurznamen, Wachstumskontingent |
| Terminal | Ausgabe, Eingabe, termios, Rohmodus für Zeileneditoren, Abbruch mit Strg+C und Strg+Umschalt+Esc |

## 4. Demo (im Gast, QEMU/KVM, 1 und 4 CPUs)

1. `run hello` im Desktop-Terminal → „Hallo aus dem Ring 3!“, Ende 0.
2. Linux-Testsätze (`scripts/test-linux-all.py`): **12 von 13** Sätzen wie unter Linux (KVM, 1 und 4 CPUs, engineer 27.11. 09:10); einzige Abweichung `badptr` in s1, weil FAT keine symbolischen Links kennt.
3. **Ladybirds JavaScript-Engine `js`** (statisch mit musl, 33 MB, unverändert) im ZehOS-Terminal: `1+1` → `2`, REPL mit `(7*6).toString(2)` → `"101010"`, Strg+C bricht Endlosschleife ab. Gastskript 11/11 bei 1 und 4 CPUs. Foto: `zehos-v3-out/js14-1/03-repl-antwort.png` (MSI).
4. **Ladybird rendert eine Seite im ZehOS-Gast** (27.11., senior): `static.html` vollständig als PNG 798 × 4087 (alle 50 Absätze, Tabelle, Umlaute), 8 Ladybird-Prozesse im Ring 3, KVM: **3 von 3 Läufen mit 1 CPU und 3 von 3 mit 4 CPUs** ohne Absturz, dazu **TCG 3 von 3** (senior, 27.11. 12:25/12:39, `story/ladybird` mit V38). Demo: `scripts/ladybird-demo-run.sh`. Ladezeit im Gast noch 5–8 s.
5. **Weitere Seiten** (27.11., senior, Gast mit 2 GB, KVM 1 und 4 CPUs, je 1 Lauf, PNG je Seite bytegleich): `css.html` (Webfonts, Verläufe, sticky-Kopf), `js.html` (Ergebnis gleich wie node auf dem Host), `img.html` (PNG dekodiert und skaliert). Mit 512 MB Gastspeicher hält der Kernel noch an (Prozesse können den Kernel-Heap leeren, V43 in Arbeit).

## 5. Zahlen zur Machbarkeit

- **`js` im Gast so schnell wie unter Linux** (KVM, 1 CPU, je 3 Läufe, [Bericht](../specs/2026-11-26-ladybird-js-gast.md)): Sortieren 10 000 Einträge 10–11 ms vs. 9–10 ms, Schleife 1 Mio. 4–5 vs. 4 ms, `fib(25)` 6–10 vs. 6–7 ms, 10⁸ Durchläufe 407 vs. 402 ms. Nur der Programmstart ist langsamer: ~0,4 s im Gast vs. ~10 ms unter Linux.

- Ladybird-Bau (glibc, ohne JIT) auf normalem PC: ~60 min, Spitzen-RAM ~4 GiB (der angenommene 32-GiB-Rechner war unnötig).
- Laufzeitbedarf (L1-C): 334–438 MiB PSS für drei Testseiten, 7 Prozesse, 69 Threads.
- Ganzes Ladybird mit musl: baut mit **87 Patchzeilen** (Grenze 1000); **unter Linux headless ohne Qt: Hub + 9 Dienste, `static.html` als PNG 798×4087** (senior25, 27.11., #636).
- Systemaufrufe von Ladybird (C2): 94 verschiedene, keiner aus den Ausschlussgruppen → **trägt**.

## 6. Qualität

- Jeder Baustein mit unabhängigem Review; mehrere Sicherheitsreviews (Linux-Schnittstelle, ELF-Lader, Dateisystem) fanden und schlossen u. a.: Kernel-OOM über Terminal, Zugriff auf Sicherheitsdatenbank per 8.3-Kurzname, Datenverlust beim Ordner-Umbenennen mit offenen Dateien.
- Hosttests: rund 1980, Windows und Linux grün; Gasttests für jede Stufe.

## 7. Grenzen (ehrlich)

- Alle Threads eines Programms auf einer CPU; fork kopiert den Adressraum sofort (kein Copy-on-Write).
- Netz nur über Loopback, keine Fenster für Linux-Programme; FAT ohne symbolische Links und ohne echte Rechte.
- Nicht geprüft: VMware, echte Hardware.

## 8. Ausblick und Aufwand

| Phase | Inhalt | Stand |
| --- | --- | --- |
| N1 Nachweise | Risiken klären | ~90 % |
| N2 Prozesse, ELF, Systemaufrufe | Linux-Programme starten | ~75 % |
| N3 Paketdienst | native Pakete | offen |
| N4 Threads, IPC, Netz | mehrere Prozesse, Sockets | ~20 % |
| N5/N6 | Apps umstellen, Ladybird mit Seite | offen |

Gesamt grob **25–30 %** des Plans (42–71 Personenwochen, Hypothese). Nächster großer Schritt: Prozesse starten Prozesse (Ladybird braucht WebContent als eigenen Prozess), dann Sockets und ein Fenster.
