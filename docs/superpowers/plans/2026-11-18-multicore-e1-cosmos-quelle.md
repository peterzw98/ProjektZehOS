# Etappe 1: Cosmos aus Quelle bauen, unverändert

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-18-multicore.md) lesen. Empfohlene Arbeitsweise:
> superpowers:executing-plans. Schritte haben Kästchen zum Abhaken.

**Ziel:** ZehOS bezieht die Cosmos-Pakete und die Werkzeuge `cosmos` und `cosmos.patcher` nicht mehr von nuget.org,
sondern baut sie aus dem Branch `cosmos` des eigenen Repositorys. Der Quellstand ist genau der des Pakets 3.0.97
(Commit `efcf20cc62884b042a0adadffe9fc4540491fd89`); am Verhalten ändert sich nichts.

**Architektur:** Ein Branch ohne gemeinsame Historie mit `master` hält den Cosmos-Quellbaum. Ein eigenes Bauskript
packt daraus die x64-Pakete mit eigener Versionsnummer in einen lokalen NuGet-Feed und installiert die beiden
Werkzeuge daraus. `master` nennt Commit und Version in zwei Dateien.

**Voraussetzung:** Peters Freigabe, den Branch `cosmos` anzulegen und zu schützen (Paket E1-A).

**Entwurf:** [Abschnitt 7](../specs/2026-11-18-multicore-design.md).

## Belegte Fakten, auf denen diese Etappe steht

Gelesen am 18. November 2026 in der Cosmos-Quelle `efcf20c` und in ZehOS `origin/master` (`6348c7a`). Zeilenangaben
gelten für diese Stände; vor dem Ändern die Stelle über den zitierten Text suchen.

- **Version:** Einzige Quelle ist `$(VersionPrefix)` (`Directory.Build.props:37-61`); ohne Vorgabe kommt sie aus `global.json` (`msbuild-sdks.Cosmos.Sdk`). Als Umgebungsvariable gesetzt, gilt sie für alle Pakete und für die in `Cosmos.Sdk` eingebackenen Versionen. `-p:VersionSuffix` ist ungeeignet (das SDK zöge dann die Originalpakete nach). Die Assemblyversion bleibt `3.0.97.0`.
- **Submodule:** `dotnet/runtime` (Commit `4271d88e0aebf3d04f188f1334c2220d80555ef6`) und `src/Cosmos.Kernel.Native.MultiArch/ACPI/uacpi` (Commit `686a9a52d9ad72690406d7cada82801377d5e2fa`) sind Pflicht; `Cosmos.Kernel.Core` übersetzt Quellen aus `dotnet/runtime` mit.
- **Limine:** Upstreams Skript klont den Branch `v10.x-binary` ohne festen Stand nach `artifacts/limine`. Das Paket 3.0.97 enthält Limine 10.8.5; SHA-256 von `BOOTX64.EFI`: `91ef81c1f95c4ac8bf77c4393a0b2fcd2628550827d7abf89a43194400e17a85`. Den Stand gibt es als Tag `v10.8.5-binary`.
- **Upstreams Bauskript ist nicht verwendbar:** `.devcontainer/postCreateCommand.sh` löscht `~/.nuget/packages/cosmos.*`, ändert die NuGet-Konfiguration des Benutzers, schreibt `global.json` um und installiert die Werkzeuge ohne Versionsangabe. Die Windows-Fassung lässt zwei Schritte aus. ZehOS bekommt deshalb ein eigenes Skript mit denselben Bauschritten ohne diese Nebenwirkungen.
- **Werkzeuge:** Der Kernelbuild ruft den Patcher als globales Werkzeug auf (`cosmos.patcher` im Pfad, unter Windows `%USERPROFILE%\.dotnet\tools`). `cosmos build` reicht keine Cosmos-Version weiter. `cosmos install --auto --tools` lädt nur clang, lld, xorriso, qemu und gdb und hängt nicht an der Paketversion. **Ohne** `--tools` ersetzte es den Patcher durch den von nuget.org: nie so aufrufen.
- **Zum Packen** braucht es nur das .NET SDK ab 10.0.201 (Cosmos `global.json`), git und Netz; native Teile werden erst beim Kernelbuild übersetzt. Die CI nutzt `10.0.x` (`ci.yml:66`), das genügt.
- **Buildpfade:** Die veröffentlichten DLLs enthalten Pfade unter `/home/runner/work/Cosmos/Cosmos/`; `scripts/check-release-paths.py:12-27` erlaubt genau diese. Bildet der eigene Bau seinen Quellordner auf denselben Pfad ab, bleibt die Prüfung unverändert gültig.
- **ZehOS hat** keine `NuGet.Config`, keine `Directory.Packages.props` und keinen Cache-Schritt in der CI.

