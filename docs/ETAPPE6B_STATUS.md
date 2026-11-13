# Etappe 6b: gesicherter Zwischenstand

Stand: 13. November 2026. Branch: `codex/etappe6b`.
Basis dieses Checkpoints: `f772960da05cf80abeee23cf79a55f39662ef450`.
Der Branch bleibt getrennt; dieser Checkpoint erstellt weder einen PR noch eine Integration in master oder andere Features.

## Behobener Reviewbefund

Beim alleinigen Abbruch des langsamen Consumers über `BackgroundTaskService.Cancel` blieb der Producer am vollen Nachrichtenpuffer hängen. Der Consumer schließt jetzt auf seinem normalen Rückweg und im Fehlerpfad den Puffer vor dem Herunterzählen von `active`. Dadurch wird auch der wartende Producer beendet.

Der neue Hosttest wartet auf den vollen Puffer, bricht ausschließlich den Consumer ab und prüft `active=0`, das Ende beider Aufgaben sowie einen erneuten Start.

## Tatsächlich ausgeführte Prüfungen

- RED: Der neue Regressionstest schlug vor dem Fix fehl: `active=1 produced=4 consumed=1 buffered=3/3`.
- GREEN: `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`: 171 erfolgreich, 0 fehlgeschlagen, 0 übersprungen.
- `git diff --check`: ohne Fehler.
- Für diesen Checkpoint wurden kein neuer nativer Kernelbuild und keine Gastprüfung ausgeführt. QEMU, VMware und echte Hardware sind für diesen Fix offen.

## Bekannte Grenze und nächste Schritte

Cosmos 3.0.94 kann bei `OperationCanceledException` verschachtelte `finally`-Blöcke überspringen; deshalb bleibt die Bereinigung der Demo explizit im normalen Rückweg und im Fehlerpfad. Die Hosttests belegen kein korrektes Exception-Unwinding im Gast.

1. Den separaten Cleanup-Fix aus Etappe 6a übernehmen; die übergeordnete Arbeit koordiniert diesen Schritt nach diesem Checkpoint.
2. Den fertigen kombinierten Stand erneut unabhängig prüfen lassen.
3. Gezielte Gasttests für Consumer-Abbruch bei vollem Puffer, beendete Aufgaben und Neustart ausführen; QEMU und VMware getrennt dokumentieren.
4. Erst später die Integration mit master planen und prüfen. Dieser Zwischenstand ist keine abgeschlossene Etappenabnahme.
