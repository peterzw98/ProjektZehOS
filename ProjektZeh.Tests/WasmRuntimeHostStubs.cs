using System;

// Nur die Hosttests binden diese Ersatztypen ein; Cosmos-Hardware und Kernelprotokoll fehlen dort absichtlich.
namespace Cosmos.Kernel.System.Diagnostics
{
    internal static class Log
    {
        internal static Action<string>? Writer { get; set; }
        public static void WriteString(string text) => Writer?.Invoke(text);
    }
}

namespace ProjektZeh.Runtime.Wasm
{
    internal static unsafe partial class WasmRuntime
    {
        // Nur der Testteil setzt Sitzungen ohne den nativen Worker; im Kernel uebernimmt dies Execute.
        internal static void SetTestSession(IWasmSession? session) => current = session;
    }
}