## Feste Stellen in ZehOS, die an `3.0.97` oder nuget.org hängen

| Datei (Stand `6348c7a`) | Inhalt | Wird |
| --- | --- | --- |
| `ProjektZeh/ProjektZeh.csproj:1` | `Sdk="Cosmos.Sdk/3.0.97"` | `Sdk="Cosmos.Sdk"`, Version aus `global.json` |
| `ProjektZeh/ProjektZeh.csproj:131-132` | zwei `PackageReference` mit `Version="3.0.97"` | `Version="[$(ZehOSCosmosVersion)]"` |
| `global.json` | kein `msbuild-sdks` | `"msbuild-sdks": { "Cosmos.Sdk": "<VERSION>" }` |
| `ProjektZeh.Tests/ProjektZeh.Tests.csproj:17`, `ScreenshotSupport.cs:17-19`, `Golden.cs:25` | Schrift aus `Cosmos.Kernel.System` 3.0.97 im NuGet-Cache | **bleibt** bei 3.0.97 von nuget.org (siehe unten) |
| `scripts/build-release.sh:21-25` | Limine aus `cosmos.build.common/3.0.97` im NuGet-Cache | Version aus `deploy/cosmos/VERSION` |
| `scripts/tests/test_release_tools.py:106` | derselbe Pfad im Test | wie das Skript |
| `.github/workflows/ci.yml:219-229`, `:279-289`, `:415-425` | `dotnet tool install … --version 3.0.97` in drei Jobs | gemeinsamer Schritt aus E1-C |
| `.github/workflows/release.yml:32-40` | dasselbe im Release | gemeinsamer Schritt aus E1-C |
| `experiments/StorageProbe/StorageProbe.csproj:8-16` | DLL-Pfade und `PackageDownload` mit `3.0.97` | Version als Eigenschaft (E1-F) |
| `README.md:237`, `:254-259`, `:273-275`, `:402`; `AGENTS.md:87`, `:89` | Bauanleitung und Regeln | E1-E |

Die Hosttests bleiben bewusst bei der Schrift aus dem Originalpaket 3.0.97: So brauchen die Jobs „Shell tests“ unter
Linux und Windows keinen Cosmos-Bau. Ändert ein späterer Patch die Schrift, ist das dort eigens zu behandeln.

## Pakete

| Paket | Inhalt | Rolle | Nach | Neben |
| --- | --- | --- | --- | --- |
| E1-A | Branch `cosmos` anlegen | Engineer, mit Peter für Freigabe und Branchregel | Freigabe | keine |
| E1-B | Bauskript, Versionsdateien, Umstellung des Kernelprojekts | Engineer senior | E1-A | E1-F |
| E1-C | CI und Release-Workflow | Agent eines Benutzers mit Workflow-Recht | E1-B | E1-E |
| E1-D | Gleichstandsprüfung | Clerk | E1-C | E1-E |
| E1-E | README, `AGENTS.md`, `docs/COSMOS_SUPPORT.md` | Engineer | E1-B | E1-C, E1-D |
| E1-F | `experiments/StorageProbe` auf die Versionseigenschaft umstellen | Engineer junior | E1-B | alle |

