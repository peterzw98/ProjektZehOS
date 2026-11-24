# exit-quiet

- **Commit:** Squash des PRs (Hash beim Merge); Basis `cosmos` 0354537f
- **Dateien:** `src/Cosmos.Kernel.Core/Scheduler/SchedulerManager.cs`
- **Grund:** Jeder endende Thread schrieb fünf Zeilen synchron auf die serielle Schnittstelle (`InvokeCurrentThreadStart` und `ExitThread`), drei davon vor der Weckung der Joiner (`OnThreadExitCallback`), eine mit maskierten Interrupts. Gemessen (ZehOS #660, KVM, Diagnose auf `cosmos` fbb5e340, je 5 Läufe, ein Lauf mit kurzer und ein Lauf mit zehnfacher Rechenzeit): Zeit vom letzten Arbeitsende bis zum letzten Join 8 bis 15 ms bei 4 und 21 bis 92 ms bei 8 CPUs; ohne die fünf Zeilen 0 bis 1 ms, bis auf zwei Ausreißer (9 und 6 ms). Die Zeilen stehen jetzt in `TraceExit` mit `[Conditional("DEBUG")]`: im Release-Bau (`scripts/build-cosmos.sh`) entfallen die Aufrufe, im Debug-Bau bleibt der Text. Unverändert bleiben die Ausgaben beim Threadstart, bei einer Ausnahme im Thread und beim Beenden aus fremdem Kontext. Im Release fehlt damit die Meldung `exiting with code`; Code 1 steht weiter als `threw exception` im Log.
- **Entfernter ZehOS-Umweg:** keiner
- **Prüfung:** Die Messung lief mit ersatzlos entfernten Zeilen (lokale Gegenprobe), nicht mit diesem Stand. Offen: Cosmos-Bau in Release und Debug, Testkern Threading, ZehOS nativ, SMP-Probe KVM mit 4 und 8 CPUs (`join_us`), Serial-Log ohne die fünf Zeilen, TCG, VMware.
