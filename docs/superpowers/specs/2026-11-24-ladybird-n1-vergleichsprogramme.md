# N1-E: Urteil zu den Vergleichsprogrammen

Stand 27. November 2026 für das [Tor #624](https://github.com/peterzw98/ProjektZehOS/issues/624).
Der [N1-Plan](../plans/2026-11-24-ladybird-n1-nachweise.md#gruppe-e-vergleichsprogramme)
verlangt die Linux-Referenzläufe und dokumentierte Aufruflisten für E1 bis E3.

| Paket | Geprüfter Referenzstand | Ergebnis |
| --- | --- | --- |
| E1, Satz S1 | Linux `6.12.90+deb13.1-amd64`, musl 1.2.5, Kopf `895a4b79d` | 12 Programme, fünf gleiche Läufe, 11 Vergleichertests grün. 39 verschiedene Aufrufe, Nachträge zu S1 dokumentiert. [PR #667](https://github.com/peterzw98/ProjektZehOS/pull/667) · [S1-Aufrufe](../../../experiments/LinuxAbi/README.md#aufrufe-in-s1) |
| E2, Satz S3 | Linux `6.12.90+deb13.1-amd64`, musl 1.2.5, Kopf `0cd5d4518` | Neun Programme, 20 aufeinanderfolgende gleiche Läufe und fünf weitere unter CPU-Last. 69 Aufrufe, 26 Ergänzungen im Entwurf dokumentiert. [PR #686](https://github.com/peterzw98/ProjektZehOS/pull/686) · [S3-Aufrufe](../../../experiments/LinuxAbi/README.md#aufrufe-in-s3) |
| E3, Werkzeuge | Linux `6.12.117+deb13-amd64`, toybox 0.8.15 mit musl 1.2.5, Rust 1.100.0 mit dem musl aus `rust-std`, Kopf `5992af6c4` | Neun Programme, zwei gleiche Läufe nach dem Neubau. 49 Aufrufe, zusätzliche Aufrufe und der vDSO-Sonderfall sind dokumentiert. [PR #696](https://github.com/peterzw98/ProjektZehOS/pull/696) · [Werkzeug-README](../../../experiments/LinuxAbi/src/tools/README.md) |

Der [Linux-Vergleicher](../../../experiments/LinuxAbi/README.md) prüft Ausgabe
und Exit-Status gegen die gespeicherten Erwartungen. Beispiel für einen Lauf
mit bereits gebauten Programmen aus Satz S1:

```bash
python3 experiments/LinuxAbi/run-linux.py --bin experiments/LinuxAbi/out/s1 \
  --expected experiments/LinuxAbi/expected/s1 --json /tmp/lb-s1.json
```

**E trägt.** Die drei Linux-Sätze erfüllen die formalen Abnahmeregeln.
Die ergänzende ZehOS-Gastprobe auf `509c37a5` ergab mit KVM je **23/24**
bei 1 und 4 CPUs. `badptr` scheiterte an der fehlenden Unterstützung
symbolischer Links auf FAT. Diese Gastprobe ist ein eigener Programmsatz
und hat eine andere Bezugsgröße als die drei Linux-Sätze oben.
[Gastnachtrag #744](https://github.com/peterzw98/ProjektZehOS/pull/744#issuecomment-7198954061)

Die Linux-Referenzen gelten für die genannten Kernel- und Werkzeugstände.
TCG, VMware und echte Hardware stehen für diesen Gastvergleich aus.