Workflow-Dateien kann das Token der Team-Agenten nicht pushen; E1-C übernimmt deshalb der Agent eines Benutzers.

## Review-Schwerpunkte

1. Der Feed enthält die gewünschte Version nicht, und NuGet nimmt still das Original 3.0.97 von nuget.org (E1-B: genaue Versionsklammern und `NU1603` als Fehler; Test in Schritt 9).
2. Zwei Commits werden unter derselben Versionsnummer gebaut, der NuGet-Cache liefert den alten Stand (E1-B: Stempelprüfung im Skript; Test in Schritt 3).
3. Auf einem Rechner mit mehreren Arbeitskopien ist der global installierte Patcher der eines anderen Standes (E1-B: Skript installiert immer die Version aus `VERSION`; Hinweis in der README).
4. Das Skript berührt den NuGet-Cache oder die NuGet-Konfiguration des Benutzers (E1-B: Review; es darf nur unter `.tools/` schreiben und die zwei Werkzeuge installieren).
5. Lokale Pfade oder der Pfad des CI-Arbeitsordners geraten in die DLLs (E1-D: `check-release-paths.py`).

---

### Paket E1-A: Branch `cosmos` anlegen

**Voraussetzung:** Peter gibt frei und richtet für `cosmos` dieselbe Branchregel ein wie für `packages` (Änderungen nur per Pull Request).

**Inhalt des ersten Commits:** der Dateibaum von `efcf20c` einschließlich der Submodul-Verweise, **ohne** den Ordner
`.github` (sonst liefen Cosmos' Workflows in unserem Repository, und Cosmos' `CODEOWNERS`-Regeln gälten für unsere
PRs), dazu drei eigene Dateien.

- [ ] **Schritt 1:** Arbeitsbaum außerhalb der ZehOS-Arbeitskopie: `git config core.longpaths true` (Windows), dann `git fetch --depth 1 https://github.com/CosmosOS/Cosmos efcf20cc62884b042a0adadffe9fc4540491fd89` und `git worktree add --detach ../zehos-cosmos FETCH_HEAD`.
- [ ] **Schritt 2:** Prüfen, ob der Bau etwas aus `.github` braucht: `grep -rn "\.github" --include=*.csproj --include=*.props --include=*.targets --include=Makefile .` im neuen Arbeitsbaum. Erwartet: keine Treffer außerhalb von `.github` und `docs`. Bei Treffern nur `.github/workflows`, `.github/dependabot.yml`, `.github/ISSUE_TEMPLATE` entfernen und den Rest behalten.
- [ ] **Schritt 3:** `git checkout --orphan cosmos`, `git rm -r -q --cached .github` und den Ordner löschen.
- [ ] **Schritt 4:** Drei Dateien im Stamm anlegen:
  - `ZEHOS.md`: Was dieser Branch ist (Cosmos-Quelle für ZehOS, Grundstand `efcf20c` = Paket 3.0.97), dass er keine Historie mit `master` teilt, dass Änderungen nur per PR kommen und in `PATCHES.md` stehen, dass nichts davon an CosmosOS gemeldet oder dort eingereicht wird, und der Lizenzhinweis: Cosmos steht unter BSD 3-Clause (`LICENSE`); eigene Änderungen stehen wie ZehOS unter GPL-3.0.
  - `PATCHES.md`: Überschrift, Abschnitt „Grundstand“ (Repository, Commit, Datum), leere Tabelle `| Kurzname | Commit | Dateien | Grund | Entfernter ZehOS-Umweg | Prüfung |`.
  - `.github/CODEOWNERS` mit `* @peterzw98 @larsernst`.
- [ ] **Schritt 5:** `git add -A`, Commit `Cosmos 3.0.97 (efcf20c) als Grundstand`. Prüfen: `git diff --stat efcf20cc HEAD` nennt nur die entfernten Dateien unter `.github` und die drei neuen; `git ls-tree HEAD dotnet/runtime src/Cosmos.Kernel.Native.MultiArch/ACPI/uacpi` zeigt beide Einträge als `160000 commit` mit den Commits von oben.
- [ ] **Schritt 6:** `git push origin cosmos`. Danach Peter um die Branchregel bitten und im Kanal den Commit melden.

