# Ladybird N4: Threads, Prozesse, IPC, Netz (Umriss)

> Umriss. Geschnitten nach Tor N2-Z. Grundlage: [Entwurf, Abschnitte 4 bis 6](../specs/2026-11-24-native-pakete-design.md#6-systemaufrufe); Bedarf: [L2-C](../specs/2026-11-19-ladybird-l2-prozesse.md), [L2-D](../specs/2026-11-19-ladybird-l2-threads.md), Aufrufliste N1-C.

**Ziel:** Satz S3. Ersetzt 12.7, 12.8 und 12.9 für native Pakete (#418, #419, #420).

| Bahn | Inhalt |
| --- | --- |
| Threads | `clone`/`clone3`, `futex`, TLS, Threadende; danach mehrere CPUs je Prozess mit TLB-Abgleich per IPI ([Entwurf N4-SMP](../specs/2026-11-28-ladybird-n4-smp-entwurf.md)) |
| Ereignisse | `poll`, `epoll`, `eventfd`, `timerfd` auf einer gemeinsamen Wartemechanik |
| Prozesse | `fork` (Kopie beim Schreiben), `vfork`, `execve`, `wait4`, Signale |
| IPC | `AF_UNIX`, `SCM_RIGHTS`, Versiegelung, geteilte Abbildungen |
| Netz | `AF_INET` auf dem vorhandenen TCP/UDP-Stack, DNS über musl |

Threads und Ereignisse zuerst, danach Prozesse und IPC nebeneinander; Netz nach Ereignissen. **Abnahme:** Tor N4-Z. **Risiko:** Deskriptor-Lebensdauer über Prozesse (Zyklen durch übergebene Sockets), verlorene Weckrufe bei `futex`, Threadgrenze 256 in Cosmos.

## Stand 28. November 2026

| Paket | Stand auf den Story-Zweigen |
| --- | --- |
| S0 · Monitor | Cosmos-Teststand `a8d2d3897`. Bei 4 CPUs auf KVM und TCG je 221 bestanden, 11 übersprungen, 0 Fehler. |
| S1 · TLB | Cosmos `472d2c50f` mit TLB-Abschuss, erstmals in ZehOS über S2 gepinnt. |
| S2 · CPU-Bindung | ZehOS `d5526ec8` bündelt TLB-Invalidierungen für zusammenhängende Bereiche. `zehos.linuxsmp=1` hebt die Bindung eines Linux-Laufs auf eine CPU auf. Voreinstellung bleibt `0`. |
| S3 · Ring-3-Abbruch | `4a43cde4`, Cosmos `57b69576e`. Kick ermöglicht den zügigen Abbruch rechnender Ring-3-Threads bei fatalen Signalen. |
| S4 · Futex | `7c10a8dd` teilt Futex-Wartende nach Schlüsseln ein und weckt einzeln. Auf Arbeitsstand `8119a79a` unter KVM 30/30 ohne Opt-in und 16/16 auf 4 CPUs mit Opt-in. |
| S5 · Gastproben | `2f495337` erweitert die Standardsätze um `futex_stress` und `n4smp` und ergänzt den SMP-Testschalter. Eine vollständige Abnahme auf dem zusammengeführten Stand steht aus. |
| Signalzustellung am IRQ-Rückweg | `a75cfb8a`, Cosmos-Pin `0fb91b8df`. Signale mit Handler erreichen auch rechnende Ring-3-Threads. Auf Arbeitsstand `4b595fc0` mit diesem Pin KVM 30/30 ohne Opt-in, 15/15 auf 4 CPUs mit Opt-in sowie die gezielte `n4smp`-Probe 5/5. |

N4-Z bleibt offen. Es fehlen die vollständige Gastmatrix und die gemeinsamen Nachweise für Deskriptorübergabe, geteilten Speicher und Fehlerisolation. Die Einzelproben belegen die angegebenen Stände. [N4-SMP-Entwurf](../specs/2026-11-28-ladybird-n4-smp-entwurf.md) · [IRQ-Signalentwurf](../specs/2026-11-28-ladybird-n4-irq-signal-entwurf.md)
