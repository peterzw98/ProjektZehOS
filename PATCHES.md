# Patches gegenüber Upstream

## Grundstand

| Repository | Commit | Entspricht | Eingespielt am |
| --- | --- | --- | --- |
| `https://github.com/CosmosOS/Cosmos` | `efcf20cc62884b042a0adadffe9fc4540491fd89` | NuGet-Pakete 3.0.97 | 18. November 2026 |

## Übernommene Upstream-Stände

Noch keine.

## Eigene Patches

Jeder Patch hat eine eigene Datei `patches/<Kurzname>.md` mit den Feldern Commit, Dateien, Grund, Entfernter ZehOS-Umweg
und Prüfung. Eine gemeinsame Tabelle führte bei jedem Squash zu Konflikten in allen offenen Patch-PRs, weil alle eine Zeile
an dieselbe Stelle anhängten; getrennte Dateien kollidieren nicht. Die Reihenfolge der Patches ist die Reihenfolge der
Commits im Branch `cosmos` (`git log --first-parent --reverse`). Die Patches #344 bis #346 sind als Merge-Commit eingegangen
(vor der Regel „nur Squash“); ihr Patch ist jeweils der Diff des Merge-Commits gegen den ersten Elternteil.
