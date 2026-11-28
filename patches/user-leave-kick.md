# user-leave-kick

- **Commit:** `task/lb-n4-s3-cosmos` c23cb918b (Basis `story/ladybird-cosmos` 94af77dbd mit S1); Entwurf `docs/superpowers/specs/2026-11-28-ladybird-n4-smp-entwurf.md`, Paket S3
- **Dateien:** `src/Cosmos.Kernel.Core/CPU/UserMode.cs`, `src/Cosmos.Kernel.Core/PublicAPI.Unshipped.txt`, `tests/Kernels/Cosmos.Kernel.Tests.UserMode/Kernel.cs`
- **Grund:** ZehOS N4-SMP: Threads eines Linux-Laufs laufen seit S2 auf mehreren CPUs. `RequestLeave` (exit_group, Absturz eines Geschwisters, Strg+C, Zeitlimit) wirkte bei einem Thread im Ring 3 auf einer anderen CPU erst mit deren nächstem Takt; bis dahin rechnete er im Speicher eines endenden Prozesses weiter.
  - `RequestLeave` schreibt den Zustand 2 jetzt mit `Interlocked.Exchange` (volle Barriere statt `Volatile.Write`) und ruft danach `Kick`.
  - Neu `UserMode.Kick(SchedulerThread)`: volle Barriere, dann nur wenn der Thread in `Enter` ist (`_userKernelRsp != 0`), `Running` ist und seine CPU (`CpuId`, Schnappschuss) eine andere, gültige und online ist: `0xF0` (Reschedule) an sie, Interrupts nur um das Senden maskiert. Sonst nichts. Keine Sperre, keine Allokation; in jedem Kontext, auch maskiert.
  - Wirkung: Der Rückweg des Interrupts in den Ring 3 läuft durch `UserModeX64.PrepareReturn` → `UserMode.Prepare`, das den Zustand 2 sieht und den Thread aus `Enter` führt. Kein neuer Vektor, kein Handler.
  - `CpuId` ohne Sperre (Entwurf Abschnitt 6, offen): `SchedulerThread.CpuId` wird nur beim Umzug unter beiden Warteschlangensperren geschrieben; ein Umzug zwischen Lesen und Senden kostet ein überflüssiges Reschedule auf der alten CPU, die neue CPU nimmt den Thread über `Prepare` wieder auf (gestaffelter Kontext, `PrepareReturn`), und die Barriere vor dem Lesen sorgt dafür, dass sie den Zustand 2 dann sieht.
  - Testschalter `UserMode.TestSkipKick` nur mit `COSMOS_TEST_HOOKS`.
- **Entfernter ZehOS-Umweg:** keiner; ZehOS ruft `RequestLeave` schon an allen Abbruchstellen (`CosmosLinuxRun.cs`) und bekommt die kürzere Latenz ohne Änderung.
- **Grenzen:** Signale mit Handler stellt ZehOS nur beim Rücksprung aus einem Aufruf oder einer Ausnahme zu, nicht beim Rücksprung aus einem Interrupt. `Kick` allein bringt ein Signal deshalb nicht zu einem Thread, der im Ring 3 rechnet, ohne Aufrufe zu machen (gilt auch auf einer CPU); dafür bräuchte `Prepare` einen Zustellpunkt. xAPIC nur (wie `SendIpi`).
- **Prüfung** (MSI, QEMU KVM, Pakete 3.0.97-zehos.0.gc23cb91 = Kernelcode dieses Patches; Folgecommit ändert nur Test und Doku):
  - `UserMode` `bare` und `bare+smp4`: 46 bestanden, 0 Fehler, 2 übersprungen von 48 (übersprungen: Zellen mit mehreren CPUs auf `bare`). Neu `RequestLeave_OtherCpu_BeforeTick` (smp4): Main auf CPU 0, Programm rechnet auf CPU 1 im Ring 3, 200 Runden `RequestLeave`: 0/200 länger als ein halber Takt (Takt 10 ms), längste 2 µs.
  - Gegenprobe Testbau `COSMOS_TEST_DEFINES=COSMOS_TEST_HOOKS` (`TestSkipKick`): ohne Anstoß 200/200 länger als ein halber Takt, längste 7475 µs; danach mit Anstoß 0/200, längste 17 µs; 46/0/2.
  - Offen: TCG, übrige Suiten, ZehOS-Gastlauf mit dem Pin, VMware, Hardware.
