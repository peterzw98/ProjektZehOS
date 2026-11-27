# user-frame-reserve

- **Commit:** wird beim Squash gesetzt (Entwurf auf `story/ladybird-cosmos` 63e672db)
- **Dateien:** `src/Cosmos.Kernel.Core/Memory/PageAllocator.cs` (`AllocPages`, Parameter `aKeepFree`), `src/Cosmos.Kernel.Core/Memory/UserPageTable.cs` (`ReservedPages`, `TotalPages`, `FreePages`, `AllocateFrame`), `src/Cosmos.Kernel.Core/PublicAPI.Unshipped.txt`
- **Grund:** ZehOS Story Ladybird V43. `UserPageTable.AllocateFrame` nahm Nutzer-Frames ohne Grenze aus demselben `PageAllocator` wie der Kernel-Heap. Füllte ein Programm den Speicher (Ladybird im 512-MB-Gast), fand der nächste Heap-Zuwachs keine Seite mehr, und der Kernel hielt mit `OutOfMemoryException` an. Jetzt lässt `AllocateFrame` die letzten `ReservedPages` Seiten frei (Prüfung unter der RAT-Sperre in `AllocPages`, also ohne Wettlauf zwischen CPUs); es liefert dann 0 wie ohne Speicher, und das Programm scheitert allein. Jede andere Zuteilung (Heap, Tabellen, Stapel) darf die Reserve weiter nutzen. Den Wert setzt die Policy des Systems (ZehOS), Vorgabe 0 = Verhalten wie bisher. `TotalPages` und `FreePages` erlauben der Policy, die Reserve nach der Speichergröße zu bemessen.
- **Grenzen:** Die Reserve schützt nur vor Nutzer-Frames; wächst der Kernel-Heap selbst über sie hinaus, hält der Kernel weiter an. Tabellenseiten von `TryMap` zählen nicht gegen sie. `FreePageCount` zählt freie Seiten, nicht zusammenhängende Läufe; eine zerstückelte Reserve hilft großen Läufen nicht.
- **Entfernter ZehOS-Umweg:** keiner
- **Prüfung:** siehe PR
