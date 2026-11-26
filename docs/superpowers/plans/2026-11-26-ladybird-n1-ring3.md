# Ladybird N1-A7: Messung Ring 3 (Entwurf, Urteil schreibt Senior)

Paket N1-A7 (#630), Plan `2026-11-24-ladybird-n1-nachweise.md`. **Entwurf der Messung; das Urteil (`trägt` / `trägt nicht` / `offen`) steht hier nicht.**

## Aufbau

- Rechner MSI, QEMU/KVM, `-cpu qemu64,+rdrand,+rdseed`, TSC 2,4996 GHz (vom Kernel gemeldet). Beim Messen lief kein anderer Gast (Last 0,8–1,3).
- Stand „mit Patches“: Cosmos A4 `4027f1c9` (`3.0.97-zehos.0.g4027f1c`) mit dem Branch von #704 (Kopf `a456519c1`) plus ein neuer Fall `sysmed` (dieser Branch).
- Stand „Basis“: `origin/master` `53cba9cd5` mit Pin smp.13 (`6b28fc44`). Unterschied zum Patchstand außer Cosmos: nur der Code der Probe und kleine Haken in `Kernel.cs`/`CosmosBootOptions.cs`, im Normalbau (ohne `-p:ZehOSUserModeProbe`) ohne Wirkung.
- Alle Bauten nativ, `-warnaserror`, 0 Warnungen, 0 Fehler.

## 1. Systemaufruf hin und zurück

Neuer Fall `sysmed` (`probe.S`, `CosmosUserModeProbe.cs`): 100 000 Aufrufe von `0x7A01`, je ein `rdtsc`-Paar im Ring 3 um `syscall`; die Differenz geht **nach** dem Messfenster per `0x7A04` an den Kernel, der sortiert und Median bildet. Davor 100 000 leere `rdtsc`-Paare als Eigenkosten. ISO mit `-p:ZehOSUserModeProbe=true`, `scripts/test-user-mode-probe.py --accel kvm --runs 3`. Ticks (1 Tick = 0,4 ns).

| CPUs | Lauf | leer (Median) | Aufruf Median | netto | p99 | µs netto |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 14 | 392 | 378 | 394 | 0,151 |
| 1 | 2 | 14 | 374 | 360 | 388 | 0,144 |
| 1 | 3 | 14 | 392 | 378 | 394 | 0,151 |
| 4 | 1 | 14 | 392 | 378 | 394 | 0,151 |
| 4 | 2 | 14 | 392 | 378 | 394 | 0,151 |
| 4 | 3 | 14 | 392 | 378 | 394 | 0,151 |

Median über die Läufe: **378 Ticks netto ≈ 0,15 µs** (Regel: ≤ 5 µs). Alle Läufe bestanden alle 19 Fälle (`done failed=0`). Die Schleife `loop` (1 000 000 Aufrufe, Mittelwert) stimmt: 371–384 Mio. Ticks ≈ 371–384 Ticks je Aufruf (inkl. Schleife).

Grenzen: KVM auf dem Host, kein TCG (dort ist Zeit sinnlos); Kosten von `fxsave`/`swapgs` einzeln nicht getrennt (der Aufruf enthält `swapgs` und Rahmenwechsel, `fxsave` nur bei Wechsel auf einen anderen Thread); einzelne Ausreißer (Höchstwert bis 84 000 Ticks, vermutlich Host-Verdrängung) berühren den Median nicht.

## 2. Doom-Bildzeit (Normalbau, KVM, ohne Probe)

`scripts/measure-wasm-frametimes.py --accel kvm --runs 3 --seconds 130` (Doom 1.1.0 aus `origin/packages`, Laufzeit wasm3: das veröffentlichte Paket hat keine AOT-Datei). **Abweichung vom Plan:** 3 statt 5 Läufe (Peters Standard, Auftrag von `agent-nweh-lead1`), und `measure-wasm-frametimes.py` statt `test-wasm-doom-desktop.py --scenario full`, weil nur das Skript eine Bildzeit meldet (`full` prüft Installation und Start, keine Bildrate). Die Millisekunden sind auf 1 ms gerundet und zu grob für 5 %; deshalb zusätzlich die Bildzahl im zweiten 60-s-Fenster (Start und Laden stehen im ersten).

| Stand | Bildzahl 2. Fenster (Läufe 1/2/3) | Median | Bildzeit-Median |
| --- | --- | --- | --- |
| Basis smp.13 | 4066 / 4078 / 4096 | 4078 | 12 ms (alle Läufe) |
| Cosmos A4 | 4079 / 4060 / 4043 | 4060 | 12 ms (alle Läufe) |

Verlust A4 gegen smp.13: (4078 − 4060) / 4078 = **0,44 %** (Regel: ≤ 5 %). Die Streuung innerhalb eines Standes (4066–4096, 4043–4079) ist so groß wie der Unterschied.

Grenzen: nur wasm3, kein AOT-Doom; kein VMware, keine echte Hardware; Aussagekraft nur für die Standardlast von Doom (Timer-Interrupts, kein Ring 3 im Betrieb, die Kosten von `swapgs`/`fxsave` je Interrupt stecken im Unterschied, sind aber nicht einzeln gemessen).

## 3. Power-Befehle

`scripts/test-power-commands.py` (Linux-Gegenstück der `.ps1`, `pwsh` fehlt auf MSI), KVM, 3 Läufe je Stand, Normalbau: A4 3/3, smp.13 3/3 bestanden (je 18 s je Lauf).

## Nicht geprüft

Hosttests (`sysmed` ist nur nativ), TCG, VMware, AOT-Doom, Doom 5 Läufe, einzelne Kosten von `fxsave` und `swapgs` je Interrupt, unabhängiges Review.

## Zu den Regeln des Plans (für das Urteil)

A6-Abnahme: laut Nachtrag in #704 erfüllt (KVM 1+4 je 10/10, TCG je 3/3, Negativ 2/2, `switch`-Gegenprobe rot); hier nicht neu gelaufen. Median ≤ 5 µs: erfüllt (0,15 µs). Doom ≤ 5 % Verlust: erfüllt (0,44 %).
