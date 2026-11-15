using System;
using ProjektZeh.UI;

namespace ProjektZeh.Apps;

/// <summary>
/// Startmenueeintrag einer installierten Paket-App. <see cref="Create"/> liefert null, wenn die App gerade nicht
/// starten kann, etwa weil der gemeinsame Wasm-Worker bereits eine andere App ausfuehrt.
/// </summary>
internal sealed record ExternalApplication(string Id, string Name, Func<UiPainter, IApplication?> Create);
