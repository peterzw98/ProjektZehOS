#if ZEHOS_WASM || ZEHOS_NETWORK_TEST
using System;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>Dienste, die eine laufende Wasm-Anwendung vom Kernel erhaelt.</summary>
internal interface IWasmSession
{
    /// <summary>Ordner, auf den die Anwendung lesend als "/" zugreift; null sperrt Dateizugriffe.</summary>
    string? Root { get; }
    /// <summary>Etappe 13.4: Ordner fuer /save (eigener App-Speicher); null, wenn die App nicht speichern darf oder kann.</summary>
    string? SaveRoot { get; }
    bool AllowsNetwork { get; }
    void Write(int fd, ReadOnlySpan<byte> data);
    void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test);
    int PollKey();
    bool TryReadEvent(out WasmInputEvent value);
    /// <summary>Faengt den Zeiger ein oder gibt ihn frei; false, wenn die Sitzung das nicht kann.</summary>
    bool SetPointerCapture(bool enabled);
    bool ShouldExit { get; }
    /// <summary>Uhr der Anwendung in Millisekunden; steht still, solange die Sitzung pausiert.</summary>
    uint TicksMilliseconds { get; }
}
#endif
