# Cosmos aus dem Branch `cosmos`

ZehOS baut Cosmos nicht aus nuget.org, sondern aus dem Branch `cosmos` dieses Repositorys (Quellstand des Pakets 3.0.97).

- `COMMIT`: voller Commit des Branches `cosmos`, aus dem gebaut wird.
- `VERSION`: Versionsnummer der selbst gebauten Pakete (`3.0.97-zehos.N`).
- `feed/`: lokaler NuGet-Feed, vom Skript gefüllt (nicht im Repository).

Regel: **neuer Commit, neue Version.** Beide Dateien ändern sich nur zusammen, und `global.json` (`msbuild-sdks`) nennt dieselbe Version.
Das Skript `scripts/build-cosmos.sh` verweigert dieselbe Version mit anderem Commit.

Bauen: `scripts/build-cosmos.sh` (Voraussetzung: .NET SDK ab 10.0.201, git, Netz). Die Werkzeuge `cosmos` und `cosmos.patcher`
liegen danach je Arbeitskopie unter `.tools/cosmos-tools/<version>`; diesen Ordner vor den Kernelbau in `PATH` setzen.
Mit `--global` installiert das Skript sie global (CI, frischer Rechner; nötig für F5 in Visual Studio).
Der Quellbaum liegt unter `.tools/cosmos-src`; das Skript schreibt nur unter `.tools/`, `deploy/cosmos/feed/` und installiert die Werkzeuge.

## SDK

`deploy/cosmos/SDK` nennt das .NET-SDK, mit dem der Referenzbau entsteht (derzeit 10.0.404). `scripts/build-cosmos.sh` bricht
bei einem anderen SDK ab; `--any-sdk` ist nur fuer lokale Versuche gedacht, solche Feeds nie fuer Release oder IL-Vergleich.

## Grenzen

- Die Versionsprüfung von `cosmos.patcher` (`ZehOSCheckPatcherVersion`) läuft nur unter Unix; unter Windows ruft das SDK
  `$(CosmosPatcherExe)` und die Prüfung entfällt. Ein altes globales Werkzeug dort fällt nicht auf: nach einem Versionswechsel
  `scripts/build-cosmos.sh --global` neu ausführen.
- Das Skript prüft vor dem Bau, ob der NuGet-Cache des Rechners dieselbe Version aus einem anderen Commit enthält, und bricht
  dann mit „VERSION erhöhen“ ab; nach dem Bau muss die nuspec von `Cosmos.Kernel` den Commit tragen.
- Cosmos-Pakete (`Cosmos.*`, `_Cosmos.*`) stammen laut `NuGet.Config` ausschließlich aus `deploy/cosmos/feed`.
