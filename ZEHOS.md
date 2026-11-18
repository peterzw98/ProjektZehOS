# Cosmos-Quelle für ZehOS

Dieser Branch enthält den Quellbaum von Cosmos Gen3, aus dem ZehOS seine Cosmos-Pakete und die Werkzeuge `cosmos` und
`cosmos.patcher` baut. Er teilt keine Historie mit `master`.

## Grundstand

Repository `https://github.com/CosmosOS/Cosmos`, Commit `efcf20cc62884b042a0adadffe9fc4540491fd89`
(„Bump version to 3.0.97“). Das ist der Quellstand der NuGet-Pakete 3.0.97.

Abweichungen vom Upstream-Baum im ersten Commit:

| Pfad | Änderung | Grund |
| --- | --- | --- |
| `.github/` | entfernt | Cosmos' Workflows, Dependabot-Konfiguration und Vorlagen sollen in diesem Repository nicht gelten |
| `.mcp.json` | entfernt | startete für Agenten in diesem Baum einen fremden MCP-Server per `npx` |
| `.claude/`, `.taskmaster/` | entfernt | Agenten- und Aufgabenkonfiguration des Cosmos-Projekts |
| `CLAUDE.md` | ersetzt | die Regeln für Agenten in diesem Branch sind die von ZehOS |
| `ZEHOS.md`, `PATCHES.md`, `.github/CODEOWNERS` | neu | Beschreibung, Patchliste, Zuständigkeit |

Alles andere ist unverändert, auch die beiden Submodul-Verweise (`dotnet/runtime`,
`src/Cosmos.Kernel.Native.MultiArch/ACPI/uacpi`); der Bau braucht sie.

## Regeln

- Änderungen kommen nur per Pull Request gegen diesen Branch.
- Jeder eigene Patch wird per Squash genau ein Commit und hat eine eigene Datei `patches/<Kurzname>.md`.
- Ein Patch behebt einen Fehler oder öffnet eine Schnittstelle. ZehOS-Policy (Scheduler, Topologie, Appregeln) gehört
  nicht hierher.
- Übernommene Upstream-Stände werden unverändert eingespielt und in `PATCHES.md` mit Branch und Commit vermerkt.
- Nichts aus diesem Branch wird an CosmosOS gemeldet oder dort eingereicht: keine Issues, keine Pull Requests, keine
  Kommentare.
- Geprüft wird ein Patch durch den ZehOS-Pull-Request auf `master`, der ihn einbindet und den zugehörigen Umweg entfernt.

Der Plan dazu liegt auf `master` unter `docs/superpowers/plans/2026-11-18-multicore.md`.

## Bauen

Nicht mit `.devcontainer/postCreateCommand.sh` oder `.ps1`: Diese Skripte löschen den NuGet-Cache für `cosmos.*`,
ändern die NuGet-Konfiguration des Benutzers und installieren die Werkzeuge ohne Versionsangabe. ZehOS baut mit
`scripts/build-cosmos.sh` auf `master` (Etappe 1 des Plans).

Unter Windows braucht eine Arbeitskopie `git config core.longpaths true`.

## Lizenz

Cosmos steht unter der BSD-3-Clause-Lizenz (`LICENSE`, Copyright CosmosOS). Der Lizenztext bleibt im Baum und wird
mit jedem Abbild weitergegeben. Eigene Änderungen sind im Code gekennzeichnet und stehen, bis die Rechteinhaber von
ZehOS anders entscheiden, wie ZehOS unter GPL-3.0.
