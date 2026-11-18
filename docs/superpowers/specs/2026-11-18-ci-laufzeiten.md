# CI-Laufzeiten: Befund und Vorschläge

Stand: 18. November 2026, `master` @ `8c0f77f`, Workflow `.github/workflows/ci.yml`. Nur gelesen (`gh run list/view`,
Jobs-API, TRX-Artefakte); nichts geändert. Auftrag: Koordinator nach Peter (Discord, 14:24).

## Datenbasis

60 erfolgreiche Läufe von `Build and tests` (Push auf `master` und PRs, 17./18. November). 40 davon mit Codeänderung (alle
Jobs gelaufen); die übrigen sind reine Doku-Läufe (nur **Detect code changes** und Lint, 10 bis 20 s). Zusätzlich die
beiden Läufe von PR #291 (E1-C, Cosmos aus Quellbau) und die TRX-Dateien des Laufs 44311867499.

## Ergebnis

Wanduhr eines Code-Laufs: **Median 5,1 min, Maximum 9,4 min**. Wartezeit auf Runner ist kein Faktor (Median 3 s je Job).

| Job | Median | Max | Größter Schritt (Median) |
| --- | --- | --- | --- |
| Shell tests (Windows) | 4,5 min | 4,9 min | `Run shell tests` 213 s (Build + 1492 Tests seriell), `Set up .NET 10` 30 s |
| Shell tests and .NET analysis | 3,4 min | 4,1 min | `Run shell tests` 134 s, Python-Tests 23 s, Build 12 s, Restore 11 s |
| NetSurf offline package | 2,5 min | 5,5 min | Build 103 s (max 284 s), apt 34 s |
| Guest recovery boot (QEMU) | 2,2 min | 7,1 min | `Install QEMU` 50 s (**max 345 s**), Recovery-Build 43 s |
| Guest boot and desktop (QEMU) | 1,8 min | 7,5 min | Gastlauf 70 s, `Install QEMU` 22 s (**max 368 s**); wartet vorher auf `kernel` |
| Release images | 1,8 min | 2,2 min | Live- und Installer-Build 74 s |
| Cosmos x64 build | 1,3 min | 1,6 min | Kernelbuild 44 s, Toolchain 9 s, CLI 8 s |
| Rechner, Lint, Detect | je ≤ 0,2 min | | |

**Kritischer Pfad:** die Windows-Hosttests (4,5 min) und `kernel` → `guest` (1,3 + 1,8 min). Die Ausreißer bis 9 min
kommen von `apt-get` in den Gastjobs (QEMU-Installation bis 6 min).

**Hosttests im Detail (TRX):** 1492 Tests, Summe der Einzelzeiten 128 s (Linux) bzw. 155 s (Windows); Beginn bis Ende des
Testlaufs entspricht der Summe, die Tests laufen also **vollständig nacheinander** (kein `[assembly: Parallelize]`; fünf
Klassen tragen schon `[DoNotParallelize]`). Zehn Tests machen unter Linux 80 der 128 s aus:

| Test | Linux | Windows | Ursache |
| --- | --- | --- | --- |
| `LayoutMigrationTests.T07_FileOfTwoGibibytesIsAConflict` | 17,3 s | 16,0 s | legt eine echte Datei mit 2 GiB an (`SetLength`) |
| `TlsTransportTests.Load_ShippedRootBundle_IsCachedAndYieldsTrustAnchors` | 12,6 s | 11,4 s | parst das ausgelieferte CA-Bündel |
| `WasmNetworkHostTests.TlsHandshakeDeadlineHoldsWithoutStatusCalls` | 11,5 s | 11,5 s | `Thread.Sleep(11500)` bis zur festen Handshake-Frist |
| `WasmNetworkHostTests.TimedOutPendingConnectionDoesNotStartLater` | 10,1 s | 10,1 s | wartet bis zu 12 s auf die Verbindungsfrist |
| `…WeakRootsAreNotLoaded…`, `…StartDeadline_Https_ParsesRoots…` | je 6,3 s | je 5,7 s | parsen das CA-Bündel erneut |
| `UserManagementTests.ManyFilesDoNotLockSecurityMetadata` | <3 s | 16,7 s | 1000 Dateien; unter Windows (NTFS, Defender) teuer |

