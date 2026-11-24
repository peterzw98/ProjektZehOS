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