---

### Paket E1-B: Bauskript und Umstellung

**Branch:** `task/mc-e1-b-cosmos-bau`

**Dateien:**
- Anlegen: `deploy/cosmos/COMMIT` (Commit aus E1-A), `deploy/cosmos/VERSION` (`3.0.97-zehos.1`), `deploy/cosmos/README.md` (zehn Zeilen: Zweck der beiden Dateien, Regel „neuer Commit, neue Version“)
- Anlegen: `deploy/cosmos/feed/.gitignore` mit den Zeilen `*` und `!.gitignore`. Der Ordner muss in jeder Arbeitskopie vorhanden sein: Eine fehlende lokale Paketquelle ließe auch `dotnet restore` des Testprojekts scheitern, ein leerer Ordner nicht.
- Anlegen: `scripts/build-cosmos.sh`, Test `scripts/tests/test_build_cosmos.py`
- Anlegen: `NuGet.Config` im Stamm
- Ändern: `global.json`, `Directory.Build.props`, `ProjektZeh/ProjektZeh.csproj:1` und `:131-132`
- Ändern: `scripts/build-release.sh:21-25`, `scripts/tests/test_release_tools.py:106`

**Schnittstelle des Skripts (liefert):**

```
scripts/build-cosmos.sh [--commit <sha>] [--version <version>] [--feed <ordner>] [--no-tools] [--print-plan]

  ohne Argumente: Commit aus deploy/cosmos/COMMIT, Version aus deploy/cosmos/VERSION,
                  Feed deploy/cosmos/feed, Quellbaum .tools/cosmos-src
  --commit:       anderer Commit (Patch in Arbeit); ohne --version gilt dann <Basis>-zehos.0.g<sha7>
  --no-tools:     Pakete bauen, die globalen Werkzeuge nicht anfassen
  --print-plan:   nur die Befehle ausgeben, nichts ausführen (für den Test)
  Ende 0: Feed enthält alle Pakete dieser Version; Ende ungleich 0 mit einer Zeile Ursache auf stderr.
```

**Was das Skript tut, in dieser Reihenfolge:**

