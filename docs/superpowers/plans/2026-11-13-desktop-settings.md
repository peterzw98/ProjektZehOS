# Desktop-Einstellungen und Taskmanager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** KDE-orientierter Desktop mit Full HD, Systemeinstellungen, Hintergrundbildern, Doppelklickstart und Taskmanager bei erhaltener FPS-Korrektur.
**Architecture:** Vorhandene Fensterverwaltung und UI weiterverwenden. Hardwareanzeigen über Cosmos-Adapter, Modelle und Einstellungen auf dem Host prüfbar. Keine neuen Pakete.
**Tech Stack:** C#, .NET 10, Cosmos Gen3 3.0.94, MSTest, QEMU/VMware.
**Spec:** docs/superpowers/specs/2026-11-13-desktop-settings-design.md; vom Nutzer freigegeben und um Taskmanager mit CPU-Auslastung erweitert.

## Global Constraints

- Keine Updates, keine fremden Änderungen, eigene Datenimagekopien für Gastschreibtests.
- F4/Esc, Recovery, Terminals und Editor-Schließrückfragen erhalten.
- CPU-Werte tatsächlich messen; eingeschränkte Messbereiche ausdrücklich beschriften.
- Vollständige Umsetzung ausdrücklich beauftragt; keine weitere Planfreigabe nötig.

## Review Focus

- Moduswechsel muss tatsächliche Bildschirmgröße verifizieren; bei Fehler zurückkehren.
- Persistenzfehler dürfen die Sitzung nicht beenden oder Erfolg vortäuschen.
- Falsche Bitmapgrößen/-offsets dürfen keine unbeschränkte Allokation bewirken.
- Doppelklick darf nach Filterwechsel oder auf anderem Eintrag nicht starten.
- Taskmanager darf ungespeicherte Editorinhalte nicht ohne Rückfrage schließen.

## Festlegungen nach Recherche und Designfreigabe (13.11.2026)

- Freigegebener Entwurf: Claude-Design-Fläche „ProjektZehOS Desktop-Entwurf“ (Desktop mit Startmenü, Einstellungen Anzeige/Personalisierung, Taskmanager). Nutzerwunsch dazu: Taskmanager zeigt mehr (Datenträger, Netzwerk, …) und ist erweiterbar.
- Cosmos 3.0.94: `Canvas.GetFullScreen(Mode)` wechselt nur bei Treibern mit `IDisplayModes`; in den installierten Paketen bietet das nur der VMware-SVGA-Treiber. Der Limine-Bildpuffer (QEMU Standard-VGA) liefert genau einen Modus. Daher bootet Limine standardmäßig 1920×1080×32; ein zweiter Bootmenüeintrag 1024×768×32 ist die sichere Rückfallwahl. Die Einstellungsapp bietet einen Laufzeitwechsel nur bei mehreren gemeldeten Modi an und erklärt sonst wahrheitsgemäß den Bootweg.
- Nach einem Moduswechsel liefert `GetBuffer()` einen neuen Puffer; `PixelSurface`, Desktop, Eingabe, Mausgrenzen und `KernelConsole` (über erneutes Setzen der Schrift) werden neu ausgerichtet.
- Die eingebaute PSF-Schrift ist eine echte 16×32-Schrift (keine verdoppelte 8×16). Eine kleinere Schrift bräuchte eine neue Schriftdatei; ohne Freigabe für einen Download bleibt 16×32, die Oberfläche wird dafür angeordnet.
- Taskmanager: Abschnitte als einfache Liste (Titel + Zeilenquelle); Übersicht und Anwendungen mit eigener Darstellung. Datenträger über `IStorageService`, Netzwerk und physischer Speicher lesend über Cosmos (`NetworkManager`, `PageAllocator`) in einem Cosmos-Adapter.
- Hintergründe: vier eingebaute flächige Motive (Kanten, Nacht, Dünen, Einfarbig) ohne Pro-Pixel-Trigonometrie; eigenes Bild als unkomprimiertes 24/32-Bit-BMP bis 4 Megapixel und 16 MiB.

### Task 1: FPS-Basis übernehmen

- [ ] Geprüften FPS-Commit `bfe0b41` übernehmen, Konflikte auf Auftragsumfang begrenzen.
- [ ] Hosttests Release mit Warnungen als Fehler ausführen.

### Task 2: Anzeige und Einstellungenmodell

Files: Graphics/DisplayService.cs, neue Graphics/DesktopSettings.cs und Graphics/DisplaySettings.cs, Input/InputService.cs, Runtime/InteractiveSession.cs, ProjektZeh.csproj, Hosttests.

- [ ] Cosmos-Modi und Limine-Bootauflösung an installierter Version prüfen.
- [ ] Tests für Einstellungenvalidierung, Rücknahme/Frist und Größenwechsel zuerst rot ausführen.
- [ ] Full-HD-Boot und unterstützte Moduswechsel mit Bestätigung und Rückkehr umsetzen; Maus, Fenster und Shell mitführen.
- [ ] Persistenz über vorhandenen FileService; fehlende/defekte Werte mit sicheren Defaults und sichtbaren Speicherfehlern behandeln.

### Task 3: KDE-Oberfläche, Hintergründe, Startmenü

Files: Graphics/Desktop.cs, Window.cs, PixelSurface.cs, UI/*.cs, neue Apps/Settings/SettingsApp.cs, Hintergrunddecoder/-ressourcen, Hosttests.

- [ ] Verhaltenstests für Themewechsel, Bitmapfehler und Doppelklickziel/-frist zuerst rot ausführen.
- [ ] Gemeinsame Palette, lesbare Schrift, dezente Rahmen, klassische Taskleiste und Startmenü umsetzen.
- [ ] Einstellungsapp mit Anzeige und Personalisierung anbinden; Hintergrundvorschau, eingebaute Bilder und eigenes begrenztes BMP laden.
- [ ] Alle vorhandenen Apps auf gemeinsame Themefarben abstimmen; Diagnosen aus Desktop entfernen.

### Task 4: Taskmanager

Files: neue Apps/TaskManager/TaskManagerApp.cs und SystemMetrics.cs, Desktop.cs, Runtime/InteractiveSession.cs, DisplayService.cs, Hosttests.

- [ ] Tests für CPU-Proben mit unbekannten/ungültigen Zeitintervallen und Fensteraktionen zuerst rot ausführen.
- [ ] Laufende Anwendungen, Speicher, Uptime, FPS und CPU-Messbereich mit Verlauf anzeigen.
- [ ] Aktivieren und reguläres Schließen über WindowManager; Dirty-Rückfragen erhalten. Keine erfundenen Prozess- oder Einzel-App-CPU-Werte.

### Task 5: Gastprüfung, Dokumentation und Veröffentlichung

- [ ] Gesamte Hosttests mit `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` ausführen.
- [ ] Normal-/Recovery-Build, QEMU Full HD/Modi/Themes/Bilder/Apps/Doppelklick/Taskmanager/F4/Esc prüfen; Persistenz nur auf Datenkopie.
- [ ] Full-HD-Bildrate messen und VMware-Nachweis abgrenzen.
- [ ] README und docs/COSMOS_SUPPORT.md samt konkreten Ergebnissen aktualisieren.
- [ ] Unabhängiges Review, berechtigte Befunde beheben und erneut prüfen.
- [ ] Commit, Push, PR samt angefragten Reviewern; kein Merge.
