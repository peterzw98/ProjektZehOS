# N1-D1 · Statisches LibJS mit musl

Für [#622](https://github.com/peterzw98/ProjektZehOS/issues/622) baut das Ziel `js` statisch und ohne JIT. In Alpine 3.24.4 (x86-64, WSL 2) lieferte `1+1` den Wert `2`. Die Sortierprobe prüfte alle 10.000 Werte und lieferte `10000`. Die Ladybird- und vcpkg-Quellbäume hatten jeweils 0 Patchzeilen. Für `js` blieb keine unlösbare Stelle im Bau.

Das ELF ist 33.092.712 Bytes groß und enthält weder `PT_INTERP` noch `DT_NEEDED`. Sein SHA-256 ist `bd719b4fd8a383e4d0d386a88ce716157c91bc205fcac7700f39bbaad3d32804`. Der Git-Blob stimmt mit dem [veröffentlichten `js`](https://github.com/peterzw98/ProjektZehOS/blob/9421ab54/ladybird/bin/js) überein. Zur Ausführung gehört auch die separat veröffentlichte [ICU-Datendatei](https://github.com/peterzw98/ProjektZehOS/blob/9421ab54/ladybird/share/Lagom/icu/icudt78l.dat) mit 27.687.488 Bytes.

Der Wiederanlauf mit dem [Buildskript](build.sh) führte die CMake-Konfiguration, die ELF- und Laufzeitprüfungen sowie `strace` aus. Ninja meldete für den vorhandenen Bauordner `no work to do`. Ein frischer Gesamtbau und ein Gastlauf sind separate Abnahmen.

## Linux-ABI

Der Trace von `js -c '1+1'` zeigt gegenüber [der Story-ABI](../../../docs/LINUX_ABI.md) auf `7f4f983f` folgende Unterschiede. Der erste `execve`-Eintrag stammt vom Start durch `strace`.

- `pipe2(O_NONBLOCK|O_CLOEXEC)` und `sysinfo` fehlen in der Story und im geprüften Integrationsstand `cf8290794`.
- `prlimit64` und `mremap` fehlen in der Story, sind aber in V10 auf dem Integrationsbranch. Dort lässt sich `RLIMIT_NOFILE` lesen, eine Änderung des Limits wird abgelehnt. Im Trace setzte das Programm das Softlimit von 10.240 auf 65.536. Alle 62 `mremap`-Aufrufe mit Flags `0` endeten bereits in Alpine mit `ENOMEM` oder `EFAULT`.
- Die ICU-Daten werden nach `readlink("/proc/self/exe")` mit `mmap(PROT_READ, MAP_SHARED)` eingelesen. Die Story unterstützt anonyme Abbildungen. Die Arbeiten an `/proc/self/exe` und Nur-Lese-Dateiabbildungen laufen in V12.
- Zwei `madvise(MADV_FREE)`-Aufrufe lieferten in Alpine `0`. Die Story dokumentiert dafür `EINVAL`.
