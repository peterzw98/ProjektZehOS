# Risiko-Review Ladybird: Übersicht

Prüfstand aller fünf Berichte: `story/ladybird` 3baf7cb0. Zeilenangaben gelten für diesen Stand. Die Bewertungen stammen aus den
Berichten; Einzelheiten, Grenzen und die P3-Befunde stehen dort.

| Block | Bericht | P1 | P2 | P3 |
| --- | --- | --- | --- | --- |
| 1 | [Speicher und Seitenfehler](2026-11-27-ladybird-risk-1-speicher.md) | 1 | 1 | 9 |
| 2 | [SMP-Rennen und Signale](2026-11-27-ladybird-risk-2-smp-signale.md) | 0 | 1 | 11 |
| 3 | [Syscall-Schicht und Zeigerprüfung](2026-11-27-ladybird-risk-3-syscalls.md) | 1 | 3 | 9 |
| 4 | [Dateisystem und Rechte](2026-11-27-ladybird-risk-4-dateisystem.md) | 0 | 0 | 8 |
| 5 | [Demo-Ablauf](2026-11-27-ladybird-risk-5-demo.md) | 0 | 3 | 12 |

## P1 und P2

"Hostprobe" heißt per Wegwerftest auf dem Host belegt, "nicht nachgestellt" heißt vom Review weder im Hosttest noch im Gast ausgeführt.
Die Zuständigkeit folgt der Festlegung des Koordinators. Die letzte Spalte nennt Commits, die nach dem Prüfstand bis 3a06a154 in
`story/ladybird` liegen; ob sie den Befund vollständig beheben, wurde für diese Übersicht nicht nachgeprüft.

| Befund | Block | Datei:Zeile | Kurz | Beleg | Zuständig | Seit 3baf7cb0 |
| --- | --- | --- | --- | --- | --- | --- |
| P1-1 | 1 | `CosmosLinuxRun.cs:33`, `LinuxUserProcess.cs:1390`, `LinuxMemFile.cs:697` | keine Gesamtgrenze für Nutzer-Frames, Kernel-Halt | nicht nachgestellt; Gastlauf eines anderen Agenten | offen (laut Bericht bekannt) | |
| P1-1 | 3 | `LinuxSyscallsInput.cs:95-109`, `:113-121` | `readv`: hintere iovec-Einträge ungekürzt und ungeprüft | Hostprobe (R1, R2) | engineer | d3838755 |
| P2-1 | 1 | `CosmosLinuxRun.cs:821-822` | V43-Entwurf: Reserve deckt Kernel-Stapel nicht ab | nicht nachgestellt (Vermutung) | offen | |
| P2-1 | 2 | `CosmosLinuxRun.cs:1083-1101` | `rt_sigreturn` über `sysretq` verliert rcx und r11 | nicht nachgestellt (Code und Assembler) | senior prüft | |
| P2-1 | 3 | `LinuxUnixSocket.cs:250-258`, `LinuxUnixSocket.Inet.cs:368-374` | 0-Byte-Nachrichten füllen die Warteschlange ohne Grenze | SEQPACKET: Hostprobe (R3); UDP: nicht nachgestellt | engineer2 | 8c7e288e |
| P2-2 | 3 | `LinuxSyscallsSocket.cs:282-306`, `LinuxUserProcess.cs:747` | `SCM_RIGHTS`: Dateien unterwegs unbegrenzt, Kreise behalten Frames | nicht nachgestellt (Codelesen) | Kreise: Grenze in [LINUX_ABI.md](../../LINUX_ABI.md); Rest offen | |
| P2-3 | 3 | `LinuxMemFile.cs:697`, `LinuxUserProcessShared.cs:40` | memfd-Frames außerhalb des Prozessbudgets | nicht nachgestellt (Codelesen) | senior (V43) | |
| P2-1 | 5 | `scripts/ladybird-demo-run.sh:42` | jedes laufende QEMU bricht den Livestart ab | nicht nachgestellt | offen | 5e8a8fee |
| P2-2 | 5 | `scripts/ladybird-demo-run.sh:35`, `scripts/linux-root-disk.py:463`, `scripts/test-linux-root-disk-guest.py:53` | Wiederholung mit demselben `-o` scheitert und leert die Protokolle | nicht nachgestellt | offen | 5e8a8fee |
| P2-3 | 5 | `scripts/ladybird-demo-run.sh:20` (`TIMEOUT=600`), Demo-Plan Z. 29-31, 59-62 | Hänger wird erst nach 600 s abgebrochen | nicht nachgestellt | offen | 5e8a8fee |

Die `SCM_RIGHTS`-Grenze in `LINUX_ABI.md` nennt den Kreis; laut Bericht 3 fehlt dort die Folge für die physischen Frames.
