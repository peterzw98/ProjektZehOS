# Patches gegenüber Upstream

## Grundstand

| Repository | Commit | Entspricht | Eingespielt am |
| --- | --- | --- | --- |
| `https://github.com/CosmosOS/Cosmos` | `efcf20cc62884b042a0adadffe9fc4540491fd89` | NuGet-Pakete 3.0.97 | 18. November 2026 |

## Übernommene Upstream-Stände

| Branch | Commit | Basis | Gespiegelt als | Eingespielt am |
| --- | --- | --- | --- | --- |
| `feat/smp-phase5b` (SMP-Stapel, E5-A) | `44952133e7820565552f30b057baa0660bd0a80d` | `efcf20cc62884b042a0adadffe9fc4540491fd89` | `cosmos-upstream/smp-phase5b`, `cosmos-upstream/smp-base` | 19. November 2026 |

Eingespielt als kumulierter Unterschied ohne eigene Änderung, die Upstream-Workflows ausgenommen:

```bash
git diff --binary efcf20cc 44952133 -- . ':(exclude).github/workflows' | git apply --3way --index
```

Konflikte gab es nur in `CLAUDE.md` (Grundstand, ZehOS-Fassung bleibt) und in den Dateien von `ausnahme-hook`; wie jeder
Patch auf diesem Stand angebracht ist, steht in seiner Datei unter „Auf SMP-Stand“. Prüfen, dass außer Grundstand und Patches
nichts abweicht: `git diff --stat 44952133 -- . ':(exclude).github' ':(exclude)PATCHES.md'`.

## Eigene Patches

Jeder Patch hat eine eigene Datei `patches/<Kurzname>.md` mit den Feldern Commit, Dateien, Grund, Entfernter ZehOS-Umweg
und Prüfung. Eine gemeinsame Tabelle führte bei jedem Squash zu Konflikten in allen offenen Patch-PRs, weil alle eine Zeile
an dieselbe Stelle anhängten; getrennte Dateien kollidieren nicht. Die Reihenfolge der Patches ist die Reihenfolge der
Commits im Branch `cosmos` (`git log --first-parent --reverse`). Die Patches #344 bis #346 sind als Merge-Commit eingegangen
(vor der Regel „nur Squash“); ihr Patch ist jeweils der Diff des Merge-Commits gegen den ersten Elternteil.
