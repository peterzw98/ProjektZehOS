# Risiko-Review Block 4: Dateisystem und Rechte (story/ladybird 3baf7cb0 gegen origin/master)

Das Review war rein lesend und lief mit frischem Kontext. Ich habe nichts geändert, nichts gepusht und nichts kommentiert. Kernelbau, Gastlauf und Hosttests liefen nicht: Alle Befunde stammen aus dem Code (Datei:Zeile) und aus `docs/LINUX_ABI.md`.

## Umfang

- **Pakete:** V7/V7b (#724/#741), V8 (Rechte der Prozesswurzel), V28b (fcntl/flock, chmod, link, HOME), V32 (MAP_SHARED auf Dateien inklusive P2-Fix), V34 (st_uid), V41/V41b (mtime).
- **Angrenzend, soweit V7 benutzt wird:**
  - V12, V25 und V26 (exec, Interpreter, x-Bit)
  - V30 (Dateiseiten)
  - V16 (Socketdateien)
  - V37 (`/tmp`, `/.cache` auf der Platte)
- **Grundlage:** Delta seit dem Merge von #741 (`16348849..3baf7cb0`) in `Processes/Vfs`, `Syscalls`, `CosmosLinuxRun.cs` und `FileService.AuthorizeProcessRoot`.
- **Nicht erneut gemeldet:** die bekannten Grenzen aus review724, review741 bis 741d und `LINUX_ABI.md`, nämlich:
  - TOCTOU am letzten Teil
  - FAT-Sonderzeichen
  - Unicode-Inodes und rename-No-op
  - Kontingent je Start
  - `-EIO` hinter einer Lücke
  - `.~replace`
  - EBUSY beim Ordner-rename
  - ETXTBSY

## Was hält

- **Ausbruch aus der Wurzel:**
  - `RootFor` (`CosmosLinuxRun.cs:262`) nimmt nie `/` und nie einen Einhängepunkt.
  - `AuthorizeProcessRoot` (`FileService.cs:930-975`) verlangt eine angemeldete Sitzung, Schreib- und Verwaltungsrecht, prüft fremde Einträge darunter und schließt `.zehos`-Teile sowie im Root-Profil `/etc`, `/var` und `/boot` aus.
  - Die Systemdateien `.zehos-*` (Konten, Pakete, `.zehos-apps` mit /save) liegen am Laufwerksstamm und damit nie in der Wurzel.
  - `ElfLoader.ReadInterpreterPath` (`ElfLoader.cs:320`) verlangt einen absoluten PT_INTERP. `ReadFromRoot` lehnt jedes `..` ab, und unter Cosmos trennt nur `/` Pfadteile.
- **V32-Sperrreihenfolge:** FileGate → `_sharedGate` → `_gate` → StorageLock, und Seitenfehler laufen über `_memGate` → `_gate` → StorageLock. Eine Umkehrung habe ich nicht gefunden.
  - `write` und `read` kopieren über Zwischenpuffer (`LinuxSyscalls.cs:150/531`), es gibt also kein `_memGate` unter `_gate`.
  - Das Zurückschreiben nimmt StorageLock je Seite.
  - `mmap` mit MAP_SHARED|PROT_WRITE prüft das Schreibrecht des Deskriptors (`LinuxSyscalls.cs:366`).
  - Zurückgeschrieben wird nur bis zum Dateiende, also ohne Wachstum am Kontingent vorbei.
- **Dateiaufrufe unter FileGate:**
  - Alle Dateiaufrufe laufen über `DispatchFileGated` (`LinuxSyscallsMore.cs:114-123`). Damit sind die Arbeitslisten `_pending` und `_parents` von `LinuxFileSystem` über alle Prozesse eines Laufs serialisiert.
  - `bind` und `connect` nehmen FileGate selbst.
- **DiskFile:** Position und Lesen beziehungsweise Schreiben stehen unter derselben StorageLock, in Abschnitten von höchstens `ChunkBytes`. Das Wachstum läuft in Schritten.
- **V28b:** chmod ist ohne Wirkung und link liefert `-EPERM`, das ist harmlos. **V34** und **V41** sind ohne Risiko für Rechte oder Daten.

## P1

Keine.

## P2

Keine.

## P3

**1. Doku-Widerspruch zum Ausführungsbit**

- `docs/LINUX_ABI.md:175` sagt: „Platte: … Dateien 0644 (schreibgeschützt 0444), also ohne Ausführungsbit (`access(X_OK)` `-EACCES`)“.
- Seit V26 gilt `DirectoryFileStore.cs:92` (`FileMode = 0x1ED`, `ReadOnlyFileMode = 0x16D`): Jede vorhandene FAT-Datei ist ausführbar. So steht es auch richtig in `LINUX_ABI.md:221/512`.
- Die Zeile 175 ist veraltet.

**2. Angelegte Modi gehen verloren (`DirectoryFileStore.cs:611-617`, `:539`)**

- `Rename` entfernt `_createdModes[to]`, bevor `RenameStorageLocked` läuft. Scheitert das rename (etwa `-EEXIST` wegen eines stehenden Namens, `-EBUSY`, E/A-Fehler), fällt das unveränderte Ziel von zum Beispiel 0600 auf 0755 zurück.
- `DeleteFile` entfernt den Eintrag ebenfalls vor der Prüfung.
- Die Folge ist nur freizügiger und betrifft keine Sicherheitsgrenze, weil es nur einen Nutzer gibt.

**3. Das Kontingent zählt nur Bytes**

- `mkdir` und leere Dateien belegen Cluster und Verzeichniseinträge auf der FAT-Platte, ohne `GrownBytes` zu belasten. Auslöser: eine Schleife mit `mkdir("/d/N")` oder `open(O_CREAT)` leerer Dateien. Das füllt die Datenplatte trotz der 64 MiB, und danach scheitern Schreibzugriffe von Konten, Shell und Paketen.
- `_createdModes` wächst dabei unbegrenzt mit (ein Eintrag je angelegter Datei, `DirectoryFileStore.cs:467`).
- Das ist eine weitere Umgehung wie der bekannte Fall „jeder Start 64 MiB“, aber innerhalb eines Starts. Eventuell eine Grenze für die Zahl der Einträge je Start einführen oder als Grenze dokumentieren.

**4. Nicht-ASCII-Groß/Klein: Die Tabellen falten mehr als FAT**

- Die Tabellen falten nach Unicode:
  - `_openEntries`, `_pendingPaths` und `_createdModes` mit `OrdinalIgnoreCase` (`DirectoryFileStore.cs:122-125`, `:93`)
  - `NodeKey` mit `ToUpperInvariant` (`LinuxFileSystem.cs:777`)
- Cosmos-FAT vergleicht nur ASCII. `ä` und `Ä` sind auf FAT also zwei Dateien (das Anlegen von `Ä` neben `ä` gelingt), in den Tabellen aber ein Schlüssel.
- **Folgen (aus dem Code abgeleitet, nicht ausgeführt):**
  - Nach `unlink` des offenen `ä` meldet `KindStorageLocked("Ä")` die vorhandene Datei `Ä` als fehlend, weil `_pendingPaths` trifft.
  - Mit V32 teilen sich die Deskriptoren von `ä` und `Ä` einen `LinuxFileNode` und damit eine Seitenablage:
    - `read` auf `Ä` überlagert die Frames von `ä` (`LinuxMemFile.ReadThrough`).
    - `write` auf `Ä` schreibt in die Frames von `ä`, und ein geschriebener Frame geht beim Flush nach `ä`.
  - Ergebnis: Daten werden zwischen zwei Dateien vermischt.
- **Auslöser:** zwei Namen, die sich nur in der Groß-/Kleinschreibung von Nicht-ASCII-Zeichen unterscheiden, und dazu MAP_SHARED. Für Ladybird und sqlite (ASCII-Namen) unwahrscheinlich.
- Review741c sagte, die Tabellen nutzten „denselben Vergleicher wie das Laufwerk“; das stimmt nur für ASCII.
- **Vorschlag:** nur ASCII falten, passend zu Cosmos.

**5. Lange StorageLock-Haltezeiten und ungeprüfte Großallokationen beim Programmstart**

- Diese Stellen halten die Sperre jeweils in einem Zug, entgegen der Regel `ChunkBytes` (`StorageLock.cs:38`). Desktop und Shell warten in dieser Zeit.
  - `FileService.ReadExecutable` (`FileService.cs:187`) liest den ganzen ladbaren Teil, bis 128 MiB, in einer Sperre.
  - `CosmosLinuxRun.ReadFromRoot` (`CosmosLinuxRun.cs:478-506`, `File.ReadAllBytes`) liest den Interpreter bis 128 MiB in einer Sperre.
  - `AuthorizeProcessRoot` (`FileService.cs:930`) arbeitet bis zu 4096 Einträge mit `GetAttributes` in einer Sperre ab.
- Für ld-musl (klein) und das aktuelle Startprogramm unkritisch, bei großen Programmen sind es Sekunden.
- Dazu kommen zwei Allokationen ohne `BufferAllocation.TryAllocate`, die bei knappem Speicher den Kernel anhalten statt `-ENOMEM` zu liefern (vgl. Kopf 3baf7cb0: „512M Kernel-Halt durch Speichermangel“):
  - `File.ReadAllBytes` in `ReadFromRoot`
  - `new byte[needed]` in `LinuxSpawn.cs:493` (execve und Interpreter, bis 128 MiB)

**6. `ReadFromRoot` umgeht die Namens- und Linkregeln von V7**

- Weder `ValidName` (`.zehos-`, 8.3-Zweitnamen) noch das Auflösen von Links innerhalb der Wurzel greifen hier, und Cosmos folgt Links absolut (`CosmosLinuxRun.cs:478ff`; im Kommentar nur als „folgt nicht“ beschrieben).
- Auf FAT ist das folgenlos: keine Links, und keine `.zehos`-Dateien unter einer erlaubten Wurzel.
- Auf einer ext2-Wurzel (Cosmos kann dort Symlinks) könnte ein Link im PT_INTERP-Pfad eine Datei außerhalb der Wurzel als Interpreter laden. Das gilt nur, falls ext2 je Prozesswurzel wird; heute ungeprüft.

**7. Persistentes `/tmp` und `/.cache` gegen die 4096er-Grenze (Vermutung, demo-relevant)**

- V37 und die Demo legen `/tmp`, `/tmp/xdg-runtime` und `/.cache` auf der Platte in der Wurzel an (`LinuxRunEnvironment.cs:30-39`), und niemand räumt sie auf.
- Was liegen bleibt, zählt bei jedem weiteren `run` gegen `FileService.MaximumProcessRootEntries = 4096` (`FileService.cs:919`):
  - fontconfig-Cache
  - Socketdateien (V16: `bind` legt eine echte FAT-Datei an, `LinuxUnixNames.cs:193`, und sie bleibt)
  - sqlite- und Profildateien
- Über der Grenze verweigert `run` mit „mehr als 4096 Einträge“.
- Die Demo baut die Platte je Lauf neu, betroffen sind also nur wiederholte Starts auf derselben Platte (Probe vor Ort). Den Abstand des Ladybird-Bündels zur Grenze kenne ich nicht.
- **Vorschlag:** Anzahl der Einträge in `/data/lb` nach zwei oder drei Läufen einmal zählen.

**8. Socketdatei auf FAT ist für `stat` eine reguläre Datei (Vermutung)**

- `BindPath` legt eine 0755-Datei an. `stat(path)` meldet `S_IFREG`, nicht `S_IFSOCK` (nur `fstat` auf dem Socket meldet `S_IFSOCK`).
- Programme, die vor `bind` nur bei `S_ISSOCK` ein `unlink` machen, bekämen beim zweiten Start auf derselben Platte `-EADDRINUSE`.
- Ob Ladybird so prüft, habe ich nicht nachgesehen. Es ist im Zusammenhang mit 7 zu sehen.

## Belegt und vermutet

- **Belegt aus dem Code:** 1, 2, 3, 5, 6 (Abläufe nachvollzogen, nicht ausgeführt).
- **Aus dem Code abgeleitet, Auslöser nicht ausgeführt:** 4.
- **Vermutung mit offener Messung:** 7, 8.