1. Commit und Version bestimmen. Liegt im Feed `<version>.commit` mit einem anderen Commit: Abbruch mit „Version <v> gehört zu Commit <x>; deploy/cosmos/VERSION erhöhen“. Liegt sie mit demselben Commit: Pakete nicht neu bauen, weiter bei Schritt 7.
2. Quellbaum: `git fetch origin <commit>` falls nötig, `git worktree add --detach .tools/cosmos-src <commit>` (vorhandenen Arbeitsbaum auf den Commit setzen), `git -C .tools/cosmos-src submodule update --init --recursive --depth 1`.
3. Limine: `git clone --depth 1 --branch v10.8.5-binary https://github.com/Limine-Bootloader/Limine.git .tools/cosmos-src/artifacts/limine`, `.git` darin löschen, SHA-256 von `BOOTX64.EFI` gegen den Wert oben prüfen; bei Abweichung Abbruch.
4. Umgebung: `VersionPrefix=<version>`; an jedem `dotnet`-Befehl `-c Release -p:PathMap=<absoluter Pfad von .tools/cosmos-src>=/home/runner/work/Cosmos/Cosmos`. `global.json` im Quellbaum wird nicht verändert.
5. Bauen und Packen, nur x64, im Quellbaum, Reihenfolge wie in `.devcontainer/postCreateCommand.sh` (Zeilen dort in Klammern):
   - `dotnet build --no-incremental` für `src/Cosmos.Build.API`, `Cosmos.Build.Common`, `Cosmos.Build.Asm`, `Cosmos.Build.CC`, `Cosmos.Build.Ilc`, `Cosmos.Build.Patcher`, `Cosmos.Build.Analyzer.Patcher.Package`, `Cosmos.Patcher`, `Cosmos.Tools` (68-78)
   - `dotnet pack … -o artifacts/package/release` für `Cosmos.Kernel.Native.X64` und `Cosmos.Kernel.Native.MultiArch` (82, 84)
   - je `dotnet build -p:GeneratePackageOnBuild=false`, dann `dotnet pack --no-build -o artifacts/package/release` für `Cosmos.Kernel.Drivers`, `Cosmos.Kernel.Debug`, `Cosmos.Kernel.Boot.Limine`, `Cosmos.Kernel.SourceGenerators` (94-101), danach für `Cosmos.Kernel.Core.X64` (111-112) und `Cosmos.Kernel.HAL.X64` (118-119)
   - `dotnet build src/Cosmos.Kernel/Cosmos.Kernel.csproj -r linux-x64 -p:CosmosArch=x64 --no-incremental` (138), dann die DLLs von `Cosmos.Kernel.Core`, `.HAL`, `.System`, `.Plugs` und `Cosmos.Kernel` nach `artifacts/multiarch/<Projekt>/x64/` kopieren, Quelle `artifacts/bin/<Projekt>/release_linux-x64/`, ersatzweise `…/release/` (142-146)
   - für diese fünf Projekte: `nuspec` unter `artifacts/obj/<Projekt>` und altes `nupkg` löschen, `dotnet pack -p:NoBuild=true -o artifacts/package/release` (165-173)
   - `dotnet build src/Cosmos.Sdk/Cosmos.Sdk.csproj --no-incremental` (177)
   - Die ARM64-Schritte (83, 113-114, 120-121, 150-158) und `Cosmos.Build.Templates` (178) entfallen.
6. Alle `*.nupkg` aus `artifacts/package/release` in den Feed kopieren, `<version>.commit` schreiben. Erwartete Paket-IDs, sonst Abbruch: `Cosmos.Sdk`, `Cosmos.Build.API`, `.Asm`, `.CC`, `.Common`, `.Ilc`, `.Patcher`, `.Analyzer.Patcher`, `Cosmos.Kernel`, `.Boot.Limine`, `.Core`, `.Core.X64`, `.Debug`, `.Drivers`, `.HAL`, `.HAL.X64`, `.Native.MultiArch`, `.Native.X64`, `.Plugs`, `.SourceGenerators`, `.System`, `Cosmos.Patcher`, `Cosmos.Tools`.
7. Ohne `--no-tools`: `dotnet tool update --global Cosmos.Patcher --version <version> --add-source <feed>` und dasselbe für `Cosmos.Tools`.

Das Skript schreibt nur unter `.tools/` und `deploy/cosmos/feed/` und installiert die zwei Werkzeuge. Es löscht nichts im NuGet-Cache und
ändert keine NuGet-Konfiguration außerhalb des Repositorys.

**Umstellung in ZehOS:**

```xml
<!-- NuGet.Config (neu): der lokale Feed zusätzlich zu nuget.org -->
<configuration>
  <packageSources>
    <add key="zehos-cosmos" value="deploy/cosmos/feed" />
  </packageSources>
</configuration>
```

```xml
<!-- Directory.Build.props: Version aus der Datei, überschreibbar mit -p:ZehOSCosmosVersion=… -->
<ZehOSCosmosVersion Condition="'$(ZehOSCosmosVersion)' == ''">$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)deploy/cosmos/VERSION').Trim())</ZehOSCosmosVersion>
```

