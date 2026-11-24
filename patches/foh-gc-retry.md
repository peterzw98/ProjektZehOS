# foh-gc-retry

- **Commit:** Entwurf, Branch `cosmos-patch/foh-gc-retry`
- **Dateien:** `src/Cosmos.Kernel.Core/Memory/Heap/Heap.cs`, `src/Cosmos.Kernel.Plugs/Internal/Runtime/FrozenObjectHeapManagerPlug.cs`, `tests/Kernels/Cosmos.Kernel.Tests.GarbageCollector/Kernel.cs`
- **Grund:** Der GC sammelt erst, wenn der verwaltete Heap nicht mehr wachsen kann. `FrozenObjectHeapManager` holt sein Segment (4 MiB, 1025 zusammenhängende Seiten) mit `Heap.Alloc`; sind fast alle Seiten von Segmenten mit toten Objekten belegt, gibt es `null` und die Laufzeit wirft `OutOfMemoryException` (ZehOS #564: `Enum.ToString` in `tasks list` beendet den Hauptthread). `Heap.AllocOrCollect` sammelt einmal außerhalb der Heap-Sperre und versucht es erneut, wie `SchedulerThread.InitializeStack`. Gegen Fragmentierung hilft das nicht.
- **Entfernter ZehOS-Umweg:** keiner
- **Prüfung:** siehe Bericht im ZehOS-PR #564; ohne Eintrag dort ungeprüft.
