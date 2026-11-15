# Moderne ZehOS-Konsole

Am 14.11.2026 im Chat freigegebener Umfang: KDE-inspirierte Konsole vollständig ausbauen, ohne Netzwerkbefehle.

## Ziel und Ansatz
Vorhandene ShellService, LineEditor, TerminalBuffer, PixelSurface und Fensterverwaltung erweitern. KDE/Qt ist kein im Bare-Metal-Kernel nutzbarer Ersatz. Keine neuen Pakete oder Versionsupdates. Eine bloße Farbänderung erfüllt den Auftrag nicht; ein Linux-/KDE-Port würde eine andere Laufzeit voraussetzen.

## Oberfläche
Tabs und horizontal/vertikal geteilte Ansichten mit getrennten Shells, Arbeitsverzeichnissen, Eingaben und Verläufen. Kontextmenü, Statusleiste, Profile/Farben, Schriftzoom, Textauswahl und ZehOS-interne Zwischenablage. Suche und Scrollleiste für die gepufferte Ausgabe; Tastaturbedienung zusätzlich zu Mausbedienung. Bestehendes F4/Esc-Recovery bleibt erhalten. Ressourcen begrenzen und geschlossene Sitzungen freigeben.

## Shell
Tab-Vervollständigung von Befehlen und Pfaden, Verlaufssuche, sitzungslokale Aliase, Pipes und > / >>. Operatoren innerhalb von Anführungszeichen sind Text. Fehler stoppen die Pipeline, Ausgabebegrenzung schützt den Kernel. Geheimnisse werden weder vervollständigt noch im Verlauf gespeichert. Alle Befehle gehen durch die bestehende Berechtigungsprüfung, auch Aliase und Pipes.

## Befehle
Vorhandene Befehle erhalten. Ergänzen: cp, mv, rm, rmdir, touch, head, tail, grep, find, wc, sort, uniq, less, history, alias, date, uname, free, df, du, exit. Optionen und Grenzen in help/README dokumentieren. less ist ein bedienbarer Pager. Dateibefehle verwenden FileService und dessen Rechte-/Ersetzungslogik; keine ungefragten rekursiven Löschungen und keine stillen Überschreibungen. Texttools dürfen Datei oder Pipeline lesen.

## Grenzen und Abnahme
Kein Netzwerk, keine Linux-Binärkompatibilität, keine Behauptung vollständiger GNU-Optionen. Keine Host-Zwischenablage. Profile und Aliase zunächst sitzungslokal; begrenzter Scrollback. Hosttests einschließlich Rechte-/Fehlerfällen, nativer Release- und Recovery-Build, QEMU-Bedienprüfung mit isoliertem Datenimage, VMware soweit verfügbar. Fehlende Nachweise offen dokumentieren. Abschließend unabhängiges Review, Commit, Push und PR ohne Merge.
