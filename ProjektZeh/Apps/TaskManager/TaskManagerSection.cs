using System;

namespace ProjektZeh.Apps.TaskManager;

/// <summary>
/// Erweiterungspunkt des Taskmanagers: ein Abschnitt mit Titel und sekundlich gelesenen Textzeilen.
/// Neue Systeminformationen werden als weiterer Abschnitt uebergeben, ohne den Taskmanager zu aendern.
/// </summary>
internal sealed record TaskManagerSection(string Title, Func<string[]> ReadLines);
