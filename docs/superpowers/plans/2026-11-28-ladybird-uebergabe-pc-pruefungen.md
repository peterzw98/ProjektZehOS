# Ladybird-Prüfungen am Windows-PC am 28. November 2026

Die [Übergabe](2026-11-28-ladybird-uebergabe-pause.md) nennt den Auftrag und die früheren KVM-Läufe. Hier stehen die zusätzlichen Prüfungen der festgeschriebenen Branch-Köpfe.

| Branch | Geprüfter ZehOS-Kopf | Cosmos-Pin |
| --- | --- | --- |
| `task/lb-n4-irq-signal` | `369822544d8ab46498b300c77fdd2062e248309f` | `2ecd10951ae935641c2832798c67ab8fcf7d87ff` |
| `task/lb-n2z-fat-lookup` | `863d2e1fb643e40c918370077555b4114fb53c4d` | `0fb91b8df67dd76bd975c60160a333e4ccf98d56` |

## Review

- IRQ, Commit `369822544`: keine P1/P2-Befunde. Nach Ende von `UserMode.Enter` und Entfernen der eigenen Signalbrücke stößt `PassOnSignals` aus den drei Rückkehrpfaden die anderen Threads bei offenen Signalen erneut an. P3: Die Gastproben weisen das erneute Anstoßen genau beim Ende von `Enter` nicht gezielt nach.
- FAT, Commit `1d6ca5a3`: keine P1/P2-Befunde. `GetAttr` liefert die für die Pfadauflösung benötigten Metadaten. Der Mount-Snapshot bleibt während der Auflösung unter der StorageLock stabil. P3: Wirft der Backend-Einstieg beim Erwerb des Auflösungsbereichs, kann die äußere Sperre hängen bleiben. Ein zweiter P3 betrifft den Test für die Sperre: Er beobachtet sie innerhalb der Backend-Aufrufe, die bereits eigene Sperrbereiche haben.

## Hosttests

Mit Windows-.NET SDK 10.0.404 und getrennten Worktrees lief auf beiden Köpfen `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` mit Exitcode 0.

| Branch | Bestanden | Fehlgeschlagen | Übersprungen | Gesamt |
| --- | ---: | ---: | ---: | ---: |
| IRQ | 2359 | 0 | 23 | 2382 |
| FAT | 2357 | 0 | 24 | 2381 |

## Gastprüfungen

QEMU 10.0.13 lief unter WSL mit TCG. Der Kernel wurde mit Windows-.NET und den jeweiligen Cosmos-Pins gebaut. Für die ABI-Proben wurden in den isolierten Worktrees die Backslashes für `.incbin` im erzeugten Assembler maskiert und die Sollausgaben mit den LF-Zeilenenden aus Git gelesen. Die ersten Versuche scheiterten an diesen beiden Host-Eigenheiten. Die FAT-Paketprobe wurde mit einem lokalen Wegwerfschlüssel gebaut und ihr serielles Protokoll für den Wiederholungslauf auf das Linux-Dateisystem gelegt, nachdem das Lesen unter DrvFS mit `OSError 61` abbrach.

- IRQ: `test-linux-all --sets v18 --configs tcg:1 tcg:4 --linuxsmp 1` bestand 2/2 Läufe. `sig`, `rspkern` und `rspnc` erfüllten ihre Sollausgaben und -status bei einer und vier vCPUs. Die SMP-Spezialprobe `n4smp` ist im Runner nur für KVM vorgesehen.
- FAT: Bei `test-linux-all --sets s1 v7b --configs tcg:1` bestand `s1`. Zweimal beendete das 60-Sekunden-Zeitlimit im Kernel nur `v7bquota`, jeweils mit der Gastplatte auf DrvFS. Die Wiederholung mit der Wegwerfplatte auf dem nativen WSL-Dateisystem bestand `v7b` vollständig. `v7bquota` schrieb 64 MiB, prüfte `ENOSPC` und beendete sich mit Exitcode 0. Der gesamte Wiederholungslauf dauerte 51 Sekunden.
- FAT: `test-package2-all --configs tcg:1` bestand 3/3 Läufe. `guest` umfasste 21 Schritte und 7 Programmstarts, `dyn` 6 dynamisch gelinkte Starts. `live` bestätigte den alten Paketstand im laufenden Prozess, einen Start aus dem neuen Paketstand und die Bereinigung des alten Versionsordners.

KVM mit vier vCPUs und die Vergleichsmessung zu Lookups und belegtem Speicher je Start bleiben für FAT offen. Die KVM-Ergebnisse aus der Übergabe gelten für die dort genannten Läufe. VMware und echte Hardware waren nicht Teil dieser Prüfung.