- `global.json`: `"msbuild-sdks": { "Cosmos.Sdk": "3.0.97-zehos.1" }`. Das ist die zweite Stelle mit der Version, weil die SDK-Zeile keine Eigenschaften lesen kann; ein Test hält beide gleich (Schritt 1).
- `ProjektZeh.csproj:1`: `Sdk="Cosmos.Sdk"`; `:131-132`: `Version="[$(ZehOSCosmosVersion)]"` (genaue Version); dazu `<WarningsAsErrors>$(WarningsAsErrors);NU1603;NU1601;NU1605</WarningsAsErrors>`, damit ein Ausweichen auf eine andere Paketversion den Bau abbricht.
- `scripts/build-release.sh`: Version aus `deploy/cosmos/VERSION` lesen statt `3.0.97`; der Paketordner im NuGet-Cache trägt die Version klein geschrieben.

- [ ] **Schritt 1: Tests schreiben** (`scripts/tests/test_build_cosmos.py`, `unittest`, Stil der vorhandenen Tests):
  - `test_version_files_agree`: `deploy/cosmos/VERSION` (ohne Leerraum) ist gleich `global.json["msbuild-sdks"]["Cosmos.Sdk"]`.
  - `test_commit_is_full_sha`: `deploy/cosmos/COMMIT` besteht aus 40 Hexzeichen.
  - `test_plan_contains_no_user_level_side_effects`: Die Ausgabe von `--print-plan` enthält weder `nuget locals`, `nuget remove source`, `nuget add source`, `.nuget/packages` noch `postCreateCommand`.
  - `test_plan_is_x64_only`: Die Ausgabe enthält kein `ARM64` und kein `arm64`.
  - `test_plan_sets_version_and_pathmap`: enthält `VersionPrefix=3.0.97-zehos.1` und `PathMap=` mit `/home/runner/work/Cosmos/Cosmos`.
  - `test_trial_version_from_commit`: `--print-plan --commit 0123456789abcdef0123456789abcdef01234567` nennt die Version `3.0.97-zehos.0.g0123456`.
- [ ] **Schritt 2:** `python3 -m unittest discover -s scripts/tests -v` schlägt fehl (Dateien fehlen).
- [ ] **Schritt 3:** `deploy/cosmos/*` und das Skript schreiben; die Stempelprüfung aus Punkt 1 mit einem weiteren Test `test_same_version_other_commit_is_refused` (Feed-Ordner im temporären Verzeichnis mit `<version>.commit`, Aufruf mit `--feed` und `--print-plan`, erwartet Ende ungleich 0 und den Text „VERSION erhöhen“).
- [ ] **Schritt 4:** Tests bestehen.
- [ ] **Schritt 5: Echter Lauf unter Linux:** `scripts/build-cosmos.sh`. Dauer notieren (sie bestimmt die Zeitgrenzen in E1-C). Erwartet: alle unter Punkt 6 genannten Paket-IDs im Feed, `cosmos --version` und `cosmos.patcher --version` nennen `3.0.97-zehos.1`.
- [ ] **Schritt 6: Echter Lauf unter Windows** (Git Bash). Schlägt er fehl, die Ursache beheben oder, wenn sie in Cosmos' Bau liegt, als Befund in `deploy/cosmos/README.md` festhalten („Paketbau nur unter Linux belegt“) und im PR nennen.
- [ ] **Schritt 7:** `NuGet.Config`, `global.json`, `Directory.Build.props`, `ProjektZeh.csproj`, `build-release.sh` und dessen Test umstellen.
- [ ] **Schritt 8:** `cosmos check`, dann Hosttests und nativer Build nach der Übersicht. Erwartet: 0 Warnungen, 0 Fehler; im Build-Log stehen die Pakete mit `3.0.97-zehos.1`.
- [ ] **Schritt 9: Gegenprobe für Schwerpunkt 1:** Die Pakete aus `deploy/cosmos/feed/` vorübergehend verschieben und den NuGet-Cache dieser Version leeren (nur die Ordner `cosmos.*/3.0.97-zehos.1`), dann `dotnet restore ProjektZeh/ProjektZeh.csproj`. Erwartet: Fehler (SDK oder Paket nicht gefunden), kein stilles Ausweichen. Danach `dotnet restore ProjektZeh.Tests/ProjektZeh.Tests.csproj`: erwartet Erfolg (die Hosttests brauchen den Feed nicht). Pakete zurücklegen.
- [ ] **Schritt 10:** `python3 scripts/check-release-paths.py` wie in `ci.yml` gegen ein mit `scripts/build-release.sh 0.0.0 <ordner>` gebautes Abbild. Erwartet: bestanden, ohne Änderung an der Erlaubnisliste.
- [ ] **Schritt 11:** Commit (`build: Cosmos aus dem Branch cosmos bauen (Stand 3.0.97)`), Review, PR. Der PR nennt die gemessene Bauzeit und das Ergebnis unter Windows.

