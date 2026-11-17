namespace ProjektZeh.Runtime.Wasm;

/// <summary>Laufzeit, die einen Wasm-Lauf ausfuehrt.</summary>
internal enum WasmEngine
{
    /// <summary>wasm3-Interpreter mit dem .wasm; Standard und Rueckfall.</summary>
    Wasm3,

    /// <summary>WAMR-AOT-Lader mit dem vom Herausgeber erzeugten .aot.</summary>
    Aot,
}

/// <summary>
/// Waehlt die Laufzeit eines Laufs (AOT-Plan Etappe D). AOT nur, wenn der Kernel WAMR enthaelt, .aot-Bytes vorliegen
/// und kein Gasbudget gilt: der AOT-Code misst kein Gas, Laeufe mit Budget bleiben deshalb bei wasm3. In jedem
/// anderen Fall laeuft das .wasm wie bisher. Etappe E ergaenzt Laufzeitschalter und Dateiwahl.
/// </summary>
internal static class WasmEngineSelection
{
    public static WasmEngine Select(bool aotBuiltIn, byte[]? aot, ulong gas) =>
        aotBuiltIn && aot is { Length: > 0 } && gas == 0 ? WasmEngine.Aot : WasmEngine.Wasm3;
}
