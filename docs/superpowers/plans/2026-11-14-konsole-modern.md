# Moderne Konsole Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or superpowers:subagent-driven-development. Steps use checkbox syntax.

**Goal:** Den im Chat freigegebenen Konsolenumfang ohne Netzwerk vollständig umsetzen.
**Architecture:** Gemeinsame Shell und Dateidienste behalten; getrennte Terminalansichten komponieren. Unabhängige Dateidienst- und Shellarbeit kann getrennt vom UI bearbeitet werden.
**Tech Stack:** C#, .NET 10, Cosmos 3.0.94, vorhandenes MSTest.
**Spec:** docs/superpowers/specs/2026-11-14-konsole-modern-design.md

## Global Constraints
Keine neuen Pakete/Updates. Deutsche Kommentare, vorhandene Dateirechte und Result-Typen. Nichtblockierende Oberfläche. F4/Esc erhalten. Vorhandene Datenimages schützen. Keine Netzwerkbefehle.

## Review Focus
- Umleitungen/Aliase/Pipes dürfen Rechte und Kennwortschutz nicht umgehen.
- Neue Dateioperationen dürfen Ziele und fremde Rechte nicht still überschreiben.
- Kleine Fenster, Zoom und geteilte Ansichten müssen geclippt und bedienbar bleiben.
- Reset, Fokuswechsel und geschlossene Tabs müssen Modifier und Sitzungen freigeben.
- Pufferüberläufe und große Verzeichnisse dürfen keine unbeschränkte Arbeit erzeugen.

### Task 1: Dateioperationen
**Files:** FileService.cs, FileShellCommands.cs, FileAccessControl.cs falls erforderlich; neue FileOperationTests.cs.
**Interfaces:** FileService.CopyFile(source,target), MoveFile(source,target), DeleteFile(path), DeleteDirectory(path), Touch(path), GetFileSize(path), GetDiskSpace(path) mit Result-Rückgaben; bestehende Methoden erhalten.
- [x] Verhaltenstests zuerst schreiben und Fehlschlag nachweisen: Kopie/Verschieben/Löschen/Touch, fehlende Quelle, bestehendes Ziel, Berechtigungsverweigerung, Rechte nach Verschieben/Löschen.
- [x] Kleinste sichere Umsetzung und Befehle cp/mv/rm/rmdir/touch/df/du/find ergänzen. Kein rekursives rm; cp/mv verweigern bestehende Ziele.
- [x] Hosttests ausführen; Änderungen isoliert zum Review bereitstellen.

### Task 2: Shell und Eingabekomfort
**Files:** ShellService.cs, neue Pipeline-/Textcommand-Dateien, LineEditor.cs, InteractiveSession.cs; neue ShellFeatureTests.cs.
**Interfaces:** ShellService erhält optional FileService files; stellt Complete(string line,int cursor), CurrentDirectory, History, ExitRequested bereit. LineEditor erhält SetText(string), Complete(ShellService) und Verlaufssuche. Terminal kann ShellService über dieselben Methoden ansprechen.
- [x] Tests für Quotes/Operatoren, Pipelinefehler/-grenze, Rechte pro Stufe, Aliase/Zyklen, Geheimnisse, Sitzungsisolation, Textfilter und History schreiben, erwartete Fehlschläge ausführen.
- [x] Pipes/Umleitungen, Aliase, Befehle head/tail/grep/wc/sort/uniq/less/history/alias/date/uname/free/exit und Vervollständigung umsetzen. less liefert per ShellService einen Pagertext an Terminal und Textshell.
- [x] LineEditor um Tab und Ctrl-R-Verlaufssuche ergänzen; Textshell ohne blockierendes Lesen anbinden.
- [x] Hosttests ausführen; Änderungen isoliert zum Review bereitstellen.

### Task 3: Terminaloberfläche
**Files:** Apps/Terminal/*.cs, Desktop.cs, PixelSurface.cs falls für Zoom nötig; neue ModernTerminalTests.cs.
**Interfaces:** TerminalApp bleibt einzelne Ansicht und behält bisherigen Konstruktor; neue TerminalWorkspaceApp komponiert Ansichten mit Func<ShellService> als Factory. ShellService-APIs aus Task 2 verwenden.
- [x] Tests für Tabs/Splits, Fokus, Close/Exit, Zwischenablage, Suche, Auswahl, Zoom, Profile und unabhängige Zustände schreiben und Fehlschläge ausführen.
- [x] Terminalansicht mit Auswahl/Scrollleiste/Suche/Zoom/Pager ausbauen, Workspace mit Tabs/Splits/Menü/Status/Profilen ergänzen und Desktop anbinden.
- [x] Hosttests und kleine/verschobene Fenster prüfen.

### Task 4: Integration und Abnahme
**Files:** Kernel.cs, README.md, docs/KONSOLE_STATUS.md, Plan/Spec.
- [ ] Kernel: Dateien und echte Systeminformationen an Shell anbinden; alle Hosttests mit -warnaserror ausführen.
- [ ] Native Release- und Recovery-Builds erstellen; QEMU Boot, Shell, Eingabe, Tabs/Splits, Filter/Dateibefehle auf isolierter Imagekopie prüfen. VMware versuchen.
- [ ] Tatsächliche Ergebnisse und Grenzen dokumentieren; unabhängigen Agenten lesend prüfen lassen, berechtigte Befunde beheben und nachprüfen.
- [ ] Commit, Push und PR mit angeforderten Reviewern, ohne Merge.

Ausführung: Aufgaben1–3 abgeschlossen; integriert auf master b51cb86. Einzelne Host-Testgrenze im übernommenen TLS-Test separat nachgewiesen. Letzte Gastabnahme und Veröffentlichung siehe docs/KONSOLE_STATUS.md.