---

### Paket E1-C: CI und Release

**Branch:** `task/mc-e1-c-ci`. **Nach:** E1-B (kann auf dessen Branch aufsetzen und gemeinsam gemergt werden).

**Dateien:**
- Anlegen: `.github/actions/cosmos/action.yml` (zusammengesetzte Aktion)
- Ändern: `.github/workflows/ci.yml` (Jobs `kernel`, `release-images`, `guest-recovery`), `.github/workflows/release.yml`

**Die Aktion** ersetzt in allen vier Stellen die zwei Zeilen `dotnet tool install --global Cosmos.Tools|Cosmos.Patcher --version 3.0.97`:

1. Cache: `actions/cache` (auf einen Commit festgelegt, wie die übrigen Aktionen im Repository) für `deploy/cosmos/feed`, Schlüssel `cosmos-${{ runner.os }}-${{ hashFiles('deploy/cosmos/COMMIT', 'deploy/cosmos/VERSION', 'scripts/build-cosmos.sh') }}`.
2. `scripts/build-cosmos.sh` (baut bei Cache-Treffer nichts, installiert die Werkzeuge).

Die Schritte `cosmos install --auto --tools` und `cosmos check` bleiben, wie sie sind. Die Jobs brauchen `fetch-depth: 0`
oder ein zusätzliches `git fetch origin <Commit>`, damit das Skript den Commit des Branches `cosmos` findet; der Job
`release-images` hat heute kein `fetch-depth: 0`.

- [ ] **Schritt 1:** Aktion schreiben, die vier Stellen umstellen.
- [ ] **Schritt 2:** Zeitgrenzen anpassen: heutige Grenze plus gemessene Bauzeit aus E1-B für die Jobs `kernel` (25 min), `release-images` (25), `guest-recovery` (25) und `release` (40). Ohne Cache-Treffer muss jeder Job allein durchlaufen.
- [ ] **Schritt 3:** PR öffnen; in der CI laufen beim ersten Mal alle drei Jobs ohne Cache. Erwartet: alle grün; beim zweiten Lauf Cache-Treffer und Laufzeit wie vorher.
- [ ] **Schritt 4:** Workflow-Lint (`lint`-Job) grün. Commit (`ci: Cosmos-Pakete aus dem Branch cosmos bauen und cachen`), Review, PR.

---

### Paket E1-D: Gleichstandsprüfung

**Rolle:** Clerk. Zwei ISOs: `master` vor E1-B (NuGet 3.0.97) und der Stand von E1-B/E1-C.

