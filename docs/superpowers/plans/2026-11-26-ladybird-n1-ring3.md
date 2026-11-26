# Ladybird N1-A7: Messung Ring 3 (Entwurf)

Paket N1-A7 (#630), Plan `2026-11-24-ladybird-n1-nachweise.md`. Messung von `agent-peter-engineer`, Urteil von `agent-peter-senior` (Abschnitt „Urteil“ am Ende).

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

Grenzen: KVM auf dem Host, kein TCG (dort ist Zeit sinnlos); Kosten von `fxsave`/`swapgs` einzeln nicht getrennt (der Aufruf enthält beide: der Stub im Pin `4027f1c9` führt `fxsave64`/`fxrstor64` bei **jedem** Interrupt und Systemaufruf aus, `Interrupts.s` Z. 804/876, `swapgs` nur bei Eintritt aus Ring 3, Z. 669–674); einzelne Ausreißer (Höchstwert bis 84 000 Ticks, vermutlich Host-Verdrängung) berühren den Median nicht.

## 2. Doom-Bildzeit (Normalbau, KVM, ohne Probe)

`scripts/measure-wasm-frametimes.py --accel kvm --runs 3 --seconds 130` (Doom 1.1.0 aus `origin/packages`, Laufzeit wasm3: das veröffentlichte Paket hat keine AOT-Datei). **Abweichung vom Plan:** 3 statt 5 Läufe (Peters Standard, Auftrag von `agent-nweh-lead1`), und `measure-wasm-frametimes.py` statt `test-wasm-doom-desktop.py --scenario full`, weil nur das Skript eine Bildzeit meldet (`full` prüft Installation und Start, keine Bildrate). Die Millisekunden sind auf 1 ms gerundet und zu grob für 5 %; deshalb zusätzlich die Bildzahl im zweiten 60-s-Fenster (Start und Laden stehen im ersten).

| Stand | Bildzahl 2. Fenster (Läufe 1/2/3) | Median | Bildzeit-Median |
| --- | --- | --- | --- |
| Basis smp.13 | 4066 / 4078 / 4096 | 4078 | 12 ms (alle Läufe) |
| Cosmos A4 | 4079 / 4060 / 4043 | 4060 | 12 ms (alle Läufe) |

Verlust A4 gegen smp.13: (4078 − 4060) / 4078 = **0,44 %** (Regel: ≤ 5 %). Die Streuung innerhalb eines Standes (4066–4096, 4043–4079) ist so groß wie der Unterschied.

Grenzen: nur wasm3, kein AOT-Doom; kein VMware, keine echte Hardware; Aussagekraft nur für die Standardlast von Doom (Timer-Interrupts, kein Ring 3 im Betrieb, die Kosten von `fxsave64`/`fxrstor64` je Interrupt stecken im Unterschied, sind aber nicht einzeln gemessen; `swapgs` läuft bei Doom nicht, weil Doom im Ring 0 läuft).

### 2a. Doom mit AOT (Nachtrag 26.11.)

Gleiches Skript, `--aot on` (Laufzeit im Protokoll: `aot wamr245-aot5-x64-bc-nrz`), Doom 1.1.0 aus `origin/packages` mit der AOT-Datei aus `aot-out/doom.aot` (Beleg-WASM-Hash `b0e47bb1…` stimmt mit dem Paket überein), lokaler Testschlüssel, je Stand ein eigenes ISO mit `-p:ZehOSPackageKeyPath`, 3 Läufe, KVM, MSI allein. Stände: A4 = Kopf #711 (`72bd9687a`, Cosmos-Pin 4027f1c9; #704 steht inzwischen auf `5802b0abe`, dessen Unterschied liegt nur in `scripts/`), Basis smp.13 = `53cba9cd5`.

| Stand | Bildzahl 2. Fenster (Läufe 1/2/3) | Summe | avg fps | Bildzeit-Median |
| --- | --- | --- | --- | --- |
| Basis smp.13 | 4201 / 4195 / 4198 | 12594 | 70,0 | 11 ms |
| Cosmos A4 | 4198 / 4200 / 4200 | 12598 | 70,0 | 11-12 ms |

Unterschied +0,03 % (A4 nicht langsamer). Grenze: Doom läuft hier mit rund 70 Bildern/s an einer Obergrenze (Bildzahl fast konstant); die Messung zeigt „kein Verlust“, ist aber kein Test der Reserve.

## 3. Power-Befehle

`scripts/test-power-commands.py` (Linux-Gegenstück der `.ps1`, `pwsh` fehlt auf MSI), KVM, 3 Läufe je Stand, Normalbau: A4 3/3, smp.13 3/3 bestanden (je 18 s je Lauf).

## Nicht geprüft

Hosttests (`sysmed` ist nur nativ), TCG, VMware, Doom 5 Läufe, einzelne Kosten von `fxsave` und `swapgs` je Interrupt, unabhängiges Review.

## Zu den Regeln des Plans (für das Urteil)

A6-Abnahme: laut Nachtrag in #704 erfüllt (KVM 1+4 je 10/10, TCG je 3/3, Negativ 2/2, `switch`-Gegenprobe rot); hier nicht neu gelaufen. Median ≤ 5 µs: erfüllt (0,15 µs). Doom ≤ 5 % Verlust: erfüllt (wasm3 0,44 %, AOT +0,03 %).

## Urteil (Senior, 26.11.2026)

**`trägt`.** Grundlage ist dieser Bericht (Kopf `b332f61d6`) und die Abnahme von #704; ich habe keinen eigenen Gastlauf gemacht.

Die Regel aus #630 im Einzelnen:

1. **A6 erfüllt:** ja. Die Abnahme in #704 (Pin `4027f1c9`) zeigt KVM 1+4 CPUs je 10/10, TCG `-cpu max` 1+4 je 3/3, Negativfälle 2/2 und beide `switch`-Mutanten rot. Diese Läufe habe ich selbst gemacht, die Zahlen stehen in den Kommentaren von #704. Das Review von #704 hat kein P1 gefunden. Die Nachprüfung des Deltas `5802b0abe` steht noch aus, das Delta betrifft nur `scripts/` und Tests.
2. **Median ≤ 5 µs:** ja, mit großem Abstand. Netto 378 Ticks ≈ 0,15 µs, also rund 1/30 der Grenze, bei 1 und 4 CPUs gleich und über 3 Läufe stabil (360–378). Die Abweichungen der Methode (`rdtsc` ohne `lfence`, P3 aus dem Review) liegen unter 30 Ticks und ändern daran nichts.
3. **Doom verliert ≤ 5 %:** gemessen ja, nämlich −0,44 % mit wasm3 und +0,03 % mit AOT. **Diese Messung ist aber wenig empfindlich:** AOT läuft mit 70,0 Bildern/s an einer Obergrenze, und wasm3 liegt mit ≈ 68 Bildern/s (4078 Bilder in 60 s) knapp darunter. Ein Mehraufwand im Kernel kann sich in der Reserve unterhalb der Obergrenze verstecken, statt die Bildzahl zu senken. Die Regel ist deshalb nur bei wasm3 belegt, und dort nur mit rund 3 % Spielraum bis zur Obergrenze, also nicht für die vollen 5 %. Die Abschätzung trägt trotzdem: Der Patch fügt Doom (Ring 0) je Interrupt nur `fxsave64`/`fxrstor64` (512 Byte) und einen `test` auf CS hinzu, zusammen einige hundert Takte. Selbst bei 10 000 Interrupts/s je CPU wären das bei 2,5 GHz rund 0,1 % Rechenzeit, deutlich unter 5 %. Diese Zahl ist geschätzt, nicht gemessen.
4. **`trägt nicht`** würde einen Fall verlangen, der an einer bekannten Ursache in Cosmos oder NativeAOT scheitert und sich nicht durch einen abgegrenzten Patch beheben lässt. Einen solchen Fall gibt es nicht.

**Zum TCG-NMI-Rest aus #700** (verschachtelter NMI, TCG mit 4 CPUs): Er kippt das Urteil nicht. Er ist kein Kriterium der Regel, kein Fall ist an ihm gescheitert (0 Abstürze, die A6-TCG-Läufe sind grün), und er tritt nur unter TCG auf, nicht unter KVM (0/20). Die Ursache ist nicht geklärt: Ob QEMU einen NMI vor dem `iretq` erneut zustellt oder ob der Stub einen Fehler hat, wissen wir nicht. Bis das geklärt ist, gilt er als offenes Risiko von #700 und muss vor dem Merge des Patches im Review von #700 entschieden werden.

**Das Urteil fällt auf `offen` zurück,** wenn eines davon eintritt:

- Ein Review von #700, #704 oder #711 findet ein P1.
- Der NMI-Rest stellt sich als Fehler im Cosmos-Stub heraus, der zu einem Absturz oder zu verlorenem Zustand führt.
- Eine Messung ohne Obergrenze zeigt mehr als 5 % Verlust. Das wäre zum Beispiel Doom ohne Deckel oder ein Lastfall mit einer hohen Interruptrate.

**Nicht belegt bleiben** VMware, echte Hardware, AMD, Messungen mit 5 Läufen und die Kosten von `fxsave64`/`swapgs` einzeln. Für einen späteren Messauftrag empfehle ich eine Last ohne Obergrenze, damit der Verlust direkt messbar wird, statt mehr Läufe von Doom.
