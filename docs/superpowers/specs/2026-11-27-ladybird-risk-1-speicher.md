# Risiko-Review Block 1: Speicher und Seitenfehler

Stand: origin/story/ladybird 3baf7cb0 gegen origin/master (322 Commits). Cosmos: origin/story/ladybird-cosmos 63e672db, V43-Entwurf
cosmos-patch/user-frame-reserve 9ff7e867 und task/lb-v43-frame-reserve 6f6c0c44. Die Prüfung war rein lesend: kein Bau, kein Gastlauf, kein Hosttest.
"Belegt" heißt hier, dass ein anderer Agent den Befund im Gastprotokoll gesehen hat (IRC-Log), oder dass die Codelogik eindeutig ist. Alles
andere ist Vermutung aus dem Lesen.

Gelesen: LinuxUserProcess.cs (Karte, Frames, Seitenfehler, Copy, Release, Destroy), LinuxUserProcessFilePages.cs, LinuxFilePages.cs (V30),
LinuxUserProcessShared.cs, LinuxMemFile.cs (V17/V32), LinuxFork.cs, LinuxSpawn.cs (execve), LinuxProcView.cs, die mmap-, munmap-, mprotect-,
mremap-, madvise- und msync-Pfade, CosmosLinuxRun.cs (CosmosUserPages, OnFault, Execute, ChildBody, ProcessBody, ReplaceImage),
Cosmos UserPageTable.cs, PageTables.cs, UserMode.cs (HandleFault mit Interrupts an), PageAllocator (V43-Patch und DIAG).

## P1

**P1-1 (schon bekannt, im IRC gemeldet, Lead: "bekannt")**: Für Nutzer-Frames gibt es keine Gesamtgrenze.
- Stellen: CosmosLinuxRun.cs:33 (`MaxBytes` 512 MB, nur je Prozess), LinuxUserProcess.cs:1390 (`NewFrame`), LinuxMemFile.cs:697 (memfd
  und Seitenablage). Cosmos: UserPageTable.AllocateFrame im Stand 63e672db.
- Auslöser: mehrere Prozesse eines Laufs (Ladybird mit rund 8 Prozessen, oder ein Programm, das forkt) oder mehrere memfds mit write von je bis
  zu 512 MB. Zusammen leeren sie den PageAllocator.
- Wirkung: Danach scheitert der nächste Kernel-Heap- oder Stack-Zuwachs, es folgt FAILFAST OutOfMemoryException und der Kernel hält an.
- Beleg: Ein anderer Agent hat es im Gastlauf mit 512 MB gesehen (base3: "Thread 85 refused: no memory for its stack", dann FAILFAST OOM).
- Demo: Mit `--memory 2G` (Standard seit 3baf7cb0) kam es bisher nicht vor. Mit 512M oder 1G ist der Kernel-Halt realistisch.

## P2

**P2-1 (V43-Entwurf, Vermutung)**: Die Reserve deckt Kernel-Stapel nicht ab.
- Stellen: CosmosLinuxRun.cs:821-822 (StartChild `new Thread`), Cosmos `SchedulerThread.DefaultStackSize` = 256 KiB,
  libSystemNative.cs:160-168.
- Problem: Bei 512 MB Gastspeicher sind das 64 MiB Reserve. Linux-Threads (bis 256 je Lauf, V36) bekommen je 256 KiB Kernelstapel als
  zusammenhängenden Lauf von 64 Seiten aus demselben Allokator. Diese Stapel nehmen die Reserve ungeprüft: 256 × 256 KiB = 64 MiB, so viel wie
  die ganze Reserve. Ladybird hatte im Lauf mindestens 85 Threads, also über 21 MiB.
- Dazu kommen große zusammenhängende Heap-Objekte, etwa das Wachstum der `_frames`-Dictionary mit bis zu 131072 Einträgen je Prozess.
  `FreePageCount` zählt freie Seiten, keine Läufe; das nennt die Patchdoku selbst.
