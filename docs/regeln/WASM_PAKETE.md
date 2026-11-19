# Wasm-Apps und Pakete

Ausgelagert aus `AGENTS.md`, damit nicht jede Sitzung diese Regeln mitlädt. Sie gelten unverändert; bei Widerspruch gilt `AGENTS.md`.

- Neue Apps sind Pakete, keine Kernelbestandteile. Für eine App keinen Kernelbuild und keine appbezogenen Buildflags einführen; fehlende Fähigkeiten als allgemeine Hostfunktion des Loaders ergänzen.
- Die Fähigkeiten und Grenzen in `docs/PAKETE_ERSTELLEN.md` sind der Vertrag mit App-Entwicklern. Neue Hostfunktionen, Imports oder Limits dort und in `docs/PACKAGE_FORMAT.md` nachtragen.
- Hostfunktionen prüfen jeden Gastzeiger und jede Länge überlaufsicher. Ausnahmen dürfen die native Grenze (`UnmanagedCallersOnly`) nicht überqueren.
- Apps erhalten keine Kernelobjekte, nur Zahlen, Kopien und instanzgebundene Handles. Der Dateizugriff bleibt auf das eigene Paket beschränkt und nur lesend, solange nichts anderes beauftragt ist.
- Den privaten Herausgeberschlüssel nie ins Repository, in Logs, Testfixtures oder auf den Server legen. Tests verwenden eigene Testschlüssel und `-p:ZehOSPackageKeyPath`/`-p:ZehOSPackageTestCaPath`; diese gehören nie in normale Builds.
- Im Branch `packages` veröffentlichte Versionen sind unveränderlich. Änderungen brauchen eine neue Version und eine höhere Katalog-Generation. Änderungen am Branch `packages` laufen nur per Pull Request (Branchregel); der Merge veröffentlicht sofort und erfolgt nur auf ausdrücklichen Auftrag.
- Lizenzen der Paketinhalte beachten: Lizenztexte ins Paket legen, bei GPL die Quellen öffentlich erreichbar halten. GPL-Code nicht in den Kernel übernehmen.
