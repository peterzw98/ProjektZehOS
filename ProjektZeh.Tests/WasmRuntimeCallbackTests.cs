using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
[DoNotParallelize]
public sealed unsafe class WasmRuntimeCallbackTests
{
    private static readonly Type Runtime = typeof(WasmRuntime);
    private readonly List<string> messages = [];
    private readonly string directory = Path.Combine(Path.GetTempPath(), "zehos-host-callback-" + Guid.NewGuid().ToString("N"));

    [TestInitialize]
    public void Initialize()
    {
        Directory.CreateDirectory(directory);
        WasmRuntime.SetTestSession(null);
        Runtime.GetField("presentFailureReported", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
        Files.Clear();
        Directories.Clear();
        Cosmos.Kernel.System.Diagnostics.Log.Writer = messages.Add;
    }

    [TestCleanup]
    public void Cleanup()
    {
        WasmRuntime.SetTestSession(null);
        Cosmos.Kernel.System.Diagnostics.Log.Writer = null;
        foreach (FileStream stream in Files.Values) stream.Dispose();
        Files.Clear();
        Directories.Clear();
        Directory.Delete(directory, true);
    }

    [TestMethod]
    public void ClockResolutionIsNanosecondsPerTickAndNeverZero()
    {
        Assert.AreEqual(1_000_000UL, WasmRuntime.ClockResolutionNs(1_000));
        Assert.AreEqual(100UL, WasmRuntime.ClockResolutionNs(10_000_000));
        Assert.AreEqual(1UL, WasmRuntime.ClockResolutionNs(2_000_000_000), "Rundung auf 0 verboten");
        Assert.AreEqual(1UL, WasmRuntime.ClockResolutionNs(0), "ungueltige Frequenz");
    }

    [TestMethod]
    public void PresentExceptionsStayInsideBothNativeCallbacksAndReportOnlyOnce()
    {
        var session = new Session { Failure = new InvalidOperationException("present") };
        WasmRuntime.SetTestSession(session);
        uint pixel = 0x123456;
        ((delegate* unmanaged<uint*, int, int, int, int, void>)Entry("HostPresent"))(&pixel, 1, 1, 0, 0);
        byte index = 0;
        uint* palette = stackalloc uint[256];
        palette[0] = pixel;
        ((delegate* unmanaged<byte*, uint*, int, int, int, void>)Entry("HostPresentIndexed"))(&index, palette, 1, 1, 0);
        Assert.AreEqual(2, session.PresentCalls);
        Assert.AreEqual(1, messages.Count);
        StringAssert.Contains(messages[0], "InvalidOperationException");

        session.Failure = null;
        ((delegate* unmanaged<uint*, int, int, int, int, void>)Entry("HostPresent"))(&pixel, 1, 1, 0, 0);
        Assert.AreEqual(3, session.PresentCalls, "Ein abgefangener Fehler darf Folgeaufrufe nicht verhindern.");
        Assert.AreEqual(pixel, session.LastPixel);
    }

    [TestMethod]
    public void IndexedFrameConvertsPaletteAndSurvivesAFailingDiagnostic()
    {
        var session = new Session { Failure = new InvalidOperationException("present") };
        WasmRuntime.SetTestSession(session);
        Cosmos.Kernel.System.Diagnostics.Log.Writer = _ => throw new InvalidOperationException("log");
        byte index = 2;
        uint* palette = stackalloc uint[256];
        palette[2] = 0xFF123456;
        ((delegate* unmanaged<byte*, uint*, int, int, int, void>)Entry("HostPresentIndexed"))(&index, palette, 1, 1, 0);
        session.Failure = null;
        ((delegate* unmanaged<byte*, uint*, int, int, int, void>)Entry("HostPresentIndexed"))(&index, palette, 1, 1, 0);
        Assert.AreEqual(2, session.PresentCalls);
        Assert.AreEqual(0x00123456u, session.LastPixel);
    }

    [TestMethod]
    public void OpenConvertsAnUnexpectedSessionExceptionToFailure()
    {
        WasmRuntime.SetTestSession(new Session { RootFailure = true });
        byte path = 0;
        Assert.AreEqual(-1, ((delegate* unmanaged<byte*, uint, int>)Entry("HostOpen"))(&path, 1));
        Assert.AreEqual(0, Files.Count);
    }

    [TestMethod]
    public void FileSizeAndReadConvertNonIoExceptionsToFailure()
    {
        using var stream = new FailingFile(Path.Combine(directory, "read"));
        Files.Add(7, stream);
        stream.Failure = "size";
        Assert.AreEqual(-1L, ((delegate* unmanaged<int, long>)Entry("HostFileSize"))(7));
        stream.Failure = "read";
        byte value = 0;
        Assert.AreEqual(-1, ((delegate* unmanaged<int, ulong, byte*, uint, int>)Entry("HostFileRead"))(7, 0, &value, 1));
        stream.Failure = null;
        Assert.AreEqual(0L, ((delegate* unmanaged<int, long>)Entry("HostFileSize"))(7));
        Assert.AreEqual(0, ((delegate* unmanaged<int, ulong, byte*, uint, int>)Entry("HostFileRead"))(7, 0, &value, 1));
    }

    [TestMethod]
    public void OpenRegistersAFileUntilCloseReleasesIt()
    {
        string path = Path.Combine(directory, "module.txt");
        File.WriteAllText(path, "Test");
        WasmRuntime.SetTestSession(new Session { RootDirectory = directory });
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("module.txt");
        int handle;
        fixed (byte* data = bytes) handle = ((delegate* unmanaged<byte*, uint, int>)Entry("HostOpen"))(data, (uint)bytes.Length);
        Assert.IsTrue(handle > 0);
        Assert.IsTrue(Files.ContainsKey(handle));
        Assert.AreEqual(4L, ((delegate* unmanaged<int, long>)Entry("HostFileSize"))(handle));
        ((delegate* unmanaged<int, void>)Entry("HostClose"))(handle);
        Assert.IsFalse(Files.ContainsKey(handle));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [TestMethod]
    public void MissingSessionAndUnknownHandlesReportFailure()
    {
        byte value = (byte)'x';
        Assert.AreEqual(-1, ((delegate* unmanaged<byte*, uint, int>)Entry("HostOpen"))(&value, 1));
        Assert.AreEqual(-1L, ((delegate* unmanaged<int, long>)Entry("HostFileSize"))(123));
        Assert.AreEqual(-1, ((delegate* unmanaged<int, ulong, byte*, uint, int>)Entry("HostFileRead"))(123, 0, &value, 1));
        ((delegate* unmanaged<int, void>)Entry("HostClose"))(123);
        Assert.AreEqual(0, Files.Count);
    }
    [TestMethod]
    public void CloseRemovesHandleEvenWhenDisposeThrows()
    {
        var stream = new FailingFile(Path.Combine(directory, "close")) { Failure = "close" };
        Files.Add(8, stream);
        ((delegate* unmanaged<int, void>)Entry("HostClose"))(8);
        Assert.IsFalse(Files.ContainsKey(8));
        ((delegate* unmanaged<int, void>)Entry("HostClose"))(8);
    }

    [TestMethod]
    public void PackageReadDir_SmallBuffer_ReturnsPartialEntriesAndUsedLength()
    {
        File.WriteAllText(Path.Combine(directory, "alpha"), "a");
        File.WriteAllText(Path.Combine(directory, "beta"), "b");
        WasmRuntime.SetTestSession(new Session { RootDirectory = directory });
        int handle = OpenDir(-1, "");
        byte* buffer = stackalloc byte[64];
        uint used = 0;
        var readdir = (delegate* unmanaged<int, byte*, uint, ulong, uint*, int>)Entry("HostPackageReadDir");
        Assert.AreEqual(0, readdir(handle, buffer, 30, 0, &used));
        Assert.AreEqual(30u, used, "Voller Puffer: Kopf (24) plus abgeschnittener Name.");
        Assert.AreEqual(0, readdir(handle, buffer, 64, 0, &used));
        Assert.AreEqual(2 * 24 + 5 + 4u, used);
        Assert.AreEqual(0, readdir(handle, buffer, 64, 2, &used));
        Assert.AreEqual(0u, used);
        Assert.AreEqual(8, readdir(handle + 100, buffer, 64, 0, &used));
    }

    [TestMethod]
    public void PackageOpenAt_RelativeToDir_OpensFile()
    {
        Directory.CreateDirectory(Path.Combine(directory, "res"));
        File.WriteAllText(Path.Combine(directory, "res", "Messages"), "Test");
        WasmRuntime.SetTestSession(new Session { RootDirectory = directory });
        int dir = OpenDir(-1, "res");
        int handle = 0;
        ulong size = 0;
        byte[] name = System.Text.Encoding.UTF8.GetBytes("Messages");
        var openat = (delegate* unmanaged<int, byte*, uint, int*, ulong*, int>)Entry("HostPackageOpenAt");
        fixed (byte* data = name) Assert.AreEqual(0, openat(dir, data, (uint)name.Length, &handle, &size));
        Assert.AreEqual(4UL, size);
        Assert.IsTrue(Files.ContainsKey(handle));
        byte[] up = System.Text.Encoding.UTF8.GetBytes("../res/Messages");
        fixed (byte* data = up) Assert.AreEqual(0, openat(dir, data, (uint)up.Length, &handle, &size), "Innerhalb der Wurzel erlaubt.");
        byte[] outside = System.Text.Encoding.UTF8.GetBytes("../../x");
        fixed (byte* data = outside) Assert.AreEqual(2, openat(dir, data, (uint)outside.Length, &handle, &size));
        fixed (byte* data = name) Assert.AreEqual(8, openat(999, data, (uint)name.Length, &handle, &size));
        byte[] type = new byte[1];
        ulong statSize = 0;
        fixed (byte* data = name)
        fixed (byte* kind = type)
            Assert.AreEqual(0, ((delegate* unmanaged<int, byte*, uint, byte*, ulong*, int>)Entry("HostPackageStatAt"))(dir, data, (uint)name.Length, kind, &statSize));
        Assert.AreEqual(4, type[0]);
        Assert.AreEqual(4UL, statSize);
    }

    [TestMethod]
    public void PackageCloseDir_FreesHandle()
    {
        WasmRuntime.SetTestSession(new Session { RootDirectory = directory });
        int handle = OpenDir(-1, "");
        Assert.IsTrue(Directories.ContainsKey(handle));
        ((delegate* unmanaged<int, void>)Entry("HostPackageCloseDir"))(handle);
        Assert.IsFalse(Directories.ContainsKey(handle));
        // Maximal 16 Handles zusammen mit Dateien.
        int[] handles = new int[16];
        for (int index = 0; index < 16; index++) handles[index] = OpenDir(-1, "");
        byte[] empty = [(byte)'.'];
        int extra = 0;
        fixed (byte* data = empty)
            Assert.AreEqual(33, ((delegate* unmanaged<int, byte*, uint, int*, int>)Entry("HostPackageOpenDir"))(-1, data, 1, &extra));
        ((delegate* unmanaged<int, void>)Entry("HostPackageCloseDir"))(handles[0]);
        Assert.AreNotEqual(0, OpenDir(-1, ""));
    }

    private static int OpenDir(int parent, string path)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(path.Length == 0 ? "." : path);
        int handle = 0;
        fixed (byte* data = bytes)
            Assert.AreEqual(0, ((delegate* unmanaged<int, byte*, uint, int*, int>)Entry("HostPackageOpenDir"))(parent, data, (uint)bytes.Length, &handle));
        return handle;
    }

    private static Dictionary<int, string> Directories => (Dictionary<int, string>)Field("directories").GetValue(null)!;
    private static Dictionary<int, FileStream> Files => (Dictionary<int, FileStream>)Field("files").GetValue(null)!;
    private static FieldInfo Field(string name) => Runtime.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static void* Entry(string name) => (void*)Runtime.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MethodHandle.GetFunctionPointer();

    private sealed class FailingFile(string path) : FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
    {
        public string? Failure { get; set; }
        public override long Length => Failure == "size" ? throw new InvalidOperationException("size") : base.Length;
        public override int Read(Span<byte> buffer) => Failure == "read" ? throw new InvalidOperationException("read") : base.Read(buffer);
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            // Nur beim ausdruecklichen Schliessen werfen: eine Ausnahme im Finalizer (disposing == false) beendet den ganzen
            // Testprozess, sobald ein anderer Test den GC laufen laesst (CI #241: "Test host process crashed ... close").
            if (disposing && Failure == "close") throw new InvalidOperationException("close");
        }
    }

    private sealed class Session : IWasmSession
    {
        public Exception? Failure { get; set; }
        public bool RootFailure { get; set; }
        public string? RootDirectory { get; set; }
        public int PresentCalls { get; private set; }
        public uint LastPixel { get; private set; }
        public string? Root => RootFailure ? throw new InvalidOperationException("root") : RootDirectory;
        public string? SaveRoot => null;
        public bool AllowsNetwork => false;
        public bool ShouldExit => false;
        public uint TicksMilliseconds => 0;
        public void Write(int fd, ReadOnlySpan<byte> data) { }
        public void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test)
        {
            PresentCalls++;
            if (Failure != null) throw Failure;
            LastPixel = pixels[0];
        }
        public int PollKey() => -1;
        public bool TryReadEvent(out WasmInputEvent value) { value = default; return false; }
        public bool SetPointerCapture(bool enabled) => false;
    }
}