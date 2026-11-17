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
        Set("current", null);
        Runtime.GetField("presentFailureReported", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
        Files.Clear();
        Cosmos.Kernel.System.Diagnostics.Log.Writer = messages.Add;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Set("current", null);
        Cosmos.Kernel.System.Diagnostics.Log.Writer = null;
        foreach (FileStream stream in Files.Values) stream.Dispose();
        Files.Clear();
        Directory.Delete(directory, true);
    }

    [TestMethod]
    public void PresentExceptionsStayInsideBothNativeCallbacksAndReportOnlyOnce()
    {
        var session = new Session { Failure = new InvalidOperationException("present") };
        Set("current", session);
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
        Set("current", session);
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
        Set("current", new Session { RootFailure = true });
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
        Set("current", new Session { RootDirectory = directory });
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
    public void CloseRemovesHandleEvenWhenDisposeThrows()
    {
        var stream = new FailingFile(Path.Combine(directory, "close")) { Failure = "close" };
        Files.Add(8, stream);
        ((delegate* unmanaged<int, void>)Entry("HostClose"))(8);
        Assert.IsFalse(Files.ContainsKey(8));
        ((delegate* unmanaged<int, void>)Entry("HostClose"))(8);
    }

    private static Dictionary<int, FileStream> Files => (Dictionary<int, FileStream>)Field("files").GetValue(null)!;
    private static FieldInfo Field(string name) => Runtime.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static void Set(string name, object? value) => Field(name).SetValue(null, value);
    private static void* Entry(string name) => (void*)Runtime.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MethodHandle.GetFunctionPointer();

    private sealed class FailingFile(string path) : FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
    {
        public string? Failure { get; set; }
        public override long Length => Failure == "size" ? throw new InvalidOperationException("size") : base.Length;
        public override int Read(Span<byte> buffer) => Failure == "read" ? throw new InvalidOperationException("read") : base.Read(buffer);
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Failure == "close") throw new InvalidOperationException("close");
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