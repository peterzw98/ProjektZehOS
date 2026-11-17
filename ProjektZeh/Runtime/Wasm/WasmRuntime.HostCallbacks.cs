#if ZEHOS_WASM || ZEHOS_NETWORK_TEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ProjektZeh.Runtime.Wasm;

// Gemeinsame Rueckrufgrenze fuer Kernel und Hosttests; der native Worker bleibt im Kernelteil.
internal static unsafe partial class WasmRuntime
{
    private const int MaximumOpenFiles = 16;
    private static readonly Dictionary<int, FileStream> files = [];
    private static IWasmSession? current;
    private static int nextHandle;
    private static uint[] indexedFrame = [];
    // Einmal je Lauf melden: present laeuft je Bild, ein Dauerfehler darf das serielle Protokoll nicht fluten.
    private static bool presentFailureReported;

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_open")]
    private static int HostOpen(byte* path, uint length)
    {
        if (files.Count >= MaximumOpenFiles) return -1;
        FileStream? stream = null;
        try
        {
            string? full = ResolvePackagePath(path, length);
            if (full == null) return -1;
            stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            int handle = ++nextHandle;
            files[handle] = stream;
            return handle;
        }
        catch (Exception)
        {
            // Erst nach erfolgreicher Registrierung besitzt die Handle-Tabelle den Stream.
            try { stream?.Dispose(); }
            catch (Exception) { /* Auch die Freigabe darf keine Ausnahme ueber die native Grenze tragen. */ }
            return -1;
        }
    }

    private static string? ResolvePackagePath(byte* path, uint length)
    {
        string? root = current?.Root;
        return root == null ? null : WasmPackagePaths.Resolve(root, Encoding.UTF8.GetString(path, (int)length));
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_file_size")]
    private static long HostFileSize(int handle)
    {
        try { return files.TryGetValue(handle, out var stream) ? stream.Length : -1; }
        catch (Exception) { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_file_read")]
    private static int HostFileRead(int handle, ulong offset, byte* buffer, uint length)
    {
        try
        {
            if (!files.TryGetValue(handle, out var stream)) return -1;
            stream.Position = (long)offset;
            return stream.Read(new Span<byte>(buffer, (int)length));
        }
        catch (Exception) { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_close")]
    private static void HostClose(int handle)
    {
        try { if (files.Remove(handle, out var stream)) stream.Dispose(); }
        catch (Exception) { /* Das Handle ist bereits entfernt; ein Schliessfehler darf die native Grenze nicht verlassen. */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_present")]
    private static void HostPresent(uint* pixels, int width, int height, int tic, int test)
    {
        try { current?.Present(new ReadOnlySpan<uint>(pixels, width * height), width, height, tic, test != 0); }
        catch (Exception exception) { ReportPresentFailure(exception); }
    }

    private static void ReportPresentFailure(Exception exception)
    {
        if (presentFailureReported) return;
        presentFailureReported = true;
        try { Cosmos.Kernel.System.Diagnostics.Log.WriteString("WASM present-Fehler: " + exception.GetType().Name + "\n"); }
        catch (Exception) { /* Protokoll nicht verfuegbar; das Bild entfaellt, die App laeuft weiter. */ }
    }

    // Palettenbilder werden nativ in einen wiederverwendeten Puffer umgerechnet; im Interpreter kostete das
    // auf dem Host fast die Haelfte der Bildzeit von Doom.
    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_present_indexed")]
    private static void HostPresentIndexed(byte* indices, uint* palette, int width, int height, int tic)
    {
        try
        {
            int count = width * height;
            if (indexedFrame.Length < count) indexedFrame = new uint[count];
            for (int index = 0; index < count; index++) indexedFrame[index] = palette[indices[index]] & 0x00FFFFFF;
            current?.Present(indexedFrame.AsSpan(0, count), width, height, tic, false);
        }
        catch (Exception exception) { ReportPresentFailure(exception); }
    }
}
#endif
