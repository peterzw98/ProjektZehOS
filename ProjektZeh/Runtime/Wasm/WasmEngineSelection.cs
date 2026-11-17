using System;
using System.IO;
using ProjektZeh.Packages;
using ProjektZeh.Shared;

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
/// Waehlt die Laufzeit eines Laufs (AOT-Plan Etappen D und E). AOT nur, wenn der Kernel WAMR enthaelt, der
/// Laufzeitschalter an ist, das Paket ein .aot mit der eigenen Kennung enthaelt, dessen Bytes zum signierten Manifest
/// passen, und kein Gasbudget gilt: der AOT-Code misst kein Gas, Laeufe mit Budget bleiben deshalb bei wasm3. In jedem
/// anderen Fall laeuft das .wasm wie bisher.
/// </summary>
internal static class WasmEngineSelection
{
    /// <summary>
    /// Kennung der eigenen AOT-ABI im Dateinamen <c>&lt;entry ohne .wasm&gt;.&lt;AotAbi&gt;.aot</c>: WAMR 2.4.5,
    /// AOT-Format 5, Ziel x86-64 und Speicher-/Stackpruefung (wamrc-Schalter in
    /// Native/AotProbe/fixture/compile-aot.sh). Aendert sich einer davon, aendert sich die Kennung; aeltere .aot werden
    /// dann nicht mehr gefunden und die App laeuft unter wasm3.
    /// </summary>
    internal const string AotAbi = "wamr245-aot5-x64-bc";

    // Wie die Obergrenze fuer das .wasm in WasmGameApp; das .aot wird vollstaendig in den Speicher gelesen.
    internal const int MaximumAotBytes = 64 * 1024 * 1024;

// Eigenschaft statt Konstante: sonst meldet der Compiler je nach Build unerreichbaren Code (Warnungen als Fehler).
#if ZEHOS_WASM_AOT
    internal static bool AotBuiltIn => true;
#else
    // WAMR ist nur mit -p:ZehOSWasmAot=true im Kernel; ohne ihn laeuft jede App unter wasm3.
    internal static bool AotBuiltIn => false;
#endif

    private static volatile bool aotEnabled;

    /// <summary>Laufzeitschalter "wasmaot on|off"; Standard aus bis Etappe F (Peters Entscheidung, AOT-Plan Ledger).</summary>
    public static bool AotEnabled
    {
        get => aotEnabled;
        set => aotEnabled = value;
    }

    public static WasmEngine Select(bool aotBuiltIn, byte[]? aot, ulong gas) =>
        aotBuiltIn && aot is { Length: > 0 } && gas == 0 ? WasmEngine.Aot : WasmEngine.Wasm3;

    /// <summary>Dateiname des .aot zu einem Einstieg; das Manifest garantiert die Endung .wasm.</summary>
    public static string AotPath(string entry) => entry[..^".wasm".Length] + "." + AotAbi + ".aot";

    /// <summary>
    /// Die .aot-Datei des Einstiegs mit genau der eigenen Kennung, oder null: AOT nicht enthalten, ausgeschaltet, keine
    /// passende Datei oder laut signiertem Manifest leer bzw. groesser als <see cref="MaximumAotBytes"/>; dann laeuft das
    /// .wasm. Ein .aot fremder Kennung wird wie jede andere Paketdatei als Daten behandelt.
    /// </summary>
    public static PackageFile? FindAot(PackageManifest manifest, bool aotBuiltIn, bool enabled)
    {
        if (!aotBuiltIn || !enabled) return null;
        string path = AotPath(manifest.Entry);
        var file = Array.Find(manifest.Files, entry => string.Equals(entry.Path, path, StringComparison.Ordinal));
        return file is { Size: > 0 and <= MaximumAotBytes } ? file : null;
    }

    /// <summary>
    /// Ob ein gescheiterter AOT-Lauf unter wasm3 wiederholt wird: nur mit .wasm und nur, wenn WAMR noch nicht mit dem
    /// Instanziieren begonnen hat. Danach kann Gastcode gelaufen sein (start-Funktion, Konstruktoren), ein zweiter Lauf
    /// wiederholte dessen Wirkungen.
    /// </summary>
    public static bool FallsBackToWasm3(bool hasWasm, bool aotFailed, bool aotInstantiated) =>
        hasWasm && aotFailed && !aotInstantiated;

    /// <summary>
    /// Liest das von <see cref="FindAot"/> gelieferte .aot genau einmal ueber einen einzigen Dateizugriff und prueft genau
    /// diese Bytes gegen den SHA-256 aus dem signierten Manifest. Nur der zurueckgegebene Puffer darf an WAMR gehen:
    /// PackageStore.VerifyOnce prueft je Paketversion nur einmal je Kernellauf, eine spaetere Aenderung der Datei bliebe
    /// sonst unbemerkt.
    /// </summary>
    public static ResultObject<byte[]> ReadAot(string filesDirectory, PackageFile file)
    {
        // Grenze auch hier: ein direkter Aufruf ohne FindAot soll nicht bis zur Paketgrenze von 128 MiB allokieren.
        if (file.Size is <= 0 or > MaximumAotBytes)
            return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, $"AOT-Datei {file.Path} hat eine ungueltige Groesse.");
        byte[] bytes = new byte[file.Size];
        try
        {
            using var stream = new FileStream(Path.Combine(filesDirectory, file.Path), FileMode.Open, FileAccess.Read);
            if (stream.Length != file.Size) return Modified(file.Path);
            stream.ReadExactly(bytes);
        }
        catch (Exception exception) when (PackageStore.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.Failure, $"AOT-Datei {file.Path} nicht lesbar: {exception.Message}");
        }
        if (!string.Equals(PackageTrust.Sha256Hex(bytes), file.Sha256, StringComparison.Ordinal)) return Modified(file.Path);
        return ResultObjectFactory.CreateSuccess(bytes);
    }

    // Gleicher Text wie PackageStore.VerifyFiles: eine veraenderte signierte Datei sperrt den Start, statt auf wasm3
    // auszuweichen.
    private static ResultObject<byte[]> Modified(string path) =>
        ResultObjectFactory.CreateFailure<byte[]>(ResultCode.Failure, $"Paketdatei {path} wurde veraendert; Start gesperrt.");
}