**Caches:** Kein Job nutzt heute einen Cache (weder NuGet noch apt noch Cosmos). PR #291 führt `actions/cache` für den
Cosmos-Feed ein: in beiden bisherigen Läufen Fehlschlag (ein Eintrag, nur für `refs/pull/291/merge`), der Quellbau kostete
dann **111–138 s in jedem der drei Jobs** `kernel`, `release-images` und `guest-recovery`. Bei einem Treffer überspringt
`scripts/build-cosmos.sh` den Bau (Stempel `deploy/cosmos/feed/<Version>.commit`); diese Zeit ist noch nicht gemessen.

**Wiederholte Arbeit:** `kernel`, `release-images` und `guest-recovery` richten je .NET, Cosmos-CLI und Toolchain ein
(je ~30 s) und bauen je einen Kernel. Sie laufen parallel und verlängern die Wanduhr nicht, kosten aber Runner-Minuten.
NetSurf und Rechner laufen bei jeder Codeänderung, auch wenn `experiments/` unverändert ist.

`concurrency` (`cancel-in-progress` je Ref) und der Pfadfilter **Detect code changes** wirken wie gedacht: Doku-Läufe sind
nach 10–20 s fertig, ältere Läufe desselben Branches werden abgebrochen (in den 25 jüngsten Läufen 9 abgebrochene).

## Vorschläge (nach Ersparnis auf der Wanduhr)

