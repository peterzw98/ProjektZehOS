# Ladybird auf ZehOS: Stichpunkte für die Vorstellung (4.12.2026)

> Arbeitsstand 26.11. abends, Story `story/ladybird` @ `b91bbe5c`. Zahlen nur aus den genannten Belegen; was bis Donnerstag dazukommt, wird nachgetragen, was fehlt, bleibt weg.

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
| Linux-Systemaufrufe | rund 90: Speicher (mmap, brk, mremap), Dateien, Pipes, poll, Zeit, Zufall, rlimit, Threads (clone, futex) |
| Dateisystem | eigene Wurzel auf der Datenplatte, Rechteprüfung des angemeldeten Nutzers, kein Ausbruch über `..`/Links/8.3-Kurznamen, Wachstumskontingent |
| Terminal | Ausgabe, Eingabe, termios, Rohmodus für Zeileneditoren, Abbruch mit Strg+C und Strg+Umschalt+Esc |

## 4. Demo (im Gast, QEMU/KVM, 1 und 4 CPUs)

1. `run hello` im Desktop-Terminal → „Hallo aus dem Ring 3!“, Ende 0.
2. Linux-Testprogramme: **20 von 22** gleiche Ausgabe wie unter Linux (Rest: FAT kennt keine symbolischen Links).
3. **Ladybirds JavaScript-Engine `js`** (statisch mit musl, 33 MB, unverändert) im ZehOS-Terminal: `1+1` → `2`, REPL mit `(7*6).toString(2)` → `"101010"`, Strg+C bricht Endlosschleife ab. Gastskript 11/11 bei 1 und 4 CPUs. Foto: `zehos-v3-out/js14-1/03-repl-antwort.png` (MSI).

## 5. Zahlen zur Machbarkeit

- Ladybird-Bau (glibc, ohne JIT) auf normalem PC: ~60 min, Spitzen-RAM ~4 GiB (der angenommene 32-GiB-Rechner war unnötig).
- Laufzeitbedarf (L1-C): 334–438 MiB PSS für drei Testseiten, 7 Prozesse, 69 Threads.
- Ganzes Ladybird mit musl: baut mit **87 Patchzeilen** (Grenze 1000); Start ohne Oberfläche noch offen.
- Systemaufrufe von Ladybird (C2): 94 verschiedene, keiner aus den Ausschlussgruppen → **trägt**.

## 6. Qualität

- Jeder Baustein mit unabhängigem Review; mehrere Sicherheitsreviews (Linux-Schnittstelle, ELF-Lader, Dateisystem) fanden und schlossen u. a.: Kernel-OOM über Terminal, Zugriff auf Sicherheitsdatenbank per 8.3-Kurzname, Datenverlust beim Ordner-Umbenennen mit offenen Dateien.
- Hosttests: rund 1980, Windows und Linux grün; Gasttests für jede Stufe.

## 7. Grenzen (ehrlich)

- Alle Threads eines Programms auf einer CPU; keine Signalzustellung; kein epoll; kein fork/exec.
- Kein Netz und keine Fenster für Linux-Programme → Ladybird zeigt noch keine Seite.
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
