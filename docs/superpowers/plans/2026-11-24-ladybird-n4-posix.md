# Ladybird N4: Threads, Prozesse, IPC, Netz (Umriss)

> Umriss. Geschnitten nach Tor N2-Z. Grundlage: [Entwurf, Abschnitte 4 bis 6](../specs/2026-11-24-native-pakete-design.md#6-systemaufrufe); Bedarf: [L2-C](../specs/2026-11-19-ladybird-l2-prozesse.md), [L2-D](../specs/2026-11-19-ladybird-l2-threads.md), Aufrufliste N1-C.

**Ziel:** Satz S3. Ersetzt 12.7, 12.8 und 12.9 für native Pakete (#418, #419, #420).

| Bahn | Inhalt |
| --- | --- |
| Threads | `clone`/`clone3`, `futex`, TLS, Threadende; danach mehrere CPUs je Prozess mit TLB-Abgleich per IPI |
| Ereignisse | `poll`, `epoll`, `eventfd`, `timerfd` auf einer gemeinsamen Wartemechanik |
| Prozesse | `fork` (Kopie beim Schreiben), `vfork`, `execve`, `wait4`, Signale |
| IPC | `AF_UNIX`, `SCM_RIGHTS`, Versiegelung, geteilte Abbildungen |
| Netz | `AF_INET` auf dem vorhandenen TCP/UDP-Stack, DNS über musl |

Threads und Ereignisse zuerst, danach Prozesse und IPC nebeneinander; Netz nach Ereignissen. **Abnahme:** Tor N4-Z. **Risiko:** Deskriptor-Lebensdauer über Prozesse (Zyklen durch übergebene Sockets), verlorene Weckrufe bei `futex`, Threadgrenze 256 in Cosmos.