- Folge: Das V43-Ziel "js/img mit 512 MB: sauberer Abbruch, kein Halt" ist dadurch nicht gesichert.
- Vorschlag: Vor `new Thread` in StartChild, StartFork und StartExec prüfen, ob `FreePages - 64 >= ReservedPages` gilt, sonst EAGAIN.
  Alternativ Stapel × Threadplätze in die Reserve einrechnen. Danach im Gast mit 512M js/img gegenprüfen.

## P3 (9)

1. **Vorhandene Seite nach mprotect, Rennen** (LinuxUserProcess.cs:947, aus dem Code eindeutig).
   - Ablauf: Thread B schreibt auf eine RO-Seite (Fehler mit Present-Bit). Bevor B `_memGate` bekommt, setzt Thread A die Seite per mprotect
     auf RW.
   - Wirkung: HandlePageFault gibt false zurück und B bekommt SIGSEGV. Linux wiederholt den Zugriff (spurious fault).
   - Einordnung: Das Gegenstück zum V38-Fix für Present-Fehler fehlt. In Ladybird ist das selten (RELRO geht RW zu RO).
2. **execve ohne vfork kurz nach pthread_join** (LinuxSpawn.cs:317 und 810, LinuxUserProcess.cs:412-437, CosmosLinuxRun.cs:918-926,
   UserPageTable.cs:302-305; Vermutung).
   - Ablauf: ExitThread zählt `_liveThreads` sofort herunter und weckt clear_child_tid, bevor der Kernelthread die Tabelle löst.
     `OnlyMainThreadLives` ist dann schon wahr. Ein execve in diesem Fenster ruft im alten Prozess Destroy auf, und `UserPageTable.Dispose`
     wirft wegen `_bound > 0`.
   - Wirkung: DestroyMemory bricht ab, alle Frames des alten Abbilds und die Tabelle bleiben verloren, FreeIds läuft nicht.
   - Einordnung: Das Fenster ist klein (gleiche CPU, execve liest erst von der Platte). Ladybird startet über posix_spawn, also den vfork-Weg,
     und ist nicht betroffen.
3. **/proc eines anderen Prozesses** (LinuxProcView.cs:83-94 gegen LinuxUserProcess.cs:1259-1273; Vermutung).
   - Problem: DestroyMemory ändert `_frames`, `_shared` und `_regions` ohne `_memGate`. Describe eines anderen Prozesses liest sie unter
     `_memGate`.
   - Wirkung: Gleichzeitig kann das in foreach eine InvalidOperationException werfen oder falsche RSS-Werte liefern. Der Speicher selbst
     bleibt intakt (verwaltete Sammlungen, keine Frame-Inhalte).
4. **W^X gilt je Abbildung, nicht je Frame** (LinuxUserProcessShared.cs:52-81, LinuxSyscalls.cs:330-345).
   - Auslöser: Dieselbe memfd oder Datei zweimal mit MAP_SHARED abbilden, einmal PROT_WRITE und einmal PROT_EXEC.
   - Wirkung: Derselbe Frame ist gleichzeitig schreibbar und ausführbar. Das ist Linux-konform und kein Bruch zum Kernel; es sollte aber in
     LINUX_ABI bei W^X stehen.
5. **ftruncate kürzt eine geteilte Datei** (LinuxMemFile.cs:382-400, Abweichung schon dokumentiert).
   - Wirkung: Prozesse behalten den alten Frame. Ihre späteren Schreibzugriffe gehen still verloren (kein Flush, weil der Frame nicht mehr in
     `_table` steht), und Leser sehen auseinanderlaufende Inhalte.
   - Einordnung: Das betrifft nur sqlite `-shm`, wenn die Datei abgebildet ist und dabei gekürzt wird.
6. **Ohne TLB-Shootdown hängt die Sicherheit an der CPU-Bindung** (CosmosLinuxRun.cs:727-733, UserPageTable.cs:465-484).
   - Problem: Läuft ein Thread doch auf einer anderen CPU, verweigert die Tabelle Unmap und Protect. munmap legt die Seiten dann still als
     gesperrte Bereiche ab, und die Frames zählen weiter gegen `MaxBytes`.
   - Einordnung: Das ist sicher, verliert aber Speicher. Die Bindung wird über Affinity vererbt (gelesen in SchedulerManager.cs:1340).
