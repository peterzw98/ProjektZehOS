# Zeitplan für N2 bis N6 nach der Ladybird-Demo

Entwurf zur Entscheidung durch Peter, Stand 27. November 2026. Die Schätzung
beginnt frühestens am Montag, 7. Dezember. Woche 1 beginnt, sobald zwei
Arbeitsstränge besetzt sind und ein KVM-Prüfslot zur Verfügung steht. Alle
Angaben sind Arbeitswochen bei durchgängiger Besetzung. Feiertage, Urlaub,
Wartezeit auf Freigaben, fehlende Prüfhardware und der Releaseabstand in N5
verschieben den Kalender. Die Pakete werden an ihren jeweiligen Toren zugeschnitten.

## Ausgangslage

Peter gab N1-Z am 27. November frei. Für musl und die Vergleichsprogramme
liegen die D- und E-Berichte auf `story/ladybird` vor. Zum Start werden die
Urteile mit dem N1-Torprotokoll abgeglichen. Demo-Ergebnisse und die
vorgezogenen V-Pakete können Nachweise beitragen. Für jedes Tor gelten die
vollständigen Kriterien und Wiederholungen aus dem Gesamtplan.
[N1-Z-Entscheidung](../../LADYBIRD_FEASIBILITY.md#tor-n1-z-go-peter-27112026) ·
[D-Bericht](../specs/2026-11-24-ladybird-n1-musl.md) ·
[E-Bericht](../specs/2026-11-24-ladybird-n1-vergleichsprogramme.md) ·
[Gesamtplan](2026-11-24-ladybird-nativ.md)

## Arbeitspakete und Schätzung

| Phase | Pakete und Übergaben | Start und Dauer | Abnahme |
| --- | --- | --- | --- |
| [N2 Prozesse](2026-11-24-ladybird-n2-prozesse.md) | Prozesslebenszyklus und Fehlerisolation zuerst stabilisieren. Dann ELF/PIE, S1-Aufrufe und Terminal/VFS nebeneinander prüfen. Bestehende Implementierung und Ergebnisse gegen den Nachweisstand abgleichen. #732 für die Cosmos-Übernahme und #733 für N2-Z früh einplanen. | Ab W1, **2–4 Wochen** | S1-Ausgaben wie Linux, Ring-3-Fehler bleibt im Prozess, 1.000 Starts mit höchstens 1 MiB Verlust, Desktop und Wasm bleiben funktionsfähig. |
| [N3 Paketdienst](2026-11-24-ladybird-n3-paketdienst.md) | P1–P4 übernehmen. Paketindex, Laufzeitlader und sysroot mit dem N2-Strang abstimmen. Separat Werkzeuge und Paketserver vorbereiten und Ablage über 100 MiB klären. Danach S2, `/dev/zehos` und Gastintegration. | Hostarbeit ab W1 parallel, **1–2 Wochen**. Integration nach N2-Z, **3–5 Wochen**. | Kette aus drei Paketen inklusive Abbruch und Wiederanlauf. Unverändertes CMake- und Cargo-Projekt bauen gegen den sysroot. |
| [N4 POSIX](2026-11-24-ladybird-n4-posix.md) | Threads/TLS und Wartemechanik zuerst. Prozesse/Signale und IPC stufenweise darauf aufbauen. TCP/UDP/DNS parallel, sobald Deskriptor- und Wartevertrag stehen. Gemeinsame Kernel-Dateien jeweils einem Verantwortlichen zuordnen. | Nach N2-Z parallel zur N3-Integration, **5–8 Wochen** | N1-E vollständig, Deskriptor und Speicher zwischen Prozessen, unabhängige Absturzisolation und `pthread` auf zwei CPUs. |
| [N5 App-Umstieg](2026-11-24-ladybird-n5-umstieg.md) | Rechner und Doom ab N3-Z. ClassiCube und NetSurf nach N4-Netz, Sicherheitsprüfung ab N4-Z. Paket-IDs, Speicherstände und Leistung absichern. | Erste Apps **2–3 Wochen**. Nach N4-Z für verbleibende Apps und Abnahme **3–5 Wochen**. Entfernung von wasm3 nach Freigabe, WAMR und `zehos_v1` ein Release später. | Vier Apps nativ, Doom und ClassiCube mindestens auf AOT-Niveau, Sicherheitsprüfung, ausdrückliche Freigabe von Peter vor Entfernung. |
| [N6 Ladybird](2026-11-24-ladybird-n6-ladybird.md) | Paketrezepte und sysroot, dann `js` und Headless-Referenzbilder. Danach Fensterfrontend, Eingabe, Netz/TLS, Neustart eines Dienstes und Dauerlauf. | Nach N3-Z und N4-Z, **5–8 Wochen** | Statische Seite im bedienbaren ZehOS-Fenster. PSS auf `static.html` höchstens 2 GiB. |

Der kritische Pfad verläuft über N2-Z, N4-Z und N6-Z, sofern N3-Z spätestens
mit N4-Z erreicht wird. Dafür ergeben sich **12–20 Arbeitswochen plus
2–3 Wochen Reserve** für Fehlerkorrekturen und Wiederholungen. Das setzt einen
Systemstrang und einen Paket-/App-Strang voraus. Für N5 und N6 gleichzeitig
braucht es getrennte Verantwortliche und Prüfslots. Bei zwei Strängen hat
nach den Toren der Ladybird-Fensterlauf Vorrang vor der abschließenden
Wasm-Ablösung. Für die Ablösung läuft ein eigener Release-Zeitplan.

## Entscheidungen für den Start

1. Peter legt Besetzung und Priorität von N5/N6 fest. Die Schätzung priorisiert
   N6 bei Kollisionen. SDL (#690) teilt dieselben Starttore wie N6 und ist in
   seiner Dauer nicht enthalten.
2. Vor N2-Z die Prüftermine für KVM, TCG, VMware und Hardware sichern und die
   noch fehlenden Fälle aus #733 festhalten. Die KVM/TCG-Torläufe je Fall auf
   1 und 4 CPUs in einem gemeinsamen Kalender einplanen.
3. Vor N3-Z die Ablage großer Pakete entscheiden. Vor Veröffentlichung oder
   Integrationsmerge über den Cosmos- und ZehOS-Stand entscheiden und den
   kombinierten Standard-, Recovery- und LinuxRun-Bau abnehmen.
   [Integrationsbericht zu e82d05a0](https://github.com/peterzw98/ProjektZehOS/blob/e82d05a02b20dff29ef1a98919ec0dd2806b1b61/docs/superpowers/plans/2026-11-27-ladybird-master-integration.md)
4. An jedem Tor Aufwand aus den verbleibenden Befunden neu schätzen und den
   nächsten Abschnitt auf dem Board zuschneiden. Die Entscheidung am Tor
   bleibt beim Projektbenutzer.