| # | Vorschlag | Ersparnis (geschätzt) | Risiko | Art |
| --- | --- | --- | --- | --- |
| 1 | Die vier Frist-Tests (TLS-Handshake, Verbindungsfrist) über eine injizierte, kurze Frist oder Uhr prüfen statt real 10–12 s zu warten | ~20 s je Testjob | gering; interner optionaler Parameter, Fristwert bleibt im Produktcode | Testcode |
| 2 | Geparstes CA-Bündel in den Tests einmal je Lauf teilen (oder ein kleines Testbündel, das ausgelieferte nur einmal prüfen) | ~15–20 s je Testjob | gering | Testcode |
| 3 | T07: Größengrenze über injizierte Dateigröße prüfen statt 2 GiB anzulegen (eine echte Datei knapp unter der Grenze nur dort, wo die Plattform sparse kann) | ~16 s je Testjob | gering | Testcode |
| 4 | MSTest auf Klassenebene parallelisieren (`[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]`, MSTest 3.6.4 kann das ohne neues Paket) | geschätzt 40–60 % der Testzeit, also ~1 min je Testjob | **mittel**: Tests mit gemeinsamem Zustand (Temp-Ordner, statische Felder, Konsole) können flackern; erst in einem Versuchs-PR mehrfach laufen lassen, Ausreißer mit `[DoNotParallelize]` markieren | Testcode |
| 5 | QEMU-Pakete der Gastjobs cachen (`.deb` per `actions/cache`, die Action führt #291 ohnehin ein) oder `apt-get update` nur bei Cache-Fehlschlag | Median ~20–40 s, vor allem die Ausreißer bis 6 min | gering bis mittel; Paketstand altert, Schlüssel z. B. nach Runner-Image | Workflow |
| 6 | Nach #291: auf `master` einmal den Feed-Cache befüllen (PR-Läufe dürfen Caches des Basisbranches lesen) und die Zeit eines Treffers messen; bleibt der Fehlschlag-Fall häufig, den Quellbau in einem Job bauen und den Feed als Artefakt weitergeben | auf `master` und PRs je ~2 min für `kernel`→`guest` | gering; nur Ablauf | Workflow (#291) |
| 7 | NetSurf und Rechner nur bei Änderungen unter `experiments/` (und den Paketskripten) bauen, sonst überspringen | keine Wanduhr, ~3 Runner-min je Lauf | gering; Hinweis: Paketbau bleibt dann ungeprüft, wenn nur gemeinsame Skripte sich ändern | Workflow |
| 8 | NuGet-Cache für die beiden Testjobs (`setup-dotnet` mit `cache: true`, braucht Lockdatei) | ~10 s je Testjob | gering; Lockdatei wäre neu | Workflow |

Mit 1–3 sinkt die Windows-Testdauer von ~4,5 auf ~3,6 min, mit 4 zusätzlich auf voraussichtlich 2,5–3 min; dann bestimmt
`kernel` → `guest` (~3 min) die Wanduhr. 5 entfernt die 7–9-min-Ausreißer.

Abstimmung: 5–8 berühren `ci.yml` und damit PR #291 (E1-C, agent-peter-jensen, Lead agent-peter-multithread). Vorschläge 1–4
ändern nur Testcode und sind davon unabhängig. Neue Actions oder Versionen nur nach Rückfrage (5 und 6 nutzen nur das schon in
#291 eingeführte `actions/cache`).

## Urteil des Leads zu 5–8 (agent-peter-multithread, #second, 18. November 14:32)

- **6:** ja, zuerst messen: nach dem Merge von #290 den Feed-Cache auf `master` befüllen und einen Treffer messen; ein Job, der den Feed als Artefakt weitergibt, nur wenn Fehlschläge häufig bleiben. `release.yml` bleibt ohne Cache (Herkunft der Pakete = Commit).
- **5:** ja, als eigener kleiner PR nach #290, Cache-Schlüssel nach Runner-Image.
- **7:** nein für jetzt: spart keine Wanduhr, und die Paketskripte blieben ungeprüft.
- **8:** nein: 10 s sind die neue Pflegepflicht einer NuGet-Lockdatei nicht wert.
- **1–4** (Testcode) gehören nicht zum Multicore-Plan; Zuständigkeit beim Koordinator.

## Messung des Feed-Caches (Block #411, 19. November 2026)

Quelle: `gh api .../actions/runs/<id>/jobs` für die letzten 60 Läufe von `ci.yml` (23 erfolgreiche mit Cosmos-Bau, 17.-18. November, `master` @ `f6bbaed9`);
Treffer = Schritt `Build Cosmos from source and install tools` unter 30 s, Fehlschlag darüber. Nur gemessen, `ci.yml` unverändert.

| Job | Treffer (Anzahl) | Jobzeit Median / Max | Fehlschlag (Anzahl) | Jobzeit Median / Max | Bauschritt Fehlschlag |
| --- | --- | --- | --- | --- | --- |
| Cosmos x64 build (`kernel`) | 19 | 83 s / 97 s | 4 | 182 s / 218 s | 121 s |
| Release images | 19 | 114 s / 125 s | 4 | 212 s / 237 s | 119 s |
| Guest recovery boot | 19 | 122 s / 218 s | 4 | 227 s / 342 s | 112 s |

- **Treffer:** der Bauschritt kostet 1-7 s (Median), Cachewiederherstellung 1-4 s. Ein Treffer spart je Job ~100 s gegenüber dem Quellbau.
- **Fehlschläge** (4 von 23 Läufen, 17 %) sind immer alle drei Jobs zugleich: der Schlüssel (`COMMIT`, `VERSION`, `SDK`, `build-cosmos.sh`) hatte sich
  geändert (Cosmos-Pin, Bauskript), danach füllt der erste Lauf den Cache. Der Cache-Speicher zeigt Einträge für `refs/heads/master` und PR-Merge-Refs
  (4,6-4,8 MB); PR-Läufe lesen die von `master` mit.
- Der Treffer ist gemessen, die Befürchtung aus Vorschlag 6 (Fehlschläge bleiben häufig) bestätigt sich nicht.

**Entscheidung Artefakt-Job: nein.** Ein Job, der den Feed einmal baut und als Artefakt weitergibt, würde bei einem Fehlschlag ~2 der 3 parallelen Quellbauten
(~230 Runner-s) sparen, die Wanduhr aber kaum: `kernel` müsste vor den anderen fertig sein (zusätzlich ~25 s Setup und Artefakt-Hoch-/Herunterladen), und
das nur in 17 % der Läufe. Neue Jobabhängigkeit und Pflege stehen dazu nicht im Verhältnis. Neu bewerten, wenn der Anteil der Fehlschläge deutlich über ~30 % steigt.

## Nicht geprüft

- Wie stark die Parallelisierung (4) tatsächlich hilft und welche Tests dabei flackern; das zeigt erst ein Versuchslauf.
- Läufe von `release.yml` und `codeql.yml` (nicht Teil des Auftrags).
- Ursache jedes einzelnen Fehlschlags (Schlüsseländerung angenommen aus dem Schlüsselaufbau, nicht je Lauf nachgewiesen).
