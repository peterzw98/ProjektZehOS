using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmEngineSelectionTests
{
    private static readonly byte[] Aot = [0x00, 0x61, 0x6f, 0x74];

    [TestMethod]
    public void AotRunsOnlyWithLoaderBytesAndWithoutBudget()
    {
        Assert.AreEqual(WasmEngine.Aot, WasmEngineSelection.Select(aotBuiltIn: true, Aot, gas: 0));
    }

    [TestMethod]
    public void WithoutAotBytesEverythingStaysWithWasm3()
    {
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: true, null, gas: 0));
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: true, [], gas: 0));
    }

    [TestMethod]
    public void KernelWithoutWamrIgnoresAotBytes()
    {
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: false, Aot, gas: 0));
    }

    [TestMethod]
    public void GasBudgetKeepsTheRunOnWasm3()
    {
        // AOT-Code misst kein Gas; ein Lauf mit Budget (Probe, Netzwerkprobe) darf es nicht verlieren.
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: true, Aot, gas: 1));
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: true, Aot, gas: ulong.MaxValue));
    }
}