7. **Seitenfehler-Kosten unter `_memGate`** (LinuxFilePages.cs:122-180, LinuxMemFile.cs:651-656).
   - Problem: Jeder Fehler auf einer Dateiseite geht die Ausschnittliste linear durch. Die Seitenablage fragt bei jedem Fehler
     `_backing.Length()` ab, also Plattenmetadaten unter StorageLock.
   - Wirkung: Nur Leistung. Unter TCG oder mit vielen Bibliotheken kann das die Ladezeit verlängern, siehe die Wartezeiten im Demo-Skript.
8. **Handler kehrt unter Speichermangel zurück** (CosmosLinuxRun.cs:1438-1470).
   - Ablauf: Ein SIGSEGV-Handler, der zurückkehrt, wiederholt den Befehl. Unter Speichermangel ist das eine Endlosschleife bis zum
     Zeitlimit, wie bei Linux.
   - Einordnung: Für die Demo nur relevant, weil der Lauf dann bis `--timeout` hängt.
9. **V43, Restwege** (9ff7e867 PageAllocator.cs:594, Doku `patches/user-frame-reserve.md`).
   - Problem: Seiten für Seitentabellen (TryMap) und alle Kernel-Heap-Allokationen, die ein Programm auslöst, umgehen die Reserve. Die
     Zerstückelung bleibt (`FreePageCount` statt Läufen). Der Pin ändert `deploy/cosmos/COMMIT`, `VERSION` und `global.json` zusammen; das ist
     ein Cosmos-Versionswechsel mit nativem Bau und Gaststart.
   - Ergebnis: Ein Gastlauf mit V43 ist nicht belegt; im Branch steht nur Code, kein Prüfstand.

## Ohne Befund geprüft

- Seitenfehler und munmap oder Protect laufen beide unter `_memGate`. Der V38-Fix (MapsTo) deckt den Not-present-Fall richtig ab.
- Thread- und Prozessende: ChildBody und ProcessBody lösen die Tabellenbindung vor ReleaseSlot. Execute und ProcessBody warten auf alle
  Threads, bevor sie die Tabelle freigeben. Dispose wartet auf `_active == 0`, die Frames kommen erst nach der Tabelle frei.
- Refzählung geteilter Frames (Acquire, Release, Unref, Tod der Ablage, gesperrte Seiten): keinen Double-Free gefunden, also kein
  FreeFrame auf eine wiedervergebene Kernelseite.
- Sperrreihenfolge: FileGate, `_sharedGate`, `_memGate`, LinuxMemFile `_gate`, StorageLock, PageAllocator. Seitenfehler nehmen kein FileGate.
  ReadThrough und WriteThrough arbeiten mit Kernelpuffern. Keinen Zyklus gefunden.
- Kernelzugriff auf Benutzerspeicher läuft nur über `_frames` und HHDM nach Prüfung der Karte. User-Grenze 0x7FFF_FFFF_F000, MinAddress
  0x10000. Die Kernelhälfte der PML4 ist ohne U-Bit, Benutzerseiten haben kein G-Bit, NX wird gesetzt. TryMap lehnt W+X ab.
- OnFault läuft im Threadkontext mit Interrupts an (UserMode.HandleFault). Sperren und Plattenlesen im Seitenfehler sind dort zulässig.
- V30: Fill vor Map, kein Fenster mit halb gefüllter Seite. SIGBUS hinter dem Dateiende. fork kopiert Ausschnitte mit eigenem Verweis.
- V32: Zurückgeschrieben werden nur geschriebene Seiten. Bei munmap und Prozessende wird geflusht, bevor die Ablage stirbt.

Offen: Kein Punkt ist von mir nachgestellt (keine Hosttests, kein Gast). P1-1 ist durch den Gastlauf eines anderen Agenten belegt, alle anderen Punkte sind gelesen.
