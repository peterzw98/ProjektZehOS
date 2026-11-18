# zehos-ci

- **Commit:** Squash von #<PR>
- **Dateien:** `.github/workflows/cosmos-ci.yml` (neu)
- **Grund:** Patch-PRs gegen `cosmos` hatten keine CI; zwei Patches, die einzeln grün sind, konnten zusammen Tests brechen (P1/P7 mit dem 1-KiB-Formatter, 18. November 2026). Der Workflow läuft nur für Pull Requests gegen `cosmos` auf einem GitHub-Runner und führt `tests/Cosmos.Kernel.Tests.System` aus. Kein Code-Patch an Cosmos.
- **Entfernter ZehOS-Umweg:** keiner
- **Prüfung:** Lauf des Workflows in diesem PR
