# Ladybird N5: Apps umstellen, Interpreter entfernen (Umriss)

> Umriss. Geschnitten nach Tor N3-Z. Grundlage: [Entwurf, Abschnitt 8](../specs/2026-11-24-native-pakete-design.md#8-übergang).

**Ziel:** Alle eigenen Pakete sind nativ; wasm3, danach WAMR, `zehos_v1` und die Wasm-Werkzeuge sind entfernt.

| Schritt | Inhalt | braucht |
| --- | --- | --- |
| 1 | Rechner, Doom | Tor N3-Z |
| 2 | ClassiCube (Netz, Zeigerfang, `/save` übernehmen) | Netz aus N4 |
| 3 | NetSurf (letzter wasm3-Nutzer) | Netz aus N4 |
| 4 | Sicherheitsprüfung der Systemaufrufe, Fuzz mit fester Saat | Tor N4-Z |
| 5 | Tor N5-Z, Freigabe Peter | 1–4 |
| 6 | wasm3 entfernen; ein Release später WAMR und `zehos_v1`. CI, Lizenzhinweise und Doku nachziehen | 5 |

Jede App behält ihre `id`; Speicherstände werden gelesen und erst überschrieben, wenn die native App läuft. Vor Schritt 6 gibt es für jedes eigene Wasm-Paket ein natives Upgrade; alles andere wird gemeldet, nicht gelöscht. **Abnahme:** Tor N5-Z und nach Schritt 6 ein Kernelbau ohne Wasm-Quellen.
