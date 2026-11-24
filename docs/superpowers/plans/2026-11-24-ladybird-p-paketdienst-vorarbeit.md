# Vorarbeit Paketdienst v2 (vor dem Tor N1-Z)

> **Für ausführende Agenten:** [Entwurf, Abschnitt 7](../specs/2026-11-24-native-pakete-design.md#7-paketdienst-v2) lesen, danach dein Paket. Ein Paket = ein Branch = ein PR. Arbeitsweise: superpowers:executing-plans, Hosttests zuerst.

**Ziel:** Die hostprüfbaren Teile des Paketdienstes v2 vorziehen. Nichts davon wird vor dem Tor N1-Z an `Kernel.cs`, `PackageShellCommands.cs` oder den Server angeschlossen; `pkg` bleibt bei v1. Neue Dateien unter `ProjektZeh/Packages/` nimmt `ProjektZeh.Tests.csproj` über das vorhandene Muster selbst auf.

**Warum vor dem Tor, und was nicht:**

| Teil | vor N1-Z? | Grund |
| --- | --- | --- |
| Manifest und Katalog v2, Auflösung, Bestand `active2`, Schreiber im Paketwerkzeug | **ja** | Abhängigkeiten, gemeinsames Aktivieren und sicheres Entfernen gehören zu Peters Ziel „Paketdienst wie Linux“ und sind von Ring 3 unabhängig; `runtime` ist nur ein Feld. Bei NO-GO bleiben sie für Wasm-Pakete verwendbar, nur `exec`, `link`, `provides` und `memory-mib` wären ohne Nutzen |
| ELF-Lader samt Fuzz | nein | ohne Ring 3 wertlos; gehört zu N2 |
| `pkgs/`-Bauvorschriften, Container, sysroot | nein | hängen an musl (N1-D) und der Aufrufliste (N1-C) |
| Pfadindex, `/dev/zehos` | nein | brauchen Prozesse |

## Format (fest für P1 bis P4)

Manifest: UTF-8, nur LF, Felder genau in dieser Reihenfolge, höchstens 1 MiB:

```text
zehos-package 2
id=<wie v1>
version=<drei Zahlen>
release=<1..9999>
runtime=linux-x64-v1
license=<SPDX-Ausdruck, 1–64 Zeichen A–Z a–z 0–9 . + - ( ) und Leerzeichen>
name=<wie v1>                      optional, nur zusammen mit entry
entry=<Pfad einer exec-Zeile>      optional
capability=net|storage|files       je höchstens einmal, in dieser Reihenfolge
memory-mib=<1..4096>               optional, nur mit entry
min-kernel=<drei Zahlen>           optional
depends=<id>>=<version>-r<release> 0..64, ordinal nach id, keine doppelte id, nicht sich selbst
provides=so:<soname>               0..64, ordinal, soname aus A–Z a–z 0–9 . _ + -
exec=<sha256> <bytes> <pfad>       exec- und file-Zeilen zusammen ordinal nach Pfad, höchstens 4096, Summe ≤ 1 GiB
file=<sha256> <bytes> <pfad>
link=<pfad> <ziel>                 ordinal nach Pfad; Ziel relativ, bleibt nach dem Auflösen unter der Wurzel
end
```

Pfade wie v1, dazu `+` als Zeichen, beginnend mit `usr/`, `lib/` oder `etc/`. Doppelte Pfade (auch nur in Groß-/Kleinschreibung verschieden, auch `link` gegen `file`) sind ungültig, ebenso ein Pfad, der zugleich Ordner eines anderen ist. `exec`- und `file`-Zeilen dürfen nicht beide fehlen.

Katalog: UTF-8, LF, höchstens 4 MiB, höchstens 4096 Pakete, ordinal nach id:

```text
zehos-catalog 2
generation=<n ≥ 1>
package=<id> <version>-r<release> <sha256 des Manifests> <Summe der Inhaltsbytes>
depends=<wie oben>                 gehört zur package-Zeile darüber
provides=<wie oben>
end
```

Vor der Aktivierung müssen `depends` und `provides` von Katalog und Manifest übereinstimmen. Vertrauen und Signatur wie v1 (`PackageTrust`).

## Pakete

### P1: Manifest v2

- **Branch:** `task/ladybird-p1-manifest2` · **Größe:** 2 · **Rolle:** Engineer · **Voraussetzung:** keine
- **Dateien:** anlegen `ProjektZeh/Packages/PackageManifest2.cs`, `ProjektZeh.Tests/PackageManifest2Tests.cs`; ändern `docs/PACKAGE_FORMAT.md` (Abschnitt „Format 2 (noch nicht angeschlossen)“, nur Manifest). `PackageManifest.cs` bleibt unverändert.

```csharp
internal readonly record struct PackageVersion2(UpdateVersion Version, int Release) : IComparable<PackageVersion2>
{
    public static bool TryParse(string text, out PackageVersion2 value);   // "1.2.3-r4"
    public override string ToString();                                      // kanonisch
}
internal sealed record PackageDependency(string Id, PackageVersion2 Minimum)
{
    public static bool TryParse(string text, out PackageDependency? value); // "musl>=1.2.5-r1"
}
internal sealed record PackageFile2(string Path, long Size, string Sha256, bool Executable);
internal sealed record PackageLink(string Path, string Target);
internal sealed class PackageManifest2
{
    public string Id { get; }  public PackageVersion2 Version { get; }  public string License { get; }
    public string? Name { get; }  public string? Entry { get; }  public int? MemoryMiB { get; }
    public bool AllowsNetwork { get; }  public bool AllowsStorage { get; }  public bool AllowsFiles { get; }
    public UpdateVersion? MinimumKernel { get; }
    public PackageDependency[] Dependencies { get; }  public string[] Provides { get; }
    public PackageFile2[] Files { get; }  public PackageLink[] Links { get; }  public byte[] Bytes { get; }
    public static ResultObject<PackageManifest2> Parse(byte[] bytes);       // wirft nie
}
```

- **Abnahme:** je Regel ein gültiger und ein ungültiger Fall; Überlauf bei Größen; Pfadausbruch über `link`; falsche Reihenfolge; CRLF; 10 000 zufällig veränderte Manifeste (feste Saat) ohne Ausnahme. Alle Hosttests grün.

### P2: Katalog v2 und Auflösung

- **Branch:** `task/ladybird-p2-katalog2` · **Größe:** 2 · **Rolle:** Engineer · **Voraussetzung:** P1
- **Dateien:** anlegen `ProjektZeh/Packages/PackageCatalog2.cs`, `ProjektZeh/Packages/PackageResolver.cs`, `ProjektZeh.Tests/PackageCatalog2Tests.cs`, `ProjektZeh.Tests/PackageResolverTests.cs`; ändern `docs/PACKAGE_FORMAT.md` (nur Katalog).

```csharp
internal sealed record CatalogEntry2(string Id, PackageVersion2 Version, string ManifestSha256, long ContentBytes,
    PackageDependency[] Dependencies, string[] Provides);
internal sealed class PackageCatalog2
{
    public long Generation { get; }  public CatalogEntry2[] Entries { get; }
    public CatalogEntry2? Find(string id);
    public static ResultObject<PackageCatalog2> Parse(byte[] bytes);        // wirft nie
}
internal sealed record InstalledPackage2(string Id, PackageVersion2 Version, bool Requested, PackageDependency[] Dependencies);
internal static class PackageResolver
{
    // Neue und zu erneuernde Einträge, Abhängigkeiten zuerst, sonst ordinal nach id; leer, wenn nichts zu tun ist.
    public static ResultObject<CatalogEntry2[]> Install(PackageCatalog2 catalog, IReadOnlyList<InstalledPackage2> installed, IReadOnlyList<string> ids);
    public static ResultObject<CatalogEntry2[]> Upgrade(PackageCatalog2 catalog, IReadOnlyList<InstalledPackage2> installed); // nie auf kleinere Versionen
    public static ResultErr CheckRemove(IReadOnlyList<InstalledPackage2> installed, string id);      // nennt die abhängigen Pakete
    public static string[] Unused(IReadOnlyList<InstalledPackage2> installed);                        // nicht angefordert, von niemandem gebraucht
}
```

- **Abnahme:** Kette, Raute, fehlend, zu alt, Zyklus (auch im Katalog neben dem Ziel), doppelter `provides` in der Zielmenge, installierte neuere Version, Upgrade erfüllt eine Mindestversion eines anderen Pakets nicht, Reihenfolge stabil; Kette mit 4096 Paketen ohne Stacküberlauf.

### P3: Bestand `active2`

- **Branch:** `task/ladybird-p3-bestand2` · **Größe:** 3 · **Rolle:** Senior (Opus) · **Voraussetzung:** P1
- **Dateien:** anlegen `ProjektZeh/Packages/PackageStore2.cs`, `ProjektZeh.Tests/PackageStore2Tests.cs`; ändern `docs/PACKAGE_FORMAT.md` (nur Bestand). `PackageStore.cs` wird nur gelesen; Ersetzen läuft über `PackageStore.ReplaceFileStorageLocked` und `RestorePendingStorageLocked`.
- **Ablage:** `<root>/v2/<id>/<version>-r<release>/{manifest,manifest.sig,files/…}`, `<root>/v2/active2`: erste Zeile `zehos-active 2`, dann `package=<id> <version>-r<release> <requested|auto> <sha256 des Manifests>` ordinal nach id, `end`. v1-Ordner werden nie angefasst.

```csharp
internal sealed record ActivePackage2(PackageManifest2 Manifest, bool Requested, string Directory);
internal sealed class PackageStore2(string root, PackageTrust? trust, UpdateVersion kernel, Func<IDisposable?>? tryBeginWrite = null)
{
    public ResultObject<ActivePackage2[]> ReadActive();                     // fehlende Datei = leerer Bestand; beschädigt = Fehler, nie leer
    public ResultObject<PackageManifest2> Stage(IPackageSource source, CatalogEntry2Key expected, CancellationToken cancellation);
    public ResultErr Activate(IReadOnlyList<(string Id, PackageVersion2 Version, bool Requested)> set, CancellationToken cancellation);
    public ResultErr Reconcile();                                           // Boot: unvollständige Stufen und nicht aktive Versionen entfernen
}
internal readonly record struct CatalogEntry2Key(string Id, PackageVersion2 Version, string ManifestSha256);
```

  `Activate` prüft vor dem Wechsel: alle Versionen vollständig gestuft, Signatur und Hash je Manifest, `min-kernel`, alle `depends` in der Menge erfüllt, kein Pfad in zwei Paketen, kein `provides` doppelt. Danach wechselt genau eine Datei. P3 braucht P2 nicht.
- **Abnahme:** Fehlerfalltests mit Abbruch nach **jedem** Dateischritt von `Stage`, `Activate` und `Reconcile`: Danach gilt der alte oder der neue Bestand, nie eine Mischung, und v1 ist byteweise unverändert. Beschädigte `active2`, veränderte Datei, Konflikt, volles Laufwerk (Schreibfehler), Wiederholung nach Abbruch.

### P4: Schreiber im Paketwerkzeug

- **Branch:** `task/ladybird-p4-werkzeug` · **Größe:** 2 · **Rolle:** Engineer · **Voraussetzung:** P1, P2
- **Dateien:** anlegen `scripts/package2.py`, `scripts/tests/test_package2.py`; ändern `ProjektZeh.Tests/PackageCatalog2Tests.cs` (Fixture aus dem Werkzeug). `build-package.py` wird nur importiert (Schlüssel, Signatur); v1-Befehle bleiben gleich.
- **Schnittstelle:** `package2.py package --key K --repo R --spec SPEC.toml DATEI=PFAD…` und `package2.py catalog --key K --repo R --generation N`; `SPEC.toml` enthält genau die Manifestfelder ohne Dateizeilen (`tomllib`). Schreibt `<id>/<version>-r<release>/…`, `catalog2`, `catalog2.sig`; bestehende Versionen werden nie überschrieben, bei Abbruch bleibt kein Versionsordner.
- **Abnahme:** Werkzeugtests; ein damit erzeugtes Manifest und ein Katalog mit drei Paketen sind als feste Testdaten (Testschlüssel) im C#-Test lesbar und byteweise kanonisch. Nichts geht in den Branch `packages`.

## Reihenfolge

P1 sofort, danach P2 und P3 nebeneinander, P4 nach P2. `docs/PACKAGE_FORMAT.md` ändern P1, P2 und P3 in getrennten Unterabschnitten: P1 legt alle drei Überschriften an, P2 und P3 füllen nur ihre.

Issues: P1 (#639), P2 (#640), P3 (#641), P4 (#642).
