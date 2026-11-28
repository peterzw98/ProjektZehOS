# Ladybird N3: Paketdienst v2 und Zielumgebung (Umriss)

> Umriss. Pakete werden nach dem [Tor N1-Z](2026-11-24-ladybird-n1-nachweise.md#n1-z-tor) geschnitten. Grundlage: [Entwurf, Abschnitt 7](../specs/2026-11-24-native-pakete-design.md#7-paketdienst-v2).

**Vorgezogen:** Format, Bestand und der Schreiber im Werkzeug entstehen in der [Vorarbeit P1 bis P4](2026-11-24-ladybird-p-paketdienst-vorarbeit.md); hier folgt der Anschluss an `pkg`.

**Ziel:** `pkg install <id>` zieht Abhängigkeiten nach; Software wird mit Bauvorschriften gegen einen sysroot gebaut.

| Bahn | Inhalt | Dateien | braucht N2 |
| --- | --- | --- | --- |
| Format | Manifest und Katalog v2, Auflösung, Konflikte; reine Hosttests | `ProjektZeh/Packages/` | nein |
| Bestand | Transaktion `active2`, Platzprüfung, Entfernen, Abräumen, Fehlerfalltests für jeden Schritt; v1-Bestand bleibt lesbar | `ProjektZeh/Packages/PackageStore*.cs` | nein |
| Werkzeug | `pkgs/<id>/`, `scripts/build-native-package.py`, fester Container, Abgleich mit `docs/LINUX_ABI.md` | `pkgs/`, `scripts/` | nein |
| Server | `catalog2`, Ablage großer Dateien entscheiden | `deploy/package-repo/`, Branch `packages` | nein |
| Index | `Pfad → Paketdatei`, Symlinks im Index | `ProjektZeh/Processes/Vfs/` | ja |
| sysroot | Pakete `musl`, `libcxx`, `libzehos`, `toybox`; `ld-musl` und Satz S2 | `pkgs/`, `ProjektZeh/Processes/Syscalls/` | ja |
| Anzeige | `/dev/zehos`, Startmenü für native Apps | `ProjektZeh/Processes/Vfs/`, `ProjektZeh/Apps/` | ja |

Veröffentlichungen im Branch `packages` brauchen je eine Freigabe. `docs/PACKAGE_FORMAT.md` und `docs/PAKETE_ERSTELLEN.md` ziehen Format und Werkzeug nach. **Abnahme:** Tor N3-Z.

## Stand 28. November 2026

Umsetzung nach [N3-Schnitt](2026-11-28-ladybird-n3-schnitt.md), Bezug `story/ladybird` `058c03eb`.

| Paket | Stand und Nachweis |
| --- | --- |
| A · `pkg` | `PackageService2` mit signiertem v2-Katalog und Generationsgrenze (`118f3de6`, Nachtrag `bd8d2d16`). |
| B1–B3 · Index und Prozesswurzel | Index und `pkg native` (`56480a10`), gebundene Paketwurzel und Nutzungszählung (`7ea95e45`). Upgrade im Gast auf `738936ea` mit KVM/4 CPUs bestanden. Ein laufender Prozess liest weiter die alte Version, der nächste Start nutzt die neue. |
| C · Bauwerkzeug | PR #754 auf `058c03eb` gemergt. Zwei lokale netzlose Podman-Bauten waren bytegleich. Das Test-ELF lief unter WSL, Testsignaturen wurden verifiziert. Die Freigabe der Projekt-Toolchain und des Containerstands steht aus. |
| D · Sysroot | Wartet auf Peters Toolchain- und Containerfreigabe. Die Abnahme mit unverändertem CMake- und Cargo-Projekt steht aus. |
| E · Lader | `PT_INTERP` über die gebundene Paketwurzel (`cc4486ef`, `bcc0dd5e`). Gastproben mit KVM/4 CPUs und TCG/1 CPU auf `502942a2`, jeweils Paket- und dynamische Probe bestanden. Der Nachtrag zum seitenweisen Laden (`c1f6d1af`) wurde auf Arbeitsstand `f59c4477` mit der Paketserie auf KVM/1 CPU geprüft. Die vollständige S2-Abnahme bleibt offen. |
| F · Anzeige | `/dev/zehos` und nativer Menüstart stehen aus. |
| G · Server | Katalogpaar-Kontrolle, atomare Umschaltung, Schutz vor v2-Rückfall in `057b9747`. Exaktes `alpine/git:2.47.2` mit BusyBox 1.37.0 lokal netzlos 5/5. Produktionsablage für Dateien über 100 MiB, Gasttransfer samt Abbruch und Veröffentlichung stehen aus. |

N3-Z bleibt offen, bis Paketkette und Fehlerfälle auf einem gemeinsamen Stand sowie die beiden unveränderten Beispielprojekte am freigegebenen sysroot belegt sind.