| Prüfung | Befehl | Erwartet |
| --- | --- | --- |
| Hosttests | `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` | gleiche Testzahl, alle grün |
| Plug-Bindung | `python3 scripts/check-plug-binding.py artifacts/kernel-build.log ProjektZeh` | alle Plugs gebunden, gleiche Liste wie vorher |
| Pfade | `python3 scripts/check-release-paths.py` wie in `ci.yml` | bestanden |
| Größe | `ls -l` der beiden `ProjektZeh.elf` | Abweichung unter 1 %; größere Abweichung erklären |
| Desktop | `python3 scripts/test-desktop-guest.py --iso <ISO> --accel tcg`, ebenso `--accel kvm` | Erfolg |
| Recovery | Recovery-ISO, `… --recovery` | Erfolg |
| Threads | Probe-ISO, `./scripts/test-thread-probe.ps1 -IsoPath <ISO>` | `THREAD_PROBE PASS`, `MESSAGES_PROBE PASS` |
| Isolation | `python3 scripts/test-isolation-probe.py --iso <ISO> --accel kvm --runs 5`, dazu `--negative`, `--nmi` | wie auf `master` |
| ext2 | Probe-ISO (`-p:ZehOSStorageProbe=true`), `python3 scripts/test-storage-ext2-guest.py --iso <ISO> --work <neuer Ordner> --accel kvm` | Ende 0: beide Läufe `STORAGE_PROBE DONE`, `e2fsck -fn` Ende 0 |
| Doom | `python3 scripts/test-wasm-doom-desktop.py --iso <ISO> --scenario quick` | Erfolg |
| Energie | `powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/test-power-commands.ps1` | Erfolg |
| VMware | `scripts/start-vmware.ps1`, Boot bis Desktop | Erfolg, sonst „offen“ |

- [ ] Alle Zeilen für beide ISOs ausführen und nebeneinander in den PR von E1-B schreiben. Jede Abweichung blockiert den Merge.

---

### Paket E1-E: Dokumentation und Regeln

**Branch:** `task/mc-e1-e-doku`. Der Teil in `AGENTS.md` braucht Peters Zustimmung zum Wortlaut.

- [ ] `README.md`: Voraussetzungen und Bauanleitung (heute `:237`, `:254-259`, `:273-275`, `:402`): vor dem ersten Bau und nach jeder Änderung von `deploy/cosmos/COMMIT` `scripts/build-cosmos.sh` ausführen; die Werkzeuge kommen aus dem lokalen Feed; `cosmos install` nur mit `--tools`; Hinweis für Rechner mit mehreren Arbeitskopien (das Skript setzt die globalen Werkzeuge auf den Stand der Arbeitskopie, in der es zuletzt lief).
- [ ] `AGENTS.md`, Abschnitt „Technischer Rahmen“ (`:87`, `:89`): Cosmos wird aus dem Branch `cosmos` gebaut; `deploy/cosmos/COMMIT` und `VERSION` ändern sich nur zusammen. Neuer Abschnitt nach „Wasm-Apps und Pakete“: die Regeln „Cosmos-Patches“ aus der [Übersicht](2026-11-18-multicore.md), dazu „Änderungen am Branch `cosmos` laufen nur per Pull Request“. Tabelle der Zuständigkeiten: Zeile `deploy/cosmos/`, Branch `cosmos`.
- [ ] `docs/COSMOS_SUPPORT.md`: neuer datierter Abschnitt „Cosmos aus Quelle“ mit Commit, Version, Bauzeit, Ergebnis der Gleichstandsprüfung und dem Befund zu Windows.
- [ ] `python scripts/check-doc-links.py`, Commit, PR.

---

### Paket E1-F: StorageProbe auf die Versionseigenschaft

**Branch:** `task/mc-e1-f-storageprobe`. Die Hostprobe unter `experiments/StorageProbe` bindet die Cosmos-DLLs fest aus
dem NuGet-Cache 3.0.97 ein (`StorageProbe.csproj:8-16`) und prüfte damit nie einen gepatchten Stand. Etappe 2 braucht
sie gegen die selbst gebauten Pakete.

- [ ] In `StorageProbe.csproj` jede `3.0.97` durch `$(ZehOSCosmosVersion)` ersetzen (Pfade im NuGet-Cache klein geschrieben; `PackageDownload` mit `[$(ZehOSCosmosVersion)]`).
- [ ] `dotnet run -c Release --project experiments/StorageProbe -- selftest` und `-- readonly-selftest` unter Linux. Erwartet: dasselbe Ergebnis wie vorher mit 3.0.97 (in `experiments/StorageProbe/results-2026-11-17.md`).
- [ ] `experiments/StorageProbe/README.md` um den Satz ergänzen, dass die Probe den Stand aus `deploy/cosmos/VERSION` prüft. Commit, Review, PR.
