#if ZEHOS_WASM || ZEHOS_NETWORK_TEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime.Wasm;

// Gemeinsame Rueckrufgrenze fuer Kernel und Hosttests; der native Worker bleibt im Kernelteil.
internal static unsafe partial class WasmRuntime
{
    private const int MaximumOpenFiles = 16;
    private static readonly Dictionary<int, FileStream> files = [];
    // Offene Paketordner: Handle -> Pfad relativ zur Paketwurzel. Sie zaehlen gegen MaximumOpenFiles wie Dateien.
    private static readonly Dictionary<int, string> directories = [];
    private static IWasmSession? current;
    private static int nextHandle;
    private static uint[] indexedFrame = [];
    // Einmal je Lauf melden: present laeuft je Bild, ein Dauerfehler darf das serielle Protokoll nicht fluten.
    private static bool presentFailureReported;

    // Auflösung der Stopwatch in ns, mindestens 1 (clock_res_get darf nie 0 melden).
    internal static ulong ClockResolutionNs(long frequency) => frequency <= 0 ? 1 : (ulong)Math.Max(1L, 1_000_000_000L / frequency);

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_open")]
    private static int HostOpen(byte* path, uint length)
    {
        if (files.Count + directories.Count >= MaximumOpenFiles) return -1;
        FileStream? stream = null;
        try
        {
            string? full = ResolvePackagePath(path, length);
            if (full == null) return -1;
            stream = StorageLock.Run(() => new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read));
            int handle = ++nextHandle;
            files[handle] = stream;
            return handle;
        }
        catch (Exception)
        {
            // Erst nach erfolgreicher Registrierung besitzt die Handle-Tabelle den Stream.
            try { if (stream != null) StorageLock.Run(stream.Dispose); }
            catch (Exception) { /* Auch die Freigabe darf keine Ausnahme ueber die native Grenze tragen. */ }
            return -1;
        }
    }

    private static string? ResolvePackagePath(byte* path, uint length)
    {
        string? root = current?.Root;
        return root == null ? null : WasmPackagePaths.ResolveChecked(root, Encoding.UTF8.GetString(path, (int)length));
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_file_size")]
    private static long HostFileSize(int handle)
    {
        try { return files.TryGetValue(handle, out var stream) ? StorageLock.Run(() => stream.Length) : -1; }
        catch (Exception) { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_file_read")]
    private static int HostFileRead(int handle, ulong offset, byte* buffer, uint length)
    {
        try
        {
            if (!files.TryGetValue(handle, out var stream)) return -1;
            // Offener Paket-Stream: je Abschnitt von hoechstens 1 MiB eine eigene Sperre (Cosmos-ext2/FAT nicht threadsicher,
            // Diagnoselauf 13), damit ein grosser Lesewunsch einer App den Hauptthread nicht lange aufhaelt (Review blue).
            nint address = (nint)buffer;
            int total = 0;
            while (total < (int)length)
            {
                int start = total;
                int count = Math.Min(StorageLock.ChunkBytes, (int)length - total);
                int read = StorageLock.Run(() =>
                {
                    stream.Position = (long)offset + start;
                    return stream.Read(new Span<byte>((void*)(address + start), count));
                });
                if (read <= 0) break;
                total += read;
            }

            return total;
        }
        catch (Exception) { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_close")]
    private static void HostClose(int handle)
    {
        try { if (files.Remove(handle, out var stream)) StorageLock.Run(stream.Dispose); }
        catch (Exception) { /* Das Handle ist bereits entfernt; ein Schliessfehler darf die native Grenze nicht verlassen. */ }
    }

    // Paketordner (Block #419, P4-A): jede Rueckgabe ist eine WASI-Fehlernummer, Ausnahmen werden zu EIO.
    // dirHandle -1 meint die Paketwurzel.
    private static int ResolveDirectory(int dirHandle, byte* path, uint length, out string relative)
    {
        relative = "";
        string directory = "";
        if (dirHandle != -1 && !directories.TryGetValue(dirHandle, out directory!)) return WasmPackagePaths.BadFile;
        try { relative = WasmPackagePaths.Join(directory, Encoding.UTF8.GetString(path, (int)length)); }
        catch (UnauthorizedAccessException) { return WasmPackagePaths.AccessDenied; }
        return WasmPackagePaths.Success;
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_package_opendir")]
    private static int HostPackageOpenDir(int dirHandle, byte* path, uint length, int* handle)
    {
        try
        {
            int status = ResolveDirectory(dirHandle, path, length, out string relative);
            if (status != WasmPackagePaths.Success) return status;
            if (files.Count + directories.Count >= MaximumOpenFiles) return WasmPackagePaths.TooManyFiles;
            status = WasmPackagePaths.List(current?.Root, relative, out _);
            if (status != WasmPackagePaths.Success) return status;
            int newHandle = ++nextHandle;
            directories[newHandle] = relative;
            *handle = newHandle;
            return WasmPackagePaths.Success;
        }
        catch (Exception) { return WasmPackagePaths.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_package_readdir")]
    private static int HostPackageReadDir(int handle, byte* buffer, uint length, ulong cookie, uint* used)
    {
        try
        {
            if (!directories.TryGetValue(handle, out string? relative)) return WasmPackagePaths.BadFile;
            int status = WasmPackagePaths.List(current?.Root, relative, out var entries);
            if (status != WasmPackagePaths.Success) return status;
            *used = (uint)WasiDirectoryEntries.Write(entries, cookie, new Span<byte>(buffer, (int)length));
            return WasmPackagePaths.Success;
        }
        catch (Exception) { return WasmPackagePaths.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_package_closedir")]
    private static void HostPackageCloseDir(int handle) => directories.Remove(handle);

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_package_openat")]
    private static int HostPackageOpenAt(int dirHandle, byte* path, uint length, int* handle, ulong* size)
    {
        FileStream? stream = null;
        try
        {
            int status = ResolveDirectory(dirHandle, path, length, out string relative);
            if (status != WasmPackagePaths.Success) return status;
            if (files.Count + directories.Count >= MaximumOpenFiles) return WasmPackagePaths.TooManyFiles;
            string? root = current?.Root;
            if (root == null) return WasmPackagePaths.AccessDenied;
            status = WasmPackagePaths.Stat(root, relative, out ulong bytes, out int type);
            if (status != WasmPackagePaths.Success) return status;
            if (type != WasmPackagePaths.TypeFile) return WasiErrno.IsDirectory;
            string full = WasmPackagePaths.ResolveChecked(root, relative);
            stream = StorageLock.Run(() => new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read));
            int newHandle = ++nextHandle;
            files[newHandle] = stream;
            *handle = newHandle;
            *size = bytes;
            return WasmPackagePaths.Success;
        }
        catch (Exception exception)
        {
            try { if (stream != null) StorageLock.Run(stream.Dispose); }
            catch (Exception) { /* Auch die Freigabe darf keine Ausnahme ueber die native Grenze tragen. */ }
            return exception is FileNotFoundException or DirectoryNotFoundException ? WasmPackagePaths.NotFound
                : exception is UnauthorizedAccessException ? WasmPackagePaths.AccessDenied : WasmPackagePaths.Io;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_package_statat")]
    private static int HostPackageStatAt(int dirHandle, byte* path, uint length, byte* type, ulong* size)
    {
        try
        {
            int status = ResolveDirectory(dirHandle, path, length, out string relative);
            if (status != WasmPackagePaths.Success) return status;
            status = WasmPackagePaths.Stat(current?.Root, relative, out ulong bytes, out int kind);
            if (status != WasmPackagePaths.Success) return status;
            *type = (byte)kind;
            *size = bytes;
            return WasmPackagePaths.Success;
        }
        catch (Exception) { return WasmPackagePaths.Io; }
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
