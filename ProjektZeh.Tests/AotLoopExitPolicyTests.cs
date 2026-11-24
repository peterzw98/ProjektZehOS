using ProjektZeh.Runtime.Wasm;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ProjektZeh.Tests;

[TestClass]
public class AotLoopExitPolicyTests
{
    private const ulong Code = 0x1000_0000, End = 0x1001_0000, Saved = 0x2000_0000;

    private static bool Leave(bool abort = true, int thread = 7, int worker = 7, bool native = true, int depth = 0,
        ulong ip = Code + 16, ulong sp = Saved - 4096, ulong code = Code, ulong end = End, ulong saved = Saved) =>
        AotLoopExitPolicy.ShouldLeave(abort, thread, worker, native, depth, ip, sp, code, end, saved);

    [TestMethod]
    public void LeavesAotCodeOfTheWorker()
    {
        Assert.IsTrue(Leave());
        Assert.IsTrue(Leave(ip: Code));
        Assert.IsTrue(Leave(ip: End - 1));
        Assert.IsTrue(Leave(sp: Saved - AotLoopExitPolicy.StackBytes));
    }

    [TestMethod] public void StaysWithoutAbort() => Assert.IsFalse(Leave(abort: false));
    [TestMethod] public void StaysInForeignThread() => Assert.IsFalse(Leave(thread: 8));
    [TestMethod] public void StaysWithoutWorker() => Assert.IsFalse(Leave(thread: -1, worker: -1));
    [TestMethod] public void StaysOutsideNativeRun() => Assert.IsFalse(Leave(native: false));

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void StaysInHostFunction(int depth) => Assert.IsFalse(Leave(depth: depth));

    // WAMR-Helfer, Allokator, Ausstieg.
    [TestMethod]
    [DataRow(Code - 1)]
    [DataRow(End)]
    public void StaysOutsideCode(ulong ip) => Assert.IsFalse(Leave(ip: ip));

    // Laden, Instanziieren, Abbau.
    [TestMethod]
    public void StaysWhenNotPublished()
    {
        Assert.IsFalse(Leave(code: 0, end: 0));
        Assert.IsFalse(Leave(ip: 0, code: 0, end: End));
        Assert.IsFalse(Leave(saved: 0));
    }

    [TestMethod]
    [DataRow(Saved)]
    [DataRow(Saved + 8)]
    [DataRow(Saved - AotLoopExitPolicy.StackBytes - 1)]
    public void StaysOnForeignStack(ulong sp) => Assert.IsFalse(Leave(sp: sp));
}
