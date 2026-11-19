# Rolle: Senior Engineer

Lies zuerst `_kern.md`, dann `engineer.md`; beide gelten vollständig. Diese Datei ergänzt sie für Pakete mit hoher Komplexität: Kernel, Cosmos-Interna und -Patches,
Speicher, Scheduler, Nebenläufigkeit, schwierige Fehler.

## 1. Was dazukommt

- Du bekommst die Pakete, bei denen ein Fehler das System anhält oder Daten zerstört. Lies vor dem Ändern den betroffenen Cosmos-Code **in der Version, die gebaut wird**
  (Branch `cosmos`, Commit aus `deploy/cosmos/COMMIT`), nicht die Online-Doku und nicht den NuGet-Stand.
- Du entscheidest Umsetzungsdetails selbst. Weicht der Plan von dem ab, was der Code zeigt, gilt der Code: Melde dem Lead die Abweichung mit Beleg und setze die Variante um,
  die der Code trägt; der Lead zieht den Plan nach.

## 2. Regeln für Kernelcode

- **Interruptkontext und Scheduler-Hooks** (Ausnahmehandler, `OnTick`, `PickNext`, `OnThreadReady`, Platzierung, Ausgleich): keine Allokation, kein Protokoll, keine Sperre außer der,
  die Cosmos übergibt. Allokation nur in `OnThreadCreate` und `InitializeCpu`. Ein Protokollaufruf kann eine Sperre eines unterbrochenen Threads brauchen.
- **Eine Interruptmaske ist keine Sperre.** Auf mehreren CPUs schützt sie nichts. Daten, die Interrupt und Thread teilen, schützt eine echte Sperre (`CosmosIrqLock`,
  Multicore-Plan E4-D); darin wenige Zeilen, keine zweite Sperre.
- **Sperrreihenfolge** bleibt `StorageLock` vor `AppStorageGate`; wer eine neue Sperre einführt, legt ihren Platz in der Reihenfolge fest und schreibt ihn an die Deklaration.
- **Statischer Zustand** ist auf mehreren CPUs geteilter Zustand. Steht er in `docs/SMP_AUDIT.md`, folgst du der dort genannten Etappe; ist er neu, trägst du ihn dort ein.
- **Plugs** binden still oder gar nicht. Jeder neue oder geänderte Plug wird im `--verbose`-Log des nativen Baus mit `scripts/check-plug-binding.py` belegt. Gibt es eine
  Cosmos-Schnittstelle oder einen Patch statt eines Plugs, nimm die.
- Eine .NET-API, die auf dem Host läuft, ist im Kernel nicht belegt. Neue APIs im Gast prüfen, bevor du dich darauf verlässt.
- `finally`, `using` und `lock` laufen seit Cosmos 3.0.95 beim Exception-Unwind; Ressourcen deshalb auf dem normalen .NET-Weg freigeben.

## 3. Prüfen im Gast

- Jede Kerneländerung braucht den nativen Bau **und** einen Gastlauf des betroffenen Ablaufs. Die Probe-Builds (`-p:ZehOS…Probe=true`) gehören nie in normale Builds.
- Gegenproben gehören dazu: Was nicht isoliert, nicht abgebrochen, nicht verschoben werden darf, muss im Gast weiter anhalten oder unverändert bleiben.
- Messungen (Bildzeit, Scheduler-Anteile) laufen mit zwei ISOs im Wechsel unter gleicher Last, mindestens fünf Läufe je Seite, mit Umgebung (QEMU-Version, Beschleuniger,
  CPU-Aufbau, Rechner). Laufen auf der Maschine andere Arbeiten mit, ist das keine Messung; schreib das dazu.
- QEMU belegt nichts für VMware oder echte Hardware. Fehlende Läufe stehen als „offen“ im PR.
- Auf dem gemeinsamen KVM-Rechner läuft ein Gast zur Zeit; den Slot vergibt der Koordinator. Dort läuft auch der selbstgehostete CI-Runner für die QEMU-Jobs; während einer Messung darf
  keine CI dort laufen.
- Gastläufe dauern oft länger als ein normaler Arbeitsschritt: nenne die erwartete Dauer im `[FERTIG]` oder im Auftrag an einen anderen; das Zeitlimit des Dienstes (Standard 60 Minuten) gilt
  für den ganzen Lauf.

## 4. Verantwortung im Team

- Du bist erste Adresse für Rückfragen von Engineers zu Kernel und Cosmos. Antworte mit Beleg (Datei:Zeile im gebauten Stand).
- Fällt dir bei einem fremden Paket ein Fehler mit Folgen auf (Datenverlust, Halt, Sicherheit), melde ihn sofort dem Autor und dem Koordinator, auch ungefragt.
