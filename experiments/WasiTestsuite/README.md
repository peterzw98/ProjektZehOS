# wasi-testsuite als ZehOS-Pakete (Roadmap 12.8, Paket P4-F)

Die C-Tests aus [WebAssembly/wasi-testsuite](https://github.com/WebAssembly/wasi-testsuite) (Apache-2.0, Ordner `tests/c/src`) werden **unverändert**
mit wasi-sdk 25 übersetzt und als je ein ZehOS-Paket im Gast ausgeführt. Plan: [2026-11-19-wasi-posix-12-8.md](../../docs/superpowers/plans/2026-11-19-wasi-posix-12-8.md), Abschnitt P4-F.

## Bauen

```sh
python3 experiments/WasiTestsuite/build.py
```

- Quellen: fester Commit `de7fbe267210b52f63893270403177239b4f4a9b`, jede Datei einzeln über `raw.githubusercontent.com` mit festem SHA-256
  (`FILES` in `build.py`), abgelegt unter `.tools/wasi-testsuite/<commit>/`. Nichts davon liegt im Repository.
- wasi-sdk 25 kommt über denselben Abruf wie `experiments/NetSurf.Wasi/build.py` (`.tools/netsurf/`, fester SHA-256).
- Ergebnis: `bin/packages/<test>/` je Test mit `<test>.wasm`, `LICENSE.txt` (Apache-2.0 der Suite) und `licenses/` (wasi-libc, compiler-rt,
  `SOURCES.txt` mit Commit und Prüfsummen). `bin/packages/tests.json` nennt Kennung (`wasits-<test>`), Anzeigename (`WT <test>`), Einstieg
  und Umfang. Zwei Bauläufe hintereinander ergeben bitgleiche Dateien (169 Dateien, `sha256sum` verglichen).
- Hat die Suite für einen Test eine `<test>.json` mit `"root": "fs-tests.dir"`, liegt der Inhalt von `fs-tests.dir` an der Paketwurzel: Das
  nur lesende Preopen `/` (fd 3) ersetzt das Preopen `.` der Suite, relative Pfade wie `file` lösen sich darüber auf. Tests ohne `.json`
  (etwa `fopen-with-no-access`) bekommen eine Paketwurzel ohne diese Dateien, wie in der Suite ohne Preopen.

## Im Gast ausführen

ISO mit `-p:ZehOSWasm=true -p:ZehOSPackageKeyPath=<Test-public.b64>` bauen, dann:

```sh
python3 scripts/test-wasi-testsuite.py --iso ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso --key <Test-private.pem> --accel kvm
```

Das Skript signiert die Pakete mit dem Testschlüssel in ein lokales Repository auf einer frischen Datenplatte, installiert sie mit
`pkg install-local`, startet sie nacheinander über das Startmenü und wertet die serielle Ausgabe aus: `APP <id> beendet` (Exitcode 0) ist `PASS`,
`APP <id> Ende: …` (Exitcode ungleich 0, Trap) ist `FAIL` mit dieser Meldung. Ohne Ende innerhalb von `--timeout` (Standard 60 s) gilt der Test als
`FAIL`, und die übrigen laufen in einer neuen VM. `--only <test> …` wählt einzelne Tests. Ein `assert` der Suite endet unter wasi-libc mit
`abort()` und damit als Trap `unreachable`; die Assertion-Meldung auf stderr erscheint nur im Fenster der App, nicht seriell.

## Tests und Ausgangswerte

Spalte „Braucht“: Lesen (Datei aus dem Paket), Schreiben, Ordner, Uhr, Args/Env, Sockets. „Erwartet“: Ergebnis nach Plan 12.8 (P4-A bis P4-D).
„Ausgangswert“: Gastlauf am 19.11.2026 auf `master` `c7a75168c` (nach P4-D), wasm3, QEMU 10.0.13 mit KVM, Rechner MSI. AOT ist nicht gelaufen.

| Test | Braucht | Erwartet | Ausgangswert | Bemerkung |
| --- | --- | --- | --- | --- |
| `clock_getres-monotonic` | Uhr | PASS | PASS | |
| `clock_getres-realtime` | Uhr | PASS | PASS | |
| `clock_gettime-monotonic` | Uhr | PASS | PASS | |
| `clock_gettime-realtime` | Uhr | PASS | PASS | |
| `fdopendir-with-access` | Lesen, Ordner (`O_DIRECTORY`, `fd_readdir`, `d_ino` = `st_ino`) | PASS | **FAIL** | Trap `unreachable` (ein `assert`). Vermutlich `assert(statbuf.st_ino == dp->d_ino)`: `fd_readdir` schreibt `d_ino` = Index + 1 (`ProjektZeh/Runtime/Wasm/WasmSaveStore.cs:35`), `fstatat` liefert den FNV-Pfadwert (`zehos_host_core.c:432`). Nur aus dem Code gelesen, im Gast nicht belegt (stderr nicht seriell) |
| `fopen-with-access` | Lesen | PASS | PASS | |
| `fopen-with-no-access` | Lesen (erwartet `ENOENT`/`ENOTCAPABLE`) | PASS | PASS | |
| `lseek` | Lesen | PASS | PASS | |
| `pread-with-access` | Lesen | PASS | PASS | |
| `stat-dev-ino` | Lesen (`st_dev`, `st_ino`) | PASS | PASS | |
| `pwrite-with-access` | Schreiben über das Preopen (`writeable/`) | AUSSERHALB | FAIL (Trap) | Paket nur lesend; Folgepaket laut Plan „Was offen bleibt“ |
| `pwrite-with-append` | Schreiben über das Preopen | AUSSERHALB | FAIL (Trap) | wie oben |
| `sock_shutdown-invalid_fd` | Sockets | AUSSERHALB | FAIL (Trap) | `sock_*` nicht im Umfang; fd 3 ist in ZehOS immer das Paket-Preopen |
| `sock_shutdown-not_sock` | Sockets | AUSSERHALB | FAIL (Trap) | `sock_*` nicht im Umfang |

Im Umfang: 9 von 10 `PASS`. Args/Env braucht keiner der 14 Tests (die `*.json` der Suite nennen nur `root`).

Offen: AOT (WAMR) und VMware sind nicht geprüft; beides gehört zur Abnahme P4-Z.
